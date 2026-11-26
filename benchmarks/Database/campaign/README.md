# DB-Perf-01 Linux database campaign

This parallel 36-descriptor implementation is retained for historical reproduction.
The batch's sole active campaign is now [the reviewed controller](../../db-perf/CAMPAIGN.md).
Its former automatic workflow is preserved byte-for-byte under
`archived-workflows/db-perf-01-linux.yml`, outside GitHub's workflow discovery path.
The two contracts, datasets, schedulers and package identities differ; their
observations must not be combined into one four-column comparison. Run
`45393596205` failed during initialization before any long segment; its original
artifacts and the later explicit transaction-retirement fix remain separate evidence.

This is a real-database comparison harness. Building it or passing its structural
check does not establish a performance result. Accepted measurements, failed
attempts, exact identities and recovery state live in
[Plan's DB-Perf-01 handoff](https://github.com/IzuSharp/Plan/blob/task/DB-Perf-01-linux/ledger/evidence/batches/DB-Perf-01/handoff-linux.md).
Do not count local capability probes as any of the eight sustained segments.

## Fixed inputs and four routes

The original product commit is `771bc77cbd68d3f932b99ed2497c0697bbba5f02`.
`bootstrap.py` verifies its two original CI-produced preview.3 packages by SHA-256,
builds the current managed candidate with the repository's normal bundle composer,
and verifies every reused native input. It then builds ordinary external C#
`PackageReference` applications as actual Linux x64 Native AOT images. No product
project reference or JIT fallback is accepted. Before and after use the same app.
Every result binds the actual executable, contract, package and loaded native image.

| Route | Entry and preparation |
| --- | --- |
| `native` | Same locked Clang Release, direct libpq/SQLite. Owned equivalent row values, typed JSON/decimal conversion, full digest consumption, transactions and cleanup. Dynamic SQL is constructed each operation; prepared physical statements are reused per connection. |
| `before` | Original independent PostgreSQL or SQLite package through current public Fluent, generated mappings and checked CRUD. |
| `after` | Identical external source and database, changing only the selected Korda package. |
| `dapper` | Dapper 2.1.79 + Dapper.AOT 1.1.0, Npgsql 11.0.0 or Microsoft.Data.Sqlite.Core 11.0.0. Every ORM call must have a generated interceptor. Prepared constant call sites use public CacheCommand. |

Prepared Korda handles reuse immutable native plans; that does not assert the
same physical statement lifetime as direct native preparation. Dapper's generated
command/parameter cache does not guarantee physical preparation: SQLite disposes
its statement when a cached command detaches its connection, and Npgsql auto-prepare
is disabled. These are recorded provider differences. Dynamic, persistent and
per-operation lanes remain separate. See [Dapper details](dapper/README.md) and
[native details](native/README.md). No raw ADO query fallback supplies missing ORM cases.

The SQLite comparator image is built from the product's exact amalgamation and
compile options, checked against an actually executed fingerprint. PostgreSQL
uses the same server, data, indexes, read-committed transactions, full durability,
`force_custom_plan` and disabled JIT. SQLite uses WAL, FULL synchronization,
foreign keys and a 5-second busy timeout. Client pools contain exactly N physical
connections; per-operation setup owners are released before the workload begins.

## Business contract

`build_contract.py --check` verifies deterministic SQL, typed parameters, row
counts and typed result digests. The frozen contract contains 36 descriptors:
32 read cases and four state transitions. Semantically repeated variants remain
explicitly marked; they are not used to inflate the distinct-case count. Data has
8 tenants × 8192 orders, 256 customers, skew, hot customers, missing associations
and NULLs. Each logical worker owns one of 128 state rows. The normal mixed load
runs every descriptor with equal planned weight, with full result consumption.
Cancellation, early disposal and genuinely concurrent conflicting writes execute
separately against a private lifecycle table and never contaminate the normal mix.

| ID | Case | Korda public entry | Semantically distinct | Expected consumed rows |
| --- | --- | --- | --- | ---: |
| R01 | numeric | fluent | true | 32 |
| R02 | text | fluent | true | 32 |
| R03 | money | fluent | true | 32 |
| R04 | json | fluent | true | 32 |
| R05 | wide | fluent | true | 32 |
| R06 | optional-filter | fluent | true | 32 |
| R07 | range | fluent | true | 32 |
| R08 | left-join | fluent | true | 32 |
| R09 | inner-join | fluent | true | 32 |
| R10 | null-notes | fluent | true | 32 |
| R11 | literal-note | fluent | true | 32 |
| R12 | customer-totals | fluent | true | 257 |
| R13 | product-totals | fluent | true | 32 |
| R14 | empty-sum | fluent | true | 1 |
| R15 | cursor | fluent | true | 32 |
| R16 | offset | fluent | true | 32 |
| R17 | exists | fluent | true | 1 |
| R18 | in-subquery | fluent | true | 32 |
| R19 | cte | fluent | true | 32 |
| R20 | window | fluent | true | 32 |
| R21 | union-all | fluent | true | 32 |
| R22 | union | fluent | true | 24 |
| R23 | intersect | fluent | true | 8 |
| R24 | except | fluent | true | 8 |
| R25 | running-total | fluent | true | 32 |
| R26 | three-join | fluent | true | 32 |
| R27 | anti-association | fluent | true | 32 |
| R28 | nullable-order | fluent | true | 32 |
| R29 | raw-sql | sql | false | 32 |
| R30 | related | relationship-api | true | 53 |
| R31 | stream-large | fluent | false | 8192 |
| R32 | optional-filter-absent | fluent | false | 32 |
| W33 | tx-commit | fluent-mutation-and-transaction-api | true | 2 |
| W34 | tx-rollback | fluent-mutation-and-transaction-api | true | 2 |
| W35 | partial-update | fluent-mutation-and-transaction-api | true | 1 |
| W36 | stale-version | fluent-mutation-and-transaction-api | true | 1 |

Actual generated SQL is exported by the public Korda consumer. Each adapter checks
the same oracle before warmup and on every measured operation. Engine EXPLAINs,
parameter types, PostgreSQL smoke statement logs and persisted state are retained.
SQL execution count, protocol submissions and physical network round trips are
separate concepts. Uninstrumented round trips remain explicitly unmeasured.

## Dedicated execution

The opt-in `.github/workflows/db-perf-01-linux.yml` runs one dedicated Ubuntu VM.
It only accepts an edited title on this repository's `task/DB-Perf-01-linux` PR.
Normal PR CI runs affected correctness, short allocation and AOT checks only.

1. Commit the source and run `python3 benchmarks/Database/check.py` and
   `python3 benchmarks/Database/campaign/build_contract.py --check`.
2. Add `[DB-Perf-01:smoke]` to the canonical PR title through a normal PR edit.
   Bootstrap, original/final public-package allocation probes, fixture creation,
   all four route smoke matrices and lifecycle checks must finish successfully.
3. With the reviewed source fixed, edit the same title to `[DB-Perf-01:run]`.
   One VM rebuilds and verifies all inputs, calibrates, then runs eight serial
   **1800-second effective windows**. Before starting, it requires at least the
   full 240 effective minutes plus cleanup/artifact budget within its 355-minute job.
   It never shortens a segment to fit a timeout.
4. Preserve each segment artifact immediately and the complete observations
   artifact even on failure. `summarize` revalidates every provider/route pair,
   1800-second window, SLO, identity, raw artifact hash, owner retirement, counts,
   environment and same VM boot before accepting 8/8 and at least 14,400 seconds.

Local dedicated-machine commands, with an external fresh build root:

```sh
python3 benchmarks/Database/campaign/bootstrap.py --output artifacts/db-perf-01 --work /absolute/external/fresh-build
python3 benchmarks/Database/campaign/campaign.py prepare --config artifacts/db-perf-01/config.json --output artifacts/db-perf-01
python3 benchmarks/Database/campaign/campaign.py smoke --config artifacts/db-perf-01/config.json --output artifacts/db-perf-01
python3 benchmarks/Database/campaign/campaign.py calibrate --config artifacts/db-perf-01/config.json --output artifacts/db-perf-01
python3 benchmarks/Database/campaign/campaign.py segment --index 1 --config artifacts/db-perf-01/config.json --output artifacts/db-perf-01
# Run indexes 2 through 8 serially on the same VM, then:
python3 benchmarks/Database/campaign/campaign.py summarize --config artifacts/db-perf-01/config.json --output artifacts/db-perf-01
```

Bootstrap needs the pinned SDK/toolchain, GitHub artifact read access, ordinary
build prerequisites, four available vCPUs, and permission to create a task-owned
cgroup v2 for PostgreSQL CPU accounting. It does not disable any controller or
access policy. The client uses two CPUs, PostgreSQL one and the observer one;
all sets are disjoint and recorded. PostgreSQL is stopped for SQLite runs.
Physical host exclusivity under VM virtualization is not claimed.

## Load, saturation and recovery

Three alternating independent processes measure short numeric and heavy streaming
lanes. The closed-loop concurrency ramp is 1, 2, 4, 8 and 16. Adaptive offered-rate
search brackets and refines the best observed concurrency, then requires three
independent 12-second qualifying points. This reports an empirical operating
point, not a mathematical global maximum. A common offered load is frozen for
before/after comparisons only after all four routes qualify under the same SLO:
error rate at most 0.001 and offered p99 at most 250 ms.

Fixed-rate requests are assigned global scheduled times independent of completion.
Reports retain planned, started, completed, failed, retried, never-started, unfinished,
queued and in-flight counts. Offered latency includes queueing. Saturation/recovery
uses one process: 10 seconds at an overloaded rate, then 20 seconds at the common
sustainable rate. Workers, owners, state and queued work continue across the
boundary; separate arrival-cohort histograms retain old backlog and late terminals.

Warmup, drain, open-owner idle, closure and closed-owner idle are outside effective
load. Native and C# emit the same nine phase markers. The external watchdog permits
30 seconds of drain plus 35 seconds of cleanup after a timed window, followed by
10 seconds of TERM grace before KILL. Setup/finite-operation probes have a separate
300-second limit; lifecycle probes have 90 seconds. Observer failure terminates
and reaps its child group and retains an explicit failed receipt. Identity, oracle,
cleanup and boundary failures stop the campaign; they cannot masquerade as capacity
limits in rate search.

## Allocation, CPU and memory interpretation

Managed bytes use cumulative whole-process `GC.GetTotalAllocatedBytes(true)` with
raw starts/ends before any division and all three GC counts. The mixed result
includes business DTO/string/list/array output, provider/ORM, runner and runtime;
no estimated framework fraction or empty-control subtraction is reported.
Public scalar probes and scoped production regressions provide narrower evidence.
Zero in a batch-local row interval is not zero for a complete query, refill,
transaction, connection lease or framework.

CPU user/system time comes from client process accounting; PostgreSQL is measured
separately in a task-owned cgroup whose counters include exited backends. SQLite
engine CPU is inside the client. Idle CPU has separate one-second observations
before and after owner closure. External 1 Hz and phase-boundary RSS/PSS snapshots
provide sampled steady/peak/retained observations; `ru_maxrss` is a distinct lifetime
high-water mark. Process identity includes start ticks to distinguish PID reuse.

A separate `libmemusage` companion retains malloc/calloc/realloc/free counts,
cumulative bytes and observed native heap peak including setup/warmup/cleanup.
The profiler and every count must be present and hash-bound. It does not intercept
all mmap/GC reservation mechanisms and is not relabeled as the exact native-heap
peak of an uninstrumented 30-minute window. No profiler is loaded during ranked
latency/throughput measurements.

Lifecycle outputs preserve capability gaps. In particular, the independent ORM's
SQLite token observation does not prove VM interruption. Korda's public SQLite
operation-owner count does not prove engine entry. The original Korda PostgreSQL
probe observed caller cancellation completing near the 5-second command deadline;
owner retirement and matching token do not establish which backend termination
mechanism won. Keep the raw observations rather than marking these as equivalent
native driver cancellation proofs.

## Recovery and provenance

Never overwrite an existing run folder or rewrite a failed raw record. Reuse only
results matching executable, loaded libraries, contract, workload and environment.
An interrupted VM cannot contribute the missing half of this same-VM 8-segment
campaign from a new boot. Completed local preflights remain useful at their stated
boundaries. `import-provenance.json` describes the historical workload import;
its original intermediate hashes are not current source or acceptance receipts.
