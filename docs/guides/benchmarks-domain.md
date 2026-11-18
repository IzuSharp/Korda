# Full domain workload measurements

## Finite recovery after an adverse operating point

The original high-rate calibration is preserved as adverse evidence. The
recovery uses [the finite operating-point plan](../../src/Benchmarks.Domain/operating-point-plan-v1.json)
and [cohorts.py](../../src/Benchmarks.Domain/cohorts.py), which delegates every
measurement and validity decision to the existing `run.py` and unchanged A21.
Only mutation, keyset and 1 MiB streaming families with observed busy or generator
failures receive a two-candidate search. Cold create, 8 MiB streaming, deliberate
upload overload and all nine diagnostic profiles retain their original inputs.
Both candidates execute once with two independent processes per compared route.
The highest complete valid candidate is frozen. If neither candidate passes,
the lower candidate is recorded as an unvalidated operating point, never as
demonstrated capacity.

After selection, each of the original 30 complete workload cases receives three
fresh calibration processes per supported route. A case receives three new
confirmation processes only when its whole calibration and measured noise bands
are valid. Every invalid case remains in the report, with its confirmation
explicitly not run. One adverse case cannot prevent unrelated cases from being
measured. There is no retry or additional tuning loop, no cross-fingerprint band
reuse and no claim that a lower offered rate repairs an original high-rate result.

```sh
python3 src/Benchmarks.Domain/cohorts.py prepare \
  --package-probe /absolute/path/to/accepted-b21-probe \
  --comparators /absolute/path/to/comparators.json \
  --instrumented /absolute/path/to/instrumented/manifest.json \
  --postgres-bin /absolute/path/to/postgresql-18.0/bin \
  --isolation-note "Recorded exclusive timing window; B22 load and builds stopped" \
  --output /absolute/path/to/new-cohorts
python3 src/Benchmarks.Domain/cohorts.py search --output /absolute/path/to/new-cohorts
python3 src/Benchmarks.Domain/cohorts.py matrix --output /absolute/path/to/new-cohorts
python3 src/Benchmarks.Domain/cohorts.py report --output /absolute/path/to/new-cohorts
```

The state file preserves completed units and their exact source/artifact pins.
An existing unfinished invocation directory is retained as interrupted; it is
not reused as a fresh successful process. Per-process receipts separately record
the observed peak client logical-operation concurrency and first/last-half
scheduled-arrival means. Configured client slots do not establish actual native
handler/database concurrency. The means disclose queue growth without inventing
a new performance acceptance threshold.
An inner controller's unestablished cleanup or explicit stop also blocks all
later outer invocations and preserves their exact unexecuted lists. Attempted,
completed and valid cohorts are counted separately. Partial invalid timestamp
records remain explicit unavailable observations; they cannot crash the report
or contribute fabricated zero durations.

For a text-only evidence handoff, preserve the observer snapshots alongside
the timing and cost projection. The analysis-only exporter retains the measured
Activity, logging, GC, callback, diagnostic-drop, queue and payload observations
from both boundaries, including unavailable and invalid rows:

```sh
python3 src/Benchmarks.Domain/export_diagnostics.py \
  /absolute/path/to/new-cohorts/report.json \
  --output /absolute/path/to/new-evidence/diagnostic-observations.json
```

It verifies the report/cohort binding and writes only fixed identifiers,
hashes and allowlisted numeric fields. It does not execute a target, change a
measurement, or replace the original local observations. Sampled peaks include
setup and can miss brief peaks; disabled listeners leave values unavailable.

The complete finite-cohort supplement preserves search, calibration and
confirmation process observations, explicit measured noise bands, unsupported
routes and unexecuted rows. It retains both boundaries of every diagnostic
snapshot through the same reviewed projection, all numeric application and
adapter resource samples, per-process histograms and additional search timing
rows. Use it after the timed execution has reached a terminal state:

```sh
python3 src/Benchmarks.Domain/export_cohort_observations.py \
  /absolute/path/to/new-cohorts \
  --output /absolute/path/to/new-evidence/all-phase-observations
```

The exporter checks the actual summary, observation and application-receipt
digests before projection. It exports no HTTP content, private configuration,
key, database, runtime trace or free-form log. Missing observations retain their
availability and identity; they do not become zero measurements. Both matrix
phases' raw numeric timing rows must accompany this supplement when creating a
compact evidence handoff. The fields `request_wire_bytes` and
`response_wire_bytes` belong to A21's JSONL IPC accounting, not HTTP or TLS wire
sizes. Mapping sizes describe virtual file mappings rather than resident code.

