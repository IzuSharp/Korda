using System.Buffers.Binary;
using System.Data;
using System.Text;
using Korda.Data;

namespace Korda.Database;

internal static class HostEncoding
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static byte[] Text(string value, int maximum, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (value.Length == 0 || value.Contains('\0')) throw new ArgumentException("A nonempty Unicode value without NUL is required.", name);
        var bytes = Utf8.GetBytes(value); if (bytes.Length > maximum) throw new ArgumentOutOfRangeException(name); return bytes;
    }
    internal static byte[] U64(ulong value) { var bytes = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, value); return bytes; }
    internal static ControlField Count(ushort id, long value) => new(id, 2, U64(checked((ulong)value)));
    internal static ControlField Duration(ushort id, TimeSpan value) => new(id, 3, U64(checked((ulong)value.Ticks * 100)));
    internal static void WaitBudget(TimeSpan value)
    { if (value < TimeSpan.Zero || value.Ticks > (long)(ulong.MaxValue / 100)) throw new ArgumentOutOfRangeException(nameof(value), "A nonnegative finite wait budget is required."); }
    internal static byte[] Sequence(IReadOnlyList<byte[]> records)
    {
        var length = checked(4 + records.Sum(r => checked(4 + r.Length))); var bytes = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)records.Count)); var offset = 4;
        foreach (var record in records) { BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), checked((uint)record.Length)); record.CopyTo(bytes, offset + 4); offset += 4 + record.Length; }
        return bytes;
    }
    internal static byte[] Transaction(TransactionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options); WaitBudget(options.CommitTimeout); WaitBudget(options.CleanupTimeout);
        var isolation = options.Isolation switch { null => 0u, IsolationLevel.ReadCommitted => 1u, IsolationLevel.RepeatableRead => 2u, IsolationLevel.Serializable => 3u, _ => throw new ArgumentOutOfRangeException(nameof(options), "Unsupported isolation level.") };
        var fields = new List<ControlField> { new(1, 1, ControlRecord.U32(isolation)), new(2, 1, ControlRecord.U32(options.ReadOnly ? 1u : 0u)), Duration(3, options.CommitTimeout), Duration(4, options.CleanupTimeout) };
        if (options.RecoveryKey is { } key) fields.Add(new(5, 4, Text(key, 1024, nameof(options.RecoveryKey))));
        return ControlRecord.Encode(3, fields.ToArray());
    }
    internal static Dictionary<ushort, byte[]> ReadControl(ReadOnlySpan<byte> bytes, ushort kind)
    {
        if (bytes.Length < 16 || bytes.Length > DbWire.MaxEncodedBytes || BinaryPrimitives.ReadUInt16LittleEndian(bytes) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]) != kind || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != bytes.Length || BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != 0)
            throw new DatabaseException("Invalid native control record.", DatabaseErrorKind.ShapeMismatch);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        if (count > 64 || checked(16L + count * 16L) > bytes.Length) throw new DatabaseException("Invalid native control fields.", DatabaseErrorKind.ShapeMismatch);
        var result = new Dictionary<ushort, byte[]>(); var next = checked(16 + (int)count * 16); ushort previous = 0;
        for (var i = 0; i < count; i++)
        {
            var at = 16 + i * 16; var id = BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]); var type = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at + 2)..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 4)..]); var offset = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(at + 8)..]);
            if (id <= previous || type is < 1 or > 7 || offset != (ulong)next || length > bytes.Length - next || type == 1 && length != 4 || type is 2 or 3 && length != 8)
                throw new DatabaseException("Invalid native control field range.", DatabaseErrorKind.ShapeMismatch);
            result.Add(id, bytes.Slice(next, (int)length).ToArray()); previous = id; next += (int)length;
        }
        if (next != bytes.Length) throw new DatabaseException("Native control record has trailing bytes.", DatabaseErrorKind.ShapeMismatch);
        return result;
    }
    internal static TransactionSnapshot Snapshot(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16 || bytes.Length > DbWire.MaxEncodedBytes || BinaryPrimitives.ReadUInt16LittleEndian(bytes) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]) != 7 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != bytes.Length || BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != 0)
            throw new DatabaseException("Invalid native control record.", DatabaseErrorKind.ShapeMismatch);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        if (count > 64 || checked(16L + count * 16L) > bytes.Length) throw new DatabaseException("Invalid native control fields.", DatabaseErrorKind.ShapeMismatch);
        // Keep only offsets into this synchronous input. Validate unknown fields
        // and the complete record before reading any required snapshot value.
        Span<(int Offset, int Length)> fields = stackalloc (int, int)[9]; fields.Clear();
        var next = checked(16 + (int)count * 16); ushort previous = 0;
        for (var i = 0; i < count; i++)
        {
            var at = 16 + i * 16; var id = BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]); var type = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at + 2)..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 4)..]); var offset = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(at + 8)..]);
            if (id <= previous || type is < 1 or > 7 || offset != (ulong)next || length > bytes.Length - next || type == 1 && length != 4 || type is 2 or 3 && length != 8)
                throw new DatabaseException("Invalid native control field range.", DatabaseErrorKind.ShapeMismatch);
            if (id <= fields.Length) fields[id - 1] = (next, (int)length);
            previous = id; next += (int)length;
        }
        if (next != bytes.Length) throw new DatabaseException("Native control record has trailing bytes.", DatabaseErrorKind.ShapeMismatch);
        var state = SnapshotUInt32(bytes, fields[0]); var outcome = SnapshotUInt32(bytes, fields[1]); var attempted = SnapshotUInt32(bytes, fields[2]);
        var cleanup = SnapshotUInt32(bytes, fields[3]); var disposition = SnapshotUInt32(bytes, fields[4]); var released = SnapshotUInt32(bytes, fields[5]); var deadline = SnapshotUInt32(bytes, fields[6]);
        if (state > 4 || outcome > 3 || attempted > 1 || cleanup > 4 || disposition > 3 || released > 1 || deadline > 1 || fields[7].Length != 8)
            throw new DatabaseException("Native snapshot values are invalid.", DatabaseErrorKind.ShapeMismatch);
        var correlation = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(fields[7].Offset, fields[7].Length));
        // Every present field starts after the header; offset zero distinguishes
        // an absent provider code from a present, empty provider code.
        var provider = fields[8].Offset == 0 ? null : Utf8.GetString(bytes.Slice(fields[8].Offset, fields[8].Length));
        return new((TransactionState)state, (TransactionOutcome)outcome, attempted == 1, new((CleanupStatus)cleanup, correlation, provider, released == 1, (ConnectionDisposition)disposition, deadline == 1));
    }
    private static uint SnapshotUInt32(ReadOnlySpan<byte> bytes, (int Offset, int Length) field)
        => field.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(field.Offset, field.Length)) : throw new DatabaseException("Native snapshot field is missing.", DatabaseErrorKind.ShapeMismatch);
}
