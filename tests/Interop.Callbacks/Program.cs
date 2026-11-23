using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Korda.Interop.Abi;
using Korda.Interop.Callbacks;

internal sealed class ManualScheduler : ICallbackScheduler
{
    private readonly ConcurrentQueue<IThreadPoolWorkItem> _queue = new();
    internal int Reject;
    internal int Accepted;
    internal int MaximumQueued;
    public bool TryQueue(IThreadPoolWorkItem item)
    {
        if (Interlocked.Decrement(ref Reject) >= 0) return false;
        _queue.Enqueue(item);
        Interlocked.Increment(ref Accepted);
        MaximumQueued = Math.Max(MaximumQueued, _queue.Count);
        return true;
    }
    internal bool RunOne()
    {
        if (!_queue.TryDequeue(out IThreadPoolWorkItem? item)) return false;
        item.Execute();
        return true;
    }
    internal void Run(int bound = 32)
    {
        for (int i = 0; i < bound && RunOne(); i++) { }
    }
}

internal static class Program
{
    private static int s_assertions;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static void Check(bool value, string message)
    {
        Interlocked.Increment(ref s_assertions);
        if (!value) throw new InvalidOperationException(message);
    }
    private static async Task Until(Func<bool> condition, ManualScheduler? scheduler = null)
    {
        long deadline = Environment.TickCount64 + 15000;
        while (!condition())
        {
            scheduler?.Run();
            if (Environment.TickCount64 > deadline) throw new TimeoutException("Controlled callback test did not progress.");
            await Task.Delay(1);
        }
    }
    private static async Task Complete(CallbackRegistration registration, ManualScheduler? scheduler = null)
    {
        registration.Dispose();
        await Until(() => registration.Retirement.IsCompleted, scheduler);
        await registration.DisposeAsync();
        CallbackSnapshot snapshot = registration.Snapshot;
        Check(snapshot.Released && !snapshot.RootLive && snapshot.NativeQuiet && snapshot.SetupSettled,
            "Retirement must release only after native/setup/root proof.");
        Check(snapshot.Controls == 0 && snapshot.NativeEntries == 0 && snapshot.Pending == 0 &&
            snapshot.ActiveHandlers == 0 && snapshot.RetainedItems == 0 && snapshot.RetainedBytes == 0,
            "Every control/callback/dispatcher budget must drain.");
    }

    private static async Task CopiedNotificationAndClosure()
    {
        using var coordinator = new CallbackCoordinator(4);
        var scheduler = new ManualScheduler();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int invoked = 0;
        byte observed = 0;
        var registration = new CallbackRegistration(Fixture.New(), new FixtureCalls(), coordinator,
            maxItems: 3, maxBytes: 12, maxPayload: 8,
            handler: async (_, payload) =>
            {
                Check(!Fixture.InNativeCall, "Ordinary handler ran in a native callback frame.");
                observed = payload.Span[0];
                Interlocked.Increment(ref invoked);
                entered.SetResult();
                await finish.Task;
                Check(payload.Span[0] == observed, "Borrowed input escaped instead of an owned copy.");
            }, scheduler: scheduler);
        registration.Start();
        byte[] bytes = [5, 2, 3];
        Check(Fixture.Emit(registration, bytes) == NativeStatus.Ok, "Notification rejected.");
        bytes[0] = 99;
        Check(registration.Snapshot.Pending == 1, "Event was not queued independently of native callback.");
        scheduler.Run();
        await entered.Task.WaitAsync(Timeout);
        Check(observed == 5, "Input copy must precede callback return.");
        Check(Fixture.Emit(registration, [7, 8, 9]) == NativeStatus.Ok, "Second event rejected.");
        registration.Dispose();
        scheduler.Run();
        CallbackSnapshot pending = registration.Snapshot;
        Check(pending.Pending == 0 && pending.ActiveHandlers == 1 && pending.RetainedBytes == 3,
            "Dispose must discard pending, retain active bytes and join active.");
        Check(pending.NativeQuiet && pending.RootLive && !registration.Retirement.IsCompleted,
            "Native acknowledgment cannot retire an asynchronous managed handler.");
        Check(Fixture.Emit(registration, [1]) == NativeStatus.InvalidState, "Closed managed control admission accepted.");
        finish.SetResult();
        await Complete(registration, scheduler);
        await registration.Completion;
        Check(invoked == 1 && coordinator.Count == 0, "Discarded queued event started after closure.");
    }