Schema version 2 represents application and adapter resource samples as explicit
column tables. The column names and interned availability/unit metadata permit
lossless reconstruction of the numeric snapshots; application phase and managed
probe references are retained. A null adapter sample and an absent sample list
remain distinct. Each invocation has its own observation file, and the manifest
binds every file's byte size and SHA-256. This formatting change does not rerun or
alter a measurement.

B23 measures the accepted B21 reference service as an assembled application:
authenticated HTTP, typed C# handlers and static JSON, Rust-backed queries and
transactions, durable replay/outbox writes, keyset pages, and bounded streamed
attachments. The application always uses the Rust Database and Server engines.
The direct Rust and managed Native AOT executables are comparison applications.

The governing inputs are B23 revision 0.4.0 and C-PERF-001/C-REFERENCE-001
revision 0.4.0 at Design commit
`c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9`. Their exact hashes are in
[`profile-v1.json`](../../src/Benchmarks.Domain/profile-v1.json).
B21 and A21 are reused as accepted inputs. B22 supplies separate fault/soak
correctness; B24 owns final release and pristine-package acceptance. Linux x64
measurements do not certify another operating system.

## Minimal use

Use the immutable B21 package-probe directory and the comparator manifest from
their recorded builds. The reference artifact was copied outside the checkout,
restored from exact NuGet packages, built ordinarily, and published for
`linux-x64` with Native AOT. Benchmark execution never rebuilds that artifact or
its Rust modules. Python requires the same `httpx`/HTTP2 support used by B21.

The [minimal consumer](../../samples/api/benchmarks-domain/minimal.py) invokes a
small **functional** comparison. It checks behavior and cannot establish a
performance result:

```sh
python3 samples/api/benchmarks-domain/minimal.py \
  --package-probe /absolute/path/to/accepted-b21-probe \
  --comparators /absolute/path/to/comparators.json \
  --output /absolute/path/to/new-functional-output
```

Each application launches as a separate process and announces its actual
listener URI. The adapter requires the selected HTTP/HTTPS scheme, negotiated
HTTP version, certificate validation for TLS, and a real Native AOT witness for
managed routes. An independent SQLite/psql observer reconciles the confirmed
operation scope, hash, exact response bytes, item/outbox cardinality and
attachment references. A direct file SHA-256 checks every distinct blob. This
is additional integrity checking of each measured dataset, not re-acceptance of
historical component tasks.

## Frozen advanced run

[`run.py`](../../src/Benchmarks.Domain/run.py) delegates all clocks, scheduled
arrivals, event accounting, histograms and statistical comparisons to the
accepted `src/Benchmarks.Harness/nativeweave_bench.py`. It introduces no timing
or percentile implementation. The HTTP adapter is an external client, never a
managed product host or database fallback.

Build the optional observation copy before any timed execution window:

```sh
python3 src/Benchmarks.Domain/build_instrumented.py \
  --package-probe /absolute/path/to/accepted-b21-probe \
  --output /absolute/path/to/new-instrumented-build
```

This command copies the exact accepted application sources and package locks,
adds one internal observation class and two explicit application hooks, then
publishes Release and Debug Native AOT executables with the fixed SDK. Every
original native-module SHA-256 must remain equal. Debug denotes an actual Debug
application AOT build; its accepted native and NuGet inputs still use Release
bytes. A standard portable x64 SDK profile is used, with no host-native ISA
override. This copy is distinct from the unchanged primary reference artifact.

After all three routes pass functional validation, coordinate an isolated window
with the B22 owner. No fault injection, soak, compiler or package production may
overlap that window. The controller records host load, affinity, CPU/OS/ISA and
available cgroup/power data; an isolation note records what the operator actually
controlled. It does not turn an unknown host power policy into a known one.

