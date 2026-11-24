using System.Runtime.CompilerServices;
using Korda.Interop;
using Korda.Interop.Abi;
using Korda.Interop.Async;
using Korda.Interop.Callbacks;

internal static partial class Program
{
    private static bool s_directRetiredOwner;

    private static AsyncOperation.RetiredValueTask<T> CreateRetired<T>(uint mode, AsyncNativeCalls calls,
        CallbackCoordinator coordinator, Func<nint, T> mapper, CancellationToken cancellationToken = default,
        IDisposable? inputLifetime = null, long inputBytes = 0, ICallbackScheduler? scheduler = null,
        Action<AsyncBoundary>? testBoundary = null)
    {
        if (s_directRetiredOwner)
        {
            var handle = Fixture.NewHandle(mode);
            try { return new(handle, calls, coordinator, mapper, cancellationToken, inputLifetime, inputBytes, scheduler, testBoundary); }
            catch { handle.Dispose(); throw; }
        }
        var owner = Fixture.New(mode);
        try { return new(owner, calls, coordinator, mapper, cancellationToken, inputLifetime, inputBytes, scheduler, testBoundary); }
        catch { owner.Dispose(); throw; }
    }

    private static async Task RetiredValueTaskChecks()
    {
        foreach (bool direct in new[] { false, true })
        {
            s_directRetiredOwner = direct;
            int before = s_assertions;
            await RetiredResultWaitsForNativeSource();
            await RetiredOutcomesAndObserverFailures();
            await RetiredRegistrationDrain();
            await RetiredExecutionContext();
            await RetiredSetupAndTakeFailures();
            await RetiredIndependentResults();
            await RetiredAbandonedResult();
            Console.WriteLine($"PASS retired private ValueTask (direct handle={direct}): {s_assertions - before} assertions; no context reuse, exact retirement, canceled diagnostics and independent results");
        }
        s_directRetiredOwner = false;
    }

    private static async Task RetiredResultWaitsForNativeSource()
    {
        using var coordinator = new CallbackCoordinator(1, TimeSpan.FromMinutes(1));
        var scheduler = new ManualScheduler();
        var input = new InputLease();
        var calls = new RetiredCalls();
        AsyncOperation.RetiredValueTask<NativeBuffer>? operation = null;
        int observations = 0;
        calls.Observed = snapshot =>
        {
            Check(snapshot.Released && !snapshot.RootLive && snapshot.Controls == 0 && snapshot.CallbackEntries == 0,
                "Private result observer ran before complete native/managed retirement.");
            Check(input.Releases == 1 && coordinator.Count == 0 && operation!.Retirement.IsCompletedSuccessfully,
                "Private result observer ran before input/coordinator retirement publication.");
            Check(!Fixture.InNativeCall, "Private retirement observer ran on a native callback/control stack.");
            observations++;
        };
        operation = CreateRetired<NativeBuffer>(0, calls, coordinator, Fixture.TakeBuffer, inputLifetime: input, inputBytes: 3, scheduler: scheduler);
        var pending = operation.Start();
        using var nativeSource = Fixture.HoldSource(operation);
        Check(Fixture.Complete(operation) == NativeStatus.Ok, "Private result native completion failed.");
        scheduler.Run();
        Task retirement = operation.Retirement;
        Check(ReferenceEquals(retirement, operation.Retirement), "Pending retirement waiters do not share their one lazy task.");
        Check(!pending.IsCompleted && !retirement.IsCompleted && observations == 0 && coordinator.Count == 1,
            "Private result was exposed while the native source still retained work.");
        Check(operation.Snapshot.ResultProcessed && operation.Snapshot.RootLive && input.Releases == 0,
            "A captured private result lost its independent owner/root/input before native quiet.");
        nativeSource.Dispose(); operation.Signal(); scheduler.Run();
        Check(pending.IsCompletedSuccessfully && retirement.IsCompletedSuccessfully && observations == 1,
            "Private result or retirement did not publish once after source release.");
        using NativeBuffer value = await pending.AsTask().WaitAsync(Deadline);
        CheckBuffer(value);
        await Finish(operation);
    }