    private static async Task SynchronousStartAndSelfStop()
    {
        using var coordinator = new CallbackCoordinator(2);
        CallbackRegistration? registration = null;
        ulong releasedBefore = Fixture.Count(1);
        int callbacks = 0;
        var native = new FixtureCalls
        {
            AfterStart = () =>
            {
                Check(registration!.Snapshot.RootLive && !registration.Snapshot.SetupSettled,
                    "Start-return handshake was not retained.");
                GC.Collect(); GC.WaitForPendingFinalizers();
                Check(Fixture.Count(1) == releasedBefore, "Native owner freed before start returned.");
            }
        };
        registration = new CallbackRegistration(Fixture.New(2), native, coordinator, 1, 64, 64,
            decision: (_, bytes) =>
            {
                Check(bytes.SequenceEqual("synchronous-start"u8), "Synchronous borrowed payload mismatch.");
                callbacks++;
                registration!.Dispose();
                try { registration.DisposeAsync(); throw new Exception("Expected decision self-wait rejection."); }
                catch (InvalidOperationException) { }
                Check(registration.Snapshot.RootLive && !registration.Retirement.IsCompleted,
                    "Synchronous callback freed its own context.");
                return true;
            });
        registration.Start();
        await Complete(registration);
        await registration.Completion;
        Check(callbacks == 1 && native.Stops == 1, "Start/reentrant stop transferred ownership more than once.");
    }

    private static async Task AsyncHandlerSelfWaitAndFailure()
    {
        using var coordinator = new CallbackCoordinator(2);
        CallbackRegistration? registration = null;
        bool rejected = false;
        registration = new CallbackRegistration(Fixture.New(), new FixtureCalls(), coordinator, 2, 64, 32,
            handler: async (_, _) =>
            {
                await Task.Yield();
                try { await registration!.DisposeAsync(); }
                catch (InvalidOperationException) { rejected = true; }
                throw new ApplicationException("observable-handler-failure");
            });
        registration.Start();
        Check(Fixture.Emit(registration, [1]) == NativeStatus.Ok, "Async handler event rejected.");
        await registration.Retirement.WaitAsync(Timeout);
        Check(rejected, "Self-wait detection lost identity across await.");
        try { await registration.Completion; throw new Exception("Handler failure was lost."); }
        catch (ApplicationException error) { Check(error.Message == "observable-handler-failure", "Wrong handler failure."); }
        await Complete(registration);

        var decision = new CallbackRegistration(Fixture.New(2), new FixtureCalls(), coordinator, 1, 64, 64,
            decision: static (_, _) => throw new ApplicationException("decision-failure"));
        decision.Start(); // accepted native start remains OK after callback failure
        await decision.Retirement.WaitAsync(Timeout);
        try { await decision.Completion; throw new Exception("Decision failure was lost."); }
        catch (ApplicationException) { }
        await Complete(decision);
    }

    private static async Task RegistrationFailureAndCapacity()
    {
        using var coordinator = new CallbackCoordinator(1);
        var calls = new FixtureCalls();
        var rejected = new CallbackRegistration(Fixture.New(1), calls, coordinator, 1, 64, 64,
            handler: static (_, _) => ValueTask.CompletedTask);
        try { rejected.Start(); throw new Exception("Expected native start rejection."); }
        catch (ArgumentException) { }
        await Complete(rejected);
        try { await rejected.Completion; } catch (ArgumentException) { }
        Check(calls.Stops == 0 && calls.Retires == 0, "Never-started rejection waited for nonexistent callback obligations.");

        var first = new CallbackRegistration(Fixture.New(), new FixtureCalls(), coordinator, 1, 64, 64,
            handler: static (_, _) => ValueTask.CompletedTask);
        using (var callerOwned = Fixture.New())
        {
            try
            {
                _ = new CallbackRegistration(callerOwned, new FixtureCalls(), coordinator, 1, 64, 64,
                    handler: static (_, _) => ValueTask.CompletedTask);
                throw new Exception("Expected capacity rejection.");
            }
            catch (InvalidOperationException) { Check(!callerOwned.IsClosing, "Rejected registration stole the native owner."); }
        }
        await Complete(first); // no Start: no registration/callback to wait for
        await first.Completion;
    }

