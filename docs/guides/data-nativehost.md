# Native database sources and sessions

`NativeWeave.Database` is the typed C# application facade. Add the independently
packaged `NativeWeave.Database.Sqlite` or `NativeWeave.Database.PostgreSql` provider
for the Rust Host that executes it. This delivery validates Linux x64 with the
repository's pinned .NET 11 and Rust 1.100 toolchains. Other platform acceptance is
not implied. The maintainer builds native assets; a normal application's build,
DTO change or Native AOT publish does not invoke Cargo.

```sh
dotnet add package NativeWeave.Database.Sqlite
# Select PostgreSql instead, or add both for independent sources.
dotnet publish -c Release -r linux-x64 -p:PublishAot=true
```

Use the package version selected by the release catalog. Owned dependency edges
are exact and the existing packaging task validates the restored closure, selected
RID, native SHA-256 and SDK output location. Loading uses the existing Core
negotiated loader. SQLite's engine is `database.sqlite` with
`libnw_database_sqlite.so` / `nw_database_sqlite_get_abi_v1`; PostgreSQL uses
`database.postgresql`, `libnw_database_postgresql.so` and
`nw_database_postgresql_get_abi_v1`. The modules remain resident. Closing a source
joins that source's runtime and pool, without unloading shared code.

## Start with an explicitly owned source

```csharp
using NativeWeave.Data;
using NativeWeave.Database;
using NativeWeave.Database.Sqlite;

await using var source = new DataSourceBuilder()
    .UseSqlite("students.db")
    .WithName("students")
    .ConfigureLimits(new DatabaseLimits { MaxConnections = 4, MaxBatchRows = 128 })
    .Build();
await using var session = await source.OpenSessionAsync();
var count = await session.ScalarAsync(
    SqlCommand.Create("SELECT COUNT(*) FROM students"), Codecs.Int64);
```

`Build` validates options and the packaged ABI and creates a lazy source-owned
pool/runtime. It opens no database connection and performs no migration. A builder
is single-writer; each build creates an independent source generation. A source can
open concurrent independent sessions, subject to its finite physical connection
and waiter budgets. Each session owns one physical connection and admits one
command at a time. An active streaming cursor retains that admission; dispose it
before issuing the next command. A transaction provides explicit effect ownership,
not a concurrent session queue.

The provider factory is a convenience for default source limits:
`SqliteDataSource.Create(path)` or `PostgreSqlDataSource.Create(connectionString)`.
Their `Engine` properties permit optional metadata inspection before construction.
SQLite's in-memory profile requires `MaxConnections = 1`; configure that limit
explicitly rather than relying on an implicit pool-policy change.

PostgreSQL defaults to verified TLS and typed connection policy. Use an explicit
TCP URI and optional root certificate path. `DisableForLocalDevelopment` must be
chosen explicitly for a local plaintext test; a TLS failure never selects it.
Provider credentials and SQL parameter values are not copied into diagnostics.

## Typed expert SQL and materialization

```csharp
await session.ExecuteAsync(SqlCommand.Create(
    "INSERT INTO students (id,name) VALUES (@id,@name)",
    new SqlParameters().Add("id", 7L, Codecs.Int64)
                       .Add("name", "Ada", Codecs.String),
    SqlCommandKind.Mutation));

var projection = Projection<Student>.Create(
    [ResultField.Create("id", Codecs.Int64), ResultField.Create("name", Codecs.String)],
    row => new Student(row.Read(0, Codecs.Int64), row.Read(1, Codecs.String)));
var students = await session.QueryAsync(
    SqlCommand.Create("SELECT id,name FROM students ORDER BY id"), projection);
```

A `SqlCommand` snapshots its inputs. Rust recognizes logical named placeholders
outside quoted text/comments and binds typed values using the existing codec and
provider components. It never interpolates application values into SQL. The
`Parameterized(FormattableString)` convenience path sends typed holes and literal
segments; it has object-array/boxing costs. A hole inside a quotation, comment or
identifier rejects. Use typed nullable codecs with `SqlParameters` for SQL NULL.
An untyped null interpolation cannot establish a codec.

Expert SQL is an application-trust boundary. Identifiers and arbitrary SQL text
are not value parameters. Do not label session-changing or transaction-control
commands as ordinary mutations. Explicit `SessionMutating` is rejected inside a
framework transaction and retires/discards the physical session on completion.
Transactions, commits, rollbacks and savepoints use the tracked APIs; unaudited
multiple statements and direct transaction-control SQL reject.

`ExecuteAsync` returns affected rows for a mutation. A query cannot be submitted
as a count command. `ScalarAsync` requires exactly one column and one row; zero or
multiple rows reject. `QueryAsync` enforces both the buffered row and encoded-byte
budgets. Setting either buffered budget to zero prohibits buffered demand.
`StreamAsync` is one-shot and starts the native query at the first `MoveNextAsync`.
A bounded native batch is copied into managed owned bytes; the complete ordered
aliases/codecs are validated before user construction. The materializer runs
synchronously and returns owned CLR data. Early exit or an exception joins cursor
cleanup before the failed public operation completes.

