using NativeWeave.Interop;
using NativeWeave.Interop.Extensibility;

namespace NativeWeave.Database.Sqlite;

/// <summary>Provides immutable metadata for the maintainer-built SQLite provider component.</summary>
/// <remarks>This component declaration contributes Engine and the exact provider options. The Create factory and UseSqlite extension compose with B04's DataSource/DataSourceBuilder; their exact signatures and ownership mapping are recorded in the component guide. Engine inspection starts no SQLite connection or runtime.</remarks>
public static partial class SqliteDataSource
{
    /// <summary>Gets the immutable descriptor for the actual precompiled component asset.</summary>
    /// <remarks>Property access constructs cached metadata only. Inspect verifies the generated asset hash and canonical bootstrap, advertising only db.sqlite.profile.v1. This component asset is not a complete database host or an independently published package. The manifest states only RIDs actually present in this build.</remarks>
    public static EngineDescriptor Engine { get; } = EnginePackage.CreateDescriptor(
        new EnginePackageManifest("nativeweave.database.sqlite", "NativeWeave.Database.Sqlite", "0.1.0-preview.1",
            new AbiVersion(1, 0), "nw_sqlite_get_abi_v1", EngineThreadingModel.SerializedWorker,
            [new NativeAssetDescriptor(GeneratedEngineProfile.RuntimeIdentifier, GeneratedEngineProfile.RelativePath,
                GeneratedEngineProfile.Sha256.ToLowerInvariant(), "x86-64")],
            [new EngineCapabilityDefinition(new EngineCapability("db.sqlite.profile.v1"), 0)]), typeof(SqliteDataSource).Assembly);
}
