# Direct native campaign query adapter

This source consumes the shared `../contract-v1.json` through libpq and sqlite3.
It is separate from the earlier minimal probe in `../../native` and does not
change that probe's validated source. The scheduler owns admission, timing,
per-case outcome accounting and result files; this adapter owns a single worker
connection and complete query execution.

## Modes and result ownership

Dynamic mode assembles the manifest's SQL clauses inside each operation, prepares
the physical statement, binds parameters, materializes all rows, consumes the
business result and releases the statement. Prepared mode retains at most one
physical statement per case or business step per physical connection. The total
cache is bounded to 2048 entries, and the admitted contract is limited to 128
cases and 16 steps per business case. It still binds, executes, materializes,
validates, drains and resets each operation. `prepare_all()` can warm the finite
statement cache without executing any query.

**Prepared here means physical statement reuse.** Current Korda compiled-plan
reuse has a different physical preparation boundary. Preserve this distinction
in comparison receipts and add a matched lifecycle layer where needed.

The adapter implements the thirty original read shapes plus the large-stream
and absent-optional-filter variants. It allocates owned typed C++ DTOs for the
reference-type rows, owned strings and a typed Settings object. Money is decoded
to exact signed cents with fixed scale two. PostgreSQL binary numeric is parsed
without decimal-to-text conversion or rounding. Scalars retain value semantics.

Related results retain child DTOs in an owned vector for every input parent,
including empty groups. The final input-position and child-count digest follows
the child-ID sort used by the managed business consumer. It does not substitute
a flat provider-byte checksum for the requested grouped result.
The bound parent ordinal is an Int32 (PostgreSQL OID 23 and four network-order
bytes); tenant/order keys are Int64 (OID 20 and eight bytes). Result metadata
checks the ordinal's Int32 type before reading its binary value.

The four state cases implement transaction commit, rollback, partial update,
and expected stale-version rejection. They use each logical worker's exclusive
`perf_state` row, identified by `worker_id + 1`, even when that worker borrows a
different pooled connection. Each SELECT creates an owned typed state row,
checks identity and the complete expected state, then hashes Counter, Version,
and nullable Note. Each mutation checks its affected-row count. The stale-version
case succeeds only with the expected zero affected rows and `conflict` outcome;
it is not evidence of a genuine concurrent race.

The Settings SAX parser implements this fixed workload's Channel/Gift object.
It is not a general substitute for all System.Text.Json behaviors. The manifest
validates names, types, nullability, result count and digest; actual database SQL,
parameter and plan equivalence still requires execution evidence.

## Connections, errors and metrics

One adapter owns one physical connection and is not called concurrently. A
bounded lease may transfer it between logical workers. Its Contract
must remain immutable and alive until all associated adapters have closed.
SQLite uses WAL/FULL, checks effective pragma values, and checks dataset metadata.
PostgreSQL records effective isolation/durability settings, uses the fixed local
TLS-disabled connection policy, consumes binary single-row results and checks
the final idle transaction state. The default product confirmation layer keeps
the product's separate description and retirement Sync boundaries; `drain-only`
is an explicitly different layer.

An unsuccessful operation is never retried internally. The adapter releases its
client owners and discards the connection; the next explicitly admitted call
reconnects and prepares its bounded cache as needed. The scheduler can therefore
continue after an error without reusing an uncertain protocol state. It must
count the failed attempt, reconnect time and any separately requested retries.
For business operations, exceptional cleanup first drains the pending command
and rolls back an active transaction with its own bounded retirement deadline.
If cleanup also fails, both exception owners and their diagnoses are retained
in a cleanup failure, the connection is discarded, and the run cannot qualify.

Counters distinguish SQL executions, protocol submissions, physical preparation,
description, confirmations, waits, fetched/consumed rows and connection activity.
They do not count physical network round trips. Live-owner counters cover only
the explicitly tracked connections, SQLite statements and PGresults; an
unconfirmed SQLite close is separately visible. They are not allocation/free or
heap-peak metrics. Whole-process native allocation profiling remains a separate
pending measurement lane.

Real-provider validation of every version remains necessary. Early-stop, caller
cancellation, and genuine concurrent-conflict lanes use the separate
`correctness.cpp` entry described below.
An operation deadline is not evidence of caller cancellation after genuine async
suspension. This adapter must not be presented as complete DB-Perf-01
coverage, and no qualifying performance result follows from a successful build.

## Build

Use explicit locations for the locked compiler, source trees and original build
archives. The build script verifies selected hashes, Release compile metadata,
SQLite feature definitions, and patched libpq provenance. Reusing the recorded
SQLite archive is disclosed; the script does not independently rebuild it.

