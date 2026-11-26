# DB-Perf-01 managed public consumers

These applications run as **published Linux x64 Native AOT executables**. The Korda
consumer references exactly one public provider package through `PackageReference`.
`build.py` copies the application outside the Korda checkout, maps that exact
package ID to the selected candidate feed, publishes it, and saves source,
package, restore graph, executable and native image hashes. No Korda
`ProjectReference` or internal implementation API is used.

The independent implementation is **Dapper 2.1.100 + Dapper.AOT 1.1.0**, with
Npgsql 11.0.0 for PostgreSQL or Microsoft.Data.Sqlite.Core 11.0.0 and
SQLitePCLRaw.provider.sqlite3 3.0.5 for SQLite. It uses generated typed mapping,
including explicit decimal and typed JSON handlers. Dapper is a SQL micro ORM:
its dynamic mode builds SQL text; Korda's dynamic mode builds the public Fluent
query graph. The SQL/result contract is shared, while those construction costs
remain separate architectural choices. Do not describe Dapper as a Fluent/LINQ
query builder.

## Build and initialize

Source the pinned task environment first. Build each provider into a new directory
outside the checkout; the scripts refuse to overwrite previous evidence.

```bash
python3 benchmarks/db-perf/managed/build.py \
  --provider sqlite --feed /absolute/original-sqlite/feed \
  --version 0.1.0-preview.3 --output /absolute/consumer-before-sqlite

python3 benchmarks/db-perf/managed/build.py \
  --provider postgresql --feed /absolute/original-postgresql/feed \
  --version 0.1.0-preview.3 --output /absolute/consumer-before-postgresql

python3 benchmarks/db-perf/managed/build_orm.py \
  --provider sqlite --kind benchmark --output /absolute/dapper-sqlite

python3 benchmarks/db-perf/managed/build_orm.py \
  --provider postgresql --kind benchmark --output /absolute/dapper-postgresql

/absolute/consumer-before-sqlite/out/DbPerf.Korda \
  --connection /absolute/db-perf.sqlite --initialize \
  --tenants 8 --orders-per-tenant 1024
```

Initialization creates the reviewed sample tables `customers`, `orders` and
`order_lines`, indexes, and `db_perf_metadata`; it never drops an existing table.
Data has deterministic hot customers, NULL foreign keys/notes, empty child
collections, skewed wide strings and typed JSON. SQLite stores exact integer
cents; PostgreSQL stores `numeric(18,2)`. Both expose `decimal` in the C# model.
SQLite uses WAL, FULL synchronous durability, foreign keys and a five second busy
timeout. Transactions explicitly use `BEGIN IMMEDIATE` in all final SQLite routes; PostgreSQL uses read-committed read-write transactions. PostgreSQL server configuration is recorded by the external runner.

Run PostgreSQL and its client from the same controller process on environments
with separate execution network namespaces. `benchmarks/db-perf/run.py` provides
that ownership and separately samples the client and PostgreSQL process trees.

## Correctness before measurement

```bash
/absolute/consumer-before-sqlite/out/DbPerf.Korda \
  --connection /absolute/db-perf.sqlite --case mixed --mode prepared \
  --warmup 1 --operations 35 --verify --output /absolute/korda-check.json

/absolute/dapper-sqlite/out/DbPerf.Dapper \
  --connection /absolute/db-perf.sqlite \
  --native-library /absolute/matching/libsqlite3.so \
  --case mixed --mode dynamic --warmup 1 --operations 35 --verify \
  --output /absolute/dapper-check.json
```

The SQLite Dapper benchmark takes a database **file path**, not an ADO.NET
connection string. Its separate minimal `DapperProbe` takes the provider connection
string and exact SQLite library path as positional arguments. The benchmark
requires and verifies the actual `/proc/self/maps` native image; it does not use
a bundled or system SQLite fallback. Its receipt records library SHA-256, engine
version and compile options. PostgreSQL accepts a `postgresql://` URI and uses the
managed Npgsql protocol implementation, so it has no libpq native image to hash.
Passing `--native-library` to the PostgreSQL Dapper benchmark is rejected; it
cannot establish a native library identity for the managed protocol provider.

