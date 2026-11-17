# Native Fluent package consumer

This complete ordinary application references `NativeWeave.Database` and the two
normal provider NuGets. It uses handwritten typed tables and direct DTO
constructors; no generator, native source project or Cargo build is required by
the application. The same application runs as .NET and Linux x64 Native AOT.

Provide an isolated PostgreSQL18 endpoint in `NW_TEST_POSTGRES`, without URL
query options; this sample explicitly selects local-development TLS policy.
It also creates a temporary SQLite file. It creates and drops `fluent_people`
and `fluent_notes`, so use an isolated example database.

```sh
dotnet restore QueryFluent.Sample.csproj
dotnet run --project QueryFluent.Sample.csproj -c Release
dotnet publish QueryFluent.Sample.csproj -c Release -r linux-x64 -p:PublishAot=true -o publish
./publish/QueryFluent.Sample --require-aot
```

The component verifier packs maintainer-built artifacts into a local feed and
copies this project outside the repository with a fresh NuGet cache. It executes
both modes and records exact package/executable/native identities. It does not
claim a pristine-machine release test.

The program checks real filter/order/limit and closure snapshots, nullable LEFT
joins with key zero, explicit DTO projections, grouping and aggregate widening,
immutable windows and CTEs, set snapshots, prepared values isolated before
await, unique offset/keyset paging, and explicit PostgreSQL transaction locks.
SQLite reports the stable unavailable row-lock capability. Native image hashes
are unchanged after both provider executions with these additional ordinary
entities and DTOs. See [the Fluent guide](../../../docs/guides/query-fluent.md)
for lifetime, errors, bounded memory and recorded call/copy costs.
