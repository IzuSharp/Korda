using System.Reflection;
using Korda.Data;
using Korda.Interop;
using Korda.Interop.Extensibility;

namespace Korda.Database;

/// <summary>The selected independently packaged C++ database provider.</summary>
public enum DatabaseProvider
{
    /// <summary>The libpq PostgreSQL provider.</summary>
    PostgreSql,
    /// <summary>The serialized sqlite3 provider.</summary>
    Sqlite
}

/// <summary>Immutable finite limits snapshotted by a data source before native creation.</summary>
/// <remarks>These are admission and encoded-byte budgets, not measurements of CLR heap or process memory.
/// Provider hard limits also apply. Increasing a value never selects an unbounded fallback.</remarks>
public sealed record DatabaseLimits
{
    /// <summary>Gets the maximum simultaneous physical sessions; defaults to 32.</summary>
    public int MaxConnections { get; init; } = 32;
    /// <summary>Gets the maximum bounded session waiters; defaults to 128.</summary>
    public int MaxWaiters { get; init; } = 128;
    /// <summary>Gets the positive finite pool admission timeout; defaults to fifteen seconds.</summary>
    public TimeSpan AdmissionTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Gets the maximum buffered result rows; defaults to 10,000. Zero prohibits buffered demand.</summary>
    public int MaxBufferedRows { get; init; } = 10_000;
    /// <summary>Gets the buffered encoded-byte budget; defaults to 16 MiB. Zero prohibits buffered demand.</summary>
    public long MaxBufferedBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>Gets the maximum rows in one transferred batch; defaults to 256.</summary>
    public int MaxBatchRows { get; init; } = 256;
    /// <summary>Gets the entire encoded batch budget including descriptors; defaults to 1 MiB.</summary>
    public long MaxBatchBytes { get; init; } = 1024 * 1024;
    /// <summary>Gets the general field byte limit; defaults to 8 MiB. An inline field must also fit its batch.</summary>
    public long MaxFieldBytes { get; init; } = 8 * 1024 * 1024;
    /// <summary>Gets the maximum query and registration nodes; defaults to 4,096.</summary>
    public int MaxQueryNodes { get; init; } = 4096;
    /// <summary>Gets the maximum native query depth; defaults to 64.</summary>
    public int MaxQueryDepth { get; init; } = 64;
    /// <summary>Gets the maximum typed parameters per operation; defaults to 999.</summary>
    public int MaxParameters { get; init; } = 999;
    /// <summary>Gets the total encoded parameter budget; defaults to 4 MiB.</summary>
    public long MaxParameterBytes { get; init; } = 4 * 1024 * 1024;
    /// <summary>Gets the source-owned prepared shape entry limit; defaults to 128.</summary>
    public int MaxPreparedPlans { get; init; } = 128;
    /// <summary>Gets the source-owned prepared shape byte limit; defaults to 8 MiB.</summary>
    public long MaxPreparedPlanBytes { get; init; } = 8 * 1024 * 1024;
    /// <summary>Gets the positive default total command timeout; defaults to thirty seconds.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal DatabaseLimits Validate()
    {
        Check(MaxConnections, 1024); Check(MaxWaiters, 4096); Check(MaxBufferedRows, 1_000_000, true);
        Check(MaxBufferedBytes, 1L << 30, true); Check(MaxBatchRows, 4096); Check(MaxBatchBytes, DbWire.MaxEncodedBytes);
        Check(MaxFieldBytes, 64L << 20); Check(MaxQueryNodes, 4096); Check(MaxQueryDepth, 256);
        Check(MaxParameters, 4096); Check(MaxParameterBytes, DbWire.MaxEncodedBytes); Check(MaxPreparedPlans, 4096);
        Check(MaxPreparedPlanBytes, 1L << 30); Time(AdmissionTimeout); Time(CommandTimeout);
        return this with { };
    }
    private static void Check(long value, long maximum, bool zero = false)
    { if (value < (zero ? 0 : 1) || value > maximum) throw new ArgumentOutOfRangeException(nameof(DatabaseLimits), "A database limit exceeds the finite native profile."); }
    private static void Time(TimeSpan value)
    { if (value <= TimeSpan.Zero || value > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(DatabaseLimits), "Timeouts must be positive and at most one hour."); }
    internal byte[] Encode() => ControlRecord.Encode(10,
        HostEncoding.Count(1, MaxConnections), HostEncoding.Count(2, MaxWaiters), HostEncoding.Duration(3, AdmissionTimeout),
        HostEncoding.Count(4, MaxBufferedRows), HostEncoding.Count(5, MaxBufferedBytes), HostEncoding.Count(6, MaxBatchRows),
        HostEncoding.Count(7, MaxBatchBytes), HostEncoding.Count(8, MaxFieldBytes), HostEncoding.Count(9, MaxQueryNodes),
        HostEncoding.Count(10, MaxQueryDepth), HostEncoding.Count(11, MaxParameters), HostEncoding.Count(12, MaxParameterBytes),
        HostEncoding.Count(13, MaxPreparedPlans), HostEncoding.Count(14, MaxPreparedPlanBytes), HostEncoding.Duration(15, CommandTimeout));
}

