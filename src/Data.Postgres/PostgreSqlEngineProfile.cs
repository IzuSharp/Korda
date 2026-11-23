// The maintainer build hashes an explicit precompiled asset into obj.
// No machine-specific native digest is committed and no consumer compiles the native provider.
using Korda.Interop;
using Korda.Interop.Extensibility;
namespace Korda.Database.PostgreSql;

internal static class PostgreSqlEngineProfile
{
    internal static readonly EngineDescriptor Descriptor = EnginePackage.CreateDescriptor(
        new EnginePackageManifest("korda.postgres.component", "Korda.Database.PostgreSql", "0.1.0-preview.1",
            new AbiVersion(1, 0), "nw_postgres_component_get_abi_v1", EngineThreadingModel.Synchronous,
            new[] { new NativeAssetDescriptor("linux-x64", "runtimes/linux-x64/native/libkorda_data_postgres.so", PostgreSqlAssetIdentity.Sha256, "x86-64") },
            new[] { new EngineCapabilityDefinition(new("postgres.component.v1"), 0) }),
        typeof(PostgreSqlDataSource).Assembly);
}
