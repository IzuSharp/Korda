using System.Buffers.Binary;
using System.Text;
using NativeWeave.Data;

namespace NativeWeave.Query.Internal;

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
        var bytes = codec.Encode(value); ReadOnlyMemory<byte>? cell = bytes is null ? null : bytes;
        return DbWire.Encode(new(PayloadKind.ExpressionValues, profile, new byte[32], 0, 0, [new(1, codec.Descriptor, boundCodec)]) { ExactRows = 1 }, 1, [cell]);
    }
}