```sh
python3 src/Benchmarks.Domain/run.py --phase functional --cases all \
  --package-probe /absolute/path/to/accepted-b21-probe \
  --comparators /absolute/path/to/comparators.json \
  --instrumented /absolute/path/to/new-instrumented-build/manifest.json \
  --postgres-bin /absolute/path/to/postgresql-18.0/bin \
  --output /absolute/path/to/final-functional-preflight

python3 src/Benchmarks.Domain/run.py --phase calibrate --cases all \
  --package-probe /absolute/path/to/accepted-b21-probe \
  --comparators /absolute/path/to/comparators.json \
  --instrumented /absolute/path/to/new-instrumented-build/manifest.json \
  --postgres-bin /absolute/path/to/postgresql-18.0/bin \
  --isolation-note "Recorded exclusive window; B22 load and builds stopped" \
  --output /absolute/path/to/calibration

python3 src/Benchmarks.Domain/run.py --phase measure --cases all \
  --package-probe /absolute/path/to/accepted-b21-probe \
  --comparators /absolute/path/to/comparators.json \
  --instrumented /absolute/path/to/new-instrumented-build/manifest.json \
  --postgres-bin /absolute/path/to/postgresql-18.0/bin \
  --calibration /absolute/path/to/calibration \
  --isolation-note "Recorded exclusive confirmation window" \
  --output /absolute/path/to/confirmation
```

Each output directory must be new. Every process receives a disposable database
and content directory. PostgreSQL 18.0, applications and clients are launched
under the same controller process tree; the controller starts and stops its own
loopback cluster with `fsync`, synchronous commit and full-page writes enabled.
SQLite uses the declared WAL/FULL/foreign-key/short-writer profile. The comparator
manifest binds the same SQLite engine source/version rather than silently using
a different system library.

`--select` accepts workload IDs or full workload/provider/protocol IDs for a
bounded subset. A subset remains explicitly scoped and cannot stand in for the
remaining matrix. Failed or interrupted directories are retained. Changing any
profile, input, executable, lock or controller file requires a new calibration;
confirmation rejects a mismatched fingerprint. Do not combine old and new input
cohorts or silently remove a saturated generator cell.

## Failed cells and bounded cleanup

The HTTP adapter validates the whole declared logical result before it emits an
`ok` event. A cycle must confirm create, replay and recovery statuses and exact
response-byte equality. A malformed successful-status DTO, unexpected status,
wrong protocol, or streaming length/hash mismatch remains a failure. New HTTP
dispatch stops after the first integrity/transport failure; already admitted
requests retain their terminal observations. Expected diagnostic HTTP 500 and
the declared upload-capacity rejection retain their distinct workload meanings.

`http-failures.jsonl` records at most 32 distinct observed responses per adapter,
including the logical request ID, stage, method/path, actual protocol/status,
parsed header-value bytes and decoded body. Each record retains at most 16 KiB
of headers and 64 KiB of body, with the full observed lengths and SHA-256 hashes.
For an interrupted GET, `body_bytes_observed` and its hash cover only decoded
bytes actually yielded by the bounded iterator; `body_complete` is false and
the full `body_bytes` value is unavailable. A protocol rejection before body
consumption therefore records zero observed bytes. `body_truncated` describes
the evidence-file prefix cap separately from transport incompleteness. Bytes
buffered inside the HTTP client but never yielded are not reconstructed. The
GET response is registered before protocol/read checks; a failure before GET
headers has no response record and cannot be attributed to the preceding PUT.
Repeated reporting of the same response does not consume another record. These
are parsed HTTP response bytes, not captured HTTP/TLS wire packets. No request
authorization header is copied. `adapter-errors.jsonl` separately bounds detailed
exceptions to 32 records and truncates each traceback to 16 KiB. The adapter emits
only one short stderr notice, so failure logging cannot fill A21's bounded pipe.

Each unchanged A21 cell runs in `cell_worker.py`, under the external Linux
`supervision.py` controller. The watchdog budget is frozen in each plan from
A21's hello/warmup bounds, scheduled arrival span, request timeout and cleanup
timeout, plus five seconds of process overhead. Expiry or operator stop permits
five seconds for TERM, then at most two seconds for KILL observation and two for
the worker wait. Process ownership uses observed PID/start-time pairs and the
private per-cell registration chain; signals use pidfds in that execution's
Linux process namespace. Separately sessioned application children are included.
A live descendant, failed worker, unverifiable cleanup or incomplete cohort
cannot become a successful measurement. Later cells do not start if owned
processes remain or cleanup cannot be established.

To stop a controller safely, atomically replace `<output>/control.json` with
`{"stop": true}`. The controller checks it between cells and every 100 ms during
a cell, then records the failed/interrupted cell and the exact `not_run` list.
Retain the directory; a new run must use a new directory and its own frozen
inputs. The supervisor's worker log, ownership registry and cleanup receipt are
included in raw archives. Polling contributes controller host overhead; it does
not replace A21's request latency clocks. Forced process termination never proves
managed or native resource retirement, which remains separately observed by B22.

