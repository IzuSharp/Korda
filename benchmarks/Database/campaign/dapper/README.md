# Independent Dapper.AOT campaign adapter

This directory is the independent **micro-ORM** route for the shared database
campaign. It uses Dapper 2.1.79, Dapper.AOT 1.1.0, Npgsql 11.0.0, and
Microsoft.Data.Sqlite.Core 11.0.0 with SQLitePCLRaw.provider.sqlite3 2.1.11 / core
3.0.5. Each provider is a separate Linux x64 Native AOT executable. The ordinary
Dapper reflection implementation is not an accepted fallback.

## Scope and contracts

The current implementation contains the shared contract's 32 read cases in 13
materialization shapes plus W33–W36 state/transaction operations, using 15 explicit
runtime `QueryUnbufferedAsync<T>` call sites and three typed mutation call sites.
Prepared execution has a separate set of 36 constant-SQL data call sites, with
public `[CacheCommand]` enabled. The build gate checks every actually compiled
call, including the separate lifecycle probes.
Every row is consumed by the shared digest. JSON becomes a typed `OrderSettings`
record; PostgreSQL numeric becomes decimal; SQLite INTEGER cents use a declared
Dapper.AOT decimal handler. Related children are grouped in bounded parent lists,
include empty parents, and are sorted by child ID before the common digest.
Those DTO, string, JSON, list and array allocations remain in end-to-end totals.
The temporary JSON text returned by `DbDataReader.GetString` is an adapter/provider
cost, separate from the business `OrderSettings` object and its `Channel` string;
it must not be excluded merely because it is a string.

W33–W36 execute commit, rollback, partial-update, and expected stale-version
conflict contracts. Each affected count, selected tuple and worker key is checked;
the digest consumes every SELECT. Cancellation, early termination and a genuine
two-worker conflict run separately through `--lifecycle-probe`, described below.
They are excluded from the mixed workload and its performance metrics. The separate
minimum capability probe proves only the cases that it actually executed.
Compilation and an interceptor gate do not prove query correctness or performance.

Two pinned-generator constraints were observed in the actual campaign build.
With runtime SQL and a `CancellationToken` property in a named parameter type,
Dapper.AOT 1.1.0 emitted an invalid `GetCancellationToken` body that referenced an
undeclared `cmd`; the read argument types therefore contain only actual SQL
arguments. This adapter currently has no cancellation case, and this removal is
not a cancellation-support proof. Also, the built-in nullable scalar row factory
bypasses the declared decimal handler. SQLite's nullable money scalar is mapped
by Dapper as `long?` cents and immediately converted to the equivalent `decimal?`
value before hashing; decimal DTO fields still use the generated handler. No
generated source is patched and no query falls back to vanilla Dapper.

`ReadPlan` checks result names, logical types, nullability and ordered parameter
counts. It rewrites the frozen SQL's `?` / `$n` placeholders into Dapper `@pN`
parameters outside the measured interval. The command evidence retains both SQL
forms, clause boundaries and all ordered typed parameter values. The PostgreSQL
provider performs its named-to-positional protocol translation; this adapter does
not claim to capture the physical network protocol.

In `dynamic` mode, each operation concatenates the frozen SQL clauses and creates
the applicable typed argument object. In `prepared` mode it reuses both and uses
Dapper.AOT's generated DbCommand/parameter cache; parameter values are rebound
on every command reuse. There is no explicit physical `Prepare`, and Npgsql's
automatic preparation is disabled. Microsoft.Data.Sqlite disposes prepared SQLite
statements when the ORM cache detaches the command's connection, so this route
does not retain physical SQLite statements across executions. Ordinary integer queries use `p1..p8`; the
text pattern has its own shape; the 32-key relation uses a distinct 96-parameter
shape. Unused arguments are left to Dapper.AOT's generated SQL parameter filter;
the generated command factory must be retained and reviewed.

