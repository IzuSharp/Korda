# Perf-01 database application measurements

Status: harness preparation. No measured result or completed long stage is
established by these files. Product behavior and packages are not modified here.
The explicitly selected compact-observation mode below changes only the external
controller's preparation, retention and evidence serialization.

## Measured path

The application is an exact B21 independent NuGet/Native AOT consumer of the
selected before/after domain closure. The external client uses real HTTP and
exercises C# handlers, typed Fluent queries, parameter binding, DTO materialization,
Rust SQLx, and durable PostgreSQL/SQLite transactions. These are **whole-application
latencies**, including HTTP, authentication, JSON and cleanup; they are not isolated
database execution, pool-wait or lock-wait measurements.

The A21 `run_cell` supplies all scheduled-arrival timing, generator
saturation accounting, histograms and percentiles. The unchanged B23 `Target`
supplies business checks and independent SQL persistence verification. B23's
external supervisor owns bounded cleanup. This controller runs one selected route
and repetition at a time; it does not claim unexecuted three-route cohorts.

| Scenario | Logical operation | HTTP calls | Correctness |
| --- | --- | ---: | --- |
| `cycle` | Create, same-key replay, authorized recovery | 3 | Exact response bytes and status; one item/operation/outbox effect |
| `page` | First keyset page of 100 typed rows | 1 | Exact ordered titles, versions and next-cursor presence |
| `mixed` | Alternating cycle/page, 50% each | 2 on average | Both checks; separate operation and tail counts |

All scenarios begin from the same 128 real HTTP-created seed rows. The independent
fixture sets their old timestamps, so newly committed writes do not alter the
first page. Each fresh process has a fresh database. SQLite keeps WAL/FULL, foreign
keys, `BEGIN IMMEDIATE`, and a five-second busy timeout. PostgreSQL is exactly 18.0
with fsync, synchronous commit and full-page writes on, and ReadCommitted
transactions. The reference application keeps 16 database connections, 64 waiters,
100-row/512-KiB batches and its existing bounded retirement rules.

No retry policy is added. `business_retry_attempts` is exactly zero; the requested
same-key replay is counted as business work, not hidden retry. Perf-01 short,
capacity and soak cohorts explicitly require `--continue-classified-overload`
on both versions. Subsequent real HTTP arrivals continue after a complete
HTTP 408/429/500/502/503/504 response or an HTTPX connect/read/write/pool timeout.
These observations are still failed operations, remain in the exact B23 error
evidence, and make the cell invalid under its unchanged zero-error criterion.
They are called service errors or client timeouts; an HTTP 500 does not establish
database BUSY or prove that overload caused the error. Per-request classifications
are retained in `classified-errors.jsonl`.

A successful-status payload violation, unexpected protocol, authentication or
validation response, or unclassified failure permanently stops measured HTTP
dispatch. A concurrent ordinary service error cannot reset that integrity halt.
The scoped adapter changes no B23 source or correctness oracle. Smokes can omit
the opt-in and retain historical fail-fast behavior, but their cancelled tail
does not establish sustained service overload. Failed, canceled and
generator-saturated cells cannot qualify as capacity.
After measured work drains, 32 new cycles at four/s check recovery in the **same
live application process**, followed by at least 60 seconds idle for a soak.
Recovery and idle are separately timed and excluded from measured load.

## Observer and package identity

Use the existing B23 exception observer on **both** NativeWeave versions when
provider-code counts are required. It copies the packed application source and
adds only catch/log/rethrow observation to existing middleware, then publishes
Native AOT with the same native hashes. It changes no database setting, retry,
transaction, response or package implementation. Its source and binary identities
are separate from the domain package identities.

```sh
python3 tests/Perf01.Database/prepare_observer.py \
  --package-probe /absolute/before-package-probe \
  --output /absolute/before-exception-observer
```

The package probe is built before timing, using the current maintainer feed:

```sh
python3 tests/Reference.TaskService/package_probe.py \
  --feed /absolute/before-feed --version 0.1.0-perf01.before.1 \
  --postgres-bin /absolute/postgresql-18.0/bin \
  --output /absolute/before-package-probe \
  --variants baseline --providers sqlite --modes aot --protocols http1 --build-only
```

