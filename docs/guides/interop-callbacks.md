# Callback registration and retirement

Task A06 implements the internal callback protocol from Design
`c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9`, C-CALLBACK-001 revision 0.4.0.
It adds no public application type or separately installable package. A domain's
statically compiled adapter supplies its typed subscription API; application
handlers do not manage unmanaged pointers or GCHandle cookies. The shared code
is compiled into `NativeWeave.Core`. The Rust crate is reused by each engine
that needs callbacks; a synchronous engine need not create a coordinator.

## Ownership and native operations

| Stage | Managed owner | Native operation and promise |
| --- | --- | --- |
| Create | Caller owns the unique native SafeHandle | Domain create returns a never-started subscription |
| Register | After successful construction, CallbackRegistration owns the A03 NativeResourceOwner, strong Normal GCHandle and one reserved coordinator slot | No callback is possible yet; constructor failure leaves the owner with its caller |
| Start | Static UCO table and complete managed state exist before native entry; a physical control lease and explicit setup sentinel survive until start returns | Domain start copies `nw_callbacks_v1`, commits once, then may synchronously call back before returning OK |
| Notify | Default profile reserves item and byte budgets before making one owned copy; the actual user handler runs on managed scheduling | Rust Subscription admits/counts a callback under its gate and invokes it after releasing that gate; payload is borrowed only until callback return |
| Close | Dispose closes managed ordinary-call and handler admission, discards queued events and requests stop without blocking | request_stop closes native production; callbacks and native source leases may still be live |
| Retire | Context retains its cookie, SafeHandle and active-handler copies | try_retire returns OK only after all native callbacks and sources have ended and future admission is permanently closed |
| Release | Setup has settled, all controls/native callback frames and active handlers have drained, pending copies are gone | Exact creator destructor runs once through A03; only then is the managed cookie freed and the reserved coordinator slot released |

Native `Subscription::reserve_source` must be used before work is handed to a
native worker/executor that can still access callback state. Its WorkLease is
independent from a currently executing callback lease. The core does not claim
that `active_callbacks == 0` alone means native quiescence.

Start failure before acceptance guarantees no retained or queued callback, so
the never-started owner can be released. Once a native start commits, it returns
OK even when its immediate callback reports failure. A managed/native call that
throws without an acceptance result is indeterminate: the coordinator retains
the owner and requires actual native quiet rather than choosing an unsafe
never-started release path.

## Notification and decision profiles

Ordinary notifications have an explicit maximum item count, aggregate retained
bytes and single-payload size. Active asynchronous handlers count against both
item and byte budgets. Exhaustion rejects the callback before accepting another
copy, stops the subscription and exposes the bridge failure through Completion.
No unbounded mailbox, implicit payload dropping or application continuation is
introduced into a raw reverse-P/Invoke frame. Empty payloads still consume an
item. Invalid lengths/null nonempty payloads are rejected before copying;
arbitrary native addresses cannot be made safe by validation or catch.

An explicit synchronous decision profile invokes its bounded handler within the
native callback. It returns fail-closed on false or an ordinary exception. It
uses a thread-local callback scope and must never synchronously wait for its
own engine to close. It is intentionally distinct from notification dispatch;
its timing/affinity contract belongs to the domain selecting that capability.

Notification handlers are serialized per subscription. Their asynchronous
execution scope propagates through await. Calling that same subscription's
DisposeAsync from its handler throws InvalidOperationException after requesting
stop; synchronous Dispose remains safe. This does not attempt to detect arbitrary
cycles deliberately constructed across threads or with ExecutionContext flow
suppressed. Detached application work is outside the joined handler scope and
must not retain a borrowed domain payload.

## Closure, failures and bounded progress

Explicit Dispose uses **discard pending, join active**. Closing the dispatcher
gate removes queued copied payloads and their budgets. An already admitted
handler retains its copy and root until its ValueTask actually finishes.
Native acknowledgment alone never completes DisposeAsync. Repeated disposal
joins the same Retirement task; Completion additionally exposes the original
handler/bridge failure after safe cleanup. A canceled external wait on Retirement
does not cancel cleanup or release live resources. A permanently hung handler
keeps its owner charged and its age/counters observable through the internal
snapshot.

An explicitly constructed, engine-owned CallbackCoordinator reserves a fixed
number of contexts. Each context is one coalescing IThreadPoolWorkItem, and one
timer per coordinator retries rejected scheduling or PENDING retirement. Idle
active subscriptions do not enqueue periodic work. Dirty/scheduled transitions
share one gate, so a wake during drain cannot be erased by a stale unschedule.
No Task is created per retry. Closing the coordinator rejects reservations and
keeps existing slots rooted until their real cleanup; the timer stops after the
last existing slot drains. A trusted cleanup-call exception quarantines the
context instead of releasing it or repeatedly busy-looping.

Callback failure recording uses fixed state and retains at most the first
exception; it performs no logging/string formatting in UCO. Error construction
and managed Task fault publication occur on the coordinator. Invalid UCO
signatures, invalid pointers, abort and process-corrupting failures are not
recoverable by a catch inside the method body.

## Costs and compatibility

Per subscription: one managed context, Normal GCHandle, preallocated queue,
completion/retirement sources and reserved slot; per coordinator: a bounded
slot array and timer. Each accepted ordinary notification makes one managed
byte-array copy plus bounded queue bookkeeping. One handler runs at a time;
an incomplete asynchronous handler has its normal managed async state. A native
callback does one admission and one release; generated native control calls
use A03's caller-local lease flag. This is an allocation/copy accounting, not a
throughput or universal performance claim.

The application API baseline remains unchanged: all new managed types are
internal, and callback records reuse the exact A01 layout. The Rust native
conformance exports use a fixture prefix; real engines supply their own versioned
operation prefixes. No ABI-table replacement, cookie reuse while live, default
Tokio runtime, new release package or other-platform support is added.

## Examples and verification

The [maintainer sample](../../samples/api/interop-callbacks/Program.cs) exercises
minimal copied notifications and advanced asynchronous-handler closure with an
actual native fixture. The [generated internal reference](../../src/Interop.Callbacks/api-reference.md)
is checked against the compiled Core assembly and XML. The sample is component
code with an explicit friend assembly, not an installable public subscription
facade. A16 owns pristine packed-consumer acceptance and domain tasks own their
typed public handlers.

```sh
python3 tests/Interop.Callbacks/verify.py
```

The Linux x64 runner records exact source/lock/compiler/native/consumer identities,
Rust unit and concurrency tests, ordinary JIT and actual Native AOT execution,
bounded-queue/control/GC/stop races, copied and asynchronous handler lifetimes,
registration rejection and isolated compile/runtime invalid-UCO negatives. The
finite GC matrix is 32 iterations with three producers and 64 attempts per
producer; the native admission race has 32 iterations with four producers and
128 attempts. These are bounded component regressions, not a model proof, a
long-running soak, a complete domain engine, or Windows acceptance. Evidence
state is product-executed only when the corresponding recorded runner succeeds.
