# Optional engine-local execution and close

> **DB-CPP-01 implementation update.** The public C# examples, signatures and ownership contracts in this guide remain the compatibility inputs. The current maintained native implementation is C++20; its module mapping, direct libpq/sqlite3 execution, Clang/CMake commands and package production are documented in [C++ native implementation](cpp-native.md). Descriptions below of Rust, SQLx, SeaQuery or Tokio internals are retained as the fixed `465cd0f` behavioral reference, not current build dependencies. The named verification entry points now build the ported C++ fixtures and retain their original managed assertions.

A08 implements the internal execution profile required by an asynchronous Rust
engine. An engine explicitly constructs an `EngineExecutor` instance with its own
worker and capacity limits. Referencing Korda.Core, inspecting package
metadata, or using the synchronous A17 codec starts no Tokio runtime. There is no
public generic scheduler, process-wide executor, new application type, or separate
executor NuGet package.

The authoritative policy is D09/C-CONCURRENCY-001 and D06/C-ASYNC-001 revision
0.4.0 at Design commit `c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9`.
`src/Interop.Execution/design-inputs.json` records its exact contract/API inputs;
`public-api.expected.json` and the generated `api-reference.md` document the
zero-public-surface contribution. This implementation covers Linux x64. Windows,
external event loops and thread-affine engine profiles are not accepted here.

## Ownership and capacity

The native engine selects fixed Tokio worker and maximum blocking-worker counts.
It owns the actual Runtime on one dedicated owner OS thread, plus a private
Handle that cannot be obtained by another component. The owned Runtime lives
until every issued, running and retiring reservation and tracked completion ends.
The conformance adapter additionally reserves one close-control operation and
uses one close-observer OS thread to turn actual join into an A07 terminal notice.
The close observer itself is joined before its native retirement acknowledgement.
These two control threads are separate from the reported Tokio worker counts.

Admission reserves all three budgets atomically under the same short mutex that
closes ordinary admission:

| Budget | Charge and release |
| --- | --- |
| Admitted operations | One slot from permit issue through final native-owner release |
| Payload bytes | The declared logical byte charge from admission through destruction of the owned payload |
| Cleanup records | One preallocated record for every accepted operation, kept through actual retirement |
| Waiting metadata | A separately bounded FIFO containing request ID, byte request and one response channel |

Zero admission or cleanup capacity disables work. A byte request above the entire
instance byte budget fails immediately. A full budget rejects `try_acquire` and
either queues bounded metadata or rejects `acquire`. Zero waiter capacity means
try-only behavior. A byte budget of zero permits only zero-byte work when other
budgets allow it. Native `AdmissionError` preserves Impossible, Full, Closed,
WrongOwner and AlreadyStarted separately. The test-only fixed ABI maps impossible
requests to INVALID_ARGUMENT, capacity exhaustion to OUT_OF_MEMORY, and closed or
duplicate use to INVALID_STATE; domain engines supply their own documented
Busy/ResourceLimit mapping before accepting a public operation.

The FIFO is the explicit admission ordering mechanism. New try-acquires cannot
bypass its head. A large head may delay a smaller request. Canceling/dropping the
head removes its metadata and makes the next eligible head runnable. Sending a
permit to a concurrently vanished receiver returns its credit exactly once.
Closing takes all remaining waiters and sends rejection outside the admission
mutex. Outstanding permits remain visible and charged. They cannot start after
close and must still be released by their actual owners.

Acquire before allocating, copying or retaining an input. `acquire` itself owns
metadata only; the engine must separately charge any payload it retains in its
calling future. The conformance adapter reserves first, fallibly creates and
copies at most 4096 input bytes, and destroys that payload before returning the
credit. The byte budget is a logical payload bound; allocator rounding, task
frames, thread stacks, upstream runtime internals and process RSS are not claimed
to be bounded by that same number. Fixed cleanup/FIFO metadata is allocated when
the instance is constructed. Tokio/Arc/channel bookkeeping uses upstream
infallible allocation; process OOM/abort is outside recoverable status guarantees.

## Work and terminal state

Every admitted asynchronous operation has one actual native future and one
bounded completion supervisor. Blocking operations have one blocking body and
one completion supervisor. The supervisor awaits the actual Tokio JoinHandle;
it catches a panic at actual poll/body execution through Tokio's JoinError, then
uses A05's unwind-safe cleanup boundary for terminal delivery. A panic poisons
the instance and closes ordinary admission. Unknown panic payloads are retained
without invoking an untrusted destructor, and the diagnostic counter records
them. Process abort, foreign unwind, invalid pointer contracts and allocation
abort remain outside that containment guarantee.