Every consumed row enters a deterministic FNV-1a digest, checked against an
independent oracle derived from seed formulas. Failure is fatal to correctness
validation and is preserved in measured error counts. Expected counts/digests,
actual query SQL, selected case IDs and per-case execution counts are recorded.
`--verify` checks all supported cases before the measured interval; it does not
make a timing sample qualified or extend the measured interval.

The complete frozen contract is `../workload/queries-v3.json`, combining the 30 validated reads from `queries-v2.json` and five lifecycle cases from `lifecycle-v1.json`. The read cases cover
projection shapes, filters, joins, groups/HAVING, NULL aggregates, composite
cursors, offset pagination, subqueries, CTEs, windows, set operations and an
explicit related batch. It is exported from successful AOT receipts by
`../workload/export_validated.py`. Dapper constants are generated from that SQL
contract; provider placeholders are changed to named parameters for ADO.NET.
The related Dapper constant currently fixes 32 parent keys; use `--rows 32` and
`--batch-rows >=32` for that case. Other read cases have formula-based oracles for
runtime row/dataset options. Both build scripts bind the final manifest SHA-256 and copy it beside the build receipt. `check_dapper_contract.py` compares all read SQL constants and the observed independent ORM CRUD SQL constants to that manifest.

Five additional cases exercise successful commit, rollback, NULL partial update,
stale optimistic conflict and early stream disposal. Write cases target tenant 1,
order `ordersPerTenant`, restore its complete initial value before commit or by
rollback, then read persisted state. They therefore preserve read oracles across
mixed operations. A successful commit case performs real changes and a real
restore within one durable transaction. Its business operation includes both
updates and all verification reads. Stale-conflict is an expected exceptional
path and must be reported separately from ordinary successful writes. `export_lifecycle.py` binds the mutation sequence and expected persistent state to actual results from both providers and both managed routes. The original single-worker v4 Korda SQLite probe used its default deferred transaction; its result is retained as correctness evidence, while final mixed-write comparison uses the explicit immediate policy in every route.

The original package's PostgreSQL direct compound query followed by a
**table-qualified outer ORDER BY** failed during validation. The final set-query
contract uses the legal public `CommonTable` wrapper and an outer ordering in
both original and optimized consumers. This is a workload construction choice;
the original direct-form failure remains a documented product limitation.

## Scheduling and measurement boundaries

Use `--cases` with an explicit comma-separated list to freeze identical mixtures
across routes. `--case mixed` follows the application's current supported list and
is useful for validation, but is not an immutable campaign specification.

```bash
/absolute/consumer-before-sqlite/out/DbPerf.Korda \
  --connection /absolute/db-perf.sqlite \
  --cases numeric,text,money,json,wide,tx-commit,tx-rollback \
  --mode prepared --session persistent --concurrency 4 --warmup 256 \
  --seconds 10 --arrival-rate 100 --queue-capacity 64 --idle-seconds 5 \
  --output /absolute/run.json
```

`--rate` and `--arrival-rate` are aliases. With a positive rate, the unchanged
shared `OfferedLoad` implementation prepares a bounded FIFO and workers before
measurement, schedules deterministic arrival times, records full-queue rejection,
and drains all accepted work. Its receipt preserves planned, attempted, sent,
started, succeeded, failed, completed and rejected counts. Successful service
latency and scheduled-to-complete latency have separate histograms; failure
response latency is also retained. Rejected operations never silently disappear.
Top-level `Latencies` describes successful service duration; offered scheduled-to-complete values are in `OfferedLoad.SuccessScheduledHistogram`, with failed completions included separately in `AllCompletedScheduledHistogram`. The empty-driver run uses the same schedule but does not simulate real I/O
continuations and must not be blindly subtracted to claim framework zero.

