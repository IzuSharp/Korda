using System.Buffers.Binary;
using System.Text.Json;

namespace Korda.Data;

internal enum PayloadKind : ushort { Parameters = 1, Rows = 2, ExpressionValues = 3, MutationValues = 4 }
internal sealed record WireDescriptor(uint Id, CodecDescriptor Codec, uint BoundCodecId);
internal sealed record WireExpectation(PayloadKind Kind, uint Profile, byte[] MetadataHash, uint EntityId, uint ProjectionId, WireDescriptor[] Descriptors)
{
    internal uint? ExactRows { get; init; }
}

// One field serialization contract, not a CLR/native struct ABI. Only owned bytes escape parsing.
internal sealed class DbWire
{
    internal const int MaxEncodedBytes = 4 * 1024 * 1024;
    internal const int MaxDescriptors = 4096;
    private DbWire(byte[] bytes, WireExpectation expected, uint rows) { Bytes = bytes; Expected = expected; Rows = rows; }
    internal byte[] Bytes { get; }
    internal WireExpectation Expected { get; }
    internal uint Rows { get; }
    internal static byte[] Encode(WireExpectation expected, uint rows, IReadOnlyList<ReadOnlyMemory<byte>?> cells)
    {
        CheckExpectation(expected, rows);
        var count = checked((int)rows * expected.Descriptors.Length);
        if (cells.Count != count) throw new ArgumentException("Cell count differs from the row-major shape.", nameof(cells));
        var payloadOffset = checked(WireSchema.HeaderBytes + expected.Descriptors.Length * WireSchema.DescriptorBytes + count * WireSchema.CellBytes);
        long total = payloadOffset;
        foreach (var cell in cells) total = checked(total + (cell?.Length ?? 0));
        if (total > MaxEncodedBytes) throw new ArgumentException("Payload exceeds the configured wire byte limit.");
        var result = new byte[(int)total]; "NWDB"u8.CopyTo(result);
        U16(result, 4, 1); U16(result, 6, (ushort)expected.Kind); U32(result, 8, WireSchema.HeaderBytes); U64(result, 16, (ulong)total);
        expected.MetadataHash.CopyTo(result, 24); U32(result, 56, rows); U32(result, 60, (uint)expected.Descriptors.Length);
        U32(result, 64, expected.EntityId); U32(result, 72, expected.ProjectionId); U32(result, 76, expected.Profile);
        U64(result, 80, WireSchema.HeaderBytes); U64(result, 88, (ulong)payloadOffset);
        for (var i = 0; i < expected.Descriptors.Length; i++)
        {
            var d = expected.Descriptors[i]; var offset = WireSchema.HeaderBytes + i * WireSchema.DescriptorBytes;
            U32(result, offset, d.Id); U16(result, offset + 4, (ushort)d.Codec.Id); U16(result, offset + 6, d.Codec.Nullable ? (ushort)1 : (ushort)0); U32(result, offset + 8, d.BoundCodecId);
        }
        var data = payloadOffset;
        for (var i = 0; i < cells.Count; i++)
        {
            var d = expected.Descriptors[i % expected.Descriptors.Length]; var cell = cells[i];
            if (cell is null && !d.Codec.Nullable) throw new ArgumentException("SQL NULL requires a nullable descriptor.");
            if (cell is { } value) ValidateScalar(d.Codec, value.Span);
            var offset = WireSchema.HeaderBytes + expected.Descriptors.Length * WireSchema.DescriptorBytes + i * WireSchema.CellBytes;
            U16(result, offset, (ushort)d.Codec.Id); U16(result, offset + 2, cell is null ? (ushort)1 : (ushort)0); U32(result, offset + 4, d.BoundCodecId);
            if (cell is { } bytes)
            {
                U64(result, offset + 8, (ulong)(bytes.IsEmpty ? payloadOffset : data)); U64(result, offset + 16, (ulong)bytes.Length);
                bytes.Span.CopyTo(result.AsSpan(data)); data += bytes.Length;
            }
        }
        return result;
    }
    internal static DbWire Parse(ReadOnlySpan<byte> bytes, WireExpectation expected)
        => ParseCore(bytes, expected, null);
    // Only the internal host's independently allocated, immutable managed batch
    // may transfer this storage. The caller must never clear or pool the array.
    internal static DbWire ParseOwned(byte[] bytes, WireExpectation expected)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return ParseCore(bytes, expected, bytes);
    }
    private static DbWire ParseCore(ReadOnlySpan<byte> bytes, WireExpectation expected, byte[]? owned)
    {
        try
        {
            if (bytes.Length < WireSchema.HeaderBytes || bytes.Length > MaxEncodedBytes || !bytes[..4].SequenceEqual("NWDB"u8)) Bad("Invalid header or size.");
            var rows = R32(bytes, 56); CheckExpectation(expected, rows);
            if (R16(bytes, 4) != 1 || R16(bytes, 6) != (ushort)expected.Kind || R32(bytes, 8) != WireSchema.HeaderBytes || R32(bytes, 12) != 0
                || R64(bytes, 16) != (ulong)bytes.Length || !bytes.Slice(24, 32).SequenceEqual(expected.MetadataHash)
                || R32(bytes, 60) != expected.Descriptors.Length || R32(bytes, 64) != expected.EntityId || R32(bytes, 68) != 0
                || R32(bytes, 72) != expected.ProjectionId || R32(bytes, 76) != expected.Profile || R64(bytes, 80) != WireSchema.HeaderBytes)
                Bad("ABI, profile, metadata, identity or reserved header mismatch.");
            var count = checked((int)rows * expected.Descriptors.Length);
            var cellsOffset = checked(WireSchema.HeaderBytes + expected.Descriptors.Length * WireSchema.DescriptorBytes);
            var payloadOffset = checked(cellsOffset + count * WireSchema.CellBytes);
            if (payloadOffset > bytes.Length || R64(bytes, 88) != (ulong)payloadOffset) Bad("Truncated or overlapping tables.");
            for (var i = 0; i < expected.Descriptors.Length; i++)
            {
                var d = expected.Descriptors[i]; var o = WireSchema.HeaderBytes + i * WireSchema.DescriptorBytes;
                if (R32(bytes, o) != d.Id || R16(bytes, o + 4) != d.Codec.Id || R16(bytes, o + 6) != (d.Codec.Nullable ? 1 : 0) || R32(bytes, o + 8) != d.BoundCodecId || R32(bytes, o + 12) != 0)
                    Bad("Descriptor count, order, ID, codec, nullability or reserved bits differ.");
            }
            var ranges = new List<(int Start, int End)>();
            for (var i = 0; i < count; i++)
            {
                var d = expected.Descriptors[i % expected.Descriptors.Length]; var o = cellsOffset + i * WireSchema.CellBytes;
                var flags = R16(bytes, o + 2); var dataOffset = R64(bytes, o + 8); var length = R64(bytes, o + 16);
                if (R16(bytes, o) != d.Codec.Id || R32(bytes, o + 4) != d.BoundCodecId || flags > 1) Bad("Cell codec or flags differ from the descriptor.");
                if (flags == 1)
                { if (!d.Codec.Nullable || dataOffset != 0 || length != 0) Bad("Invalid SQL NULL cell."); continue; }
                if (dataOffset < (ulong)payloadOffset || dataOffset > (ulong)bytes.Length || length > (ulong)bytes.Length - dataOffset || (length == 0 && dataOffset != (ulong)payloadOffset)) Bad("Invalid cell range.");
                if (length > 0) ranges.Add(((int)dataOffset, (int)(dataOffset + length)));
                ValidateScalar(d.Codec, bytes.Slice((int)dataOffset, (int)length));
            }
            ranges.Sort((x, y) => x.Start.CompareTo(y.Start));
            for (var i = 1; i < ranges.Count; i++) if (ranges[i].Start < ranges[i - 1].End) Bad("Nonempty cell ranges overlap.");
            return new(owned ?? bytes.ToArray(), expected, rows);
        }
        catch (Exception e) when (e is OverflowException or ArgumentOutOfRangeException or JsonException)
        { throw new ArgumentException("Malformed bounded db-wire payload.", nameof(bytes), e); }
    }
    internal static WireExpectation KeyExpectation<TEntity>(EntityKey<TEntity> key, uint profile, uint[] boundCodecs) where TEntity : class
    {
        key.RequireComplete();
        if (boundCodecs.Length != key.Values.Length) throw new ArgumentException("The parent key requires every bound codec.", nameof(boundCodecs));
        return new(PayloadKind.MutationValues, profile, Convert.FromHexString(key.Table.MetadataHash), key.Table.Identity.Id, 0,
            key.Values.Select((v, i) => new WireDescriptor(v.Column.Definition.Id, v.Column.Definition.Codec, boundCodecs[i])).ToArray())
        { ExactRows = 1 };
    }
    internal ReadOnlyMemory<byte>? Cell(uint row, int column)
    {
        if (row >= Rows || (uint)column >= Expected.Descriptors.Length) throw new ArgumentOutOfRangeException(nameof(row));
        var o = checked(WireSchema.HeaderBytes + Expected.Descriptors.Length * WireSchema.DescriptorBytes + ((int)row * Expected.Descriptors.Length + column) * WireSchema.CellBytes);
        if (R16(Bytes, o + 2) == 1) return null;
        return new ReadOnlyMemory<byte>(Bytes, checked((int)R64(Bytes, o + 8)), checked((int)R64(Bytes, o + 16)));
    }
    private static void CheckExpectation(WireExpectation expected, uint rows)
    {
        if (expected.ExactRows is { } exact && rows != exact) Bad("The consumer requires an exact row count.");
        if (expected.Kind == PayloadKind.Rows && expected.Descriptors.Length == 0) Bad("Result rows require an actual row shape.");
        if (expected.Profile is not (1 or 2) || expected.MetadataHash.Length != 32 || expected.Descriptors.Length > MaxDescriptors || !Enum.IsDefined(expected.Kind)) Bad("Unknown profile, kind or shape limit.");
        if (expected.Kind == PayloadKind.Parameters && rows != 1 || expected.Kind == PayloadKind.MutationValues && (rows == 0 || expected.EntityId == 0)
            || expected.Kind == PayloadKind.ExpressionValues && (expected.Descriptors.Length != 1 || expected.Descriptors[0].Id != 1 || expected.MetadataHash.Any(b => b != 0))
            || expected.Kind != PayloadKind.MutationValues && expected.EntityId != 0 || (expected.Kind == PayloadKind.Rows) != (expected.ProjectionId > 0)) Bad("Invalid kind-specific identity/count.");
        var ids = new HashSet<uint>();
        foreach (var d in expected.Descriptors)
        {
            CodecRegistry.Validate(d.Codec);
            if (!ids.Add(d.Id) || (expected.Kind != PayloadKind.Rows && d.Id == 0)) Bad("Duplicate/reserved descriptor ID.");
            if (d.Codec.Configuration is null ? d.BoundCodecId != d.Codec.Id : d.BoundCodecId < 1024) Bad("Invalid source-bound codec ID.");
        }
    }
    internal static void ValidateScalar(CodecDescriptor codec, ReadOnlySpan<byte> bytes)
    {
        CodecRegistry.Validate(codec);
        switch (codec.Id)
        {
            case 1: if (bytes.Length != 2) Bad("Int16 width."); break;
            case 2: case 17: if (bytes.Length != 4) Bad("Int32 width."); break;
            case 3: case 15: if (bytes.Length != 8) Bad("Int64 width."); break;
            case 4: if (bytes.Length != 1 || bytes[0] > 1) Bad("Boolean value."); break;
            case 5: _ = Validation.Utf8.GetCharCount(bytes); break;
            case 6: break;
            case 7: if (bytes.Length != 16) Bad("UUID width."); break;
            case 8: if (bytes.Length != 4 || RInt32(bytes) is < -719162 or > 2932896) Bad("Date range."); break;
            case 9: if (bytes.Length != 8 || R64(bytes, 0) > 86399999999) Bad("Time range."); break;
            case 10: if (bytes.Length != 8 || BinaryPrimitives.ReadInt64LittleEndian(bytes) is < -62135596800000000 or > 253402300799999999) Bad("Instant range."); break;
            case 11: if (bytes.Length != 8 || !double.IsFinite(BinaryPrimitives.ReadDoubleLittleEndian(bytes))) Bad("Double value."); break;
            case 12: if (bytes.Length != 4 || !float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(bytes))) Bad("Single value."); break;
            case 13:
                var v = DecimalBytes.Decode(bytes); using (var config = JsonDocument.Parse(codec.Configuration!)) DecimalBytes.CheckFit(v, config.RootElement.GetProperty("precision").GetByte(), config.RootElement.GetProperty("scale").GetByte()); break;
            case 14: _ = DecimalBytes.Decode(bytes); break;
            case 16:
                _ = Validation.Utf8.GetCharCount(bytes);
                if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) Bad("JSON BOM.");
                // Validate the complete value without owning a copy or materializing
                // a document. The default reader options reject comments, trailing
                // commas and multiple root values, as the document parser did.
                var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
                if (!reader.Read()) throw new JsonException("JSON requires a value.");
                while (reader.Read()) { }
                break;
        }
    }
    private static int RInt32(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadInt32LittleEndian(b);
    internal static ushort R16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    internal static uint R32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    internal static ulong R64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);
    internal static void U16(Span<byte> b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b[o..], v);
    internal static void U32(Span<byte> b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b[o..], v);
    internal static void U64(Span<byte> b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b[o..], v);
    private static void Bad(string message) => throw new ArgumentException(message);
}

