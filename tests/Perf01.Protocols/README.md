# Perf-01 Linux x64 protocol measurements

This is a test application and measurement driver. It does not change the product,
ABI, public API, runtime ownership, native packages, or protocol compression support.
The server consumes the complete, separately produced B24 package closure. The
independent client consumes Grpc.Net.Client and Google.Protobuf and has no
NativeWeave reference. Both are published as actual Native AOT executables.

`profile-v1.json` is the prospective configuration. The Perf-01 integration owner
must record its frozen hash, selected loads and CPU affinity before the first
product change. Calibration and profiling are distinct from formal repetitions.
No result is created by a build or `prepare` command.

## Application paths

| Group | Operation | Frozen payload and correctness |
| --- | --- | --- |
| HTTP | Tiny handler | Exact 16-byte response, status 200, actual requested HTTP version |
| HTTP | Static JSON | Typed source-generated JSON DTO, exact field values |
| HTTP | Upload | 1 MiB through 32 KiB stream writes, server SHA-256 and acknowledged length |
| HTTP | Download | 1 MiB through real response stream, Content-Length, hash header and independent SHA-256 |
| HTTP | Slow download | Same 1 MiB, client delays 2 ms after each read of at most 32 KiB |
| HTTP | Upload cancellation | 8 MiB declared length; cancel after four completed 32 KiB writes; observed cancellation required |
| gRPC | Unary | 64-byte and 64 KiB payloads; exact generated protobuf equality |
| gRPC | Server stream | 32 ordered 4 KiB messages; every protobuf field, count, status and trailers |
| gRPC | Client stream | 32 ordered 4 KiB messages; explicit half-close; independent aggregate SHA-256/byte/count DTO |
| gRPC | Duplex | Concurrent request writes and response reads; 32 ordered 4 KiB messages each way |
| gRPC | Deadline | Commit response metadata, receive one 64-byte message, observe DeadlineExceeded at a 150 ms deadline |
| gRPC | Cancel | Commit response metadata, receive one message, cancel, observe Cancelled; subsequent drain/owner validation |

All HTTP variants use separate TLS listeners for exact HTTP/1.1 and HTTP/2.
All gRPC calls use the actual HTTP/2 TLS service. TLS trusts only the supplied test
certificate fingerprint, and the client refuses non-loopback targets. A synthetic
bearer credential is checked by the service. The fixture is deterministic (seed
20261119); Python freezes independent payload SHA-256 values before a client starts.
HTTP compression is disabled and gRPC is identity-only. No codec or wire
compression benefit is inferred from these cells.

The weighted HTTP mix is 50% tiny, 10% static JSON, 15% upload, 15% download,
5% slow download and 5% canceled upload. Each path is split equally between the
two TLS protocols over the repeated 200-entry fixture cycle. The gRPC mix is 40%
small unary, 10% 64 KiB unary, 15% each server/client/duplex stream, 3% deadline and
2% cancellation over the repeated 100-entry cycle. A seeded permutation prevents
a contiguous block of one operation from determining the whole queue profile.

## Build a package consumer

Use the repository SDK 11.0.100, C# 15, Release, Native AOT and portable
`x86-64-v2`. The B24 feed must already exist; ordinary application compilation
does not run Cargo. The package and native producer manifests must name the same
clean product source commit. The script checks the package bytes actually resolved
from the NuGet cache against the selected producing feed, not only package names.

```bash
source "$ROOT/perf01-toolchain/env.sh"
cd "$ROOT/.worktree/Perf-01-linux/NativeWeave"
python3 tests/Perf01.Protocols/build.py \
  --feed "$ROOT/perf01-build/before/packages/previous/feed" \
  --version 0.1.0-perf01.before.1 \
  --sdk "$DOTNET_ROOT/dotnet" \
  --packages "$ROOT/perf01-toolchain/nuget" \
  --output "$ROOT/perf01-build/before/protocols"
```

`--package-manifest` and `--native-manifest` can override the sibling B24 and
native producer manifest paths. Outputs are new, immutable attempt directories.
`--compile-only` produces ineligible diagnostics, never a measurement executable.

`build.json` separates the producing product commit from the driver commit/dirty
state and binds source snapshots, resolved package archives, producer manifests,
consumer locks, final binaries and actual loaded native module hashes. Runtime
`/proc/self/maps` observations must exactly match the package server's native
hash set. Failed builds retain their logs.

## Direct Rust control

