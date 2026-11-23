# Static generation and explicit manual mapping

Install the single `Korda.Database.Sqlite` facade using the exact version in
the project. The default build receives the model generator transitively. There
are no direct analyzer references, project references, native source inputs or
runtime reflection configuration in this application.

```sh
dotnet run --project QueryGenerator.Sample.csproj
dotnet publish QueryGenerator.Sample.csproj -c Release -r linux-x64 -p:PublishAot=true
./bin/Release/net11.0/linux-x64/publish/QueryGenerator.Sample --require-aot
```

Both attributed models query the actual SQLite Host through the same Fluent and
static materialization path. The program checks the returned DTOs and prints the
canonical model/projection hashes. Source and session ownership use `await using`.
Exceptions leave the native operation cleanup to those owners before the temporary
database is deleted. Ordinary model edits rebuild the managed consumer only.

The advanced manual path explicitly excludes generation and supplies equivalent
`TableBuilder` descriptors, static readers and writers:

```sh
dotnet run --project QueryGenerator.Sample.csproj -p:ManualMappings=true
dotnet publish QueryGenerator.Sample.csproj -c Release -r linux-x64 -p:PublishAot=true -p:ManualMappings=true
```

Both paths preserve CLR spelling by default and the exact `id`, `email`, and
`version` overrides. The Linux verifier runs generated/manual JIT and NativeAOT
consumers from one immutable local package feed, adds a mapped `Rank` property to
the second entity, and repeats those rows without changing the native SHA-256.
The generator's compiler diagnostics and deterministic incremental output are
checked separately; no other-platform or pristine-machine claim is made here.
