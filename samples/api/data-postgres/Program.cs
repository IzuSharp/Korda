using NativeWeave.Database.PostgreSql;
using NativeWeave.Interop;

// Minimal: constructing configuration and reading a descriptor start no executor.
var options = new PostgreSqlOptions();
Console.WriteLine($"TLS={options.TlsMode}; connect timeout={options.ConnectTimeout}");
EngineDescriptor descriptor = PostgreSqlDataSource.Engine;
Console.WriteLine($"{descriptor.EngineId}: {string.Join(", ", descriptor.SupportedRuntimeIdentifiers)}");

// Advanced: optional exact-asset preflight. Production defaults use the package's
// output-location record, emitted from the actual maintainer asset at build.
EngineInfo ordinary = descriptor.Inspect();
Console.WriteLine($"Ordinary component preflight: {ordinary.EngineId}");
if (Environment.GetEnvironmentVariable("NW_PG_COMPONENT_ASSET") is string path)
{
    EngineInfo info = descriptor.Inspect(new() { NativeAssetPath = path, RequiredCapabilities = new[] { new EngineCapability("postgres.component.v1") } });
    Console.WriteLine($"Validated {info.AbiVersion.Major}.{info.AbiVersion.Minor}: {info.Supports(new("postgres.component.v1"))}");
}
// DataSource/Create/UsePostgreSql composition belongs to B04; see the guide's exact
// signature map. This example does not substitute a mock datasource or provider.