    private static async Task RetiredOutcomesAndObserverFailures()
    {
        foreach (uint status in new[] { NativeStatus.Ok, NativeStatus.Canceled, NativeStatus.Timeout })
        {
            using var coordinator = new CallbackCoordinator(1);
            using var cancellation = new CancellationTokenSource();
            var calls = new RetiredCalls();
            var operation = CreateRetired<NativeBuffer>(0, calls, coordinator,
                Fixture.TakeBuffer, cancellation.Token);
            var pending = operation.Start();
            cancellation.Cancel();
            Check(!pending.IsCompleted, "Cancellation request selected the private outcome before native terminal completion.");
            Check(Fixture.Complete(operation, status) == NativeStatus.Ok, "Private native terminal commit failed.");
            Task<NativeBuffer> task = pending.AsTask();
            if (status == NativeStatus.Ok)
            {
                using var result = await task.WaitAsync(Deadline);
                CheckBuffer(result);
            }
            else if (status == NativeStatus.Canceled)
            {
                var error = await Failure<TaskCanceledException>(task);
                Check(error.CancellationToken == cancellation.Token && error.Task is { IsCanceled: true } && task.IsCanceled,
                    "Private cancellation lost the token, canceled state or original canceled Task diagnostic.");
            }
            else await Failure<TimeoutException>(task);
            await Finish(operation);
            Check(calls.Observations == 1, "Private retirement observation was not exactly once.");
        }
        using (var coordinator = new CallbackCoordinator(1))
        using (var cancellation = new CancellationTokenSource())
        {
            var cleanup = new ApplicationException("retained-cleanup-diagnostic");
            var enriched = new OperationCanceledException("native-canceled-with-cleanup", cleanup, cancellation.Token);
            var calls = new RetiredCalls { MappedFailure = enriched };
            var operation = CreateRetired<NativeBuffer>(0, calls, coordinator,
                Fixture.TakeBuffer, cancellation.Token);
            var pending = operation.Start();
            Check(Fixture.Complete(operation, NativeStatus.Canceled) == NativeStatus.Ok, "Enriched cancel did not commit.");
            Task<NativeBuffer> task = pending.AsTask();
            var error = await Failure<OperationCanceledException>(task);
            Check(ReferenceEquals(error, enriched) && ReferenceEquals(error.InnerException, cleanup)
                && error.CancellationToken == cancellation.Token && task.IsCanceled,
                "Private canceled result lost the original owned exception, inner diagnostic or token.");
            await Finish(operation);
        }
        foreach (bool earlierFailure in new[] { false, true })
        {
            using var coordinator = new CallbackCoordinator(1);
            var observerFailure = new ApplicationException("retirement-observer-failure");
            var calls = new RetiredCalls { Observed = _ => throw observerFailure };
            var operation = CreateRetired<int>(0, calls, coordinator,
                pointer => { using var buffer = Fixture.TakeBuffer(pointer); return 42; });
            var pending = operation.Start();
            Check(Fixture.Complete(operation, earlierFailure ? NativeStatus.Timeout : NativeStatus.Ok) == NativeStatus.Ok,
                "Observer override native completion failed.");
            var error = await Failure<ApplicationException>(pending.AsTask());
            Check(ReferenceEquals(error, observerFailure) && calls.Observations == 1 && calls.Inner.Poisoned == 0,
                "Observer failure did not override the former result like the domain finally, or poisoned retired state.");
            await Finish(operation);
        }
    }

    private static async Task RetiredRegistrationDrain()
    {
        using var coordinator = new CallbackCoordinator(1);
        using var cancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var calls = new RetiredCalls();
        var operation = CreateRetired<NativeBuffer>(0, calls, coordinator, Fixture.TakeBuffer,
            cancellation.Token, testBoundary: boundary =>
            {
                if (boundary != AsyncBoundary.CancelEntered) return;
                entered.Set(); Check(resume.Wait(Deadline), "Private cancel barrier did not resume.");
            });
        var pending = operation.Start();
        Task canceling = Task.Run(cancellation.Cancel);
        Check(entered.Wait(Deadline), "Private cancellation registration did not enter.");
        Check(Fixture.Spawn(operation) == NativeStatus.Ok, "Private completion worker did not start.");
        await Until(() => operation.Snapshot.NoticeReceived, "Private terminal notice did not arrive.");
        Check(!pending.IsCompleted && !operation.Retirement.IsCompleted && operation.Snapshot.RootLive && calls.Observations == 0,
            "Private completion retired an active cancellation callback.");
        using (var canceledWait = new CancellationTokenSource())
        {
            canceledWait.Cancel();
            await Failure<OperationCanceledException>(operation.DisposeAsync().AsTask().WaitAsync(canceledWait.Token));
        }
        Check(!pending.IsCompleted && operation.Snapshot.RootLive, "Canceled private cleanup wait discharged live work.");
        resume.Set(); await canceling.WaitAsync(Deadline);
        using var result = await pending.AsTask().WaitAsync(Deadline);
        CheckBuffer(result); await Finish(operation);
    }