internal static class CodecRegistry
{
    internal static void Validate(CodecDescriptor codec)
    {
        if (codec.Id is < 1 or > 17) throw new ArgumentException("Unknown required logical codec.");
        if (codec.Id is 13 or 15)
        {
            if (codec.Configuration is null) throw new ArgumentException("Configured codec requires configuration.");
            using var json = JsonDocument.Parse(codec.Configuration); var value = json.RootElement;
            string canonical;
            if (codec.Id == 13)
            { var p = value.GetProperty("precision").GetByte(); var s = value.GetProperty("scale").GetByte(); canonical = Codecs.Decimal(p, s).Descriptor.Configuration!; }
            else { var s = value.GetProperty("scale").GetByte(); canonical = Codecs.FixedScaleMoney(s).Descriptor.Configuration!; }
            if (canonical != codec.Configuration) throw new ArgumentException("Unknown, duplicate or noncanonical codec configuration.");
        }
        else if (codec.Configuration is not null) throw new ArgumentException("This codec has no configuration.");
    }
}

internal sealed record ControlField(ushort Id, ushort Type, byte[] Bytes);
internal static class ControlRecord
{
    internal static byte[] Encode(ushort kind, params ControlField[] fields)
    {
        if (fields.Select(f => f.Id).Distinct().Count() != fields.Length || fields.Any(f => f.Id == 0)) throw new ArgumentException("Invalid control field IDs.");
        fields = fields.OrderBy(f => f.Id).ToArray(); var start = checked(16 + fields.Length * 16); var size = checked(start + fields.Sum(f => f.Bytes.Length));
        if (size > DbWire.MaxEncodedBytes) throw new ArgumentException("Control record exceeds limit.");
        var bytes = new byte[size]; DbWire.U16(bytes, 0, 1); DbWire.U16(bytes, 2, kind); DbWire.U32(bytes, 4, (uint)size); DbWire.U32(bytes, 8, (uint)fields.Length);
        for (var i = 0; i < fields.Length; i++)
        { var f = fields[i]; var o = 16 + i * 16; DbWire.U16(bytes, o, f.Id); DbWire.U16(bytes, o + 2, f.Type); DbWire.U32(bytes, o + 4, (uint)f.Bytes.Length); DbWire.U64(bytes, o + 8, (ulong)start); f.Bytes.CopyTo(bytes, start); start += f.Bytes.Length; }
        return bytes;
    }
    internal static byte[] U32(uint value) { var bytes = new byte[4]; DbWire.U32(bytes, 0, value); return bytes; }
    internal static byte[] Vector(IEnumerable<uint> values)
    { var array = values.ToArray(); var bytes = new byte[checked(array.Length * 4)]; for (var i = 0; i < array.Length; i++) DbWire.U32(bytes, i * 4, array[i]); return bytes; }
    internal static byte[] Relation<TParent, TChild>(Relation<TParent, TChild> relation) where TParent : class where TChild : class
    {
        var canonical = relation.CanonicalBytes();
        return Encode(WireSchema.RelationControlKind,
            new(1, 1, U32(relation.Parent.Identity.Id)), new(2, 5, Convert.FromHexString(relation.Parent.MetadataHash)),
            new(3, 1, U32(relation.Child.Identity.Id)), new(4, 5, Convert.FromHexString(relation.Child.MetadataHash)),
            new(5, 7, Vector(relation.Parents.Select(c => c.Definition.Id))), new(6, 7, Vector(relation.Children.Select(c => c.Definition.Id))),
            new(7, 7, Vector(relation.Children.Select(c => c.Definition.Codec.Nullable ? 1u : 0u))), new(8, 5, System.Security.Cryptography.SHA256.HashData(canonical)));
    }
}