/// <summary>An immutable snapshot of the capabilities actually negotiated for this provider module.</summary>
/// <remarks>Unknown names return false. A capability name does not authorize a different provider or an arbitrary SQL feature.</remarks>
public sealed class DatabaseCapabilities
{
    private readonly HashSet<string> _names;
    internal DatabaseCapabilities(DatabaseProvider provider, EngineInfo info)
    {
        Provider = provider; Profile = provider == DatabaseProvider.Sqlite ? "sqlite.v1" : "postgresql.v1";
        Names = Array.AsReadOnly(info.Capabilities.Select(c => c.Name).Order(StringComparer.Ordinal).ToArray());
        _names = new(Names, StringComparer.Ordinal);
    }
    /// <summary>Gets the selected provider.</summary>
    public DatabaseProvider Provider { get; }
    /// <summary>Gets the stable selected semantic profile.</summary>
    public string Profile { get; }
    /// <summary>Gets the immutable ordinal capability-name snapshot.</summary>
    public IReadOnlyList<string> Names { get; }
    /// <summary>Tests one case-sensitive stable capability name without a native call.</summary>
    /// <param name="capability">The requested name; an unknown or empty name returns false.</param>
    /// <returns>True only if the actual module advertised the capability.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="capability"/> is null.</exception>
    public bool Supports(string capability) { ArgumentNullException.ThrowIfNull(capability); return _names.Contains(capability); }
    internal void Require(string capability)
    { if (!Supports(capability)) throw new DatabaseCapabilityException(capability, Profile); }
}

internal static class DatabaseEngineProfile
{
    private static readonly string[] Names = ["db.metadata.v1", "db.query.v1", "db.transactions.v1", "db.savepoints.v1", "db.streaming.v1", "db.migrations.v1", "db.schema.read.v1", "db.returning.v1", "db.upsert.v1", "db.cte.v1", "db.window.v1", "db.row-lock.v1", "db.null-safe-equality.v1", "db.json.v1", "db.decimal.numeric.v1", "db.decimal.text.v1", "db.fixed-money.v1", "db.relationships.explicit.v1", "db.prepared-shape.v1", "db.bulk-batches.v1", "db.dynamic-fields.v1", "db.recursive-cte.v1", "db.set-operations.v1", "db.scalar-codecs.v1", "db.sql.expert.v1", "db.schema.draft.v1", "db.query.command.v1"];
    internal static EngineDescriptor Create(string engine, string package, string rid, string path, string hash, Assembly assembly) =>
        EnginePackage.CreateDescriptor(new EnginePackageManifest(engine, package, PackageVersion(assembly), new AbiVersion(1, 0), engine switch { "database.sqlite" => "nw_database_sqlite_get_abi_v1", "database.postgresql" => "nw_database_postgresql_get_abi_v1", _ => throw new ArgumentException("Unknown database provider engine.", nameof(engine)) }, EngineThreadingModel.EngineLocalAsync,
            [new NativeAssetDescriptor(rid, path, hash.ToLowerInvariant(), "x86-64")], Names.Select((name, bit) => new EngineCapabilityDefinition(new(name), bit)).ToArray()), assembly);

    private static string PackageVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? throw new InvalidOperationException("The database facade is missing its package version.");
}