```bash
python3 tests/Perf01.Protocols/build-direct.py \
  --client-build "$ROOT/perf01-build/before/protocols/build.json" \
  --output "$ROOT/perf01-build/direct/protocols"
```

The control calls Hyper/rustls/Tonic directly, uses Tokio's current-thread runtime
as the product server does, and links no NativeWeave crate. Its release profile is
the root profile: thin LTO, one codegen unit, unwind panic containment and stripped
debug info. Both Rust flag override variables are cleared, matching the product
native producer's generic `x86-64` ISA; the common AOT client remains explicit
`x86-64-v2`. Every resolved registry dependency version and
checksum must already occur in the product Cargo.lock. No third-party source is
vendored or relicensed. The original registry source/license metadata and existing
repository third-party inventory remain applicable. The fixture codec itself is
new test code under this repository's MIT license.

The direct server uses the same independent AOT client, fixture/authentication,
TLS, 32 KiB HTTP chunks, stream/message caps, connection/request limits, ten-second
TLS/header timers, 64 handshake slots per listener, sixty-second idle keepalive,
and graceful shutdown deadline. Conservative gRPC codec plus request-metadata
allowances are charged against the same native 64 MiB/4 MiB limits. Escaping
payload frames retain their byte credits and request lease until their final
owner drops. The direct server records pending requests, native credits, byte
owners and connections and requires all to reach zero at close.

There are explicit implementation differences, which are part of the direct
engine comparison rather than changes to the fixture's result:

- The repository does not lock a Rust protobuf generator/runtime. The Rust
  control uses a typed codec for exactly the five fields in `perf01.proto`; the
  independent client uses generated Google.Protobuf. It supports the fixture's
  varints, byte/string fields and ordinary unknown wire fields, but does not
  claim arbitrary protobuf-group compatibility. Compare valid fixture messages;
  keep product B18 malformed-input regression acceptance separate.
- Direct Tonic starts with 8 KiB codec buffers that grow under its configured
  limits. Yield threshold is one, matching one-frame-per-message delivery. Its
  conservative reserved codec allowance remains the product maximum. It is not
  a control that removes quotas or batches extra messages implicitly. This claim
  is restricted to the frozen fixture: the largest unary protobuf is 65,542
  bytes (65,547 with the gRPC envelope), and the largest streaming protobuf is
  4,103 bytes (4,108 framed), with at most 32 messages. Growth/retention for those
  fixtures remains well below the 1 MiB maximum codec allowance. This control
  does not establish arbitrary near-1-MiB protobuf growth/backing equivalence.
- Direct HTTP can retain shared immutable payload slices; C# stream handlers
  use the real public streaming API. The source/copy cost is an intended measured
  difference. Both perform actual sockets and validate the same bytes.
- Direct Rust has no managed application heap. Its managed allocation/GC values
  are unavailable/not applicable, not inferred as measured zero. Managed payload
  credits are not needed by that route. RSS and native payload limits still apply.

## Prepare and run a cell

Reuse the **same client build** for all product versions and direct Rust. Reuse
the same TLS files for comparison identity. `prepare` can generate a self-signed
synthetic test certificate; for subsequent versions pass those exact files.

```bash
python3 tests/Perf01.Protocols/run.py prepare \
  --build "$ROOT/perf01-build/before/protocols/build.json" \
  --client-build "$ROOT/perf01-build/before/protocols/build.json" \
  --group grpc --rate 32 --concurrency 16 --duration 30 \
  --output "$ROOT/perf01-results/before/grpc-r32-c16-01"

taskset -c 2,3 python3 tests/Perf01.Protocols/run.py run \
  --plan "$ROOT/perf01-results/before/grpc-r32-c16-01/plan.json" \
  --exclusive-lock "$ROOT/perf01-results/timing.lock"
```

For HTTP use `--group http`. For the high-load acceptance cell use
`--duration 1802` and the prospectively selected, stable rate/concurrency. The
bounded two-second allowance is a prospective schedule guard, not credited
duration. The same value must be frozen for both versions. Preparation still
accepts shorter schedules including 1800 seconds; none qualifies automatically.
For a
short ladder prepare independent cells at `(8,1)`, `(16,4)`, `(32,16)`, `(64,32)`
and `(128,64)`, then extend the rate until observed queueing/error/recovery bounds
are established. Rates are candidates, not established sustainable capacity.
The integration owner serializes every domain and route on one measurement
resource. The lock is advisory and must be the same lock across orchestrators.
Do not compile, profile or run another benchmark during that window.

