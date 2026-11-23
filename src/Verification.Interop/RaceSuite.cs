using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Korda.Interop;
using Korda.Interop.Abi;
using Korda.Interop.Async;
using Korda.Interop.Callbacks;
using Korda.Interop.Lifetime;

namespace Korda.Verification.Interop;

/// <summary>Concrete refinements of bounded D04-D09 schedules using real gates, native leases and reverse callbacks.</summary>
internal static class RaceSuite
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    internal static async Task Run()
    {
        await Admission();
        await SetupBoundaries();
        await EnumeratedOrders();
        await CallbackDrain();
        await Mailbox();
        await ResultFailures();
        Check.That(AsyncNative.Count(0) == 0 && AsyncNative.Count(3) == 0 && AsyncNative.Count(4) == 0 && AsyncNative.Count(5) == 0 && AsyncNative.Count(6) == 0, "async native objects/input/backings returned; no invalid release");
        Check.That(CallbacksNative.Count(0) == CallbacksNative.Count(1) && CallbacksNative.Count(2) == 0, "callback creator/destructor balance");
        Check.That(MemoryNative.Counter(0) == 0 && MemoryNative.Counter(2) == 0 && MemoryNative.Counter(4) == 0, "lifetime memory owners/capacity returned");
        Console.WriteLine("RESOURCE native-operations=0 callback-owners=0 memory-owners=0 backings=0 retained-input=0 bad-releases=0; managed roots observed per retirement");
    }

    private static async Task Finish(AsyncOperation operation)
    {
        await operation.Retirement.WaitAsync(Deadline);
        var s = operation.Snapshot;
        Check.That(s.SetupSettled && s.RegistrationReady && s.RegistrationDrained && s.NativeQuiet && s.Released && !s.RootLive && !s.Quarantined && s.Controls == 0 && s.CallbackEntries == 0 && s.RetainedInputBytes == 0, "independent managed/native retirement facts");
    }
    private static async Task Fail<T>(Task task) where T : Exception
    {
        try { await task.WaitAsync(Deadline); }
        catch (T) { Check.That(true, typeof(T).Name); return; }
        throw new InvalidOperationException("Missing " + typeof(T).Name);
    }

    private static async Task Admission()
    {
        foreach (LeaseBoundary boundary in Enum.GetValues<LeaseBoundary>())
        {
            using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim(); using var disposeEntered = new ManualResetEventSlim();
            var handle = MemoryNative.Create([1, 2, 3]);
            var owner = new NativeResourceOwner(handle);
            owner.TestBoundary = value => { if (value == boundary) { entered.Set(); Check.That(resume.Wait(Deadline), "lease barrier resumes"); } };
            bool acquired = false;
            Task call = Task.Run(() => { _ = owner.Acquire(ref acquired); });
            Check.That(entered.Wait(Deadline), "actual lease boundary entered");
            Task disposing = Task.Run(() => { disposeEntered.Set(); owner.Dispose(); });
            Check.That(disposeEntered.Wait(Deadline) && !disposing.IsCompleted, "Dispose cannot pass atomic admission gate");
            resume.Set(); await call.WaitAsync(Deadline); await disposing.WaitAsync(Deadline);
            owner.TestBoundary = null;
            Check.That(owner.IsClosing && owner.ActiveLeases == 1 && !owner.Retirement.IsCompleted && MemoryNative.Counter(0) == 1, "old physical lease remains after logical close");
            bool later = false;
            Check.Throws<ObjectDisposedException>(() => owner.Acquire(ref later), "new logical admission rejected");
            owner.Release(ref acquired); owner.Release(ref acquired); owner.Dispose();
            await owner.Retirement.WaitAsync(Deadline);
            Check.That(MemoryNative.Counter(0) == 0, "one creator release after final lease");
            using var failing = new NativeResourceOwner(MemoryNative.Create([1]));
            failing.TestBoundary = value => { if (value == boundary) throw new FormatException("acquire fault"); };
            bool flag = false;
            Check.Throws<FormatException>(() => failing.Acquire(ref flag), "injected lease boundary failure");
            Check.That(!flag && failing.ActiveLeases == 0, "logical and physical compensation");
        }
        // Safe negative control: SafeHandle alone still admits a physical reference
        // after Dispose while an earlier reference is live; no freed pointer is touched.
        var unsafeAdmission = MemoryNative.Create([1]); bool first = false, after = false;
        unsafeAdmission.DangerousAddRef(ref first); unsafeAdmission.Dispose();
        unsafeAdmission.DangerousAddRef(ref after);
        Check.That(after, "negative logical-admission guard is non-vacuous");
        unsafeAdmission.DangerousRelease(); unsafeAdmission.DangerousRelease();
        Console.WriteLine("SCHEDULE admission:4 gate/Dispose barriers + 4 rollback faults; threads=2 live-calls=1; SafeHandle-only negative detected");
    }

    private static async Task SetupBoundaries()
    {
        foreach (AsyncBoundary boundary in new[] { AsyncBoundary.BeforeRegister, AsyncBoundary.Registered, AsyncBoundary.BeforeStart, AsyncBoundary.StartReturned, AsyncBoundary.CancelEntered, AsyncBoundary.TerminalFieldsCopied, AsyncBoundary.BeforeTake })
        {
            using var coordinator = new CallbackCoordinator(1);
            using var cancellation = new CancellationTokenSource();
            using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
            var operation = new AsyncOperation<NativeBuffer>(AsyncNative.New(boundary is AsyncBoundary.CancelEntered or AsyncBoundary.TerminalFieldsCopied ? 0U : 2U), new OperationCalls(), coordinator,
                AsyncNative.TakeBuffer, cancellation.Token, testBoundary: value =>
                {
                    if (value == boundary) { entered.Set(); Check.That(resume.Wait(Deadline), "stage barrier resumes"); }
                });
            Task start = Task.Run(() => { _ = operation.Start(); });
            Task? cancel = null;
            if (boundary is AsyncBoundary.CancelEntered or AsyncBoundary.TerminalFieldsCopied)
            {
                await start.WaitAsync(Deadline);
                if (boundary == AsyncBoundary.CancelEntered) cancel = Task.Run(cancellation.Cancel);
                else Check.That(AsyncNative.Spawn(operation) == 0, "actual Rust worker starts");
            }
            Check.That(entered.Wait(Deadline), "selected actual async boundary entered " + boundary);
            Check.That(operation.Snapshot.RootLive && !operation.Retirement.IsCompleted, "root remains at " + boundary);
            if (boundary == AsyncBoundary.CancelEntered) Check.That(AsyncNative.Complete(operation) == 0, "success while cancellation callback is blocked");
            operation.Dispose();
            GC.Collect(); GC.WaitForPendingFinalizers();
            Check.That(operation.Snapshot.RootLive, "GC reachability separate from native counters " + boundary);
            resume.Set(); await start.WaitAsync(Deadline); if (cancel is not null) await cancel.WaitAsync(Deadline);
            if (boundary is AsyncBoundary.BeforeRegister or AsyncBoundary.Registered or AsyncBoundary.BeforeStart) await Fail<ObjectDisposedException>(operation.Task);
            else { using NativeBuffer result = await operation.Task.WaitAsync(Deadline); Check.That(result.Length == 3, "success preserved after closure"); }
            await Finish(operation);
        }
        foreach (AsyncBoundary point in new[] { AsyncBoundary.BeforeRoot, AsyncBoundary.RootAllocated })
        {
            using var coordinator = new CallbackCoordinator(1); using var owner = AsyncNative.New();
            Check.Throws<OutOfMemoryException>(() => _ = new AsyncOperation<NativeBuffer>(owner, new OperationCalls(), coordinator, AsyncNative.TakeBuffer,
                testBoundary: b => { if (b == point) throw new OutOfMemoryException("explicit construction fault"); }), "construction fault boundary");
            Check.That(coordinator.Count == 0, "caller retains pre-construction owner");
        }
        using (var coordinator = new CallbackCoordinator(1))
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); bool synchronous = false; AsyncOperation<NativeBuffer>? operation = null;
            operation = new(AsyncNative.New(), new OperationCalls(), coordinator, AsyncNative.TakeBuffer, cancellation.Token,
                testBoundary: b => { if (b == AsyncBoundary.CancelEntered) synchronous = !operation!.Snapshot.RegistrationReady; });
            await Fail<OperationCanceledException>(operation.Start()); await Finish(operation);
            Check.That(synchronous && operation.Snapshot.Start == AsyncStartOutcome.Skipped, "UnsafeRegister before assignment");
        }
        Console.WriteLine("SCHEDULE async-stages:7 controlled barriers, 2 constructor faults, pre-canceled registration; operations=1 producer=1 queue-slots=1");
    }

    /// <summary>Exhausts only the declared 6 action orders x 4 drain positions x 3 native outcomes.</summary>
    private static async Task EnumeratedOrders()
    {
        string[] orders = ["NCD", "NDC", "CND", "CDN", "DNC", "DCN"];
        int schedules = 0;
        foreach (string order in orders) foreach (int drain in Enumerable.Range(0, 4)) foreach (uint status in new uint[] { 0, 2, 13 })
        {
            using var coordinator = new CallbackCoordinator(1, TimeSpan.FromMinutes(1));
            using var cancellation = new CancellationTokenSource();
            var scheduler = new ManualScheduler(); var calls = new OperationCalls();
            var operation = new AsyncOperation<NativeBuffer>(AsyncNative.New(), calls, coordinator, AsyncNative.TakeBuffer, cancellation.Token, scheduler: scheduler);
            Task<NativeBuffer> result = operation.Start();
            using SourceHandle source = AsyncNative.HoldSource(operation);
            bool control = false; nint pointer = operation.AcquireControl(ref control);
            try
            {
                for (int i = 0; i <= 3; i++)
                {
                    if (i == drain) scheduler.Run();
                    if (i == 3) break;
                    char action = order[i];
                    await Task.Run(() =>
                    {
                        if (action == 'N') Check.That(AsyncNative.CompleteNative(pointer, status) == 0, "native unique terminal");
                        else if (action == 'C') cancellation.Cancel();
                        else operation.Dispose();
                    });
                }
                Check.That(AsyncNative.CompleteNative(pointer, status == 0 ? 2U : 0U) == 4, "duplicate conflicting terminal rejected");
                Check.That(AsyncNative.Duplicate(pointer) == 8, "duplicate live-cookie notification contained");
                scheduler.Run();
                Check.That(!result.IsCompleted && operation.Snapshot.RootLive, "control barrier prevents materialization/reclamation");
            }
            finally { operation.ReleaseControl(ref control); }
            scheduler.Run();
            NativeBuffer? owned = null;
            if (status == 0) owned = await result.WaitAsync(Deadline);
            else if (status == 2) await Fail<OperationCanceledException>(result);
            else await Fail<NativeEngineException>(result);
            Check.That(!operation.Retirement.IsCompleted && operation.Snapshot.RootLive && !operation.Snapshot.NativeQuiet, "published Task does not discharge native source");
            source.Dispose(); operation.Retry(); scheduler.Run(); await Finish(operation);
            if (owned is not null) { Check.That(owned.ToArray().AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }), "transferred result survives operation release"); owned.Dispose(); }
            Check.That(operation.Snapshot.NativeStatus == status && operation.Snapshot.DuplicateNotices == 1 && calls.Poisoned == 1 && scheduler.Maximum == 1, "finite order preserves outcome and bounded mailbox");
            Console.WriteLine($"TRACE order={order} drain={drain} terminal={status} nativeQuiet=1 managedRoot=0 released=1");
            schedules++;
        }
        Check.That(schedules == 72, "all declared bounded combinations executed");
    }

    private static async Task CallbackDrain()
    {
        for (int order = 0; order < 2; order++)
        {
            using var coordinator = new CallbackCoordinator(1);
            var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handlerExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int delivered = 0;
            var subscription = new CallbackRegistration(CallbacksNative.New(), new SubscriptionCalls(), coordinator, 2, 10, 5,
                handler: async (_, bytes) => { Check.That(bytes.Length <= 5, "bounded borrowed-to-owned callback"); Interlocked.Increment(ref delivered); handlerEntered.SetResult(); await handlerExit.Task; });
            subscription.Start(); bool control = false; nint pointer = subscription.AcquireControl(ref control);
            Task<uint> producer = Task.Run(() => CallbacksNative.GatedEmit(pointer));
            await Check.Until(() => CallbacksNative.Gate(pointer, 0) == 0, "native callback admitted before managed entry");
            if (order == 0) subscription.Dispose();
            Check.That(CallbacksNative.Gate(pointer, 1) == 0, "admitted native callback resumes");
            Check.That(await producer.WaitAsync(Deadline) == 0, "admitted callback drains safely");
            if (order == 1)
            {
                await handlerEntered.Task.WaitAsync(Deadline);
                Check.That(CallbacksNative.Emit(subscription, [1, 2, 3, 4, 5]) == 0, "second notification queued");
                subscription.Dispose();
                Check.That(subscription.Snapshot.Pending == 0 && subscription.Snapshot.ActiveHandlers == 1 && subscription.Snapshot.RetainedItems == 1, "close discards queue but charges active handler");
            }
            Check.That(subscription.Snapshot.RootLive && !subscription.Retirement.IsCompleted, "native/managed control barrier keeps callback root");
            subscription.ReleaseControl(ref control); handlerExit.TrySetResult();
            await subscription.Retirement.WaitAsync(Deadline); await subscription.Completion.WaitAsync(Deadline);
            Check.That(!subscription.Snapshot.RootLive && subscription.Snapshot.RetainedBytes == 0 && delivered == order, "both stop/admission orders retire exactly");
        }
        using (var coordinator = new CallbackCoordinator(1))
        {
            CallbackRegistration? subscription = null; bool selfWait = false;
            subscription = new(CallbacksNative.New(2), new SubscriptionCalls(), coordinator, 2, 32, 32, decision: (_, _) =>
            {
                subscription!.Dispose();
                try { _ = subscription.DisposeAsync(); } catch (InvalidOperationException) { selfWait = true; }
                throw new FormatException("ordinary decision exception");
            });
            subscription.Start(); await subscription.Retirement.WaitAsync(Deadline); await Fail<FormatException>(subscription.Completion);
            Check.That(selfWait && !subscription.Snapshot.RootLive, "synchronous start exception and self-close drain");
        }
        Console.WriteLine("SCHEDULE callback-stop:before-entry/active-handler, queued discard, synchronous start/reentrant close/exception; callbacks=1 queued=1 active-handler=1 bytes<=10");
    }

    private static async Task Mailbox()
    {
        for (int stage = 0; stage < 4; stage++)
        {
            var scheduler = new ManualScheduler();
            using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
            var probe = new MailboxProbe(scheduler, () => { entered.Set(); Check.That(resume.Wait(Deadline), "mailbox drain barrier"); });
            probe.Signal(); if (stage == 0) probe.Signal();
            Task running = Task.Run(scheduler.RunOne);
            Check.That(entered.Wait(Deadline), "mailbox actual Process entered");
            if (stage == 1) probe.Signal();
            if (stage == 2) { probe.Signal(); probe.Signal(); }
            resume.Set(); await running.WaitAsync(Deadline);
            if (stage == 3) probe.Signal();
            scheduler.Run();
            Check.That(probe.Processed == (stage == 0 ? 1 : 2) && scheduler.Maximum == 1 && scheduler.Count == 0, "no lost wake at store/drain/unschedule boundary");
        }
        var rejecting = new ManualScheduler { Reject = 2 };
        var retried = new MailboxProbe(rejecting, () => { });
        retried.Signal(); retried.Retry(); Check.That(retried.SchedulingRejections == 2, "fixed scheduling rejection count");
        retried.Retry(); rejecting.Run(); Check.That(retried.Processed == 1 && rejecting.Maximum == 1, "rejected enqueue preserves one dirty slot");
        Console.WriteLine("SCHEDULE mailbox:4 wake positions + 2 rejected scheduling attempts; concrete A06/A07 slot, one queued record");
    }

    private static async Task ResultFailures()
    {
        foreach (int fault in Enumerable.Range(0, 4))
        {
            using var coordinator = new CallbackCoordinator(1);
            var operation = new AsyncOperation<NativeBuffer>(AsyncNative.New(fault == 3 ? 18U : 2U), new OperationCalls(), coordinator,
                p => AsyncNative.TakeBuffer(p, throwAfterTake: fault == 1), testBoundary: b =>
                {
                    if ((fault == 0 && b == AsyncBoundary.BeforeTake) || (fault == 2 && b == AsyncBoundary.TerminalFieldsCopied)) throw new FormatException("declared conversion fault");
                });
            Task task = operation.Start();
            if (fault == 0) await Fail<FormatException>(task);
            else if (fault == 1) await Fail<ApplicationException>(task);
            else await Fail<NativeEngineException>(task);
            await Finish(operation);
            Check.That(AsyncNative.Count(5) == 0 && AsyncNative.Count(6) == 0, "taken/untaken result failure returned owner capacity");
        }
        // Parent backing remains charged until the independently owned child view drops.
        using BufferHandle parent = MemoryNative.Create([1, 2, 3, 4], 512);
        bool lease = false; parent.DangerousAddRef(ref lease); nint child;
        try { Check.That(MemoryNative.Slice(parent.DangerousGetHandle(), 1, 2, out child) == 0, "real retained child slice"); }
        finally { parent.DangerousRelease(); }
        using var childHandle = new BufferHandle(child);
        using NativeBuffer childBuffer = MemoryNative.Adopt(childHandle); parent.Dispose();
        Check.That(MemoryNative.Counter(0) == 1 && MemoryNative.Counter(4) >= 516 && childBuffer.ToArray().AsSpan().SequenceEqual(new byte[] { 2, 3 }), "child retains full parent backing charge after parent closure");
        Console.WriteLine("SCHEDULE owner-transfer:before-take/after-take/terminal-copy/take-error, parent-close-with-child");
    }
}

/// <summary>Bounded deterministic scheduler for the real coordinator slot; it never executes work inline.</summary>
internal sealed class ManualScheduler : ICallbackScheduler
{
    private readonly ConcurrentQueue<IThreadPoolWorkItem> _queue = new();
    internal int Reject;
    internal int Maximum;
    internal int Count => _queue.Count;
    public bool TryQueue(IThreadPoolWorkItem item)
    {
        if (Reject > 0) { Reject--; return false; }
        _queue.Enqueue(item); Maximum = Math.Max(Maximum, _queue.Count); return true;
    }
    internal void RunOne() { if (_queue.TryDequeue(out var item)) item.Execute(); }
    internal void Run()
    {
        int budget = 32;
        while (!_queue.IsEmpty) { if (--budget == 0) throw new InvalidOperationException("Bounded scheduling step did not quiesce."); RunOne(); }
    }
}

internal sealed class MailboxProbe(ManualScheduler scheduler, Action barrier) : CoordinatedCallback(scheduler)
{
    internal int Processed;
    protected override void Process() { if (++Processed == 1) barrier(); }
    protected override void SchedulingFault(Exception error) => throw new InvalidOperationException("Mailbox probe failed.", error);
}
