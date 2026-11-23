using System.Runtime.CompilerServices;
using Korda.Interop;
using Korda.Interop.Abi;
using Korda.Interop.Async;
using Korda.Interop.Execution;

internal static class Program
{
    private static int s_assertions;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static void Check(bool condition, string message)
    {
        Interlocked.Increment(ref s_assertions);
        if (!condition) throw new Exception(message);
    }
    private static async Task Until(Func<bool> predicate, string message)
    {
        long end = Environment.TickCount64 + (long)Deadline.TotalMilliseconds;
        while (!predicate())
        {
            if (Environment.TickCount64 > end) throw new TimeoutException(message);
            await Task.Delay(1);
        }
    }
    private static async Task<T> Failure<T>(Task task) where T : Exception
    {
        try { await task.WaitAsync(Deadline); }
        catch (T error) { Check(true, typeof(T).Name); return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static async Task Retired(AsyncOperation operation)
    {
        await operation.Retirement.WaitAsync(Deadline);
        var state = operation.Snapshot;
        Check(state.Released && state.NativeQuiet && !state.RootLive && !state.Quarantined, "Operation retirement is incomplete.");
        Check(state.Controls == 0 && state.CallbackEntries == 0 && state.RegistrationDrained, "Managed/native controls remain live.");
    }
    private static async Task Joined(FixtureEngine engine, ulong? expectedWorkers = null)
    {
        var result = await engine.Lifetime.CloseAsync().WaitAsync(Deadline);
        Check(result.Joined && result.WorkersStarted == result.WorkersStopped && result.WorkersStarted > 0, "Actual worker join is missing.");
        Check(result.OutstandingOperations == 0 && result.ReservedBytes == 0, "Join completed before owner and byte retirement.");
        if (expectedWorkers.HasValue) Check(result.WorkersStarted == expectedWorkers.Value, "Configured worker ownership differs.");
        Check(engine.Lifetime.Snapshot.Released && !engine.Lifetime.Snapshot.RootLive && engine.CleanupSlots == 0, "Close observer or managed cleanup still retains an owner.");
        Check(engine.Read(1) == 1 && engine.Read(2) == 0 && engine.Read(3) == 0 && engine.Read(13) == 0, "Native joined state has outstanding work.");
        Check(engine.CloseCalls.Starts == 1, "Repeated close started another worker/operation.");
    }

    private static async Task Capacity()
    {
        foreach (uint field in new uint[] { 0, 1, 2 })
        {
            using var engine = new FixtureEngine(admitted: field == 0 ? 0u : 2u, bytes: field == 1 ? 0u : 8u, cleanup: field == 2 ? 0u : 2u);
            ulong before = Fixture.Count(0);
            Check(!engine.TryNewJob(0, [1], default, out _, out uint status) && status == NativeStatus.InvalidArgument, "Zero budget admitted payload.");
            Check(Fixture.Count(0) == before && engine.Read(2) == 0 && engine.Read(3) == 0, "Rejected admission allocated an owner or payload credit.");
            await Joined(engine, 1);
        }
        using (var engine = new FixtureEngine())
        {
            var first = engine.NewJob(bytes: [1, 2, 3, 4, 5, 6]);
            Check(!engine.TryNewJob(0, [1, 2, 3], default, out _, out uint full) && full == NativeStatus.OutOfMemory, "Full byte budget accepted work.");
            Check(!engine.TryNewJob(0, new byte[9], default, out _, out uint impossible) && impossible == NativeStatus.InvalidArgument, "Oversize request entered a wait.");
            var second = engine.NewJob(bytes: [1, 2]);
            Check(!engine.TryNewJob(0, [], default, out _, out uint count) && count == NativeStatus.OutOfMemory, "Full operation budget accepted work.");
            Check(engine.Read(2) == 2 && engine.Read(3) == 8 && engine.Read(4) == 2, "Atomic admission counters differ.");
            first.Dispose(); second.Dispose();
            await Failure<ObjectDisposedException>(first.Task); await Failure<ObjectDisposedException>(second.Task);
            await Retired(first); await Retired(second);
            await Joined(engine, 1);
        }
        using (var engine = new FixtureEngine(admitted: 8, cleanup: 1))
        {
            var first = engine.NewJob(bytes: [1]);
            Check(!engine.TryNewJob(0, [1], default, out _, out uint status) && status == NativeStatus.OutOfMemory, "Independent cleanup limit was ignored.");
            first.Dispose(); await Failure<ObjectDisposedException>(first.Task); await Retired(first);
            await Joined(engine, 1);
        }
    }

    private static async Task SuccessAndPreCancel()
    {
        using var engine = new FixtureEngine(workers: 2);
        Check(!engine.Lifetime.CloseRequested && engine.CloseCalls.Starts == 0, "Construction started close unexpectedly.");
        var operation = engine.NewJob();
        Task<ulong> task = operation.Start();
        Check(await task.WaitAsync(Deadline) == 6 && await task == 6, "Native typed result changed across awaits.");
        await Retired(operation);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var skipped = engine.NewJob(token: canceled.Token);
        var error = await Failure<OperationCanceledException>(skipped.Start());
        Check(error.CancellationToken == canceled.Token && skipped.Snapshot.Start == AsyncStartOutcome.Skipped, "Precanceled owner was handed to the executor.");
        await Retired(skipped);
        await Joined(engine, 2);
    }

    private static async Task TaskTerminalIsNotRetirement()
    {
        using var engine = new FixtureEngine();
        var operation = engine.NewJob(mode: 5, bytes: new byte[8]);
        using var source = engine.StartWithHeldSource(operation);
        Check(await operation.Task.WaitAsync(Deadline) == 0, "Terminal result did not arrive.");
        await Until(() => engine.Read(6) == 1, "Supervisor did not enter retirement.");
        Check(!operation.Retirement.IsCompleted && operation.Snapshot.RootLive && engine.Read(3) == 8, "Task success released native source/bytes early.");
        await Task.Delay(2);
        Check(engine.Read(9) >= 1, "Oldest cleanup age does not describe a pending retirement record.");
        using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Failure<OperationCanceledException>(engine.Lifetime.CloseAsync(wait.Token));
        Check(engine.Lifetime.Snapshot.RootLive && engine.Read(1) == 0 && engine.Read(3) == 8, "Timed close freed a retained source.");
        source.Dispose();
        await Retired(operation);
        await Joined(engine, 1);
    }

    private static async Task StartedBlockingWorkAndRepeatedTimeouts()
    {
        using var engine = new FixtureEngine();
        var operation = engine.NewJob(mode: 1, bytes: new byte[8]);
        Task<ulong> task = operation.Start();
        await Until(() => engine.Read(10) == 1, "Blocking worker never entered the controlled body.");
        for (int i = 0; i < 8; i++)
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(5));
            await Failure<OperationCanceledException>(engine.Lifetime.CloseAsync(wait.Token));
            Check(!task.IsCompleted && operation.Snapshot.RootLive && !operation.Retirement.IsCompleted, "Timeout rewrote business terminal or reclaimed an active worker.");
            Check(engine.Read(2) == 1 && engine.Read(3) == 8 && engine.Read(5) == 1 && engine.Read(1) == 0, "Timeout returned active capacity.");
            Check(engine.CleanupSlots == 2 && engine.CloseCalls.Starts == 1 && engine.CloseCalls.Cancels == 0, "Repeated waits allocated another close or canceled its cleanup.");
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check(engine.Read(3) == 8 && operation.Snapshot.RootLive, "GC reclaimed started blocking work.");
        engine.OpenGate();
        await Failure<OperationCanceledException>(task);
        await Retired(operation);
        await Joined(engine, 2);
    }

    private static async Task CooperativeNativeCancellation()
    {
        using var engine = new FixtureEngine();
        var operation = engine.NewJob(mode: 2);
        var task = operation.Start();
        var close = engine.Lifetime.CloseAsync();
        await Failure<OperationCanceledException>(task);
        await Retired(operation);
        await close.WaitAsync(Deadline);
        await Joined(engine, 1);
    }

    private static async Task IndividualCancellationRetainsStartedWork()
    {
        using var engine = new FixtureEngine();
        using var cancel = new CancellationTokenSource();
        var operation = engine.NewJob(mode: 1, token: cancel.Token);
        var task = operation.Start();
        await Until(() => engine.Read(10) == 1, "Blocking worker did not enter.");
        cancel.Cancel();
        Check(!task.IsCompleted && engine.Read(3) == 3 && operation.Snapshot.RootLive, "Individual cancel was treated as completion.");
        engine.OpenGate();
        var error = await Failure<OperationCanceledException>(task);
        Check(error.CancellationToken == cancel.Token, "Native cancel lost the declared token.");
        await Retired(operation);
        Check(engine.Read(0) == 0, "Canceling one operation closed the engine.");
        var later = engine.NewJob();
        Check(await later.Start().WaitAsync(Deadline) == 6, "The engine did not remain usable after one cancellation.");
        await Retired(later); await Joined(engine, 2);
    }

    private static async Task CloseWithIssuedPermit()
    {
        using var engine = new FixtureEngine();
        var issued = engine.NewJob();
        var close = engine.Lifetime.CloseAsync();
        Check(!close.IsCompleted && engine.Read(4) == 1 && engine.Read(3) == 3, "Closing silently dropped an issued owner.");
        await Failure<OperationCanceledException>(issued.Start());
        Check(issued.Snapshot.Start == AsyncStartOutcome.AcceptedReturned && !issued.Snapshot.Quarantined, "Issued close race rejected after emitting a callback.");
        await Retired(issued); await Joined(engine, 1);
        Check(!engine.TryNewJob(0, [1], default, out _, out uint status) && status == NativeStatus.InvalidState, "Closed instance admitted new work.");
    }

    private static async Task ConcurrentStartAndClose()
    {
        for (int i = 0; i < 32; i++)
        {
            using var engine = new FixtureEngine();
            var issued = engine.NewJob();
            Task<ulong> business = Task.Run(() => issued.Start());
            var close = engine.Lifetime.CloseAsync();
            try { Check(await business.WaitAsync(Deadline) == 6, "Successful raced work changed its result."); }
            catch (OperationCanceledException) { Check(true, "Close won the operation race."); }
            await Retired(issued); await close.WaitAsync(Deadline); await Joined(engine, 1);
        }
    }

    private static async Task CallbackCloseRejectsSelfWait()
    {
        using var engine = new FixtureEngine();
        int refused = 0;
        engine.BeforeTake = () =>
        {
            try { engine.Lifetime.CloseAsync(); }
            catch (InvalidOperationException) { refused++; }
            engine.Lifetime.Dispose(); // the same owned callback may safely request stop
        };
        var operation = engine.NewJob();
        Check(await operation.Start().WaitAsync(Deadline) == 6, "Callback close prevented result extraction.");
        Check(refused == 1 && engine.Lifetime.CloseRequested, "Owned callback self-wait was not rejected after requesting safe close.");
        await Retired(operation); await Joined(engine, 1);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<AsyncOperation<ulong>> LoseWrapper(FixtureEngine engine)
    {
        var operation = engine.NewJob(mode: 1, bytes: new byte[8]);
        operation.Start();
        return new(operation);
    }
    private static async Task LostWrapperKeepsNativeRoot()
    {
        using var engine = new FixtureEngine();
        var weak = LoseWrapper(engine);
        await Until(() => engine.Read(10) == 1, "Lost-wrapper worker did not start.");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check(weak.TryGetTarget(out var retained) && retained.Snapshot.RootLive, "An active operation lost its explicit managed root.");
        retained = null;
        using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
        await Failure<OperationCanceledException>(engine.Lifetime.CloseAsync(wait.Token));
        Check(engine.Read(3) == 8 && engine.CleanupSlots == 2, "A canceled orphan wait lost cleanup accounting.");
        engine.OpenGate(); await Joined(engine, 2);
    }

    private static async Task ActualTaskPanics()
    {
        foreach (uint mode in new uint[] { 3, 4 })
        {
            using var engine = new FixtureEngine();
            var operation = engine.NewJob(mode);
            var error = await Failure<NativeEngineException>(operation.Start());
            Check(error.StatusCode == NativeStatus.NativePanic, "Actual poll/body panic lost its fixed native status.");
            await Retired(operation);
            Check(engine.Read(11) == 1 && engine.Read(12) == 1, "Panic did not poison the engine and account for its payload.");
            Check(!engine.TryNewJob(0, [1], default, out _, out uint status) && status == NativeStatus.InvalidState, "Poisoned engine accepted more work.");
            await Joined(engine, mode == 4 ? 2u : 1u);
        }
    }

    internal static async Task Main(string[] args)
    {
        if (args.Contains("--require-aot")) Check(!RuntimeFeature.IsDynamicCodeSupported, "Actual Native AOT execution is required.");
        if (args.Contains("--core-only"))
        {
            Check(!typeof(ExecutorLifetime).IsPublic, "Executor plumbing became public.");
            Check(!File.ReadAllText("/proc/self/maps").Contains("libkorda_interop_execution", StringComparison.Ordinal), "Core inspection implicitly loaded the native executor.");
            Console.WriteLine($"A08 Core-only metadata: assertions={s_assertions}; native executor not loaded.");
            return;
        }
        Check(Fixture.Count(0) == 0 && Fixture.Count(3) == 0, "Loading the component implicitly created an executor.");
        await Capacity();
        await SuccessAndPreCancel();
        await TaskTerminalIsNotRetirement();
        await StartedBlockingWorkAndRepeatedTimeouts();
        await CooperativeNativeCancellation();
        await IndividualCancellationRetainsStartedWork();
        await CloseWithIssuedPermit();
        await ConcurrentStartAndClose();
        await CallbackCloseRejectsSelfWait();
        await LostWrapperKeepsNativeRoot();
        await ActualTaskPanics();
        Check(Fixture.Count(0) == Fixture.Count(1) && Fixture.Count(2) == 0 && Fixture.Count(6) == 0, "A native job was leaked or released before retirement.");
        Check(Fixture.Count(3) == Fixture.Count(4) && Fixture.Count(7) == Fixture.Count(8), "An engine or close observer owner remains live.");
        Console.WriteLine($"A08 Linux execution: assertions={s_assertions}; jobs={Fixture.Count(0)}/{Fixture.Count(1)}; engines={Fixture.Count(3)}/{Fixture.Count(4)}; close owners={Fixture.Count(7)}/{Fixture.Count(8)}; live jobs={Fixture.Count(2)}; bad releases={Fixture.Count(6)}; actual worker joins passed.");
    }
}
