using System.Buffers.Binary;
using System.Text;
using NativeWeave.Data;

namespace NativeWeave.Database.PostgreSql;

// The canonical shared contract owns the field envelope. This module only owns
// the kind8 field meanings; B04 composes the surrounding datasource record.
internal sealed class PostgreSqlConfiguration
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly byte[] _record;
    private PostgreSqlConfiguration(byte[] record) => _record = record;
    internal ReadOnlyMemory<byte> Record => _record;

    internal static PostgreSqlConfiguration Snapshot(PostgreSqlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        PostgreSqlTlsMode tls = options.TlsMode;
        string? root = options.RootCertificatePath;
        string name = options.ApplicationName;
        TimeSpan timeout = options.ConnectTimeout;
        if (!Enum.IsDefined(tls)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown TLS policy.");
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(options), "ConnectTimeout must be positive and at most one hour.");
        byte[] application = Text(name, 256);
        byte[]? rootBytes = null;
        if (root is not null)
        {
            if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("RootCertificatePath must be absolute.", nameof(options));
            rootBytes = Text(root, 4096);
        }
        byte[] tlsBytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(tlsBytes, (uint)tls);
        byte[] duration = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(duration, checked((ulong)timeout.Ticks * 100));
        var fields = new List<ControlField>(4) { new(1, 1, tlsBytes) };
        if (rootBytes is not null) fields.Add(new(2, 4, rootBytes));
        fields.Add(new(3, 4, application)); fields.Add(new(4, 3, duration));
        return new(ControlRecord.Encode(8, fields.ToArray()));
    }
    private static byte[] Text(string value, int maximum)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] bytes = Utf8.GetBytes(value);
        if (bytes.Length is 0 || bytes.Length > maximum || value.Contains('\0')) throw new ArgumentException("Invalid PostgreSQL option text.", nameof(value));
        return bytes;
    }
}