    private static async Task BoundedQueueAndRetry()
    {
        using var coordinator = new CallbackCoordinator(2);
        var scheduler = new ManualScheduler { Reject = 3 };
        int handled = 0;
        var registration = new CallbackRegistration(Fixture.New(), new FixtureCalls(), coordinator, 2, 8, 8,
            handler: (_, _) => { handled++; return ValueTask.CompletedTask; }, scheduler: scheduler);
        registration.Start();
        Check(Fixture.Emit(registration, [1, 2, 3, 4]) == NativeStatus.Ok, "First bounded event rejected.");
        for (int i = 0; i < 1000; i++) registration.Signal();
        Check(registration.SchedulingRejections >= 3 && scheduler.MaximumQueued == 1,
            "Rejected scheduling or duplicate wakes lost bounded one-slot bookkeeping.");
        scheduler.Run();
        Check(handled == 1, "Queued event lost after scheduling failure.");
        Check(Fixture.Emit(registration, [1, 2, 3, 4, 5, 6]) == NativeStatus.Ok, "Byte budget first event rejected.");
        Check(Fixture.Emit(registration, [7, 8, 9]) == NativeStatus.CallbackFailed, "Byte capacity must reject before acceptance.");
        Check(registration.Snapshot.RetainedBytes == 0, "Failure must discard pending owned copies.");
        await Complete(registration, scheduler);
        try { await registration.Completion; } catch (Korda.Interop.NativeEngineException) { }
        Check(handled == 1, "Overflow-close dispatched discarded data.");

        var itemScheduler = new ManualScheduler();
        var itemBound = new CallbackRegistration(Fixture.New(), new FixtureCalls(), coordinator, 1, 16, 16,
            handler: static (_, _) => ValueTask.CompletedTask, scheduler: itemScheduler);
        itemBound.Start();
        Check(Fixture.Emit(itemBound, []) == NativeStatus.Ok, "Zero-byte event rejected.");
        Check(Fixture.Emit(itemBound, []) == NativeStatus.CallbackFailed, "Zero-byte events bypassed item capacity.");
        await Complete(itemBound, itemScheduler);
        try { await itemBound.Completion; } catch (Korda.Interop.NativeEngineException) { }
    }

    private static async Task NativeSourceAndControlDrain()
    {
        using var coordinator = new CallbackCoordinator(1);
        var scheduler = new ManualScheduler();
        var registration = new CallbackRegistration(Fixture.New(), new FixtureCalls(), coordinator, 1, 16, 16,
            handler: static (_, _) => ValueTask.CompletedTask, scheduler: scheduler);
        registration.Start();
        bool lease = false;
        nint pointer = registration.AcquireControl(ref lease);
        Check(Fixture.Source(pointer, 1) == NativeStatus.Ok, "Native source reservation failed.");
        registration.Dispose();
        coordinator.Dispose(); // admitted contexts still retained, timer still progresses
        scheduler.Run();
        Check(!registration.Snapshot.NativeQuiet && registration.Snapshot.RootLive, "Native source was confused with callback quiet.");
        int accepted = scheduler.Accepted;
        scheduler.Run(1000);
        Check(scheduler.Accepted <= accepted + 1, "PENDING cleanup busy-enqueued itself rather than waiting for bounded retry.");
        Check(Fixture.Source(pointer, 0) == NativeStatus.Ok, "Native source release failed.");
        registration.Signal(); scheduler.Run();
        Check(registration.Snapshot.NativeQuiet && registration.Snapshot.Controls == 1 && registration.Snapshot.RootLive,
            "Native retirement freed an admitted managed control call.");
        registration.ReleaseControl(ref lease);
        await Complete(registration, scheduler);
        await registration.Completion;
        Check(coordinator.Count == 0, "Coordinator closed without removing drained reservation.");
    }