For the after build, pass the same before `--client-build`, same profile, rate,
duration, concurrency, `--certificate` and `--private-key`. The application source
hashes must match the common client build; only the package closure changes.
`comparison_identity_sha256` binds this workload identity, including the actual
common client executable. A direct Rust cell uses its own `--build` and that same
common client; its fixture identity remains comparable.

For a separately frozen HTTP comparison profile, optional `http_protocols` may
select `["http1-tls"]`, `["http2-tls"]` or both. It must be a nonempty list of
unique supported TLS protocol names. Omission preserves the existing equal
HTTP/1.1-TLS and HTTP/2-TLS expansion; the long profile remains unchanged. gRPC
always uses HTTP/2-TLS. The profile hash already binds this selection into each
comparison identity. Both listeners remain present in the immutable server even
when one protocol is selected, and the same common client is reused.

HTTP result scenario keys distinguish the path and protocol, but not multiple
payload sizes of the same path inside one cell. Put 1 MiB and 8 MiB upload/download
comparisons in separate profiles so their p99/byte results remain distinct.

### Optional generator CPU isolation

For a separately calibrated diagnostic cohort, prepare and run from the two
service CPUs and add `--generator-cpus 0,1` to **prepare**:

```bash
taskset -c 2,3 python3 tests/Perf01.Protocols/run.py prepare \
  --build /absolute/build.json --client-build /absolute/common-client-build.json \
  --group grpc --rate 32 --concurrency 16 --duration 30 \
  --generator-cpus 0,1 --output /absolute/new-split-attempt
taskset -c 2,3 python3 tests/Perf01.Protocols/run.py run \
  --plan /absolute/new-split-attempt/plan.json --exclusive-lock /absolute/timing.lock
```

The adapter inherits service CPUs 2,3 and starts the server there. It then moves
its own current threads to 0,1 before spawning the independent client. The parent
controller moves its current threads after spawning the adapter. Moving all
current Python threads includes an asyncio child-watcher thread that might have
been created by the first spawn. No server/runtime thread is migrated afterward.
Both masks must contain two CPUs and be disjoint. Actual main masks and all live
thread-mask subsets are checked at ready and after load/recovery; surviving
processes are checked after idle. Controller affinity is restored during cleanup.

The choice is part of the frozen comparison identity and receipts. The default
performs no affinity changes. A split result belongs to a new calibrated cohort;
it cannot be compared with a shared-CPU result as if resource allocation had not
changed. This Python-only option does not require C# application/native rebuilds.

## Measurement and acceptance accounting

The controller reuses A21's immutable expected-value Oracle, strict RunState,
outcome validation, histogram bins and exact nearest-rank percentile rules.
It retains scheduled arrivals even when late or rejected; service latency is
not substituted for arrival latency. The adapter wire is compact because all
nineteen per-operation A21 counters are unavailable here. They are represented
once in the result, while actual process/GC/native counters have separate traces.
This is a single-route cell and does not claim the A21 three-route plan gate.

The protocol sampler reads CPU, memory and descriptor evidence independently.
A descriptor that closes between enumeration and `readlink` increments
`descriptor_scan_misses`; it does not erase already observed CPU/RSS values.
This includes the sampler's own temporary procfs enumeration descriptor.
Descriptor/socket totals describe the non-atomic scan, while unavailable CPU
and descriptor fields retain separate error markers. The summary preserves
available RSS/thread observations even when a CPU observation is unavailable.

`result.json.scenarios` groups every planned operation by its frozen oracle
scenario, protocol and unary payload size, including operations rejected before
client dispatch. Each group records offered/dispatched/admitted counts, A21
controller outcomes, independent client/wire outcomes, expected probes, and
`arrival_success` plus `arrival_all_outcomes` p50/p95/p99 histograms. Successful
business calls require both an on-time A21 success and a correct ordinary client
result. Expected cancellation/deadline checks remain separately counted and do
not inflate business throughput. An eventual client success after an A21
deadline is not a successful business call under that deadline.

`successful_business_totals` and `successful_business_rates` provide validated
request/response message counts and payload throughput for those ordinary
successful calls. Existing `totals` retain all observed client bytes/messages,
including partial failures and probes. Bytes submitted before cancellation do
not prove server receipt. Per-scenario rates divide by the same complete measured
window as the mixed result; they describe each scenario's contribution to that
mix, not a separately measured standalone capacity. CPU/RSS remains measured for
the whole server process and cannot be attributed to an individual scenario.

The following phases are distinct:

