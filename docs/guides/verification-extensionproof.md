# Linux affinity and platform-boundary conformance

A23 provides an **internal maintainer probe** for the available Linux x64 ownership
profile. Its real Rust state is `Rc<Cell<u64>>`, confined to the creating OS thread's
TLS. A separate Send/Sync control record carries a bounded command queue, the A06
`Subscription`, status counters and owner identity. No `unsafe impl Send/Sync` is
added for the affine state, and no private window, device or driver layout is copied.
The loaded descriptor advertises only `affinity.owner.v1`, with `ExternalLoop`:
the host supplies explicit progress, and loading the module starts no runtime.

This headless probe does not create a Winit event loop, window, wgpu device, surface,
GPU submission or fence. Those D21 rows need actual supported hardware/backend and
remain unavailable. Their absence is not simulated as device loss or successful
fence completion. Windows, mobile, static linkage and WASM are separate unsupported
or deferred profiles. There is no public GUI/GPU/mobile API or new package release.

## Internal use and progress

The minimal example in `samples/api/verification-extensionproof` constructs an
`AffinityProbe` on its stable owner thread, explicitly calls `Start`, creates a typed
`OwnedWork`, posts it, and calls `Pump` with a finite budget. It reads the real native
cell on that same thread. All application-facing types remain internal to the
conformance assembly; the example has an explicit assembly friend grant.

The advanced tests show a cross-thread producer, bounded queue saturation/retry,
wrong-thread access, an asynchronous managed notification held across close and an
actual A03 control lease held across close. Normal producers never access the Rc.
The native entry checks `ThreadId` before any TLS lookup or Cell access. A wrong-thread
read/pump returns `NOT_SUPPORTED` and leaves output/state unchanged.

Each instance admits one through eight queued fixed-size commands, each containing
one `u64` value. At most eight affine instances exist on one owner thread. A successful
post transfers the command's unique creator owner into the native queue and reserves
an A06 source lease. `PENDING` means full; `INVALID_STATE` means closed. Both retain the
original caller owner, which may be retried or disposed. The scoped managed adapter
uses A03 physical leases and a success-only SafeHandle ownership handoff.

`Pump(1..8)` executes at most its budget; addition uses explicitly wrapping `u64`
arithmetic. Completion and close counters are separate: every accepted command either
executes once or is canceled once when close drains the queue. A06 notifications are
copied into its existing bounded managed dispatcher (eight items, 64 retained payload
bytes, eight bytes per notification). Notification delivery may be discarded by close;
that does not erase the native command's recorded terminal outcome.

## Closing and destruction

`Dispose` closes managed ingress and requests the existing A06 cleanup. It does not
block the owner thread. Native close cancels queued commands and drops their source
leases; a native callback lease, native source, admitted control call or active managed
handler still delays its corresponding retirement proof. A06 separately drains native
quiet, callbacks, controls, queued notifications and active asynchronous handlers.
Only then does its A03 owner dispose the main SafeHandle and release the managed root. The separate `Completion` task preserves handler/bridge failure after cleanup; successful retirement is not substituted for successful handler execution.

That SafeHandle's destructor is bounded and callable from any thread. It only closes
the thread-safe control, requests deferred release and drops its own control reference.
It never touches or frees the Rc. An independent metadata observer keeps `ClosePending`
visible after managed callback retirement. The owner must pump again; only then, after
all queue/native/managed/control obligations have ended, does the native implementation
remove and drop the Rc on its owner thread. The observer can then be released with
`TryFinishClose`. A real SafeHandle finalizer is tested through this same release path on a never-started control. Active A06 registrations require explicit close; their strong callback roots are not treated as collectible resources.

A synchronous wait for work that requires this same owner's pump is invalid. The
probe's bounded test waits join only work that can independently progress; it pumps
explicitly to finish affine retirement. If the owner never progresses, cleanup remains
pending. If its OS thread exits while affine roots remain, the TLS destructor closes
thread-safe ingress and conservatively retains those bounded per-thread roots for
process teardown. It does not invent safe immediate cleanup after loss of the owner.
There is no forced unload or forced resource destruction path.

Fixed Box/Arc/Rc allocations and process OOM remain infallible abort boundaries.
Queue/TLS growth uses fallible reservation within declared count bounds. These limits
do not bound all caller-owned unaccepted commands, all threads, allocator retention or
whole-process RSS. Unwind containment reuses A05; an indeterminate state remains safely
retained rather than being declared quiescent.

## Capability, source and cost evidence

The native adapter reuses the accepted A01 ABI/bootstrap, A03 control lease model,
A06 `Subscription`/`WorkLease` and managed `CallbackRegistration`/coordinator, and A05
panic boundary. No new common lifecycle algorithm or global runtime is introduced.
A13 registers the optional Cargo member and two internal friend grants for Core/ABI.
The internal probe is not compiled into mandatory Core.

Each instance adds its native thread-safe control, owner-thread Rc, explicit observer,
bounded queue and existing one-slot managed callback coordinator. Each accepted
notification adds one eight-byte payload copy; commands remain creator-owned until
execution or cancellation. The finite receipt records binary/executable sizes,
source and toolchain identities and actual owner/callback counters. No throughput,
allocation-free, zero-copy or RSS superiority claim is made.

Design input is the exact Plan-pinned Design commit and hashes in
`src/Verification.ExtensionProof/design-inputs.json`, including D21 and its R32/R33
source studies. The platform source studies are reused as requirements, not relabelled
as device execution. No device-support inference is made merely from upstream APIs.

## Verification

Use the pinned Linux toolchain and a spacious temporary directory, then run:

```sh
python3 tests/Verification.ExtensionProof/verify.py --output artifacts/a23-linux
```

The runner checks native format/lints and actual cross-thread C ABI behavior, linked
export isolation, exact compiled API/XML (zero public runtime symbols), normal and
actual Native AOT tests and the internal sample. `--prepare` refreshes candidate locks
and reference output without claiming acceptance. Final receipts bind a committed,
unchanged source snapshot. Tests include actual owner access, wrong-thread rejection,
full/closed ownership preservation, accepted-post/close races, held managed handlers,
A03 control leases and the finalizer's deferred-release path. Final live affine,
control, observer and command counts must be zero in the executed cases.

This verification-only assembly is not distributed as a public NuGet component, so
an independent new-package row is inapplicable. A16 remains responsible for the actual
package closure. Device/window/fence and other-platform rows stay separately recorded.
