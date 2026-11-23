using System.Collections.Generic;
using System.Security.Cryptography;
using Korda.Data;

namespace Korda.Database;

/// <summary>Selects native transactional execution or explicitly resumable PostgreSQL execution.</summary>
public enum MigrationTransactionMode
{
    /// <summary>Commits the script and its ledger atomically; SQLite also wraps the entire selected batch.</summary>
    Transactional,
    /// <summary>Requires PostgreSQL and an explicit opt-in; partial effects can remain after failure.</summary>
    NonTransactional
}
/// <summary>Defines one trusted, immutable SQL migration.</summary>
/// <remarks>This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.</remarks>
/// <param name="Version">A positive version, strictly ascending within its bundle.</param>
/// <param name="Description">A nonempty diagnostic description without credentials.</param>
/// <param name="Sql">Exact Unicode SQL; UTF-8 bytes are checksummed without newline normalization.</param>
/// <param name="TransactionMode">Transactional by default; explicit transaction-control statements are rejected.</param>
public sealed record Migration(long Version, string Description, string Sql, MigrationTransactionMode TransactionMode = MigrationTransactionMode.Transactional);

/// <summary>Snapshots a strictly ordered, provider-bound set of explicit SQL scripts.</summary>
/// <remarks>Construction performs no native load or database I/O. Its SHA-256 identity covers the canonical
/// kind12 provider and entry sequence before the hash field is added. Each entry separately contains SQL SHA-256;
/// the native ledger preserves the existing SQLx-compatible SHA-384 checksum. Scripts must be trusted deployment artifacts.</remarks>
public sealed class MigrationBundle
{
    internal DatabaseProvider Provider { get; }
    internal byte[] Bytes { get; }
    internal Migration[] Entries { get; }
    /// <summary>Creates an immutable snapshot and validates versions, strings, modes and finite encoding limits.</summary>
    /// <param name="provider">The exact provider that may consume the bundle.</param>
    /// <param name="migrations">The trusted scripts in strictly ascending positive version order.</param>
    /// <exception cref="ArgumentNullException">The collection, an entry or a string is null.</exception>
    /// <exception cref="ArgumentException">Versions, strings, modes or encoded limits are invalid.</exception>
    /// <remarks>The input collection may be changed after return; this instance retains its own snapshot.</remarks>
    public MigrationBundle(DatabaseProvider provider, IReadOnlyList<Migration> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        if (!Enum.IsDefined(provider)) throw new ArgumentOutOfRangeException(nameof(provider));
        var count = migrations.Count;
        if (count > 4096) throw new ArgumentOutOfRangeException(nameof(migrations));
        Provider = provider; Entries = new Migration[count]; long previous = 0;
        // Count the complete kind12 record before copying any script bytes. The
        // 104-byte envelope includes its provider, sequence count and bundle hash;
        // each sequence entry adds 144 bytes plus its two UTF-8 strings.
        long encodedLength = 104;
        for (var i = 0; i < Entries.Length; i++)
        {
            var value = migrations[i]; ArgumentNullException.ThrowIfNull(value);
            if (value.Version <= previous || !Enum.IsDefined(value.TransactionMode)) throw new ArgumentException("Migration versions must be positive and strictly ascending, with a known transaction mode.", nameof(migrations));
            previous = value.Version;
            encodedLength = checked(encodedLength + 144 + TextLength(value.Description, nameof(value.Description)) + TextLength(value.Sql, nameof(value.Sql)));
            if (encodedLength > DbWire.MaxEncodedBytes) throw new ArgumentException("Migration bundle exceeds the encoded record limit.", nameof(migrations));
            Entries[i] = value;
        }
        var records = new byte[count][];
        for (var i = 0; i < records.Length; i++)
        {
            var value = Entries[i];
            var description = HostEncoding.Text(value.Description, DbWire.MaxEncodedBytes, nameof(value.Description));
            var sql = HostEncoding.Text(value.Sql, DbWire.MaxEncodedBytes, nameof(value.Sql));
            records[i] = ControlRecord.Encode(11, HostEncoding.Count(1, value.Version), new(2, 4, description), new(3, 4, sql), new(4, 1, ControlRecord.U32((uint)value.TransactionMode)), new(5, 5, SHA256.HashData(sql)));
        }
        var sequence = HostEncoding.Sequence(records);
        var fields = new ControlField[] { new(1, 1, ControlRecord.U32((uint)provider)), new(2, 5, sequence) };
        var hash = SHA256.HashData(ControlRecord.Encode(12, fields));
        ContentHash = Convert.ToHexStringLower(hash);
        Bytes = ControlRecord.Encode(12, fields[0], fields[1], new(3, 5, hash));
    }
    private static int TextLength(string value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (value.Length == 0 || value.Contains('\0')) throw new ArgumentException("A nonempty Unicode value without NUL is required.", name);
        return HostEncoding.Utf8.GetByteCount(value);
    }
    /// <summary>Gets the lowercase SHA-256 of the canonical provider and immutable entry sequence.</summary>
    public string ContentHash { get; }
}
/// <summary>Controls one explicit forward migration call.</summary>
/// <remarks>This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.</remarks>
public sealed record MigrationOptions
{
    /// <summary>Gets the last version eligible for forward application; null selects the complete bundle.</summary>
    /// <remarks>A value below the applied current version is rejected; no downgrade is performed.</remarks>
    public long? TargetVersion { get; init; }
    /// <summary>Gets the positive finite administrative writer/advisory-lock wait, defaulting to thirty seconds.</summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Gets whether explicitly nontransactional PostgreSQL entries may execute or be resumed.</summary>
    /// <remarks>A new call is explicit replay of the identical script after an unfinished phase. The script must
    /// handle any previously completed effects. Korda never retries a write within the call.</remarks>
    public bool AllowNonTransactional { get; init; }
}
/// <summary>Describes one bounded immutable bundle/ledger comparison.</summary>
/// <remarks>This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.</remarks>
/// <param name="Version">The positive migration version.</param>
/// <param name="Description">The immutable bundle description.</param>
/// <param name="Checksum">The lowercase hexadecimal SQLx-compatible SHA-384 checksum, distinct from the bundle hash.</param>
/// <param name="Applied">True only when the successful retained migration ledger contains this exact migration.</param>
public sealed record MigrationStatus(long Version, string Description, string Checksum, bool Applied);
/// <summary>Reports a successfully completed explicit migration call.</summary>
/// <remarks>This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.</remarks>
/// <param name="AppliedCount">The number applied by this invocation.</param>
/// <param name="CurrentVersion">The last confirmed applied version, or null for an empty database.</param>
/// <param name="Completed">True on success; failures throw and retain their original error.</param>
public sealed record MigrationReport(int AppliedCount, long? CurrentVersion, bool Completed);
/// <summary>Describes a provider catalog column without exposing a provider row object.</summary>
/// <remarks>This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.</remarks>
/// <param name="Name">The exact store column name.</param>
/// <param name="StoreType">The provider's declared/formatted type.</param>
/// <param name="IsNullable">Whether the provider permits SQL NULL.</param>
/// <param name="IsPrimaryKey">Whether the column participates in a primary key.</param>
public sealed record SchemaColumn(string Name, string StoreType, bool IsNullable, bool IsPrimaryKey);
/// <summary>Describes a table and an immutable, ordinally ordered column snapshot.</summary>
/// <remarks>Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.</remarks>
/// <param name="Schema">The admitted provider schema; SQLite reports main.</param>
/// <param name="Name">The exact table name.</param>
/// <param name="Columns">The bounded columns in native catalog ordinal order.</param>
public sealed record SchemaTable(string? Schema, string Name, IReadOnlyList<SchemaColumn> Columns);
