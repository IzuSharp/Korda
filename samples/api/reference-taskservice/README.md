# Reference task service: minimum and advanced consumers

This sample compiles the canonical C# application sources in
`src/Reference.TaskService/` and references only packed NativeWeave packages.
There is one application implementation, no sample SQL/HTTP substitute and no
consumer Cargo target. The complete [guide](../../../docs/guides/reference-taskservice.md)
describes the authenticated REST contract and finite persistence profile.

The minimum journey uses SQLite, an explicitly configured bearer identity and
`POST /v1/items`, followed by an identical-key replay and a typed item listing.
Its default project includes only the SQLite and Server domain packages, with
their normal transitive dependencies. The canonical business code is compiled
with a SQLite-only provider-selection guard.
The advanced journey uses PostgreSQL, the Rust TLS HTTP/1+HTTP/2 listener,
principal-scoped operation recovery, HMAC keyset cursors, version-checked
streamed attachments and the separate persistent outbox consumer. Both execute
the same statically compiled Entity/DTO/Handler code.

## Build and run

Use the maintainer-built local NuGet feed and its source-mapping configuration
from the B21 package probe, or an explicitly selected compatible release feed.
The domain packages are not inferred to be publicly published from the unrelated
HelloWorld preview. A normal application build has no explicit RID:

```sh
dotnet restore samples/api/reference-taskservice/Reference.TaskService.Sample.csproj --configfile /absolute/path/to/consumer/NuGet.Config
dotnet build samples/api/reference-taskservice/Reference.TaskService.Sample.csproj -c Release --no-restore
dotnet run --project samples/api/reference-taskservice/Reference.TaskService.Sample.csproj -c Release --no-build -- --config /absolute/path/to/reference.json
```

For the advanced consumer, add `-p:ReferenceIncludePostgreSql=true` to restore,
build and run. That profile includes both provider packages because the durable
deduplicating consumer remains a separate SQLite database. The profiles have
separate lock files; restore the selected profile before building it. The full
canonical executable already includes both providers without a sample property.

The configuration example and the exact CLI are in the guide. Configurations
contain credentials and belong outside the source repository. Startup emits
`READY <URI>` after the native listener has started. SIGTERM/Ctrl+C stops
admission, finishes admitted publication deadlines and joins real retirement.
`CLOSED` is printed only after the Server, both database sources and owned
application recovery-protection tasks have joined.

## Static application types and handlers

The minimum compiled declarations are `CreateItem`, `Item`, `CreatedItem`,
`Items.Table` and `TaskService.CreateAsync`. `Program` maps the typed JSON handler
with `MapPost<CreateItem>`. JSON uses generated `AppJson` metadata, and the
ordinary item query uses native-backed `WhereEqual`, `OrderBy`, `ThenBy`, `Take`
and `SelectAll`.

The advanced compiled declarations are `Attachment`, `AttachedItem`,
`OutboxEvent`, `OperationResult` and their static table descriptors. Attachment
handlers combine `Request.Body.CopyToAsync`, a bounded hash/file sink,
`DataSession.UpdateCheckedAsync` and `Results.Stream`. No Entity/DTO/Handler
change is translated into Rust code. The package probe adds ordinary managed
model/DTO/handler members in a copied consumer and executes the changed result
while comparing every native-module hash.

The sample project links the canonical source to keep these examples compiled;
the independent package probe copies that source out of the checkout and uses a
separate PackageReference consumer. A successful sample build alone is not a
pristine no-Rust environment claim. The real Linux JIT/AOT and HTTP/provider
rows, independent oracles, source/package hashes and review status are recorded
under `Plan/ledger/evidence/B21/`.
