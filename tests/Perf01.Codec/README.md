# Perf-01 codec measurements

Status: test tooling. No performance conclusion follows from source inspection,
compilation, the finite controller tests or the functional checks.

The product operation is the existing `ZlibCodec` public API from the independent
A17 package. B24's 17-package domain closure does not include this codec. The
producer therefore reuses `tests/Engines.SyncProof/package_probe.py` to build the
actual two-package A17 closure from each chosen product commit, then publishes
the **same** independent C# driver as Native AOT against each private feed/cache.
No consumer invokes Rust. `prepare` is a maintainer build, outside timing.

## Preparation

Use the repository's exact SDK 11.0.100, Rust 1.100.0 and Native AOT linker
prerequisites. The codec still locks miniz_oxide 0.9.1 and adler2 2.0.1. The test
direct comparator's lock has exactly those same versions and checksums. No new
runtime dependency or public API is introduced.

Keep the reviewed driver in a separate checkout from the clean product snapshot:

```sh
python3 tests/Perf01.Codec/driver.py prepare \
  --product-root "$BASELINE_NATIVEWEAVE" --output "$EVIDENCE/codec-build-before"
python3 tests/Perf01.Codec/driver.py corpus \
  --direct "$EVIDENCE/codec-build-before/direct-target/release/perf01-codec-direct" \
  --output "$EVIDENCE/codec-corpus"
```

Repeat `prepare` with the final product checkout and a new `codec-build-after`
directory. Reuse the exact corpus. The build manifest pins the product commit,
driver files, locks, executable, packages and native libraries. Running the
driver rechecks artifacts/corpus and obtains the actually mapped native file
from Linux procfs before assigning the native performance row.

The fixed corpus has 256 B, 4 KiB, 1 MiB and 4 MiB inputs, each with a repeated
64-byte pattern and a pseudorandom counterpart. Both use xorshift32 seed
`0x9e3779b9`; Python independently verifies the raw bytes generated in Rust.
The direct Rust producer checks encoded identity with the pinned upstream
`compress_to_vec_zlib(..., 6)` and bounded round trips before freezing SHA-256s.
Format is zlib with Adler32, level six, raw/decoded limit 4 MiB and encoded limit
4 MiB + 64 KiB. Changing the limits or corpus creates a different workload.

## Short ladder and long stage

The integrated Perf-01 freeze must select worker counts, rates, queue/in-flight
bounds, deadlines and generator-lag thresholds **before product optimization**.
No default numeric rate here purports to be a sustainable measured load.
Run independent fresh processes at every selected worker/rate setting, in the
interleaved order chosen by the integration controller. Never time this driver
alongside other builds, profiling or timed domains on the same measurement CPU.

```sh
python3 tests/Perf01.Codec/driver.py run \
  --manifest "$EVIDENCE/codec-build-before/manifest.json" \
  --corpus "$EVIDENCE/codec-corpus" --route nativeweave \
  --workers "$WORKERS" --rate "$RATE" --queue 64 --inflight 256 \
  --deadline-ms 1000 --max-lag-ms 50 \
  --recovery-seconds 10 --recovery-rate 1 \
  --duration-seconds 1800 --warmup-seconds 20 --idle-seconds 60 \
  --output "$EVIDENCE/codec-before-long-001"
```

The same command with the after manifest and new result directory performs the
paired long stage. A declared dispatch guard of `max-lag-ms` extends the planned
window slightly; the actual first-to-last dispatch span must still reach the
requested duration. The receipt records scheduled duration, actual dispatch
span, last completion, same-process low-rate recovery, drain and idle separately.
Recovery and idle never receive measurement-duration credit. Planned, attempted,
sent, admitted and completed counts remain separate per phase. Interrupted runs
retain dispatch events, unfinished request IDs, phase timestamps and zero long
stage credit; no existing result directory is overwritten.

Use `--duration-seconds` for short stages. `--workers 1` separates single-caller
cost; higher worker counts test concurrent calls. `--case 0..7`, `--action
compress|decompress` and `--interface array|owned` isolate layers. The default
mixed 160-action cycle is seeded once and repeated: 50% compression/50%
decompression; 50% managed-array/50% NativeBuffer; 50% compressible/50%
pseudorandom; size weights 50%/25%/20%/5% respectively. Operations, raw bytes and
encoded bytes are counted separately per action/interface/corpus. Do not report
their weighted aggregate as a single-payload codec speed.

`--route direct-rust` uses owned `Vec<u8>` results with the same bounded public
miniz core loops, full-input-consumption checks and byte oracles, without Core,
FFI, a managed array copy or native owner publication. It is the strict valid
comparison. The independently coded test adapter adds no new product export.
Its Release profile matches product thin LTO, one codegen unit, unwind panic and
debug-info stripping. The Rust comparator always returns Vec; requested array/
owned labels only partition the shared workload and do not add a Rust copy.
Both direct and NativeWeave targets validate every result byte against the
same frozen encoded/raw corpus and release the result before completion.

