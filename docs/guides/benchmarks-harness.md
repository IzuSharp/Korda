# A21: benchmark controller and resource observations

This component provides the external load controller required by the pinned
`C-PERF-001@0.4.0` contract. It runs separately from target processes. It adds no
application API, native runtime, NuGet dependency, or managed database/server
fallback. The guide and executable fixtures belong to A21; B23 owns actual
Database/ORM/Server comparisons and B22 owns domain fault/soak oracles.

Design input: `IzuSharp/NativeWeave-Design` at
`c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9`,
`docs/performance/methodology.md` and `docs/platforms/support-and-aot.md`.
Task input: `IzuSharp/Plan/tasks/A21.md` revision 0.4.0.

## Minimal execution

Use the existing repository toolchain: Python 3.9+, .NET SDK 11.0.100 and Rust
1.100.0. The verification row in this batch is Linux x64. Maintainers build the
three targets once; running the controller itself never invokes Cargo, rustc,
MSBuild, or a package restore.

```sh
python3 -m unittest discover -s tests/Benchmarks.Harness -v
python3 tests/Benchmarks.Harness/verify.py --output artifacts/A21/attempt-001
```

For a network-free verification environment, pass `--offline-packages` pointing
to a NuGet cache containing the six .NET 11.0.0 SDK/compiler/runtime packages
listed in the script. Only those Microsoft archives are copied to a local feed;
all remote sources are cleared. Vulnerability-data refresh is unavailable for
that invocation and is recorded as such. This is not an advisory-check pass.

The command builds and runs ordinary JIT consumers and real `linux-x64` Native
AOT executables. AOT targets report that dynamic code is unavailable. It builds
the same scalar operation as a direct Rust executable and a test-only C ABI
library, verifies equal outputs, pins all executable/library/lock hashes, and
launches two fresh processes per route in a seeded randomized order. It retains
the generated workload plan, commands, raw events, observations, histograms,
invalid cells and artifact hashes beneath the chosen new output directory.

The scalar fixture measures only controller and adapter integration. It cannot
establish engine throughput, production memory use, or a NativeWeave performance
advantage. Regression decisions are disabled for this smoke workload because it
has no reviewed domain baseline. The analysis implementation is independently
exercised using finite known effects and explicitly supplied noise bands.

## Advanced execution and protocol

After building targets, execute a predeclared immutable plan directly:

```sh
python3 src/Benchmarks.Harness/nativeweave_bench.py run artifacts/A21/attempt-001/workload.json artifacts/A21/repetition
python3 src/Benchmarks.Harness/nativeweave_bench.py host
python3 src/Benchmarks.Harness/nativeweave_bench.py omission
```

The plan declares exactly `direct-rust`, `nativeweave-aot-rust` and `managed-aot`,
with separate executable argument vectors and artifact hashes. It pins toolchain
and lock identities, source commit, dataset/workload identity, seed, repetition
count, rate, count, deadline, in-flight bound, generator-lag threshold, histogram
range/resolution, warmup and cache/durability/TLS/serialization/allocator/ISA
settings. Settings are shared by all routes. Portable profiles reject `native`
ISA; the bundled targets explicitly use `x86-64-v2`.

The bundled target protocol is deliberately a fixture contract:

1. The target emits a JSON `hello` event naming its route and actual dynamic-code
   availability. A JIT process cannot satisfy an AOT route.
2. Requests are tab-separated `id`, unsigned scalar value, and mode. Each finite
   input is sent according to its scheduled arrival, independently of responses.
3. Targets emit separate JSON admission and completion events. Completion has a
   stable outcome, scalar result, and complete observation schema. Duplicate,
   unknown or out-of-order IDs, wrong values and malformed counters fail the cell.
4. The controller stops dispatch on integrity failure and retains unstarted rows
   as cancelled. Timeouts retain their original classification; later completions
   are retained separately and still checked for integrity.

Every measurement-phase target line is flushed to a raw event JSONL file before
parsing, including malformed input. The exact bytes are base64 encoded and the
file is hashed. Each completion retains its own full counter observation on the
request row; the last-counter convenience view cannot replace those observations.

