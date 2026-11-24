using System.Buffers.Binary;
using System.Text;
using Korda.Data;

namespace Korda.Query.Internal;

/// <summary>Exact immediate kind6/kind13/kind22 fields over B01 encoders; no query tree is accumulated.</summary>
internal static class OperandEncoding
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static ControlField U32(ushort id, uint value) => new(id, 1, ControlRecord.U32(value));
    internal static ControlField U64(ushort id, ulong value, ushort type = 2)
    { var bytes = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, value); return new(id, type, bytes); }
    internal static ControlField Text(ushort id, string value) => new(id, 4, Utf8.GetBytes(value));
    internal static ControlField Bytes(ushort id, byte[] bytes, ushort type = 5) => new(id, type, bytes);
    internal static byte[] Clause(params ControlField[] fields) => ControlRecord.Encode(6, fields);
    internal static byte[] Tokens(QueryBinding query, ReadOnlySpan<ExpressionReference> expressions)
    {
        if (expressions.IsEmpty || expressions.Length > DbWire.MaxDescriptors) throw new ArgumentException("Invalid expression count.", nameof(expressions));
        var bytes = new byte[checked(expressions.Length * 8)];
        for (var i = 0; i < expressions.Length; i++) BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * 8), expressions[i].In(query));
        return bytes;
    }
    internal static byte[] Projection<T>(Projection<T> projection, Func<CodecDescriptor, uint> bindCodec, ReadOnlySpan<ulong> expressions = default)
    {
        var projectionFields = projection.Fields;
        if (!expressions.IsEmpty && expressions.Length != projectionFields.Length) throw new ArgumentException("Projection expression count differs.", nameof(expressions));
        // Public projections own immutable, Unicode-validated field metadata.
        // Size the bounded frame without binding codecs, then write directly to
        // its single owned output. Oversized/noncanonical inputs retain the old
        // encoder, including binding order and its eventual validation errors.
        if (projection.ShapeHash is not { Length: 64 } || !TryProjectionSize(projectionFields, !expressions.IsEmpty, out var size))
            return ProjectionFallback(projection, bindCodec, expressions);

        var bytes = new byte[size];
        var vectorSize = size - 80; // outer header/table and the 32-byte shape digest
        WriteHeader(bytes, 13, size, 2);
        WriteField(bytes, 0, 1, 5, vectorSize, 48);
        WriteField(bytes, 1, 2, 5, 32, 48 + vectorSize);
        DbWire.U32(bytes, 48, (uint)projectionFields.Length);
        var position = 52;
        var fieldCount = expressions.IsEmpty ? 4 : 5;
        for (var i = 0; i < projectionFields.Length; i++)
        {
            var field = projectionFields[i];
            var frame = bytes.AsSpan(position + 4);
            var payload = 16 + fieldCount * 16;
            WriteField(frame, 0, 1, 1, 4, payload);
            DbWire.U32(frame, payload, (uint)i); payload += 4;
            var nameLength = Utf8.GetBytes(field.Name, frame[payload..]);
            WriteField(frame, 1, 2, 4, nameLength, payload); payload += nameLength;
            // Keep exactly one binding call per field, after its UTF-8 name.
            var codec = bindCodec(field.Descriptor);
            WriteField(frame, 2, 3, 1, 4, payload);
            DbWire.U32(frame, payload, codec); payload += 4;
            WriteField(frame, 3, 4, 1, 4, payload);
            DbWire.U32(frame, payload, field.IsNullable ? 1u : 0u); payload += 4;
            if (!expressions.IsEmpty)
            {
                WriteField(frame, 4, 5, 2, 8, payload);
                DbWire.U64(frame, payload, expressions[i]); payload += 8;
            }
            WriteHeader(frame, 22, payload, fieldCount);
            DbWire.U32(bytes, position, (uint)payload); position += 4 + payload;
        }
        var status = Convert.FromHexString(projection.ShapeHash, bytes.AsSpan(position, 32), out var consumed, out var written);
        if (status != System.Buffers.OperationStatus.Done || consumed != 64 || written != 32)
            Convert.FromHexString(projection.ShapeHash).CopyTo(bytes, position);
        return bytes;
    }
    private static bool TryProjectionSize(ResultField[] fields, bool expressions, out int size)
    {
        size = 0;
        long total = 84; // outer header/table, vector count and shape digest
        try
        {
            foreach (var field in fields)
            {
                total += 4L + (expressions ? 116 : 92) + Utf8.GetByteCount(field.Name);
                if (total > DbWire.MaxEncodedBytes) return false;
            }
        }
        catch (ArgumentException) { return false; }
        catch (NullReferenceException) { return false; }
        size = (int)total;
        return true;
    }
    private static void WriteHeader(Span<byte> bytes, ushort kind, int size, int fields)
    {
        DbWire.U16(bytes, 0, 1); DbWire.U16(bytes, 2, kind);
        DbWire.U32(bytes, 4, (uint)size); DbWire.U32(bytes, 8, (uint)fields);
    }
    private static void WriteField(Span<byte> bytes, int index, ushort id, ushort type, int length, int start)
    {
        var offset = 16 + index * 16;
        DbWire.U16(bytes, offset, id); DbWire.U16(bytes, offset + 2, type);
        DbWire.U32(bytes, offset + 4, (uint)length); DbWire.U64(bytes, offset + 8, (ulong)start);
    }
    private static byte[] ProjectionFallback<T>(Projection<T> projection, Func<CodecDescriptor, uint> bindCodec, ReadOnlySpan<ulong> expressions)
    {
        var projectionFields = projection.Fields;
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(checked((uint)projectionFields.Length));
        for (var i = 0; i < projectionFields.Length; i++)
        {
            var field = projectionFields[i]; var fields = new List<ControlField> { U32(1, (uint)i), Text(2, field.Name), U32(3, bindCodec(field.Descriptor)), U32(4, field.IsNullable ? 1u : 0u) };
            if (!expressions.IsEmpty) fields.Add(U64(5, expressions[i]));
            var record = ControlRecord.Encode(22, [.. fields]); writer.Write(checked((uint)record.Length)); writer.Write(record);
        }
        return ControlRecord.Encode(13, Bytes(1, stream.ToArray()), Bytes(2, Convert.FromHexString(projection.ShapeHash)));
    }
    internal static byte[] Value<T>(uint profile, ValueCodec<T> codec, uint boundCodec, T value)
    {
        var bytes = codec.Encode(value); ReadOnlyMemory<byte>? cell = bytes is null ? (ReadOnlyMemory<byte>?)null : new ReadOnlyMemory<byte>(bytes);
        return DbWire.Encode(new(PayloadKind.ExpressionValues, profile, new byte[32], 0, 0, [new(1, codec.Descriptor, boundCodec)]) { ExactRows = 1 }, 1, [cell]);
    }
}