## Transactions and truthful cleanup

```csharp
await using var tx = await session.BeginTransactionAsync(
    new TransactionOptions { RecoveryKey = "application-operation-123" });
await session.ExecuteAsync(command);
await tx.CommitAsync();
```

Cancellation requests do not establish rollback. An already canceled token avoids
native admission; accepted work retains its inputs, parent source, native operation
and callback root until the actual retirement receipt. The total command deadline
covers the accepted command/cursor lifetime. Expiry requests native stopping and
waits for real provider retirement; it never drops a running provider future and
returns its connection early. PostgreSQL provider stopping can require draining the
in-flight command under its fixed native statement timeout.

`Dispose` closes admission and requests cleanup without blocking for asynchronous
I/O. Prefer `await using` / `DisposeAsync` to join actual local safety. A finite
`CloseAsync(waitBudget, token)` controls only the caller's wait. Zero takes an
observation; cancellation or a deadline does not cancel cleanup itself. A child
that outlives source disposal retains its creating-module owner safely and rejects
new work. Its later disposal can observe the already-established receipt.

`Cleanup.Status`, `LocalResourcesReleased`, `ConnectionDisposition` and transaction
`Outcome` describe different facts. A pooled session reports `Usable` only after
provider-confirmed pool return. A discarded session reports the physical close
receipt; a failed or unconfirmed provider close is not rewritten as success merely
because memory is locally safe. Unknown commit outcome keeps its recovery key and
must not be retried implicitly. Provider errors carry bounded classification,
provider code and correlation identity; the framework omits SQL and credentials.

## Optional dependency injection

Add `NativeWeave.Database.DependencyInjection` only when using a container:

```csharp
using Microsoft.Extensions.DependencyInjection;
using NativeWeave.Database.Sqlite;

services.AddNativeWeaveDatabase(b => b.UseSqlite("students.db"));
services.AddKeyedNativeWeaveDatabase("audit", b => b.UseSqlite("audit.db"));
```

Duplicate unkeyed registrations or duplicate keys fail during registration.
Configuration executes once when the singleton owner is constructed. Standard
`GetRequiredKeyedService<DataSource>("audit")` resolves a keyed source. The container
owns each singleton and should itself be disposed asynchronously. No session is
registered implicitly; open separate sessions explicitly for concurrent requests.
An options change creates and drains separate sources rather than mutating a live
pool. DI does not become a required facade dependency.

## Runtime and allocation costs

Each source creates one bounded Tokio runtime (two worker threads, at most four
blocking workers) and one lazy native pool. The Core library acquires no global
executor. Native metadata, shape-cache entries, waiters, operations, parameters,
fields and batches all have finite source limits. Shape caches retain immutable
query shape/metadata, not mutable parameter values or sessions. A native query
owner has one separate boxed query payload so small sessions, plans and operations
do not each reserve the large mutable AST's inline size.

One asynchronous call uses create/start/terminal/typed-take/retire/release and a
managed callback root. Registration and cancellation controls participate in
retirement separately from native worker completion. A transferred cursor retains
its own session/source parent after the producing operation is released. Only one
unreleased batch is admitted per cursor. SQL/configuration use explicit bounded
UTF-8 copies; db-wire results have a native batch and a managed byte copy. These
facts are not throughput or memory-performance claims; comparative benchmarks
remain with their assigned tasks.

## Examples and verification

The executable [minimal and advanced sample](../../samples/api/data-nativehost/Program.cs)
executes typed insert/query, explicit transactions/savepoints, scalar cardinality
and early streaming exit on both providers. The guide uses trusted schema setup
only to make the sample self-contained; source construction never auto-migrates.

`tests/Data.NativeHost` exercises the actual two provider modules in ordinary JIT
and explicit Linux x64 Native AOT. It covers bounded admission and cancellation,
wrong-module owners, one-time typed transfer, per-fetch row limits, outstanding
buffer protection, provider/shape/materializer faults, callback-root/registration/
start/take failure compensation, physical pool return/discard, independent child
closure and optional DI disposal. Native Host tests separately force synchronous
completion, rejected/duplicate start, duplicate terminal, in-flight callback/control
races, callback failure poison and actual runtime shutdown. Native resource counts
and managed retirement snapshots are observed independently.

`tests/Data.NativeHost/api.py` compares every compiled declaration to the Plan-pinned
Design inventory and enforces B04's required symbols, XML and generated reference.
The current generated reference contributions are [Database](../../src/Data.NativeHost/reference/database.md),
[SQLite](../../src/Data.NativeHost/reference/sqlite.md),
[PostgreSQL](../../src/Data.NativeHost/reference/postgresql.md) and
[optional DI](../../src/Data.NativeHost/reference/dependency-injection.md).
`Administration`, Fluent query and relation methods are completed by B08, B13 and
B14 in the same facade assembly; they are not placeholder implementations in B04.
The package probe reuses the existing A14 exact-closure packer/output-record task
and blocks any consumer invocation of Cargo, rustc or rustup. Final pristine and
full-release composition remains with A16/B24.