    private static async Task DecisionAdmittedBeforeClosureFailsClosed()
    {
        using var coordinator = new CallbackCoordinator(1);
        int decisions = 0;
        var registration = new CallbackRegistration(Fixture.New(), new FixtureCalls(), coordinator, 1, 64, 64,
            decision: (_, _) => { decisions++; return true; });
        registration.Start();
        bool lease = false;
        nint pointer = registration.AcquireControl(ref lease);
        Task<uint> callback = Task.Run(() => Fixture.GatedEmit(pointer));
        await Until(() => Fixture.Gate(pointer, 0) == NativeStatus.Ok);
        registration.Dispose();
        Check(registration.Snapshot.RootLive && !registration.Retirement.IsCompleted,
            "Admitted native callback was freed before managed entry.");
        Fixture.Gate(pointer, 1);
        uint result = await callback.WaitAsync(Timeout);
        registration.ReleaseControl(ref lease);
        await Complete(registration);
        await registration.Completion;
        Check(decisions == 0 && result == NativeStatus.CallbackFailed,
            "A06-R1: synchronous decision admitted before closure must refuse after managed admission closes.");
    }

    private static async Task ConcurrentStopGcAndCookieReuse()
    {
        using var coordinator = new CallbackCoordinator(8);
        for (int iteration = 0; iteration < 32; iteration++)
        {
            int wrong = 0;
            uint expected = (uint)(iteration + 1);
            var registration = new CallbackRegistration(Fixture.New(), new FixtureCalls(), coordinator, 256, 4096, 16,
                handler: (kind, _) =>
                {
                    if (kind != expected || Fixture.InNativeCall) Interlocked.Increment(ref wrong);
                    return ValueTask.CompletedTask;
                });
            registration.Start();
            using var begin = new ManualResetEventSlim(false);
            Task[] producers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
            {
                begin.Wait();
                for (int j = 0; j < 64; j++)
                {
                    uint result = Fixture.Emit(registration, [1], expected);
                    Check(result is NativeStatus.Ok or NativeStatus.InvalidState, "Unexpected concurrent callback status.");
                }
            })).ToArray();
            begin.Set();
            GC.Collect(); GC.WaitForPendingFinalizers();
            registration.Dispose();
            await Task.WhenAll(producers).WaitAsync(Timeout);
            await Complete(registration);
            await registration.Completion;
            Check(wrong == 0, "GC pressure routed a callback to a different context.");
            Check(Fixture.Emit(registration, [0]) == NativeStatus.InvalidState, "Released admission reopened.");
        }
    }

    private static async Task Main(string[] args)
    {
        if (args.Contains("--require-aot")) Check(!RuntimeFeature.IsDynamicCodeSupported, "Actual Native AOT execution required.");
        ulong created = Fixture.Count(0), released = Fixture.Count(1);
        await CopiedNotificationAndClosure();
        await SynchronousStartAndSelfStop();
        await AsyncHandlerSelfWaitAndFailure();
        await RegistrationFailureAndCapacity();
        await BoundedQueueAndRetry();
        await NativeSourceAndControlDrain();
        await DecisionAdmittedBeforeClosureFailsClosed();
        await ConcurrentStopGcAndCookieReuse();
        GC.Collect(); GC.WaitForPendingFinalizers();
        Check(Fixture.Count(0) - created == Fixture.Count(1) - released, "Native subscription owners leaked.");
        Check(Fixture.Count(2) == 0, "Release occurred before the native protocol permitted it.");
        Check(!typeof(CallbackRegistration).IsPublic && !typeof(CallbackCoordinator).IsPublic,
            "Internal callback plumbing became public application API.");
        Console.WriteLine($"A06 callback conformance passed: assertions={s_assertions}; owners={Fixture.Count(0) - created}; " +
            $"nativeAot={!RuntimeFeature.IsDynamicCodeSupported}; controlledGcRaces=32; pending=0; active=0; roots=0.");
    }
}