```sh
python3 benchmarks/Database/campaign/native/build.py \
  --clang /path/to/llvm-23.1.5/bin/clang++ \
  --sqlite-source /path/to/sqlite-amalgamation-3510300 \
  --sqlite-archive /path/to/linux-x64-release/libnw_sqlite_engine.a \
  --sqlite-build-dir /path/to/linux-x64-release \
  --postgres-prefix /path/to/postgres-tools/install \
  --json-source /path/to/nlohmann-json/json \
  --output /path/to/new-native-build \
  --validate-only
```

Remove `--validate-only` to build the adapter object. Add
`--main benchmarks/Database/campaign/native/main.cpp` to link the scheduler. Each
attempt requires a new output directory and preserves a build receipt, command
logs and source snapshots. Compilation reads those copied C++ sources and their
copied local header, rather than live files that can change after hashing. This
command does not start a database or workload.
Call `runtime_identity()` before the run and retain its result, the actual
executable and loaded library hashes, the contract hash and effective settings.

## Scheduler entry

`main.cpp` implements the common campaign CLI:

```sh
db-perf-native --provider sqlite \
  --manifest /path/to/frozen/contract-v1.json \
  --connection /path/to/dedicated/campaign.sqlite \
  --output /path/to/new-result.json \
  --mode dynamic --session-mode per-operation \
  --concurrency 4 --warmup 64 --seconds 10 --rate 100 \
  --case mixed
```

Use a PostgreSQL connection string with `--provider postgresql`. The program
never initializes the dataset and refuses to overwrite an existing result file.
`--exclude` accepts comma-separated case names. `--verify-only` executes every
selected correctness oracle but produces no performance observation.
The parsed contract remains shared, but each adapter allocates plan-cache slots
and parameter scratch capacity only for the selected cases. Verification and
warmup execute that same selected mix. Effective-configuration receipts expose
the selected-case count, statement-slot capacity and maximum parameter count.

Each worker gets a physical connection. Persistent mode retains that connection
for the worker; per-operation mode borrows and returns a connection from a fixed
pool with the same capacity. Borrow/return cost is inside operation timing.
The command, connection-recovery and pool-wait limits are each five seconds.
Each SQL command's limit covers preparation, all rows and protocol confirmation,
rather than restarting for every fetched row. A multi-command business operation
also observes the scheduler's absolute drain deadline. Exceptional rollback gets
a separate five-second retirement budget, and late retirement invalidates the
sample. Connection recovery uses libpq's
nonblocking connection poll and result APIs.

For fixed rate, exactly `floor(rate * seconds)` requests are planned with global
sequence `worker + iteration * concurrency`. Case selection uses that sequence,
and late workers retain their queued requests. Service latency begins at actual
start; offered latency begins at planned start. Closed loop (`--rate 0`) starts
the next operation after its predecessor and claims each case from a shared
atomic sequence. This prevents heavy cases from remaining attached to one worker
when the concurrency and mix length have a common divisor. `--operations N` selects a finite
closed-loop count, makes `--seconds` inactive, and is rejected with fixed rate.
Scheduling uses absolute `CLOCK_MONOTONIC` `clock_nanosleep`, retries EINTR,
and sleeps at most 50 milliseconds per worker slice so abort is observable.
It uses the same blocking policy as the C# runner and has no spin tail.

`--recovery-rate R --recovery-after T` changes a fixed-rate run into two arrival
phases inside the same process. `--seconds` remains the full window, `--rate`
is the first rate, and the exact plan contains `floor(rate*T)` requests followed
by `floor(R*(seconds-T))` requests. Both options are required together; both rates
must be positive, `0 < T < seconds`, and each phase must plan at least one request.
This mode cannot be combined with an operation-count override. The global case
sequence, worker threads, physical connections and bounded pool continue through
T without a reset, drain or cancellation.

The optional `arrival_phases` receipt contains `saturation` and `recovery`
request cohorts. These labels describe the requested schedule, not proof of
observed saturation or recovery. Requests belong to the phase of their planned
arrival. Each phase's primary counts and histograms stop at its own end; its total
histograms retain delayed successes through the final global drain deadline.
Initial-phase backlog therefore stays attributed to the initial cohort when it
completes during recovery. The global per-second series shows those completions
and permits reconstruction of total backlog across the transition. Final
unstarted/unfinished counts always use the global drain boundary. A slow overall
recovery-cohort p99 alone does not distinguish initial backlog clearance from a
failure to recover later; retain the complete time series. The ordinary schedule
allocates no phase histogram storage and emits an empty phase array.

