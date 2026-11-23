# Task operations, cancellation and native retirement

> **DB-CPP-01 implementation update.** The public C# examples, signatures and ownership contracts in this guide remain the compatibility inputs. The current maintained native implementation is C++20; its module mapping, direct libpq/sqlite3 execution, Clang/CMake commands and package production are documented in [C++ native implementation](cpp-native.md). Descriptions below of Rust, SQLx, SeaQuery or Tokio internals are retained as the fixed `465cd0f` behavioral reference, not current build dependencies. The named verification entry points now build the ported C++ fixtures and retain their original managed assertions.

A07 supplies the internal Task bridge used by a concrete engine's typed Async
methods. Applications await `Task<TResult>`, pass `CancellationToken` and dispose
their independently owned results. They do not create a callback cookie, publish
a CLR generic through the ABI, or construct a global native runtime. These
internals compile into the existing `Korda.Core` package; A07 introduces
no separate public package or scheduler.

The normative input is Plan's A07 revision 0.4.0 and its `async-runtime` fragment,
with Design commit `c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9`.
The exact source digests are recorded in
[`design-inputs.json`](../../src/Interop.Async/design-inputs.json). Existing A02
loading failures and A05 `NativeEngineException` remain their original public
types. The [generated contribution](../../src/Interop.Async/api-reference.md)
checks this module's zero public symbols and compiled static callback signature.

## Consumer and engine responsibilities

The [minimal and advanced maintainer sample](../../samples/api/interop-async/Program.cs)
executes the real bridge and native operation component. It is a friend assembly
to demonstrate internal adapter responsibilities; it is not an application API.
A concrete domain driver wraps the same sequence in its exact Async method.
Normal application Entity/DTO/Handler changes use the maintainer's precompiled
NuGet assets and do not recompile Rust.

The adapter reserves its operation/input quotas, creates its exact native
operation with callback admission closed, and immediately adopts it in a
preallocated SafeHandle. It constructs `AsyncOperation<TResult>` with the A03
owner, its exact `AsyncNativeCalls`, an engine-owned A06 `CallbackCoordinator`,
its statically compiled typed result mapper and any input/quota lease.
Construction transfers the owner and input lease only when it succeeds; a
constructor exception leaves those resources with the adapter. `Start()` then
registers cancellation and invokes the native start method. Argument validation
and failed construction are synchronous; started operation failures are delivered
through the Task.

The native reusable `Operation<R>` records Created, Running, TerminalReady and
Retired, a cancellation request flag, one frozen terminal record and a single
result owner. `R` stays inside Rust. An engine adapter exposes a concrete typed
take, initializes failure outputs to empty, and performs no fallible work after
moving its result to the caller. The managed mapper prepares its owner wrapper
before take and releases any transferred result if conversion fails. The sample
uses the actual A04 buffer creator/destructor and verifies a second take yields
`INVALID_STATE` and no owner.

This component does not implement a runtime-independent Future ABI. Concrete
engines own their native Future/runtime semantics. Each native Future or worker
reserves a `WorkLease<R>` before it can retain callback state or inputs and drops
that lease after its final access. A18 owns mature-engine composition; A08 owns
optional runtime/admission/worker-join behavior. A07's bounded Rust conformance
worker exercises foreign-thread callbacks without claiming executor shutdown.

## Three independent completion facts

| Fact | Meaning | Resources that can remain live |
| --- | --- | --- |
| Cancellation requested | Native code has received a cooperative request | Operation, callback root, registration, result and inputs |
| Task completed | The native terminal outcome has been translated, or a bridge failure reported | Native sources, callback/control frames, input quota and cleanup slot |
| Retirement completed | Setup and registration are drained, native `try_retire` returned OK, result processing and managed frames ended | Only independently transferred results |

The native terminal commit chooses success, cancellation or failure once.
Requesting cancellation never calls `TrySetCanceled` and never changes a committed
success. Native `CANCELED` associates the caller token; `TIMEOUT` becomes the
existing timeout classification. A native domain failure is not evidence of
rollback or known remote effect. If the terminal binding fails after copying the
fixed record, the bridge reports its own contract failure while preserving the
original native status and available domain/code/correlation values.

An operation returns a normal, multiply awaitable Task with
`RunContinuationsAsynchronously`. A static nongeneric Cdecl UCO entry copies only
the fixed terminal status/error record into a preallocated latch. It does not
call the typed mapper or application continuation. The mapper runs on the
external completion coordinator. Internal scheduling and `UnsafeRegister` avoid
capturing application ExecutionContext; the application's own `await` retains
normal ExecutionContext/AsyncLocal behavior.

## Setup, failure compensation and result ownership