Without a rate, execution is explicitly closed-loop: each worker issues its next
operation after its preceding completion. Such results measure that workload;
they do not establish fixed-offered-rate tail latency or maximum throughput under
a stated error/latency criterion. `--operations` sets a total finite operation
budget across workers. `--seconds` sets duration. `--session per-operation` includes
session/connection acquisition and return; the default keeps one session per
worker. Pool acquisition semantics differ between Korda and the external provider
and must be reported when that mode is compared.

Warmup, plan/oracle creation, worker state, histograms, and scheduling queues are
outside the allocation boundary. The measured cumulative values are
`GC.GetTotalAllocatedBytes(precise: true)` before and after the entire interval
including accepted-work drain, with generation 0/1/2 collection counts. The
measurement covers all process threads. `SteadyStartMonotonicNs` and `SteadyEndMonotonicNs` identify this workload interval on Linux monotonic time, including drain at the end, for filtering the external resource samples. Values remain raw cumulative bytes before
per-operation/per-row division. It includes business DTO/string/JSON objects,
provider/framework work and harness async state; **framework-only allocation is
not attributed or proven by this application alone**.

`--lifecycle-checks` selects separate exceptional-path correctness probes: pre-canceled
commands, cancellation of a pending real database query, connection reuse after
cancellation, and two simultaneous optimistic compare-and-swap writes with a
single winner. It records raw process allocation totals and observed suspension,
then restores the complete original row and drains ownership. It is not part of
the normal mixed-load interval. SQLite's synchronous independent provider cannot
establish an equivalent active engine-cancellation path; this is an explicit
capability gap, not a successful cancellation result. Failed earlier probes and
their actual generated code remain evidence; Dapper token propagation must be
verified in the generated command factory and by real execution.

## Retained-pool saturation and recovery

`--recovery-checks` keeps the same source, workers, persistent sessions, prepared
plans and connection pool alive across a reference phase, an overload phase,
complete accepted-work drain, and a recovery phase. Each phase uses the same
bounded offered-load scheduler and selected case order. A new scheduler is
prepared outside each phase's allocation boundary, and its preparation bytes
are reported separately; the database pool is never reconstructed between phases.

```bash
/absolute/consumer-before-sqlite/out/DbPerf.Korda \
  --connection /absolute/db-perf.sqlite --case mixed --mode prepared \
  --session persistent --concurrency 4 --warmup 35 --queue-capacity 8 \
  --recovery-checks --recovery-rate 70 --recovery-seconds 0.5 \
  --overload-rate 100000 --overload-seconds 0.5 --idle-seconds 1 \
  --output /absolute/recovery.json
```

The short values above exercise accounting and recovery correctness. Calibrate
the fixed rate and use longer observation intervals before interpreting latency.
Each phase retains cumulative allocation counters, collections, client CPU and
memory, scheduled/service latency histograms, the complete admission ledger,
actual per-case counters and quiescent ownership snapshots. Every accepted job is
joined before the next phase. Final source/session disposal happens only after
recovery, followed by the optional idle observation.

`Passed` means all planned reference and recovery requests succeeded without
rejection, overload rejection occurred, ownership returned to its warm baseline
after every phase, and tracked owners reached zero at final disposal. Overload
failures remain visible in the phase ledger and are **not** implied to be zero by
that flag. The receipt reports the reference/recovery scheduled p99 ratio without
inventing a latency acceptance threshold. Any campaign must additionally apply
its stated error and tail-latency criteria. An earlier harness revision issued
the persisted-state query after commit/rollback confirmation but before joining
transaction disposal. PostgreSQL then legitimately reported `ConcurrentUse`
while cleanup still owned the session gate. Those failed attempts remain
evidence of an invalid lifecycle order, not a product regression. Both managed
routes now complete transaction disposal before the persisted-state read;
Korda additionally verifies `Cleanup.LocalResourcesReleased`. All of this
cleanup remains inside the measured business operation.

## Fully consumed large streams