    private static async Task RetiredSetupAndTakeFailures()
    {
        foreach (AsyncBoundary boundary in new[] { AsyncBoundary.BeforeRegister, AsyncBoundary.Registered,
            AsyncBoundary.BeforeStart, AsyncBoundary.StartReturned, AsyncBoundary.BeforeTake })
        {
            using var coordinator = new CallbackCoordinator(1);
            var calls = new RetiredCalls(); var input = new InputLease();
            var original = new ApplicationException("private-boundary-" + boundary);
            var operation = CreateRetired<NativeBuffer>(2, calls, coordinator,
                Fixture.TakeBuffer, inputLifetime: input, inputBytes: 3,
                testBoundary: point => { if (point == boundary) throw original; });
            var failure = await Failure<ApplicationException>(operation.Start().AsTask());
            Check(ReferenceEquals(failure, original) && input.Releases == 1 && calls.Observations == 1,
                "Private setup/take failure changed identity or did not compensate input ownership.");
            await Finish(operation);
        }
        using (var coordinator = new CallbackCoordinator(1))
        {
            var calls = new RetiredCalls();
            var operation = CreateRetired<NativeBuffer>(2, calls, coordinator,
                pointer => Fixture.TakeBuffer(pointer, throwAfterTake: true));
            await Failure<ApplicationException>(operation.Start().AsTask()); await Finish(operation);
            Check(Fixture.Count(5) == 0 && Fixture.Count(6) == 0, "Private mapper failure leaked its taken native result.");
        }
        using (var coordinator = new CallbackCoordinator(1))
        {
            bool prevented = false;
            AsyncOperation.RetiredValueTask<NativeBuffer>? operation = null;
            operation = CreateRetired<NativeBuffer>(2, new RetiredCalls(), coordinator, Fixture.TakeBuffer, testBoundary: boundary =>
            {
                if (boundary != AsyncBoundary.BeforeTake) return;
                try { _ = operation!.DisposeAsync(); } catch (InvalidOperationException) { prevented = true; }
            });
            using var result = await operation.Start().AsTask().WaitAsync(Deadline);
            Check(prevented, "Private result mapper could wait for its own retirement.");
            await Finish(operation);
        }
    }

    private static async Task RetiredExecutionContext()
    {
        using var coordinator = new CallbackCoordinator(1);
        Ambient.Value = "retired-awaiting-caller";
        try
        {
            var calls = new RetiredCalls
            {
                Observed = _ => Check(Ambient.Value is null,
                    "The internal retirement diagnostic unexpectedly captured application ExecutionContext.")
            };
            var operation = CreateRetired<NativeBuffer>(0, calls, coordinator, pointer =>
            {
                Check(Ambient.Value is null && !Fixture.InNativeCall,
                    "Private typed take captured caller context or ran on the native callback stack.");
                return Fixture.TakeBuffer(pointer);
            });
            var pending = operation.Start();
            Check(!pending.IsCompleted, "The private context probe must start with a real pending result.");
            Task<NativeBuffer> awaiting = AwaitPrivateContext(pending);
            Check(!awaiting.IsCompleted, "The application await did not suspend before native completion.");
            Check(Fixture.Spawn(operation) == NativeStatus.Ok, "Private context probe native worker did not start.");
            using var result = await awaiting.WaitAsync(Deadline);
            Check(Ambient.Value == "retired-awaiting-caller", "The outer application context changed after private completion.");
            CheckBuffer(result); await Finish(operation);
        }
        finally { Ambient.Value = null; }
    }

    private static async Task<NativeBuffer> AwaitPrivateContext(ValueTask<NativeBuffer> pending)
    {
        var result = await pending.ConfigureAwait(false);
        Check(Ambient.Value == "retired-awaiting-caller", "A suspended private ValueTask await lost the caller's AsyncLocal value.");
        return result;
    }