The B24 packer historically retains eight common package archives when advancing
a candidate. Perf-01 shared-code changes must instead build a complete fresh
closure for each measured version; otherwise Core/Data.Streaming changes could be
absent from the measured package. The integration owner verifies every package
and loaded native hash. `perf01.json` hashes actual executable/`.so` procfs mappings.
Framework code compiled into an AOT executable is represented by that executable
hash, not a claim that its original managed DLL is dynamically loaded.

Provider-code counts come from the observer's actual stderr records. SQLite's
extended code is reduced with `code & 255`; primary 5 and 6 are separately reported
as BUSY and LOCKED. The count scope includes setup and recovery and is stated in
the receipt. Timed logical errors remain separately counted by A21. Without the
observer these counts are unavailable, never inferred from HTTP 500 or set to zero.
The zero application-retry count does not count SQLite's internal busy-handler
sleep/retry work under its existing five-second `busy_timeout`. Internal busy
handler attempts and time are unavailable in this external adapter.

Independent SQL snapshots before recovery and before shutdown also retain the
persisted mutation count, partitioned by seed, warmup, measured work and recovery
request keys. Item/outbox row counts are recorded alongside it. These snapshots
survive an exact durable-oracle failure and can reveal committed work whose HTTP
confirmation failed. They do not weaken the original content, cardinality or
response-byte oracle; a failed cell remains failed.

## Frozen operating-point proposal

The integration owner must freeze the final per-provider rate before product
changes. Suggested discovery ladder: 5, 10, 20, 40, 80, 160, 320 logical operations/s
at concurrency 16, with 1/4/8 concurrency checks around the observed queue knee.
Use `cycle`, `page` and `mixed` short cells on each provider; retain the first
overload and its same-process recovery. A largest passing sampled point is an
observed lower bound on sustainable capacity, not proof of a universal maximum.

The controller defaults are a declared p99 limit of 500 ms, zero unexpected
errors, application peak RSS at most 256 MiB, generator dispatch lag at most
100 ms, finite 512 pending requests and a 30-second request deadline. These are
proposed Perf-01 measurement constraints, not product support guarantees. Freeze
them with the common machine/affinity and workload ratio. Any change requires a
new cohort and cannot retrofit a passing verdict to old samples.

```sh
taskset -c CPU_A,CPU_B python3 tests/Perf01.Database/run.py \
  --package-probe /absolute/before-package-probe \
  --observer /absolute/before-exception-observer/manifest.json \
  --provider sqlite --scenario mixed --stage short --variant before \
  --continue-classified-overload \
  --rate 40 --seconds 20 --concurrency 16 --repetition 0 \
  --output /absolute/before-sqlite-mixed-40-process0 \
  --isolation-note 'Actual exclusive host window; all builders and other loads stopped'
```

Replace the rate with the frozen, actually calibrated value for the long stage:

```sh
taskset -c CPU_A,CPU_B python3 tests/Perf01.Database/run.py \
  --package-probe /absolute/before-package-probe \
  --observer /absolute/before-exception-observer/manifest.json \
  --provider sqlite --scenario mixed --stage soak --variant before \
  --continue-classified-overload \
  --rate FROZEN_RATE --seconds 1802 --concurrency 16 \
  --output /absolute/before-sqlite-soak \
  --isolation-note 'Actual exclusive host window; fixed two CPU IDs and no concurrent timing'
```

PostgreSQL adds `--provider postgresql --postgres-bin /absolute/postgresql-18.0/bin`.
The cluster and applications run in the same invocation/process tree. Reuse the
same protocol, fixed arrivals, concurrency, payload, seed and limits after
optimization. Comparator cells add the exact comparator manifest and
`--route direct-rust --variant direct` or `--route managed-aot --variant managed`.
The comparator path retains its narrower B23 cleanup/capability caveats.

### Optional compact observation

`--compact-observation` explicitly selects the external-controller changes below on
all routes of a new Perf-01 DB cohort. Without the flag, A21 and its direct
`RunState` callers retain the original behavior. The flag is recorded in the
target manifest, plan features and frozen inputs, and passed to the existing
supervised cell worker. It must match before/after/direct/managed cells; old
driver hashes or calibration cells cannot be silently rebound to this mode.