| Boundary | Current owner and compensation |
| --- | --- |
| Native create fails | Output stays null; native temporary allocation/input charges roll back; no callback root exists |
| Context/root/slot preparation fails | The adapter retains its Created native owner and input quota; any allocated GCHandle is freed; no registration/start occurred |
| Known failure before registration | Publish NoRegistration and StartSkipped; release the Created owner after the settled setup gate |
| Precancel or known failure after registration | Publish Registered and StartSkipped; externally await `CancellationTokenRegistration.DisposeAsync`, then release the Created owner |
| Native start rejects | Native guarantees no accepted work/callback; publish StartRejected and follow Created cleanup |
| Synchronous terminal callback during start | Copy the latch, retain setup/start ownership, and wait for StartAcceptedReturned before processing or reclamation |
| Unknown registration/start acceptance, or callback followed by start rejection | Publish Indeterminate, poison ordinary engine admission and retain owner/root/quota; retirement is not reported successful |
| Queue rejects or throws before acceptance | Keep the latch and dirty slot; the one engine retry source retries the existing bounded slot |
| Result wrapper allocation or take fails before transfer | Native still owns the result and frees it at final operation release |
| Materialization fails after a successful take | The managed result wrapper releases the exact transferred owner before Task fault publication |
| Cleanup control/retire fails | Poison the engine immediately from the external coordinator; retain the quarantine even when no later wake occurs |

`UnsafeRegister` may invoke its controlled callback synchronously before the
returned registration is assigned. The fully initialized root and control state
already exist. An explicit Registered/NoRegistration sentinel and one Settled
setup publication prevent cleanup from waiting for a registration/start that will
never occur or from freeing state before a later setup step. Unknown acceptance
does not fabricate these sentinels.

The first terminal entry claims the latch using CAS. Even an injected ordinary
postcopy exception release-publishes `notice_received` in `finally`, preserving
the original status and copied error fields. Duplicate notices retain the first
latch, diagnose the contract defect and poison engine admission. Native callback
failure records delivery failure and does not trigger a second business terminal
commit. Invalid cookies, unreadable native pointers and fatal process failures
are outside this ordinary exception containment guarantee.

## Closure, bounded memory and progress

`Dispose()` closes ordinary control admission and requests cleanup.
`DisposeAsync()` joins actual retirement. Calling that join from this operation's
callback, cancellation callback or result mapper is rejected to prevent a
self-wait. Cancellation registration drain runs externally, without holding a
native or managed business lock. Every native control call still holds its own
physical owner lease, including internal take, cancel and retire after ordinary
admission closes.

The engine's coordinator reserves one fixed cleanup slot per admitted operation.
At most one work item per operation is scheduled. Dirty publication and scheduled
clearing use one gate, preserving a wake arriving while a consumer drains.
`PENDING` retire checks use the existing shared timer and retained slot; they do
not create a Task/timer per poll. A canceled or timed-out wait leaves the rooted
operation and all still-live input/budget charges in place. Diagnostics expose
setup outcome, registration drain, original terminal status, control/callback
counts, age, retained input bytes, native quiet and quarantine state without
publishing native pointers.

Input quotas stay charged through Reclaimed. Result quotas belong to the
concrete engine's independent result owner after transfer. An ignored Task does
not prevent operation cleanup. An owned result already transferred into that
Task still follows application disposal and its final-release fallback; ignoring
the result cannot promise deterministic release. This bridge does not add a new
byte-admission policy or unbounded waiting queue; the owning engine supplies its
quota token and exact limits.

## Product regression mapping and Linux verification

The controlled suite maps the accepted finite A19 retirement/result/mailbox
transitions into actual managed/native state. It does not repeat historical
model acceptance or treat finite exploration as a runtime proof.

| Model/contract transition | Product observation |
| --- | --- |
| Setup settlement before reclaim | Synchronous callback before native start returns; no result/reclaim until setup settles |
| Registration drain | Precancel before returned registration assignment; blocked live cancellation callback with concurrent native completion |
| Controls and native sources | Completion while ordinary control is held; transferred result while native source still prevents retirement |
| Single terminal/result owner | Duplicate native commit, duplicate terminal notice, repeated Task await, second typed take, conversion failure |
| Lost-wake counterexample | Actual A06 `CoordinatedCallback`: publish while its drain is scheduled, then unschedule; a second pass is mandatory without any timer/scanner rescue |
| Late wake, GC and unobserved work | Rejected/throwing scheduling, callback during close, 32 native-worker/cancel/GC iterations and a discarded Task's final release |
| Isolation and diagnostics | Indeterminate start, callback-then-reject, retire failure without another wake, and postcopy DomainError binding failure |

Run from the repository with the fixed toolchain:

```bash
python3 tests/Interop.Async/verify.py --offline
```

The runner builds the C++ assertions through the shared CMake preset, an actual downstream
cdylib export check, ordinary .NET execution, explicit `linux-x64` Native AOT
publish **and execution**, the minimal/advanced sample and compiled API/XML checks.
The native conformance fixture is explicitly enabled using `--features
conformance`; its symbols and A04 fixture dependency are absent from default
reusable engine linkage. Expected quarantine cases run in separate processes so
their intentionally retained unsafe-to-release owners cannot masquerade as the
normal suite's zero-live-owner result.

Receipts record source/lock/toolchain/native/executable hashes, actual command
outcomes, failed runs and bounded iteration counts. This Linux component result
does not claim Windows, mature-engine acceptance, performance improvement or
pristine package consumption. A14/A16 own the applicable package-composition and
consumer checks. The baseline costs include context/TCS/root, one reserved slot,
possible input copy and create/start/take/retire/release FFI; pooling and broad
performance claims require separate measurements.
