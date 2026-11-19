# Perf-01 public default inspection allocation profile

Status: source preparation only until actual build and execution receipts exist.
This directory adds one independent public package consumer; it changes no
product runtime, public API, frozen driver or existing profile helper.

The exact public path is
`NativeWeave.Engines.SyncProof.ZlibCodec.Descriptor.Inspect(null)`, through
`NativeWeave.Interop.EngineDescriptor` and the shared A02 loader. The descriptor
comes from `NativeWeave.Engines.SyncProof` 0.1.0-proof.1 and its corresponding
`NativeWeave.Core` 0.1.0-proof.1 package. It is metadata inspection: no codec
operation, database, network, domain instance or executor is started.

## Measurement scope

The program requires Linux x64 and actual Native AOT with .NET 11.0.0. It obtains
the descriptor once and records its **first Inspect call separately**. Descriptor
construction, package/executable hashing, native mapping verification and
counter priming are outside that first call. This is first use within a fresh
process, not a cold-filesystem or cold-host claim. It validates the returned
engine/package/RID/ABI/threading/capability metadata, then warms and measures
only the null/default path. Explicit option correctness remains covered by A02;
this profiler neither changes nor duplicates those tests.

All measurement arrays are allocated before the first call. Each steady batch
brackets a synchronous loop with `GC.GetAllocatedBytesForCurrentThread()` and
`Stopwatch.GetTimestamp()`. Inside that bracket are only the public Inspect
calls, allocation-free successful reference comparisons, integer loop/call
accounting, value-type counter returns and stopwatch sampling. Every return
must be the previously validated cached `EngineInfo` instance. There is no
delegate, per-call array, JSON, file I/O, process-stat read, await or callback in
the bracket. A failed call/check marks the receipt failed and preserves completed
call/batch counts; interrupted or partial receipts are not successful profiles.

Process CPU observations deliberately surround the allocation/wall bracket so
their `/proc`/managed observation allocations cannot enter the calling-thread
allocation count. Process CPU includes runtime/GC threads and a slightly wider
counter-observation interval; it is not calling-thread CPU. `TimeSpan` ticks are
100 ns units but the OS CPU observation has coarser effective resolution. Keep
raw zero CPU deltas as resolution-limited; do not infer a zero-cost call. GC
collection deltas also have the outer observation scope. No forced collection,
no-GC region, GC disabling or timing subtraction is used.

The receipt preserves raw per-batch wall ticks, process CPU ticks, calling-thread
allocated bytes, collection deltas and exact completed counts. Derive bytes/call
from the sum of completed steady allocation deltas divided by measured calls;
derive wall ns/call as `sum(wall_ticks) * 1e9 / stopwatch_frequency / calls`.
Report first use and warmup separately. These values localize allocations to
this public default call bracket; they do not attribute allocation stacks or
individual product types, count native allocator bytes, establish business
throughput, or provide a semantically equivalent direct-Rust comparison.
Every result has **zero formal long-duration credit**.

## Existing packages and isolated preparation

The selected actual before producer is:

```
/workspace/scratch/01c249676f47/perf01-codec-checks/package-before-004/manifest.json
SHA-256 d34298ebc8dc82b525f19860555947a1a04eff2bf8aecc38cede2922c81285a7
```

Its codec package SHA-256 is
`387ab88f17adac74946c4d4158862a557a720982639b273491988508b1ace78a`.
`prepare.py` requires that actual manifest hash, both actual package hashes,
the embedded product/native identity and the producer's actual AOT dependency
lock. It copies only that two-package closure into a **new private feed**.
Every invocation creates a **new build directory and a separate NuGet cache**,
so equal experimental package IDs/versions cannot reuse the other variant's
bytes. Source mapping routes NativeWeave packages only to that private feed.
The inherited AOT dependency lock is restored in locked mode; do not regenerate
or relax it to make a build pass. SDK 11.0.100, Release, Linux x64,
`IlcInstructionSet=x86-64-v2`, no shared compiler/server and Rust invocation
traps follow the existing isolated profiling convention. This consumer never
builds native code and does not require a new Rust or .NET toolchain.

Commit the probe source first. Prepare both variants from the **same immutable
probe checkout**, changing only the actual package manifest/hash and new output
directory. Existing codec producer/build evidence remains untouched. Run builds
only in a separately authorized preparation window, never during calibration,
profiling or formal timing:

```bash
PERF01_ROOT=/workspace/scratch/01c249676f47
source "$PERF01_ROOT/perf01-toolchain/env.sh"
PERF01_PROBE="$PERF01_ROOT/.worktree/Perf-01-inspect-profile-linux/NativeWeave/tests/Perf01.InspectProfile"
PERF01_PY=/opt/codex/runtimes/codex-primary-runtime/dependencies/python/bin/python3
/usr/bin/flock --nonblock "$PERF01_ROOT/perf01-evidence/timing.lock" \
  taskset -c 2,3 "$PERF01_PY" "$PERF01_PROBE/prepare.py" \
  --codec-manifest "$PERF01_ROOT/perf01-codec-checks/package-before-004/manifest.json" \
  --codec-manifest-sha256 d34298ebc8dc82b525f19860555947a1a04eff2bf8aecc38cede2922c81285a7 \
  --output "$PERF01_ROOT/perf01-build/inspect-before-001"
```

For after, pass the actual final codec producer manifest and its independently
recorded SHA to the same command
with fresh `inspect-after-001` output; do not insert invented future hashes or
reuse the before cache. A failed prepare directory and logs remain failed.
The script publishes only; it never executes the profile.

## Later exclusive profiling window

After the integration owner has reviewed the actual build manifest, use an
unchanged call configuration for before and after. The initial prospective
configuration is 10,000 warmup calls and ten batches of 100,000 measured calls
(1,000,000 total), on CPUs 2,3. Keep the same GC environment and exact program
source. Use fresh independent processes and result files; before/after wall/CPU
claims require the owner's paired/interleaved repeats, not just these inner
batches. No load or profile execution is authorized by this source document.

```bash
PERF01_INSPECT_BUILD="$PERF01_ROOT/perf01-build/inspect-before-001"
read -r PERF01_INSPECT_SHA PERF01_INSPECT_TOKEN < <(sha256sum "$PERF01_INSPECT_BUILD/manifest.json")
/usr/bin/flock --nonblock "$PERF01_ROOT/perf01-evidence/timing.lock" \
  taskset -c 2,3 "$PERF01_INSPECT_BUILD/aot/NativeWeave.Perf01.InspectProbe" \
  "$PERF01_INSPECT_BUILD/manifest.json" "$PERF01_INSPECT_SHA" \
  10000 10 100000 "$PERF01_ROOT/perf01-evidence/profile/inspect-before-001.json"
```

Create the result's parent directory before starting; the result itself must not
exist. Preserve the actual launch argv, UTC endpoints, exit, stdout/stderr and
result hash with the build receipt. The program checks the running executable
hash, retained packages and the one actual codec mapping under its own AOT
directory; it rechecks the manifest/executable/native identities and affinity
after measurement. The immutable build receipt binds the producer manifest,
actual product commit, package bytes, source files, inherited dependency lock,
build commands/logs and AOT artifact hashes. The result records its own manifest
hash, actual executable/native identity, managed thread, CPU affinity and GC
environment. These are diagnostic observations, never substituted for the five
domain groups' formal before/after long tests.