Before target startup and timing, the dataset is generated incrementally with
the same formatted JSON bytes as the original list writer. The existing bounded
JSON-array reader loads each entry, validates it, and shares identical read-only
expected results before appending the entry to the oracle. All entries are still
validated before measurement; input tokens, dataset hash and live equality checks
are unchanged. The temporary sharing cache permits at most 1024 unique keys and
4 MiB of key bytes, with at most 64 KiB/key, 4096 traversal nodes and depth 64.
Values that exceed a bound keep their independent objects. The reader source is
part of the frozen input hashes and calibration-controller pins.

During timing, every completion is parsed, ordered, checked against the oracle
and counter schema, and accepted or rejected by the original rules. The
controller retains scalar operation state, observer timestamps, one last-counter
snapshot, a byte flag and an int64 raw-line index per scheduled operation.
Per-record result/counter objects and full late-event payloads are read again only
after target/reader/sampler drain and after the original measurement endpoint is
fixed. Replay matches the accepted ID, observer timestamp and raw-line index;
same-timestamp duplicates cannot replace an accepted result. An incremental
SHA-256 of the exact live-written UTF-8 log binds replay to the original bytes;
changing a valid counter, failed result or late status cannot pass by retaining
the same event identity. Malformed and
unaccepted records are skipped. Missing or invalid accepted payloads fail the
cell. The streamed finalizer first exhausts these checks and builds a finite
offset/length/per-line-digest index. It then writes reconstructed rows in original
ID order and late events in original arrival order, with bounded per-row formatted
emission. Metadata and resource arrays use incremental JSON encoding. Per-line
digests protect indexed reads; a second whole-raw-file digest check includes all
unaccepted and malformed bytes before the temporary observation is atomically
published. A failed check cannot publish a final observation. Replay and output
are each one-shot, including failed attempts.

The complete on-disk observation retains the original schema, values, key order,
pretty JSON bytes and exact raw wire evidence. Only this opt-in worker asks A21
to return scalar in-memory records after writing the complete file; default A21
callers and the original full-rehydration helper keep their full return objects.
The profiling receipt labels that distinction explicitly. The DB parent streams
the observation and keeps the scalar fields used by the unchanged B23 shape
validator and Perf-01 summary calculations. It skips histogram bins structurally
while retaining their scale and the exact saved percentiles.

Memory remains finite and proportional to operation count: scheduled scalar
records, oracle input entries, timestamp/histogram work and resource samples are
retained. The post-window index adds offsets, lengths, digests and late IDs. This
is not constant-memory execution or a measured upper bound on finalization RSS.
Iteration can briefly retain the prior row while decoding the next; the claim is
bounded per-row payload emission, not exactly one live payload allocation.

Replay can increase post-window CPU/RSS and serialization time. Those costs are
not reported as application CPU or included in an operation latency/window.
This mode does not disable or force GC, change a GC threshold, alter an arrival,
deadline, capacity limit, correctness oracle, or database/application setting.
It is a measurement-tool candidate, not a product performance improvement.
Run the finite A21 and compact parent gates, then repeat the diagnostic/calibration
with fresh source hashes before freezing any formal DB cohort:

```sh
python3 -m unittest discover -s tests/Benchmarks.Harness -v
python3 tests/Perf01.Database/test_streaming.py -v
python3 tests/Perf01/test_verify_long.py -v
```

### Optional generator CPU isolation

Launch the controller on its two **service** CPUs first. Adding
`--generator-cpus 0,1` uses two separate CPUs for the A21 scheduler and HTTPX
adapter while keeping the actual application and PostgreSQL on the original
service mask. For example, `taskset -c 2,3 ... --generator-cpus 0,1` assigns the
application/database to 2,3 and the generator to 0,1. The two sets must be
disjoint and each must contain exactly two CPUs. A different affinity layout
requires a new calibration/frozen cohort; do not compare it with old shared-CPU
measurements as though the resources were unchanged.

The PostgreSQL cluster starts before the controller changes its own mask. A21
and the HTTPX adapter subsequently inherit the generator mask. Only the actual
application command is wrapped with the existing, hash-pinned `taskset`, which
execs the application before its .NET/Rust threads start. No Python target thread
temporarily adopts the service mask. The real application executable is hashed
through `/proc/<application-pid>/exe` after READY, independently of that launcher.
Existing route artifact/package/native pins remain in force.