`freeze_prepared.py` derives `PreparedReads.cs` from the exact frozen manifest.
The build reproduces and checks it; execution rejects a different contract hash.
Constant SQL is necessary because this pinned generator removes CacheCommand
when SQL/parameter shape cannot be resolved at compile time. The generated gate
requires the real TryReuseThreadStatic/TryRecycleThreadStatic implementations for
all 36 prepared sites, and rejects command caching at dynamic/lifecycle sites.
It never patches generated interceptors.

Each cached call site owns per-thread slots and a shared queue with target
capacity 16, whose Count/Enqueue race can exceed 16. With at most N concurrent
returns, the queue component is bounded by 15+N; thread slots and in-flight
commands must be accounted separately. Fixed business concurrency alone does
not imply a total 16+N cache bound. Receipts record public runtime thread limits
and the observed thread count; they do not reflect or invent private cache counts.

Provider-managed pools are disabled. Persistent mode owns one physical connection
per worker. Per-operation mode borrows and returns from an explicit shared pool of
exactly N=concurrency physical connections, pre-opened and configured outside the
measurement budget. It holds no initializer lease. A failed operation retires its
physical owner; a later operation can rebuild that slot without retrying the failed
operation. Failed retirement quarantines the slot and disables new physical owners. Connection and command limits are five seconds. PostgreSQL sessions
set UTC and a five-second statement timeout. SQLite uses foreign keys, WAL,
`synchronous=FULL`, private cache and a five-second busy timeout. All session
configuration occurs once per physical connection, including a replacement after
a failure. Failed operation and failed cleanup exceptions are both retained.

## Build outside the source tree

The common `Runner.cs` must be present. The build freezes `dapper` and `common`
into a new directory and verifies their bytes did not change while copying.
Existing SDK/ORM caches can be supplied as read-only NuGet fallback folders;
new package writes go to the new build directory. No machine-specific path is
required by the sources.

```bash
python3 benchmarks/Database/campaign/dapper/publish.py \
  --provider sqlite \
  --output /absolute/new/build-directory \
  --dotnet /absolute/dotnet-11.0.100/dotnet \
  --clang /absolute/clang-23.1.5/bin/clang \
  --fallback-cache /absolute/optional-sdk-package-cache \
  --fallback-cache /absolute/optional-orm-package-cache
```

Use `--provider postgresql` and a different output directory for Npgsql. The
script records exact commands, timestamps, raw output, source hashes, compiler
identities, package locks, and the resulting executable hash. MSBuild uses
`-m:1 -nr:false -p:UseSharedCompilation=false` and a locked restore. It performs
no database initialization and runs no load.

The build fails if any active ORM source call lacks exactly one generated
interceptor, if the concrete DTO/argument/handler symbols are missing, or if the
locked package versions change. `publish/dapper-interceptors.json` binds the
generated source and checked call locations to the published ELF hash; the
generated source is preserved alongside it. Execution checks that binding before
entering the common runner and refuses a JIT process.

The optional `run_preflight.py` serially launches the 8 combinations of
dynamic/prepared, persistent/bounded pool and concurrency 1/2. Each combination
runs verify-only and a short fixed-count repetition, checking exact result
oracles, counter conservation and owner retirement. It uses the common Runner
unchanged, creates no dataset, and cannot qualify as a long benchmark.

## Run the shared dataset

The campaign coordinator must initialize the exact common dataset first. This
adapter refuses `--initialize` and cannot silently substitute a different seed.
For SQLite, provide the frozen **shared library** using `--sqlite-native` or
`DB_PERF_SQLITE_NATIVE`; the bootstrap checks SQLite 3.51.3, identifies the mapped
image through the actual function address, records its SHA-256/source ID/compile
options, and installs that handle into SQLitePCLRaw. Compilation options still
need to match the Korda and native routes before performance is qualified.