Reports retain invalid raw A21 observations and incomplete application receipts.
Missing or malformed worker-result identity, verdict, receipt or required
statistical fields become an invalid cell inside the guarded collection path;
they cannot leave the run falsely marked as still running or escape its ledger.
An otherwise valid subset of a declared three-process cohort cannot establish a
comparison or substitute for its missing repetitions. Original failed calibration
evidence remains immutable; source fixes require fresh calibration input hashes.

## Workload meanings

| Workload | Measured logical operation | Declared conditions |
| --- | --- | --- |
| Cold small create | One authenticated create and durable response | Fresh process/database; first business request; host page caches are not flushed |
| Warm low/high cycle | Create, same-key replay, then result recovery | Three HTTP calls per logical operation; new mutation key; bounded client concurrency |
| Large create | Create with a 1024-byte title | Same durable semantics; larger valid application payload |
| Keyset page | Typed query, materialization and JSON for 100 items | 128 real HTTP-created rows; explicit SQL fixture timestamps make ordering equal across independent IDs |
| 1 MiB / 8 MiB stream | Create, authorized attachment PUT, streamed GET and hash | 32 KiB client chunks; content reuse after first upload; new authorized item per operation |
| Upload overload | Slow request producer with one active upload permit | Actual `503 upload_capacity` rejection retained; product-specific budget, no unmatched comparator claim |
| Minimal / advanced DI | Same typed 100-row query in the same copied executable | Captured service versus service resolution through request DI; static JSON and disabled diagnostics equal |
| Diagnostic modes | Typed query followed by a controlled application failure | Real HTTP 500 is expected and separately counted; no business-success throughput claim |

Primary cells use SQLite and HTTP/1 plaintext. Extended cells include PostgreSQL
and both HTTP/1 TLS and HTTP/2 TLS. Unsupported route/profile combinations are
recorded before execution. Kestrel/ADO.NET preparation, buffering and runtime
differences remain explicit even when responses and durable effects match.
The product-only overload ledger is not claimed to be equivalent to a comparator
just because their maximum request sizes agree.
Its PUT client uses a fresh HTTP/1 connection with `Expect: 100-continue` and a
concurrent response reader, so a real early rejection can be observed before
sending the large body. It requires a complete actual `503 upload_capacity`;
transport resets without a response remain failures. Ordinary cells retain
their persistent HTTPX client profile.

Periodic outbox delivery is disabled equally during foreground timing. Every
confirmed mutation must still have one transactional outbox record. These cells
measure durable writes, not external publisher throughput. Stream cells use the
same deterministic bytes within a profile; repeated content-addressed reuse must
not be interpreted as unbounded unique-content storage throughput.

The reference application does not exercise every independent ORM family or
optional protocol facade. Joins/relations/aggregate/bulk, isolated typed-versus-
raw-SQL, query-construction-only, isolated JSON-only and explicit-chunk-versus-
Stream microbenchmarks are not represented by these rows. B23 does not replace
them with empty handlers or infer their performance from the assembled workload.
The exact typed Fluent, trusted parameterized claim SQL, serialization and
streaming paths used by the reference app are included in the end-to-end costs.

## Counters and diagnostics

The adapter samples the actual application PID separately from the Python client
and PostgreSQL process. CPU ticks, RSS, private working set, lifetime high-water
RSS, threads, file descriptors and sockets have their Linux procfs observation
methods recorded. File-backed virtual mappings identify loaded code/libraries;
virtual mapped bytes are not resident bytes. CPU is normalized per offered
logical operation, with rejected/failed outcomes still present.

The optional copied application observes standard GC totals and the existing
`NativeWeave.Server` Activity/Meter/ILogger integration. It does not add a product
diagnostic API or new native export. The modes are:

| Mode | Actual listener/sink configuration |
| --- | --- |
| `release-disabled` | Release AOT, no Activity/Meter/logger listener |
| `debug-default` | Debug AOT, no explicit listener |
| `debug-local` | Debug AOT, one-in-eight Activity sampling, Meter and warning/error logger |
| `sampled` | Release AOT, one-in-eight Activity sampling, Meter and logger |
| `full` | Release AOT, all Activities, Meter and logger |
| `meter-only` | Meter only |
| `log-only` | Configured warning/error logger only |
| `exporter-saturation` | Full observation, one blocked sink worker, enough failures to fill the bounded diagnostic queue |
| `exporter-failure` | Full observation, sink throws on each actual export attempt |

