using System.Buffers.Binary;
using NativeWeave.Data;

namespace NativeWeave.Database;

internal static class AdministrationEncoding
{
    internal static byte[] Request(DataSource source, uint action, MigrationBundle? bundle, MigrationOptions? options, string? schema)
    {
        var capability = action == 3 ? "db.schema.read.v1" : "db.migrations.v1";
        source.Capabilities.Require(capability);
        var fields = new List<ControlField> { new(1, 1, ControlRecord.U32((uint)source.Provider)) };
        if (bundle is not null)
        {
            if (bundle.Provider != source.Provider) throw new ArgumentException("The bundle belongs to another provider.", nameof(bundle));
            if (source.Provider == DatabaseProvider.Sqlite && bundle.Entries.Any(entry => entry.TransactionMode == MigrationTransactionMode.NonTransactional))
                throw new DatabaseCapabilityException("db.migrations.v1", source.Provider.ToString());
            if (source.Provider == DatabaseProvider.PostgreSql && action == 0 && options?.AllowNonTransactional != true && bundle.Entries.Any(entry => entry.TransactionMode == MigrationTransactionMode.NonTransactional && (options?.TargetVersion is not { } target || entry.Version <= target)))
                throw new ArgumentException("Selected nontransactional PostgreSQL entries require explicit AllowNonTransactional.", nameof(options));
            if (bundle.Entries.Length > source.Limits.MaxQueryNodes || bundle.Entries.Any(value => HostEncoding.Utf8.GetByteCount(value.Description) > source.Limits.MaxFieldBytes || HostEncoding.Utf8.GetByteCount(value.Sql) > source.Limits.MaxFieldBytes)) throw new ArgumentException("Migration manifest exceeds the source limits.", nameof(bundle));
            fields.Add(new(2, 5, bundle.Bytes));
        }
        if (options is not null)
        {
            if (options.TargetVersion is { } target) { if (target <= 0) throw new ArgumentOutOfRangeException(nameof(options)); fields.Add(HostEncoding.Count(3, target)); }
            if (options.LockTimeout <= TimeSpan.Zero || options.LockTimeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(options));
            fields.Add(HostEncoding.Duration(4, options.LockTimeout)); fields.Add(new(5, 1, ControlRecord.U32(options.AllowNonTransactional ? 1u : 0u)));
        }
        if (schema is not null) fields.Add(new(6, 4, HostEncoding.Text(schema, 63, nameof(schema))));
        var result = ControlRecord.Encode(5, fields.ToArray());
        if (result.Length > source.Limits.MaxParameterBytes) throw new ArgumentException("Administrative request exceeds the source input limit.");
        return result;
    }
    private static DatabaseException Invalid() => new("Invalid administrative result record.", DatabaseErrorKind.ShapeMismatch);
    private static Dictionary<ushort, byte[]> Record(byte[] bytes, ushort kind, params (ushort Id, ushort Type, bool Required)[] schema)
    {
        var fields = HostEncoding.ReadControl(bytes, kind);
        foreach (var definition in schema) if (definition.Required && !fields.ContainsKey(definition.Id)) throw Invalid();
        for (var index = 0; index < fields.Count; index++)
        {
            var at = 16 + index * 16; var id = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at)); var type = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 2));
            if (!schema.Any(value => value.Id == id && value.Type == type)) throw Invalid();
        }
        return fields;
    }
    private static Dictionary<ushort, byte[]> Result(byte[] bytes, uint action, DatabaseLimits limits)
    {
        if (bytes.LongLength > limits.MaxBufferedBytes) throw new DatabaseException("Administrative bytes exceed the source limit.", DatabaseErrorKind.ResourceLimit);
        var fields = Record(bytes, 19, (1, 1, true), (2, 5, false), (3, 1, false), (4, 2, false), (5, 1, false));
        if (U32(fields[1]) != action || action is 1 or 3 && (fields.Count != 2 || !fields.ContainsKey(2)) || action == 2 && fields.Count != 1 || action == 0 && (!fields.ContainsKey(3) || !fields.ContainsKey(5) || fields.ContainsKey(2))) throw Invalid();
        return fields;
    }
    private static uint U32(byte[] value) => value.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(value) : throw Invalid();
    private static long Version(byte[] value)
    {
        if (value.Length != 8) throw Invalid(); var n = BinaryPrimitives.ReadUInt64LittleEndian(value); return n is > 0 and <= long.MaxValue ? (long)n : throw Invalid();
    }
    private static bool Boolean(byte[] value) => U32(value) switch { 0 => false, 1 => true, _ => throw Invalid() };
    private static string Text(byte[] value, DatabaseLimits limits, bool empty = false)
    {
        if (value.LongLength > limits.MaxFieldBytes) throw new DatabaseException("Administrative field exceeds the source limit.", DatabaseErrorKind.ResourceLimit);
        var result = HostEncoding.Utf8.GetString(value); if ((!empty && result.Length == 0) || result.Contains('\0')) throw Invalid(); return result;
    }
    private static IReadOnlyList<byte[]> Sequence(byte[] bytes, DatabaseLimits limits, ref long rows)
    {
        if (bytes.Length < 4) throw Invalid(); var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes); rows = checked(rows + count);
        if (rows > limits.MaxBufferedRows || count > 4096) throw new DatabaseException("Administrative record count exceeds the source limit.", DatabaseErrorKind.ResourceLimit);
        var values = new byte[count][]; var at = 4;
        for (var i = 0; i < values.Length; i++)
        {
            if (bytes.Length - at < 4) throw Invalid(); var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)); at += 4;
            if (size == 0 || size > bytes.Length - at) throw Invalid(); values[i] = bytes.AsSpan(at, (int)size).ToArray(); at += (int)size;
        }
        if (at != bytes.Length) throw Invalid(); return values;
    }
    internal static MigrationReport Report(byte[] bytes, DatabaseLimits limits)
    {
        var fields = Result(bytes, 0, limits); var count = U32(fields[3]); var completed = Boolean(fields[5]);
        if (count > int.MaxValue || !completed) throw Invalid();
        return new((int)count, fields.TryGetValue(4, out var version) ? Version(version) : null, completed);
    }
    internal static void Validated(byte[] bytes, DatabaseLimits limits) => Result(bytes, 2, limits);
    internal static IReadOnlyList<MigrationStatus> Status(byte[] bytes, DatabaseLimits limits)
    {
        var fields = Result(bytes, 1, limits); long rows = 0, previous = 0; var result = new List<MigrationStatus>();
        foreach (var record in Sequence(fields[2], limits, ref rows))
        {
            var entry = Record(record, 16, (1, 2, true), (2, 4, true), (3, 4, true), (4, 1, true)); var version = Version(entry[1]);
            if (version <= previous) throw Invalid(); previous = version; var checksum = Text(entry[3], limits);
            if (checksum.Length != 96 || checksum.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw Invalid();
            result.Add(new(version, Text(entry[2], limits), checksum, Boolean(entry[4])));
        }
        return Array.AsReadOnly(result.ToArray());
    }
    internal static IReadOnlyList<SchemaTable> Tables(byte[] bytes, DatabaseLimits limits)
    {
        var fields = Result(bytes, 3, limits); long rows = 0; var result = new List<SchemaTable>();
        foreach (var record in Sequence(fields[2], limits, ref rows))
        {
            var table = Record(record, 18, (1, 4, false), (2, 4, true), (3, 5, true)); var columns = new List<SchemaColumn>();
            foreach (var column in Sequence(table[3], limits, ref rows))
            {
                var entry = Record(column, 17, (1, 4, true), (2, 4, true), (3, 1, true), (4, 1, true));
                columns.Add(new(Text(entry[1], limits), Text(entry[2], limits, true), Boolean(entry[3]), Boolean(entry[4])));
            }
            result.Add(new(table.TryGetValue(1, out var schema) ? Text(schema, limits) : null, Text(table[2], limits), Array.AsReadOnly(columns.ToArray())));
        }
        return Array.AsReadOnly(result.ToArray());
    }
}