The fixture modes `failed`, `rejected`, `cancelled`, `timeout` and unsupported
values test accounting. They are not real database/network fault injection. B23
can reuse the controller with `workload_kind: domain` and an `expected-values`
oracle: a SHA-256-pinned JSON dataset with one `{input, expected}` entry per
scheduled request. An input is a line-safe token identifying work in the target's
preloaded domain dataset; the expected value may be a scalar or a structured
result/digest supplied by an independent domain oracle. Warmup repeats dataset
entries outside the measurement window. The scalar oracle cannot establish a
domain result, and A21 does not supply or claim execution of a domain engine.

All four timestamps—scheduled, dispatch, observed admission and observed
completion—come from one controller monotonic clock. Admission is the observer's
receipt of an event, so IPC and observer scheduling are included. The controller
does not subtract unrelated process clocks or claim target-internal precision.
Queue/admission delay and service-observer duration remain separate from
arrival-to-completion latency. Deadlines start at scheduled arrival. Effective
deadlines and actual timeout-observation timestamps are distinct. When late
completions are included in the completed count, the throughput observation
window extends through their actual observed completion; they cannot inflate
throughput by using the earlier timeout endpoint.

## Failure and measurement rules

Failures, timeouts, rejections and cancellations remain in the dataset and in
all-outcome arrival histograms. Success-only histograms are additional views.
Offered, admitted and completed counts/rates are separate. Dispatch lag above the
declared limit or exhausted controller capacity invalidates the intended rate
cell. A timer cannot free application/native storage: cleanup closes input,
waits the declared finite budget and, if needed, kills only the target process.
Process exit or termination is explicitly not a resource-retirement proof.

Histograms retain integer bins, bin conventions, range, resolution and every
overflow value. Percentiles use exact samples and remain unknown below the
minimum tail population. Observed scheduled-arrival latency rejects any
expected-interval correction, preventing double correction. The finite omission
control independently demonstrates service p99 of **1 synthetic ms** versus
arrival p99 of **100 synthetic ms**; those are accounting values, not product
measurements.

## Resource and cost schema

The source's versioned `COUNTERS` inventory requires each value's unit,
availability (`measured`, `derived`, or `unknown`) and observation method.
Unknown values are null, never zero. It covers managed/native allocations,
copies, pins, retained bytes, FFI crossings, owners, permits, callback roots,
operations, queues/credits, connections/runtimes and GC/native allocator memory.
Instrumentation scope must be explicit: the fixture's managed allocation delta
surrounds only the scalar operation and excludes line parsing/JSON/IPC.

Linux samples record RSS, high-water RSS, private clean/dirty working set, thread
count, main-thread stack and executable mappings when procfs exposes them.
Whole-thread stacks, native allocator internals, kernel buffering and child
memory remain unknown unless separately instrumented. No queue-byte or
application-credit bound is described as a whole-RSS cap. The controller's own
allocations and sampling overhead are outside target counters and included in
observer timing; preserve the stated sampling method when comparing runs.

Plans record engineering effort, FFI calls and native rebuild scope, and all
actual artifact sizes can be recovered from the pinned files. B23 must add the
real engine's owner acquire/release points, quiescent values, retained allocation
accounting, build/setup time and generated/handwritten source counts. These
cannot be inferred from the scalar fixture.

## API, documentation and acceptance applicability

There is no new public/protected application type, so a NuGet API baseline,
application XML reference and package compatibility comparison are not applicable
to the Python controller. The C# fixtures use internal types, compile generated
JSON/LibraryImport code and emit XML without suppressing missing-documentation
warnings. The test-only export is separate from the product Engine ABI. The
commands and protocol above are the tooling reference and have strict versioned
input validation; unknown schema versions are rejected.

The executable source examples are in `samples/api/benchmarks-harness/`. Their
execution state and exact source/toolchain/log/artifact identities live in
`Plan/ledger/evidence/A21/`. Linux evidence does not certify Windows. A16/B24
retain final package/pristine-consumer/release obligations; A21 does not claim
their completion. Independent review must cover scheduling, integrity, all
outcome accounting, unknown counters, documentation and the limited claim.
