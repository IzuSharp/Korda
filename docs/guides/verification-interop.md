# Cross-language ABI, ownership and race verification

> **DB-CPP-01 implementation update.** The public C# examples, signatures and ownership contracts in this guide remain the compatibility inputs. The current maintained native implementation is C++20; its module mapping, direct libpq/sqlite3 execution, Clang/CMake commands and package production are documented in [C++ native implementation](cpp-native.md). Descriptions below of Rust, SQLx, SeaQuery or Tokio internals are retained as the fixed `465cd0f` behavioral reference, not current build dependencies. The named verification entry points now build the ported C++ fixtures and retain their original managed assertions.

This is the A20 internal maintainer profile for Linux x64. It invokes the actual
accepted Core C/Rust/C# ABI, memory, error, callback and async implementations.
`NativeWeave.Verification.Interop` contributes no public application types, no
mandatory Core runtime and no independently distributed package. It does not
replace the real synchronous/asynchronous engine or pristine package gates.

## Build and run

Use the repository's pinned SDK 11.0.100, C# 15 and LLVM 23.1.5. The maintainer
profile also needs Clang with libFuzzer, AddressSanitizer and UndefinedBehaviorSanitizer.
The command records actual compiler versions and executable hashes. Rust libraries
remain optimized stable-toolchain builds, without sanitizer instrumentation.

```sh
python3 tests/Verification.Interop/verify.py --prepare-native --output artifacts/a20-native-preparation
dotnet build tests/Verification.Interop/NativeWeave.Verification.Interop.Tests.csproj -c Release
dotnet run --project tests/Verification.Interop/NativeWeave.Verification.Interop.Tests.csproj -c Release --no-build
dotnet run --project samples/api/verification-interop/Interop.Sample.csproj -c Release -- --advanced
python3 tests/Verification.Interop/verify.py --output artifacts/a20-formal
```

The explicit preparation step compiles maintainer adapters and stages the native
assets. Ordinary managed builds copy those assets; they do not run Cargo. A missing
asset produces a direct preparation error. `--prepare` refreshes normal and AOT
lock files and the generated API reference before a source commit; preparation is
never formal acceptance. Formal execution requires clean, committed source, locked
restores and an unused output directory. It runs the actual Linux x64 Native AOT
test and both minimal/advanced samples with a runtime AOT guard.

On a ptrace environment where LeakSanitizer is unsupported, use the explicit
`--disable-leak-sanitizer` flag. The receipt then records LSan as omitted while
retaining C ASan/UBSan. A prior LSan failure remains a separate failed run; this flag
does not convert it into a successful leak-sanitizer result. Native owner/backing
counters and managed retirement observations remain checked independently.

## Properties and independent oracles

The source manifest binds the compiled product, private adapters, C header, locks,
runner, samples and API checker. Design inputs are pinned in
`src/Verification.Interop/design-inputs.json` at the Plan-selected Design commit.

| Property | Actual comparison and non-vacuity |
| --- | --- |
| ABI layout | All 31 size/alignment/field-offset values of five v1 records from C, Rust and C#, plus the contract and a perturbed-offset control |
| Extents | 4,096 deterministic descriptors and six edge cases; independent unbounded `BigInteger` arithmetic versus the real managed and native validators |
| UTF-8 | Strict CLR decoding and Rune byte budgets versus Rust UTF-8 and bounded detail; valid, malformed, truncated, embedded-NUL and short-output cases |
| Fixed errors | 16 statuses across eight truthful capacities, field/tail canaries, malformed record rejection and exact cancellation token identity |
| Ownership | 128 valid creator allocations; range/copy, short-output, decoder, reserve/publication and adoption failures; exact creator release and retained child backing |

The deterministic generator is `splitmix64-a20-v1`, seed `0a20c0dec0ffee001`.
Extent failure messages retain eight hexadecimal fields and can be replayed with
`--extent address:length:count:width:offset:capacity:limit:alignment`. All addresses
in extent properties are scalar numbers; neither validator dereferences them.
Actual buffer properties borrow only from live allocations and release through
the same creator. No caller may infer pointer validity from a successful scalar
extent result.