The normal 35-case fixture and manifest remain unchanged. Two separate immutable
contracts describe the wide source-generated DTO stream: 1,024 fully consumed rows
on the normal fixture, and 8,192 fully consumed rows on an independent fixture
with one tenant and 8,192 orders. These are `large-stream-1024-v1.json` and
`large-stream-8192-v1.json` in `../workload/`. They bind exact SQL, typed parameters,
column order, deterministic complete-row digest and the initial successful
original-package/Dapper AOT receipts for both databases and construction modes.
Further validation belongs in new receipts rather than changing these manifests.

```bash
/absolute/consumer-before-sqlite/out/DbPerf.Korda \
  --connection /absolute/db-perf-wide8192.sqlite --initialize \
  --tenants 1 --orders-per-tenant 8192 --rows 8192 --batch-rows 32

/absolute/consumer-before-sqlite/out/DbPerf.Korda \
  --connection /absolute/db-perf-wide8192.sqlite --case wide --mode prepared \
  --tenants 1 --orders-per-tenant 8192 --rows 8192 --batch-rows 32 \
  --warmup 1 --operations 2 --output /absolute/wide8192.json
```

Use a separate PostgreSQL database with the same fixture parameters. Fully consume
all 8,192 rows; the early-stop case, which reads only three rows, does not count
as this large-stream lane. The expected wide digest per operation is
`4480919483496718695`, and two operations must record 16,384 consumed rows.
`export_large_stream.py` verifies that contract against actual managed reports.
It is a heavy read lane; it does not replace the mixed transaction workload.

Korda `prepared` means public `PreparedQuery<T>` reuse for Fluent reads, immutable
`SqlCommand` reuse for raw SQL, and no prepared overload for the public related
batch or CRUD/transaction controls. Each receipt states those differences by
case. Dapper's prepared constant call sites use its generated cached command
factories; dynamic sites rebuild SQL/parameters and disable command caching.
Server-side prepared retention remains provider-specific and is not inferred
from the word `prepared`.

All queries consume their complete business result except the explicit early-stop
case. Latencies use fixed 8192-bin base-2 histograms with 128 subdivisions per
exponent and report upper bounds. Whole-process user/system CPU and RSS/PSS are
captured; the external controller supplies time series and lifetime peaks.
`--idle-seconds` observes CPU and retained RSS/PSS after workload/session/source
drain, outside effective time and the raw allocation boundary, without forced GC.
Korda native source/session/operation/cursor/buffer/owner counters must return to
zero. Dapper reports its application-owned logical connection count and clears
provider pools; it does not claim visibility into every internal reader/native
owner. Native heap peaks still require an independent allocator observation.

## Prepared-layout allocation isolation

`build.py --kind prepared-probe` publishes the separate `PreparedProbe` application
through the same external ordinary `PackageReference` path. The default `workload`
kind continues to copy the complete Korda workload and shared scheduler sources.
The probe uses existing `orders` data and first executes real queries to check
parameterless results and parameter values captured before enumeration.

```bash
python3 benchmarks/db-perf/managed/build.py --provider sqlite \
  --kind prepared-probe --feed /absolute/candidate/feed \
  --version 0.1.0-candidate.1 --output /absolute/prepared-probe

/absolute/prepared-probe/out/DbPerf.PreparedProbe \
  /absolute/db-perf.sqlite /absolute/prepared-allocation.json 100000 9
```

After warmup, interleaved windows measure an empty delegate, parameterless stream
construction with null or supplied-empty values, one existing integer snapshot,
and integer binding plus stream construction. Each window records the precise
whole-process cumulative allocation counters before and after the full loop,
operation count and all generation collections. No stream is enumerated inside
these windows: native SQL and ownership counters must remain unchanged, and all
native owners must reach zero at final disposal. This isolates the public
`PreparedQuery.AsAsyncEnumerable` construction path; it does not measure complete
query execution, result materialization or real asynchronous I/O allocation.
Empty-driver observations are retained without subtraction.

