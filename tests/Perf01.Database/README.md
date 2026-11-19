# Perf-01 database application measurements

Status: harness preparation. No measured result or completed long stage is
established by these files. Product behavior, packages and A21/B23 machinery are
not modified here.

## Measured path

The application is an exact B21 independent NuGet/Native AOT consumer of the
selected before/after domain closure. The external client uses real HTTP and
exercises C# handlers, typed Fluent queries, parameter binding, DTO materialization,
Rust SQLx, and durable PostgreSQL/SQLite transactions. These are **whole-application
latencies**, including HTTP, authentication, JSON and cleanup; they are not isolated
database execution, pool-wait or lock-wait measurements.

The unchanged A21 `run_cell` supplies all scheduled-arrival timing, generator
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
same-key replay is counted as business work, not hidden retry. An error stops new
application dispatch through the existing B23 fail-fast oracle. Failed, canceled
and generator-saturated cells remain invalid and cannot qualify as capacity.
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
  --rate FROZEN_RATE --seconds 1800 --concurrency 16 \
  --output /absolute/before-sqlite-soak \
  --isolation-note 'Actual exclusive host window; fixed two CPU IDs and no concurrent timing'
```

PostgreSQL adds `--provider postgresql --postgres-bin /absolute/postgresql-18.0/bin`.
The cluster and applications run in the same invocation/process tree. Reuse the
same protocol, fixed arrivals, concurrency, payload, seed and limits after
optimization. Comparator cells add the exact comparator manifest and
`--route direct-rust --variant direct` or `--route managed-aot --variant managed`.
The comparator path retains its narrower B23 cleanup/capability caveats.

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

A soak schedules just over 1,800 seconds (250-ms guard plus period rounding) and
requires at least 1,800 seconds between the **actual first and last dispatches**.
It rejects a late-first-dispatch 1,799-second stage. Warmup, recovery, idle and
shutdown receive no load-duration credit. Every process must finish normally,
pass durable oracles and show valid generator accounting; elapsed wall time alone
cannot make a stage pass.

Each invocation requires a new output directory. It writes frozen source/tool/
artifact/client/configuration hashes, raw A21 records/events, application and
generator resources, independent SQL reconciliation, shutdown receipts and a
compact summary. Repeating an interrupted stage requires a new directory; a
complete prior exact-identity stage may be reused by the integration ledger.
Set the existing `control.json` to `{"stop": true}` for bounded stop and keep all
failed evidence. Private generated configuration/TLS keys remain local.

`summary.json` separates successful throughput, p50/p95/p99, per-kind counts,
planned/dispatched load, actual duration and SLO eligibility. CPU/RSS samples
describe the application process and generator separately; PostgreSQL backend
resources require the integration profiler. Database execution/lock/pool timing,
copy bytes and FFI counts remain unavailable in this whole-app adapter. The
shared-path profiler and layered probes must establish any bottleneck attribution.
Short-process mean/noise comparisons use A21's existing `noise_comparison` over
independent interleaved processes; a single long stage is not a confidence interval.