Frozen inputs record both masks and the launcher hash. `perf01.json` captures
actual application, HTTPX adapter, A21 scheduler and currently live PostgreSQL
descendant/thread masks after seeding, after measured load and after idle. Main
thread masks must match exactly; runtime-pinned worker threads may use a subset
but cannot run outside their declared mask. Controller before/end/restored
observations are separate files. PostgreSQL postmaster start identity is checked
to avoid confusing a reused PID. These are live snapshots, not continuous
observations of every transient thread/backend.

Without the option the driver performs no affinity mutation. The separate
PostgreSQL attribution script does not supply the option and retains its existing
CPU behavior. No package rebuild is needed for this harness-only setting.

Prepare fresh comparator executables instead of using the historical absolute
paths in B23's checked-in manifest:

```sh
python3 tests/Perf01.Database/prepare_comparators.py \
  --product-root /absolute/committed-NativeWeave \
  --amalgamation /absolute/cargo-registry/libsqlite3-sys-0.37.0/sqlite3 \
  --output /absolute/Perf-01-comparators
```

The producer copies the unchanged B23 source, linked protocol/filesystem code,
SQL resources and locked build inputs into its own tree. It builds the managed
route with SDK 11.0.100, locked dependencies and explicit `x86-64-v2` Native AOT,
and the direct Rust route with Rust 1.100.0, generic x86-64, thin LTO, one codegen
unit and unwinding. Its SQLite shared library uses the same checked amalgamation
hashes and SQLite 3.51.3 as `libsqlite3-sys` 0.37.0. The resulting
`comparators.json` records fresh absolute source/lock/artifact hashes and is
accepted by `--comparators`. Builds are isolated from product worktrees and do
not change a runtime or package source file.

This preparation executes and compares actual golden vectors, including the
managed executable's Native AOT witness. It does **not** import historical
HTTP/SQL readiness or claim performance acceptance. The normal B23 functional
probe and the Perf-01 application oracles establish those separate facts against
these exact new binaries. Producer command durations are setup observations and
are not service latency measurements.

## Duration, evidence and interpretation

Formal Perf-01 before/after soaks prospectively use `--seconds 1802`. The driver
retains its additional 250-ms scheduling guard and period rounding, and its
original `actual_dispatch_span_seconds`/`duration_valid` receipt fields. Those
timestamps are controller dispatch-start observations before command encoding and
IPC send, not exact socket-send times. Their at-least-1800-second span remains a
necessary check and rejects a late-first-dispatch 1,799-second stage.

Final acceptance also uses `tests/Perf01/verify_long.py`: the last successful
ordinary operation's controller dispatch-start minus the earliest successful
ordinary operation's observed completion must be at least 1800 seconds. Completion
follows actual business work; the later command's work cannot precede its
dispatch-start. This gives a conservative real-operation span. The two-second
schedule guard never assumes a pass. Warmup, recovery, protocol probes, idle and
shutdown do not extend those endpoints. Every process must finish normally,
pass independent durable oracles and show complete valid generator accounting,
the unchanged service/resource limits, recovery and resource drain. A new
verification receipt supplements the preserved driver result; elapsed wall time
or a scheduled duration alone cannot make a stage pass.

Each invocation requires a new output directory. It writes frozen source/tool/
artifact/client/configuration hashes, raw A21 records/events, application and
generator resources, independent SQL reconciliation, shutdown receipts and a
compact summary. Repeating an interrupted stage requires a new directory; a
complete prior exact-identity stage may be reused by the integration ledger.
Set the existing `control.json` to `{"stop": true}` for bounded stop and keep all
failed evidence. Private generated configuration/TLS keys remain local.

`summary.json` separates successful throughput, p50/p95/p99, per-kind counts,
planned/dispatched load, actual duration and SLO eligibility. CPU/RSS samples
describe the application process and generator separately. PostgreSQL backend
CPU/RSS time series are unavailable in this timed driver; its PG process-tree
snapshots establish identity/affinity only. SQLite engine CPU/RSS is included in
the application process and is not separately attributed. Database execution/lock/pool timing,
copy bytes and FFI counts remain unavailable in this whole-app adapter. The
shared-path profiler and layered probes must establish any bottleneck attribution.
Short-process mean/noise comparisons use A21's existing `noise_comparison` over
independent interleaved processes; a single long stage is not a confidence interval.