## Controlled schedules

`tests/Verification.Interop/coverage.json` lists the exact cases, code owners,
thread/operation/queue bounds, deadlines and unsupported schedules. The tests use
actual Core gates and real Rust leases, reverse callbacks and worker entries.

The lifecycle profile pauses each of four lease acquisition boundaries against
Dispose and injects compensation faults. The async profile pauses seven named
root/registration/start/cancel/terminal-copy/take positions, tests pre-canceled
synchronous registration and two root-construction faults. It enumerates exactly
72 combinations: six terminal/cancel/Dispose orders, four managed drain positions
and three native terminal outcomes. A held native source lease and a separate
control lease make early reclamation observable. A duplicate notification may
poison the instance but cannot retake or free the already transferred result.

Callback cases stop before managed entry and during an active handler, discard
queued notifications while charging active work, and check synchronous decision
exceptions and reentrant close. Four mailbox wake positions and two scheduling
rejections verify the actual consumed coordinator's bounded dirty/scheduled slot.
Result transfer failures occur before take, after take, while mapping terminal
fields and at native take rejection. A retained slice keeps its parent's full
backing capacity charged after the parent wrapper closes.

Task publication, native quiet, cancellation-registration drain, managed root
release and final creator destruction are observed separately. Actual GC/finalizer
pressure is used while roots must remain live; it is not an exhaustive GC
scheduler. The finite orders do not enumerate the OS scheduler, every Rust/CLR
instruction, weak-memory executions or unbounded liveness. Optional executor
worker/blocking-work joins belong to A08; A20 does not make that a new dependency.

## Coverage-guided inputs and isolated negatives

The checked-in corpus has a versioned four-tag decoder: numeric extents, UTF-8,
truthful fixed error records and valid creator-owned buffers. Ordinary libFuzzer
execution has 20,000 units, a 256-byte input limit, fixed seed, per-input timeout,
RSS cap and parent deadline. ASan/UBSan instrument only the C decoder/oracles and
driver; they do not cover the complete Rust/CLR/Native AOT FFI. Final coverage,
original/final corpus hashes, raw logs, flags and executable hashes are retained.

A separate binary deliberately weakens one numeric oracle guard. Its saved
counterexample is minimized with a bounded libFuzzer run, replayed against the
defect, then accepted by the unmodified correct oracle. Both original and minimized
inputs remain present. This proves that the oracle control detects the named
arithmetic error; it does not introduce a product defect.

Deliberately invalid pointer and wrong-creator release cases run only as native
child processes. The parent requires the intended marker and specific ASan
SEGV/double-free diagnostic, not just a nonzero exit. Managed JIT and AOT children
also demonstrate rejected-after-callback quarantine and a stale-cookie protocol
violation after real cookie reuse. Quarantined/uncertain owners are retained until
the child exits and are never counted as successful retirement. Failure to obtain
cookie reuse within 1,024 attempts is inconclusive, not success.

Every command has an argv, working directory, UTC interval, exit status, deadline
and stdout/stderr hashes. A timeout kills the complete child process group and
remains failed/inconclusive. Unexpected fuzz crashes, timeouts and OOM are not
discarded or silently retried. The parent remains alive to retain diagnostics.

## API, cost and acceptance limits

All harness types are internal. The existing shared compiled API/XML/reference
checker enforces the empty exported surface; internal responsibilities have XML
and this guide. Samples exercise real Core behavior and require the same explicit
maintainer assets. They do not use Design stubs or claim application installation
without the harness prerequisite.

These verification allocations, barriers, full-array copies and FFI calls are
intentional observability overhead. They are not production performance data.
Zero live native owners/backings and acknowledged managed retirement establish the
recorded bounded cases, not zero process RSS, no runtime caches or a soundness
proof. Windows and full-product/long-load/pristine-consumer acceptance remain
outside this Linux component profile. Independent technical review must inspect
the immutable source, failures, corpus and receipts before recording acceptance.