An Arc clone of a permit is a reference to one reservation, never another right
to start work. A supervisor retains it until delivery ends. The concrete native
operation owner retains it further until A07 proves native sources and callbacks
quiet and managed control/setup/dispatcher work drained. A successful Task can
therefore coexist with `retiring=1`, retained input bytes and a live managed root.
Diagnostics report issued/running/retiring stages, current worker start/stop facts,
poison status, and age of the oldest actual retiring cleanup record.

Permit allocation and oldest-age inspection scan the fixed cleanup array, so
their worst-case cost is linear in the configured cleanup capacity. Each
instance also owns one managed close-observation state machine; each explicitly
cancelable wait may allocate its own caller-owned `Task.WaitAsync` observation,
without creating another native close operation or coordinator retry task.

The adapter calls A07 `start` before reserving its native work-source lease. If
close wins the subsequent issued-permit race, start has already accepted the
callback table: the adapter publishes one canceled terminal and returns OK from
start. It never emits a callback and then reports a never-started rejection.

## Closing and timeout

`request_close` closes admission and wakes metadata waiters, then signals native
cooperative work. Engine-specific work must observe either its own A07 cancel
flag or the instance's close signal. Started `spawn_blocking` bodies remain
charged until their real return; no abort or dropped JoinHandle is used as proof.
After work and owned operation resources retire, the runtime owner drops the
actual Runtime, allowing Tokio to join its worker and blocking threads. The
designated close observer then joins that owner OS thread. It does not call
`shutdown_timeout` or `shutdown_background`.
An attempted pending join from a Tokio runtime context requests close and
returns false without blocking; actual join belongs to the external close worker.

The internal C# `ExecutorLifetime` takes one preallocated typed A07 close operation
with no cancellation token. `RequestClose` and synchronous `Dispose` start close
once and return immediately. `CloseAsync(waitCancellation)` cancels only that
caller's wait. Its one shared cleanup operation, fixed coordinator slot, native
close owner and GCHandle remain rooted. Retrying close observes that same
operation. Successful `DisposeAsync` requires actual worker-join counters, zero
outstanding operation/byte ownership, and the separate A07 retirement task.

The engine supplies a trusted query identifying callbacks/result mappers that
contribute to its own drain. From such a scope, requesting close is supported;
awaiting close is rejected with `InvalidOperationException` after requesting stop.
An application must likewise release child resources that intentionally outlive
its parent wrapper before awaiting their parent's final close. There is no
forced destruction on a deadline and no blocking finalizer. A permanently stalled
operation remains safely retained; diagnostics expose the pending stage and
age. Poison closes admission while trusted known-safe cleanup remains available.

No external callback, arbitrary owner destruction, await or OS join executes while
the admission mutex is held. The completion callback executes outside it; the
managed terminal trampoline remains A07's static exact ABI entry.

## Maintainer verification and examples

```sh
python3 tests/Interop.Execution/verify.py --output artifacts/task04/A08/acceptance
```

The entry point uses the fixed repository SDK/Clang versions and locked manifests.
It runs real native capacity/FIFO/cancel/issued-permit races, real native Tokio
future and blocking panic paths, actual OS-worker join, the C# consumer under JIT
and explicit `linux-x64` Native AOT, and both minimal/timeout sample paths. It also
checks that a normal downstream native link exposes only its explicit probe
symbol, while the opt-in `conformance` feature supplies the test adapter. A
Core-only consumer executes without loading the native executor. The previously
accepted A17 synchronous engine row supplies its own real-engine no-runtime
evidence and is reused without repeating its acceptance.

The finite tests include zero/full/oversize budgets, head-of-line FIFO order,
waiter cancellation, separate cleanup capacity, callback self-close, retained
native-source bytes after Task success, repeated canceled close waits, a lost
application wrapper under GC, cooperative cancellation and actual join after a
started blocking body returns. Receipts retain exact source/lock/features/native
and executable hashes and all failed attempts. They do not claim exhaustive
scheduling, a long-running load benchmark or cross-platform acceptance.

The [minimal and advanced sample](../../samples/api/interop-execution/Program.cs)
is a maintainer-only engine adapter example using actual component code. Ordinary
users await their selected engine's strongly typed methods and its documented
close/disposal API; they do not write unmanaged callbacks or construct this
internal scheduler. A14/A16 own final package composition and pristine consumers.
