# Static generation and explicit manual mapping

Install the single `Korda.Database.Sqlite` facade using the exact version in
the project. The default build receives the model generator transitively. There
are no direct analyzer references, project references, native source inputs or
runtime reflection configuration in this application.

```sh
dotnet publish QueryGenerator.Sample.csproj -c Release -r linux-x64 -p:PublishAot=true
./bin/Release/net11.0/linux-x64/publish/QueryGenerator.Sample --require-aot
```

The generated path uses `[DbTable]`, `[DbKey]`, `[DbColumn]` and `[DbVersion]`
from `Korda.Data`. See the [annotation upgrade guide](../../../docs/guides/db-attribute-upgrade.md)
for existing models.

Both attributed models query the actual SQLite Host through the same Fluent and
static materialization path. The program checks the returned DTOs and prints the
canonical model/projection hashes. Source and session ownership use `await using`.
Exceptions leave the native operation cleanup to those owners before the temporary
database is deleted. Ordinary model edits rebuild the managed consumer only.

The advanced manual path explicitly excludes generation and supplies equivalent
`TableBuilder` descriptors, static readers and writers:

```sh
dotnet publish QueryGenerator.Sample.csproj -c Release -r linux-x64 -p:PublishAot=true -p:ManualMappings=true
./bin/Release/net11.0/linux-x64/publish/QueryGenerator.Sample --require-aot
```

Both paths preserve CLR spelling by default and the exact `id`, `email`, and
`version` overrides. The Linux verifier runs generated/manual Native AOT
consumers from one immutable local package feed, adds a mapped `Rank` property to
the second entity, and repeats those rows without changing the native SHA-256.
The generator's compiler diagnostics and deterministic incremental output are
checked separately; no other-platform or pristine-machine claim is made here.