The report includes managed allocated bytes/op, GC heap/collections, CPU,
Activities, actual log calls/exports/failures, callback duration and queue/drop/
payload gauges when observed. The one-second Meter cache is respected; a declared
1.1-second settling interval happens after request and CPU measurement boundaries.
Sampled peaks can miss shorter peaks and include setup. A shortened functional
saturation cell verifies the blocking configuration only; the full timed cell
must observe positive bounded-queue drops.

No-listener profiles leave unavailable native/diagnostic counters unavailable;
they do not install a Meter behind the user's back. FFI calls, copied/pinned bytes,
native allocator live/retained bytes and native allocations/op cannot be derived
from RSS or GC totals. They remain `null` with an explanation. GC deltas include
observation boundaries and any concurrent app allocations. Payload credits cover
their documented charged owners, not the entire process or kernel TLS buffers.
Process exit and a flat RSS trace are not retirement or leak proofs; B22 owns those
independent observations.

## Statistical interpretation and report

Independent calibration and confirmation cohorts use the declared seed and
randomized route/process order. A case's noise band is the maximum measured
within-route range of independent calibration process means, with at least one
histogram bucket. Every executed route needs its required valid calibration
processes. There is no universal percentage target. A21 supplies its seeded
independent-process bootstrap intervals. Three process repetitions give limited
interval precision and are disclosed as such.

Latency begins at scheduled arrival. The report separately retains generator
dispatch delay, observed client queue/admission delay and successful service
duration. Adapter admission is not a native Server/database-pool timestamp.
Generator lag/capacity/deadline failures invalidate the offered-rate cell. Any
functional oracle failure stops new application HTTP dispatch and preserves the
failed cohort. Errors, actual rejections, cancellations, timeouts and Unknown
error codes remain in raw evidence; none becomes a successful performance row.

Percentiles use A21's sample-count rules; p99.9 is unavailable below 1000 samples.
Pooled percentile tables describe requests, while confidence intervals compare
independent process means. Report adverse outcomes and inconclusive comparisons
as observed. The first baseline does not establish a historical-release regression.
An A21 comparison labelled `regression` in this report means that the measured
route is slower than its comparison route under the declared measured-noise
rule. Preserve that classification and describe the observed route overhead;
it is not a comparison with a previous NativeWeave release. A functionally valid
cohort does not establish that the near-Rust performance goal has been achieved.

```sh
python3 src/Benchmarks.Domain/report.py /absolute/path/to/confirmation \
  --edit-probe /absolute/path/to/accepted-b21-baseline-and-edit-probe \
  --instrumented /absolute/path/to/new-instrumented-build/manifest.json \
  --output /absolute/path/to/new-report --archive
```

The output includes Markdown, machine-readable analysis and an optional raw
archive. Raw application receipts, resources, A21 events/histograms, source/lock/
binary pins, commands and failure records remain linked by SHA-256. Private bearer
configuration, TLS private keys and disposable databases are excluded from the
shareable archive. Keep the accepted immutable binaries/package evidence with
the corresponding task handoff.

Deployment reporting separates executable, native-module, symbol and metadata
bytes. The full consumer contains both providers. The Server native closure
includes HTTP/TLS/Tonic/rmcp code even when the optional facades are unconfigured.
Source counts are physical lines by role, not a productivity score; dependencies,
generator packages and unpersisted generated output remain disclosed. Recorded
B21 ordinary model/DTO/handler edit evidence is reused with unchanged native
hashes and no consumer Rust invocation. Available build durations are identified
as external or MSBuild-reported values; missing durations are not invented.

## Public API and documentation applicability

B23 owns internal tooling and comparison applications, with no new exported
application type or NuGet package. Exact public API/generator/XML compatibility
remains bound to the accepted consumed modules. The copied instrumentation
compiles against those actual packed APIs in both Release and Debug Native AOT.
Its private authenticated routes are benchmark-only and never shipped in the
reference application. [`api-reference.md`](../../src/Benchmarks.Domain/api-reference.md)
records this applicability. The minimal/advanced Python consumers invoke the
real compiled applications through the external controller; no Design stub
execution is represented as a product result.