All selected cases are validated on the real connections before warmup and before
the measured window. `--empty-driver` still performs this validation and warmup;
only measured operations use the already-known expected result. It is a harness
control, not a database performance result.

The duration window has a fixed cutoff. Queued operations may drain for thirty
additional seconds; a terminal result that arrives after that deadline remains
unfinished at the deadline and cannot become a successful sample. Threads retire
safely before owners are disposed. A drain overrun is recorded as an invalid
measurement; the external runner still needs its final watchdog.

The receipt preserves raw histogram bins, per-case/per-second counts, scheduled
and actual starts, failures, late starts, queued/in-flight work at cutoff,
post-cutoff completions and unstarted/unfinished work. Primary throughput and
latency include only successes completed inside the effective window; the total
lane includes bounded drain. Native allocation/free and complete heap peak stay
explicitly unmeasured.

After operations drain, the program observes one second with idle open
connections, closes the pool, and observes another idle second. These intervals
and warmup are outside the effective load. CPU and process-memory snapshots have
separate window, drain, open-idle, close and closed-idle boundaries. Workers remain
alive until the open-owner idle observation ends, then join before owner closure.
`DB_PERF_PHASE READY/START/END/DRAINED/IDLE_OPEN/IDLE_OPEN_DONE/CLOSED/IDLE/IDLE_DONE` markers
support an external client/server sampler. END follows the effective cutoff
counter; DRAINED follows retirement of admitted APIs; IDLE_OPEN_DONE ends open-owner
idle before workers or owners are disposed; IDLE starts the closed-owner
idle interval and IDLE_DONE ends it. Metric JSON is created after measured work.

Ordinary database/connection/timeouts remain counted in a
`completed_with_failures` receipt and are evaluated by the external SLO policy.
Validation errors, incomplete accounting, cleanup failures and drain overruns
produce an invalid receipt and exit code 2. Successful observation, including a
fully accounted run with ordinary provider failures, returns 0. No SLO acceptance
or complete DB-Perf-01 acceptance is asserted by that exit code.
`can_qualify` also requires a measured window of at least 1800 seconds, a timed
run, and a real driver. A successful short or finite-operation check is not an
eligible long-run segment. `errors_normalized` contains the shared seven-category
totals; per-worker observations retain first raw category/code/message details.

Actual compiler and two-provider correctness gates are established only by their
version-bound build and execution receipts. The implementation itself is not
evidence that any performance or zero-allocation target has been reached.

## Separate lifecycle correctness executable

Build with the same `build.py` input arguments, but select
`--main benchmarks/Database/campaign/native/correctness.cpp` and a fresh output
directory. The resulting `db-perf-native` is a different executable from the
scheduler; retain both hashes and keep the directories separate. Invoke it with
`--provider`, `--connection`, `--manifest`, and a fresh `--output` JSON path.
It does not run a load window or emit campaign phase markers.

The probe owns the dedicated `perf_lifecycle` table and restores its single row
to `(Id=1, Counter=0, Version=1)`. It does not mutate `perf_state` or the order
dataset. Run it exclusively against the fixture; another lifecycle probe must
not use the same table concurrently.

- Two real worker threads and physical connections each read the initial
  version, wait behind a barrier, and attempt conditional updates. The probe
  requires one affected row in total, one stale result, and persisted `(1,2)`
  counter/version. Actual update-call intervals and whether they overlapped are
  retained; there are no implicit retries.
- The frozen 8192-row stream consumes one business value, retires the stream,
  and runs a normal query on the same physical connection. SQLite finalizes its
  statement immediately. Direct libpq must explicitly drain and clear its
  remaining PGresults before reuse; those 8191 provider rows are recorded as
  cleanup reads, not business materialization.
- SQLite cancellation starts a recursive query on one thread, observes actual
  VM progress while `sqlite3_step` is still incomplete, and calls
  `sqlite3_interrupt` from the controlling thread. PostgreSQL cancellation
  observes the target backend as active in `PgSleep`, confirms libpq is still
  busy, and uses the nonblocking `PQcancelCreate/Start/Poll/Finish` API. Each
  requires the provider's cancellation code before the command deadline and a
  successful subsequent query on the same connection. A timeout or a token
  requested before execution cannot satisfy these checks.

Explicit connection, statement, PGresult and cancellation-connection owners
must return to zero. These are lifecycle counters, not a complete native
allocation trace. This standalone diagnostic is intentionally outside the
normal mixed benchmark and cannot qualify as a long-run segment or a zero-GC
measurement.