1. Startup and 24 correctness-checked warmup calls, covering each unique scenario.
2. Open-loop measured arrivals, with bounded client workers/queue and fixed
   maximum generator lag (100 ms) and in-flight bound. The scheduled span includes
   a lag guard, and a 30-minute result must still prove the **actual first-to-last
   dispatch span** is at least 1800 seconds. The planned and actual counts are kept.
   Final Perf-01 acceptance additionally requires the conservative ordinary
   operation bound below; scheduled time and admission acknowledgements do not
   establish wire-operation duration.
3. Thirty-two low-rate recovery calls on the same still-running server/client,
   covering every path again, separately counted and bounded to 60 seconds.
4. Client closure followed by sixty seconds without load. Request/owner gauges
   must recede. Explicit server close must report Completed and zero pending and
   retained byte owners; process exit alone is insufficient.

Every scheduled operation retains its outcome, and successful responses undergo
the complete content/hash/protobuf/status/trailer check. Intended cancel and
deadline probes are logical check successes but are also reported as actual wire
outcomes. `business_success_per_second` excludes those probes. Unexpected errors,
timeouts, correctness failures and resource rejection remain visible. Generator
saturation and correctness failures invalidate a timed sample; service overload
remains an observed outcome and fails the predefined service standard.

The prospective common standard is zero unexpected errors, success-arrival p99
at most 500 ms and application RSS at most 256 MiB, plus both payload budgets,
successful recovery and owner drain. The existing `full_30_minute_valid_load`
requires the actual dispatch span and service standard;
`full_30_minute_completed_duration` records that dispatch-span test separately.
These driver fields are necessary but not sufficient for final long-run
acceptance. The common post-run verifier must also require `D - C >= 1800 s`,
where `C` is the controller-observed completion of the first successful ordinary
measurement operation and `D` is a later successful ordinary operation's last
controller dispatch-start. Completion follows full response validation, while
dispatch-start precedes that command's actual wire operation. Thus the interval
is conservative and excludes the leading completion and trailing call time.
Frozen oracle tokens exclude `http-cancel`, `grpc-cancel` and `grpc-deadline`
from both endpoints; they remain checked and counted separately. Controller
success, matching correct client results, complete load, unchanged SLO/resource
limits, recovery and owner drain all remain required. The 1802-second schedule
guard alone cannot satisfy this proof. Partial attempts are retained with
dispatch journals, raw events, any partial request ledger and supervised process
outcomes; they never count as completed acceptance.

## Evidence and honest limits

Each prepared directory contains `plan.json`, `adapter.json`, `oracle.json`, a
profile snapshot and certificate/config hashes. A completed directory adds:

- `result.json`: validity, exact UTC/monotonic window, scheduled/actual/successful
  throughput, p50/p95/p99, scenario/message/payload totals, recovery, SLO and hashes.
- `dispatches.jsonl`, `events.jsonl`, `requests.jsonl`: actual dispatch journal,
  exact compact client wire events, full scheduled-request/outcome ledger.
- `execution/client/operations.jsonl`: independent protobuf/HTTP/wire outcome,
  message counts, submitted/received payload and encoded protobuf sizes.
- `execution/process-resources.jsonl`: one-second server, independent client,
  adapter and controller procfs CPU/RSS/thread/socket/descriptor observations.
- `execution/server/managed-resources.jsonl` or `native-resources.jsonl`: GC,
  managed allocation and observable native gauges, or direct native owner counts.
- `execution/ready.json`, `receipt.json`, `server/closed.json`: actual loaded
  native hashes, idle/retirement evidence and exact close status.

Encoded protobuf bytes exclude the five-byte gRPC envelope, HTTP/2 framing and
TLS overhead. Submitted bytes after cancellation are not asserted to have been
received by the server. Actual successful hashes/acknowledgements prove completed
transfers. Client in-progress calls do not prove instantaneous on-wire HTTP/2
stream count; stream limits and socket counts are separate observations. GC heap
snapshots reflect the runtime's last collection, and 1 Hz gauges can miss peaks;
RSS/native limits are additionally enforced by application/configuration policy.
Exact FFI calls, copies, native allocator bytes, lock wait and scheduler switches
are unavailable in timing cells and require separate profiling evidence.

There is no new pure managed server comparator in this isolated fixture. Existing
B23 managed and direct Rust whole-service comparisons remain separate scenarios;
do not combine their numbers with this protocol-only load. Root analysis should
interleave independent before/after processes and reuse A21 `noise_comparison`
over process summaries. A single segment is not a noise estimate.
