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
        var fields = ReadControl(bytes, 7);
        uint Get(ushort id) => fields.TryGetValue(id, out var field) && field.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(field) : throw new DatabaseException("Native snapshot field is missing.", DatabaseErrorKind.ShapeMismatch);
        var state = Get(1); var outcome = Get(2); var attempted = Get(3); var cleanup = Get(4); var disposition = Get(5); var released = Get(6); var deadline = Get(7);
        if (state > 4 || outcome > 3 || attempted > 1 || cleanup > 4 || disposition > 3 || released > 1 || deadline > 1 || !fields.TryGetValue(8, out var correlation) || correlation.Length != 8)
            throw new DatabaseException("Native snapshot values are invalid.", DatabaseErrorKind.ShapeMismatch);
        return new((TransactionState)state, (TransactionOutcome)outcome, attempted == 1, new((CleanupStatus)cleanup, BinaryPrimitives.ReadUInt64LittleEndian(correlation), fields.TryGetValue(9, out var provider) ? Utf8.GetString(provider) : null, released == 1, (ConnectionDisposition)disposition, deadline == 1));
    }
}
