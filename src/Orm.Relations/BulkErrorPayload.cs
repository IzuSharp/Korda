using System.Buffers.Binary;
using NativeWeave.Data;

namespace NativeWeave.Database;

/// <summary>Kind7 followed by one self-sized kind15, without a new enclosing wire schema.</summary>
internal static class BulkErrorPayload
{
    internal const string NativeKey = "NativeWeave.Database.NativeBulkProgress.v1";
    internal static ReadOnlySpan<byte> Read(ReadOnlySpan<byte> payload, out BulkProgress? progress)
    {
        progress = null;
        if (payload.Length < 16) throw new DatabaseException("Truncated native error record.", DatabaseErrorKind.ShapeMismatch);
        var firstLength = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        if (firstLength < 16 || firstLength > payload.Length) throw new DatabaseException("Invalid native error record size.", DatabaseErrorKind.ShapeMismatch);
        var first = payload[..checked((int)firstLength)];
        if (firstLength == payload.Length) return first;
        var second = payload[checked((int)firstLength)..];
        var fields = HostEncoding.ReadControl(second, 15);
        if (fields.Count != 4) throw new DatabaseException("The native bulk progress field count differs.", DatabaseErrorKind.ShapeMismatch);
        for (var i = 0; i < 4; i++)
            if (BinaryPrimitives.ReadUInt16LittleEndian(second.Slice(16 + i * 16 + 2)) != (i == 0 ? 2 : 1))
                throw new DatabaseException("The native bulk progress field types differ.", DatabaseErrorKind.ShapeMismatch);
        if (fields.Count != 4 || !fields.TryGetValue(1, out var rows) || rows.Length != 8 || !fields.TryGetValue(2, out var batches) || batches.Length != 4
            || !fields.TryGetValue(3, out var committed) || committed.Length != 4 || !fields.TryGetValue(4, out var outcome) || outcome.Length != 4)
            throw new DatabaseException("The native bulk progress shape differs.", DatabaseErrorKind.ShapeMismatch);
        var r = BinaryPrimitives.ReadUInt64LittleEndian(rows); var b = BinaryPrimitives.ReadUInt32LittleEndian(batches);
        var c = BinaryPrimitives.ReadUInt32LittleEndian(committed); var o = BinaryPrimitives.ReadUInt32LittleEndian(outcome);
        if (r > long.MaxValue || b > int.MaxValue || c > 1 || o > (uint)TransactionOutcome.Unknown)
            throw new DatabaseException("The native bulk progress range differs.", DatabaseErrorKind.ShapeMismatch);
        progress = new((long)r, (int)b, c != 0, (TransactionOutcome)o); return first;
    }
    internal static Exception AttachNative(Exception failure, BulkProgress? progress)
    { if (progress is not null) failure.Data[NativeKey] = progress; return failure; }
}