`--route managed-zlib` invokes .NET `ZLibStream` with explicit numeric level six
and bounded managed output. It checks round trips, corruption and output/input
caps. **It is not a full semantic comparator:** buffered `ZLibStream` does not
expose the exact consumed compressed-input position used by NativeWeave to
reject trailing/concatenated streams. Its compression correctness check also
decodes each newly generated stream, which adds validation cost outside
`call_ns`. Its full operation/arrival times must not be ranked as equivalent
NativeWeave/Rust results. Its isolated codec-call timings describe valid corpus
algorithm behavior only; equal compression level does not imply equal encoded
bytes or compression ratio across implementations.
Both requested ownership labels use the same managed-array ZLibStream path;
there is no .NET NativeBuffer comparator hidden under the owned label.

## Observations and limits

`operations.jsonl` preserves the external monotonic scheduled/dispatch/admission/
completion clocks and target call/validation/release durations. `events.jsonl`
preserves target observations, including errors and telemetry.
`resources.jsonl` samples both target and generator every second using A21
procfs observations plus process CPU and file-descriptor counts. The generator
never waits for one request's response before scheduling the next. Capacity or
dispatch-lag exhaustion invalidates the intended load. Queue rejection/timeouts
remain visible overload results and separately fail the zero-error service
standard; they are never counted as successes.

Throughput uses the full observation window through the last actual completion,
not an earlier timeout or scheduled endpoint. A21 nearest-rank percentiles are
withheld for insufficient tail population. Reports split raw MiB/s by measured
wall time from raw MiB/s by summed codec-call time; concurrent summed service
time is not wall throughput. `managed_allocated_bytes` brackets only the codec
call on dedicated synchronous workers; `managed_allocated_bytes_including_validation`
also includes ownership consumption/validation. Whole-process telemetry includes
driver observations and is explicitly broader. Derived copy/FFI counts are
source-defined counts, not hardware memory-traffic measurements. Native allocator
allocation counts and retained allocator memory remain unavailable.

Native owner counters distinguish live result owners, released owners, retained
result capacity and active error-boundary calls. Concurrent samples are separate
atomic reads, not a coherent transaction; zero values after all workers join
are the authoritative retirement check. RSS includes the frozen corpus,
allocator caches and the managed runtime; it is not a live native-buffer count.
The codec is synchronous and noninterruptible after admission. There is no
native async executor, callback, per-operation cancellation or protocol
compression in this codec group.

## Optional generator CPU diagnostic

By default, the controller and target retain their inherited affinity. To
investigate generator scheduling separately, launch the controller on the two
target CPUs and explicitly give it a disjoint two-CPU generator mask:

```sh
taskset -c 2,3 python3 tests/Perf01.Codec/driver.py run \
  --manifest "$EVIDENCE/codec-build-before/manifest.json" \
  --corpus "$EVIDENCE/codec-corpus" --route nativeweave \
  --workers 2 --rate "$RATE" --duration-seconds 30 \
  --generator-cpus 0,1 --output "$NEW_DIAGNOSTIC_DIRECTORY"
```

The target inherits CPUs 2,3 when spawned. Only afterward, inside protected
cleanup, the controller moves its main thread and every current Python TID to
0,1. This includes an asyncio child-watcher thread that may already exist.
Future controller-created threads inherit the new main-thread mask. The target
and its threads are never migrated. Requested masks, observed main masks and
every live thread mask are recorded in the plan and one-second resources.
Phase checks and resource observations require the exact main mask and reject
any live thread outside its declared mask. Narrower runtime thread masks within
the target's two CPUs remain allowed. Affinity setup failure reaps the target;
cleanup restores all current controller threads to their original mask and
records failure if restoration cannot be verified.

This optional mode allocates two additional generator CPUs. It is a separate
diagnostic cohort until root freezes a final resource policy. Never compare a
split run against a shared-CPU run as if their resources were identical. The
new controller/helper hashes require a fresh `prepare` manifest for both product
variants; do not edit an old manifest to bypass the driver source guard. The
target C#/Rust source and fixed payload corpus remain identical.

Run finite tooling/functional checks separately:

```sh
python3 -m unittest discover -s tests/Perf01.Codec -p 'test_*.py' -v
"$EVIDENCE/codec-build-before/direct-target/release/perf01-codec-direct" \
  --check-only --corpus "$EVIDENCE/codec-corpus" --workers 1 --queue 4
"$EVIDENCE/codec-build-before/aot/NativeWeave.Engines.SyncProof.Tests" \
  --check-only --require-aot --route nativeweave \
  --corpus "$EVIDENCE/codec-corpus" --workers 1 --queue 4
```

Targets' check-only path does not collect timing. The source-defined ownership
regressions include a read admitted before disposal, repeated disposal and
post-disposal rejection. A17's reused package verifier additionally checks
wrapper-adoption failure and concurrent calls. Formal acceptance still requires
the integration review, exact frozen configuration and actual complete results.
