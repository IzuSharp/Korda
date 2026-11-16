// The maintainer build hashes an explicit precompiled asset into obj.
// No machine-specific native digest is committed and no consumer invokes Cargo.
using NativeWeave.Interop;
using NativeWeave.Interop.Extensibility;
namespace NativeWeave.Database.PostgreSql;
internal static class PostgreSqlEngineProfile
{
    internal static readonly EngineDescriptor Descriptor = EnginePackage.CreateDescriptor(
        new EnginePackageManifest("nativeweave.postgres.component", "NativeWeave.Database.PostgreSql", "0.1.0-preview.1",
            new AbiVersion(1, 0), "nw_postgres_component_get_abi_v1", EngineThreadingModel.Synchronous,
            new[] { new NativeAssetDescriptor("linux-x64", "runtimes/linux-x64/native/libnativeweave_data_postgres.so", PostgreSqlAssetIdentity.Sha256, "x86-64") },
            new[] { new EngineCapabilityDefinition(new("postgres.component.v1"), 0) }),
        typeof(PostgreSqlDataSource).Assembly);
}