```bash
/absolute/build-directory/publish/DbPerf.Dapper \
  --manifest /absolute/frozen/contract-v1.json \
  --connection /absolute/frozen/seed.sqlite \
  --sqlite-native /absolute/frozen/libsqlite3.so \
  --output /absolute/results/dapper-sqlite-prepared.json \
  --mode prepared --session-mode persistent --case mixed --verify-only
```

The PostgreSQL executable takes an Npgsql connection string or a PostgreSQL URI
without URI query parameters. Its driver is Npgsql's managed PostgreSQL protocol
implementation, not libpq. Run limits and rate scheduling belong to the common
runner. This adapter adds `<output>.commands.json` outside the measured interval
without replacing its final JSON.

The owner-retirement hook reports successful connection/enumerator/transaction
acquisitions and disposals plus bounded-pool acquisitions, returns, replacements
and quarantine. It closes the shared pool after every worker has retired. It does not infer provider-internal ownership. SQLite also reports
`sqlite3_memory_used()` and process-lifetime `sqlite3_memory_highwater(0)`; these
cover SQLite's allocator, not the entire native heap. The one process-global
SQLite library handle remains until exit. End-to-end allocation totals include
the adapter and provider; no framework-only or zero-allocation claim follows
from this adapter's implementation.

## Separate lifecycle correctness

Use the same published executable and explicit provider identity:

```bash
/absolute/build-directory/publish/DbPerf.Dapper \
  --lifecycle-probe \
  --manifest /absolute/frozen/contract-v1.json \
  --connection /absolute/frozen/seed.sqlite \
  --sqlite-native /absolute/frozen/libsqlite3.so \
  --output /absolute/results/dapper-sqlite-lifecycle.json \
  --mode prepared --session-mode per-operation --concurrency 2
```

The PostgreSQL form takes the same connection string/URI as the ordinary runner
and omits `--sqlite-native`. Run this only in the dedicated benchmark database,
without other agents/processes mutating its lifecycle table concurrently. This
explicit branch creates/resets `perf_lifecycle` with quoted Id/Counter/Version
columns, using generated Dapper ExecuteAsync calls outside any measurement.
Normal mixed execution never runs this setup and does not touch this table.

Two physical connections each read `(1,0,1)` before a barrier releases their
checked updates. Exactly one update must affect one row and the other zero;
the persisted tuple must be `(1,1,2)`. The early-enumeration probe opens tenant
1's 8192-row stream, consumes one row, disposes the enumerator, returns the lease,
then visits both physical pool slots and successfully queries on the original
connection. It does not claim that the provider avoided draining unread wire data.

Cancellation uses the normal generated ORM enumerable's
`GetAsyncEnumerator(CancellationToken)`. The PG in-flight probe first observes
the target backend actively waiting in `pg_sleep` through another generated
Dapper query, then cancels the supplied token and requires a genuinely incomplete
first MoveNext plus OperationCanceledException. Both providers also test an
already-canceled token. SQLite separately retains a finite recursive-query
control and a 25 ms cancellation attempt. An exception after a synchronously
blocking MoveNext does not prove interruption of SQLite VM execution; the result
marks that capability unproven instead of claiming asynchronous cancellation.

Cancellation follows the adapter's conservative failure policy: dispose the
enumerator, retire its physical connection, and permit the next pool borrow to
replace the slot. No failed operation is retried. Lifecycle receipts retain
exact SQL, before/after tuples, affected counts, token/exception identity,
suspension and timer observations, same-owner reuse and owner counters. Cleanup
errors are retained with the primary failure. They are not allocation or load
acceptance results.

The separate `build_sqlite_image.py` takes the real product `compile_commands.json`,
locked source/compiler inputs, and an actual native runtime fingerprint. It retains
all product macros and verifies the rebuilt image's source ID and full compile option
list. The required SQLite C ABI visibility and shared-versus-static linkage difference
are recorded explicitly. It performs no database operation.