Before/after probes must have identical source/project hashes and use separately
restored package identities. Local experimental packages must carry their own
version, a dirty production patch hash and complete entry provenance; the normal
bundle composer must regenerate matching closure/shared digests, and the provider
facade's embedded identity must match that experimental version. Such experiments
do not replace final producer-CI or public NuGet package validation. The affected
Query.Fluent correctness suite retains snapshot/source/NULL/disposal/concurrency
checks and verifies that first and repeated lazy construction dispatch no SQL and
change no native ownership. Timing, allocation ceilings and whole-process GC
sampling run only in these local diagnostics, outside CI correctness gates.

## Evidence and limits

Keep each `build-receipt.json`, `project.assets.json`, `publish.log`, published ELF,
actual loaded native image identity and each run's command/report/log together.
Before and after Korda binaries must use identical consumer source hashes,
workload parameters, dataset and measurement environment. `bin/obj` may be removed
after those artifacts are preserved; do not delete the restore graph with them.

The short probes establish actual package/AOT/provider functionality. They do not
satisfy the required eight uninterrupted 30 minute qualified-host segments. No
absence of a GC, rounded average, sampled missing stack, or local zero row cost
establishes full-framework zero allocation.

Primary implementation guidance:

- Dapper.AOT: <https://aot.dapperlib.dev/gettingstarted>
- Dapper.AOT release/source: <https://github.com/DapperLib/DapperAOT/tree/1.1.0>
- Npgsql AOT compatibility: <https://www.npgsql.org/doc/compatibility.html>
- SQLite async behavior: <https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async>
- SQLite native version selection: <https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions>

## Local streaming allocation diagnostics

The `Data.Streaming` correctness verifier publishes the existing Native AOT test
consumer and executes provider, storage identity, owner transfer/release,
malformed-batch, row-lease and race assertions. It does not run allocation
windows. A normal invocation and `--correctness-only` both select correctness.

After the consumer has been built, run these diagnostics locally in separate
processes with fresh output directories and no concurrent build or timing run:

```bash
/absolute/streaming-aot/Korda.Data.Streaming.Tests /absolute/inbatch-allocation \
  --require-aot --allocation-gate-only
/absolute/streaming-aot/Korda.Data.Streaming.Tests /absolute/owned-batch-allocation \
  --require-aot --owned-batch-probe-only
/absolute/streaming-aot/Korda.Data.Streaming.Tests /absolute/reused-lease-allocation \
  --require-aot --row-lease-reuse-probe-only
```

These retain their existing raw process counters, exact-zero or bounded-byte
criteria, and owner/checksum assertions. They isolate synthetic in-batch,
exclusive managed storage, or reused-lease work. They do not establish complete
public query allocation or measured PostgreSQL/SQLite throughput. No CI action,
ordinary component verifier or release command invokes these profiling flags.

### Additional explicit local diagnostics

The default component verifiers run correctness, API and ownership tests. Allocation windows for abstraction codecs, control records, query projection encoding and Host snapshot parsing are local diagnostics only. The existing published AOT test executables retain these explicit entries:

```bash
<abstractions-aot>/Korda.Data.Tests <fresh-output> --require-aot --allocation-probe --allocation-regression
<abstractions-aot>/Korda.Data.Tests <fresh-output> --require-aot --owned-wire-allocation-regression
<abstractions-aot>/Korda.Data.Tests <fresh-output> --require-aot --control-record-allocation-regression
<abstractions-aot>/Korda.Data.Tests <fresh-output> --require-aot --configured-scalar-probe --allocation-regression
<binding-aot>/Korda.Query.Binding.Tests --projection-allocation <fresh-output>
<binding-aot>/Korda.Query.Binding.Tests --projection-scratch-allocation <fresh-output>
<host-aot>/Korda.Data.NativeHost.Tests --snapshot-probe <fresh-output> 10000 3
```

The parser-only snapshot entry also checks its existing allocation budget before collecting its explicit windows. These commands are not invoked by a default verifier, workflow, release script or package build. Keep their raw process allocation and GC counters with their exact source and binary identities; they do not establish complete-query performance. The ordinary suites still execute wire goldens, codec rejection matrices, snapshot parser equivalence, projection scratch/ownership checks, bounded-input rejection, and all batch lifetime checks.