    private static async Task RetiredIndependentResults()
    {
        using var coordinator = new CallbackCoordinator(1);
        var first = CreateRetired<NativeBuffer>(2, new RetiredCalls(), coordinator, Fixture.TakeBuffer);
        var unconsumed = first.Start();
        await first.Retirement.WaitAsync(Deadline);
        await Until(() => unconsumed.IsCompleted, "First private result did not publish after retirement observation.");
        Check(unconsumed.IsCompletedSuccessfully, "Retired private result did not become readable.");
        var second = CreateRetired<NativeBuffer>(2, new RetiredCalls(), coordinator, Fixture.TakeBuffer);
        using var secondResult = await second.Start().AsTask().WaitAsync(Deadline);
        await Finish(second);
        Task<NativeBuffer> firstTask = unconsumed.AsTask();
        using var firstResult = await firstTask.WaitAsync(Deadline);
        Check(ReferenceEquals(firstResult, await firstTask) && !ReferenceEquals(firstResult, secondResult),
            "Another private operation overwrote a delayed result or retained Task identity.");
        CheckBuffer(firstResult); CheckBuffer(secondResult);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AbandonRetiredResult(CallbackCoordinator coordinator)
    {
        var operation = CreateRetired<NativeBuffer>(2, new RetiredCalls(), coordinator, Fixture.TakeBuffer);
        _ = operation.Start();
        return new WeakReference(operation);
    }

    private static async Task RetiredAbandonedResult()
    {
        using var coordinator = new CallbackCoordinator(1);
        var abandoned = AbandonRetiredResult(coordinator);
        await Until(() => coordinator.Count == 0, "Unconsumed private result blocked native retirement.");
        await Until(() =>
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            return !abandoned.IsAlive && Fixture.Count(5) == 0;
        }, "Unconsumed private result retained its one-shot context/native owner after collection.");
        Check(Fixture.Count(0) == 0 && Fixture.Count(6) == 0, "Abandoned private completion leaked native backing storage.");
    }

    private static void RetiredValueTaskQuarantine(bool direct = false)
    {
        s_directRetiredOwner = direct;
        using var coordinator = new CallbackCoordinator(2, TimeSpan.FromMinutes(1));
        foreach (bool failedRetire in new[] { false, true })
        {
            var scheduler = new ManualScheduler(); var input = new InputLease();
            var calls = new RetiredCalls(); calls.Inner.ThrowAfterStart = !failedRetire;
            var operation = CreateRetired<NativeBuffer>(failedRetire ? 34u : 0u,
                calls, coordinator, Fixture.TakeBuffer, inputLifetime: input, inputBytes: 3, scheduler: scheduler);
            var pending = operation.Start(); scheduler.Run();
            Check(operation.Snapshot.Quarantined && operation.Snapshot.RootLive && calls.Inner.Poisoned == 1,
                "Private indeterminate/failed-retire path lost its quarantine/root/poison.");
            Check(!pending.IsCompleted && !operation.Retirement.IsCompleted && input.Releases == 0 && calls.Observations == 0,
                "Private quarantine exposed a result or fabricated retirement.");
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check(coordinator.Count == 2 && Fixture.Count(0) == 2,
            "Private quarantined contexts were released despite unresolved native obligations.");
        string mode = direct ? "--direct-retired-value-task-quarantine" : "--retired-value-task-quarantine";
        Console.WriteLine($"{{\"mode\":\"{mode}\",\"passed\":true,\"retained_native_owners\":2,\"retirement_reported_success\":false,\"assertions\":{s_assertions}}}");
    }

    private sealed class RetiredCalls : AsyncNativeCalls
    {
        internal readonly FixtureCalls Inner = new();
        internal Action<AsyncSnapshot>? Observed;
        internal Exception? MappedFailure;
        internal int Observations;
        internal override uint Start(nint operation, in NwOperationCallbacksV1 callbacks) => Inner.Start(operation, in callbacks);
        internal override uint RequestCancel(nint operation) => Inner.RequestCancel(operation);
        internal override uint TryRetire(nint operation) => Inner.TryRetire(operation);
        internal override void Poison() => Inner.Poison();
        internal override Exception MapFailure(nint operation, uint status, in NwErrorInfoV1 error, CancellationToken token) =>
            MappedFailure ?? base.MapFailure(operation, status, in error, token);
        internal override void ObserveRetirement(AsyncOperation operation)
        { Observations++; Observed?.Invoke(operation.Snapshot); }
    }
}