### Independent resource fields and generator roles

The scoped `perf01_resources.py` helper retains A21's memory observations and
reads CPU, file descriptors and main-thread affinity independently. A descriptor
that closes during enumeration cannot erase valid CPU or RSS evidence. Read
errors remain named per-field errors and unavailable values remain null. FD and
socket counts are non-atomic observations; disappeared entries are counted as
`descriptor_scan_misses`, including the self-enumeration descriptor when present.
The historical A21/B23 files are unchanged. Only this benchmark target process
substitutes its application observation function; the existing nominal 20 Hz
application sampler and request timing are retained.

The target additionally writes `generator-resources.jsonl` at nominal 1 Hz,
including a final observation, with roles `target` (HTTPX client/adapter),
`scheduler` (A21 cell worker), and `controller` (this driver's supervisor).
Each role is bound to an actual PID and procfs start time; rows retain phase,
monotonic/UTC timestamps, CPU ticks, A21 memory fields, descriptor counts and the
observed main-thread CPU mask. The controller identity, sample intervals and
helper hash are frozen before launch. Actual timestamps establish the observation
interval; the sampler does not infer missed samples or per-thread CPU use.

`generator-observer-receipt.json` is written before observation and on completion;
raw batches are flushed as they are written. The sampler is stopped and joined
when the target finishes, including failure cleanup. A killed process leaves its
partial records and running receipt. An observer task failure cannot yield a
passed Perf-01 receipt. Individual unavailable procfs fields are recorded without
discarding other observations. A definite role identity mismatch fails the
observer; rows whose bound identity could not be verified remain raw evidence
and are excluded from that role's attributed aggregates.

`summary.json` exposes `generator_resources.<role>.<phase>` with `cpu_seconds`,
`cpu_percent_one_core`, `cpu_span_seconds`, `rss_peak_bytes`, `fd_peak_count`,
`socket_peak_count`, sample errors, PID/start identities and observed masks.
Application phases keep their existing `app_cpu_seconds` and
`app_cpu_percent_one_core` aliases under `resources`. CPU deltas use the first
and last CPU-bearing samples for the same process identity, independently of RSS
peaks. Fewer than two valid CPU observations produce null CPU summaries. These
observations do not change arrivals, functional/persistence oracles, resource
caps, dispatch-lag thresholds or latency acceptance criteria.

## Separate PostgreSQL attribution profile

`profile_postgresql.py` reuses the same unchanged package application, typed
query/business workload, private PostgreSQL 18.0 supervisor and full durability.
It enables `log_min_duration_statement=0` only in that disposable cluster and
samples `pg_stat_activity` through one independent psql connection. The script
does not build or modify application/runtime packages. Run it only in an
exclusive profiling window, before or after an uninstrumented cohort:

```sh
taskset -c CPU_A,CPU_B python3 tests/Perf01.Database/profile_postgresql.py \
  --package-probe /absolute/before-package-probe \
  --observer /absolute/before-exception-observer/manifest.json \
  --postgres-bin /absolute/postgresql-18.0/bin \
  --scenario mixed --rate FROZEN_RATE --seconds 20 --concurrency 16 \
  --wait-sample-ms 100 --output /absolute/before-postgresql-profile
```

Raw `postgres/server.log` is retained. Parse, Bind, Execute and simple-statement
durations are separated; three protocol-step records are not three business
queries. The summary groups by phase and SQL first log line, and excludes psql
oracle/observer queries from application aggregates. Full multiline SQL remains
in the raw server log. Bind parameter logging is disabled. The wait file records
actual observer query start/end timestamps and server timestamps, so observer
query time is visible rather than treated as an exact fixed sampling interval.

Server elapsed time includes lock/I/O waits and may overlap across connections;
it is not database CPU time and cannot be subtracted from aggregate application
wall time to infer FFI overhead. Activity snapshots provide sampled wait
occupancy, not exact per-request lock duration. Connection-pool waits and SQLite
execution/lock duration remain unavailable in this adapter. Both logging and
polling add overhead, and the output is always marked `formal_timing_eligible:
false`. The source contract was checked against the locked PostgreSQL 18.0
`doc/src/sgml/config.sgml` duration/parameter logging entries and
`doc/src/sgml/monitoring.sgml` activity/wait-state caveat.
