using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using NativeWeave.Interop;
using NativeWeave.Interop.Abi;
using NativeWeave.Interop.Async;
using NativeWeave.Interop.Callbacks;

internal static class Program
{
    private static int s_assertions;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly AsyncLocal<string?> Ambient = new();

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
        catch (T failure) { Check(true, typeof(T).Name); return failure; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static async Task Finish(AsyncOperation operation)
    {
        await operation.Retirement.WaitAsync(Deadline);
        AsyncSnapshot state = operation.Snapshot;
        Check(state.SetupSettled && state.RegistrationReady && state.RegistrationDrained, "Setup and registration did not drain.");
        Check(state.NativeQuiet && state.ResultProcessed && state.Released && !state.RootLive && !state.Quarantined, "Actual native/managed retirement is incomplete.");
        Check(state.Controls == 0 && state.CallbackEntries == 0 && state.RetainedInputBytes == 0, "A retired operation retains controls or input budget.");
    }

    private static void CheckBuffer(NativeBuffer buffer) =>
        Check(buffer.Read(static bytes => bytes.SequenceEqual(new byte[] { 1, 2, 3 })), "Transferred native result bytes changed.");

    private static async Task SynchronousCompletion()
    {
        using var coordinator = new CallbackCoordinator(1);
        using var cancel = new CancellationTokenSource();
        var input = new InputLease();
        var calls = new FixtureCalls();
        AsyncOperation<NativeBuffer>? operation = null;
        operation = new(Fixture.New(2), calls, coordinator, pointer =>
        {
            Check(!Fixture.InNativeCall, "Materialization ran inline on a native control/callback stack.");
            return Fixture.TakeBuffer(pointer);
        }, cancel.Token, input, 3, testBoundary: boundary =>
        {
            if (boundary != AsyncBoundary.StartReturned) return;
            Check(operation!.Snapshot.NoticeReceived && !operation.Snapshot.SetupSettled, "Synchronous notice did not precede start-return settlement.");
            Check(!operation.Task.IsCompleted && operation.Snapshot.RootLive, "Start frame was reclaimed or published before setup settled.");
            cancel.Cancel(); // cancellation after committed success must not replace success
        });
        Task<NativeBuffer> task = operation.Start();
        using NativeBuffer result = await task.WaitAsync(Deadline);
        Check(ReferenceEquals(result, await task), "Multiple Task awaits produced multiple native owners.");
        await Finish(operation);
        CheckBuffer(result); // independent result survives operation destruction
        Check(input.Releases == 1 && coordinator.Count == 0, "Input/bounded coordinator reservation did not retire once.");
        Check(operation.Snapshot.NativeStatus == NativeStatus.Ok && calls.Poisoned == 0, "Late cancellation changed native success.");
    }

    private static async Task PreCanceledRegistration()
    {
        using var coordinator = new CallbackCoordinator(1);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        bool synchronous = false;
        AsyncOperation<NativeBuffer>? operation = null;
        operation = new(Fixture.New(), new FixtureCalls(), coordinator, Fixture.TakeBuffer, cancel.Token,
            testBoundary: boundary =>
            {
                if (boundary != AsyncBoundary.CancelEntered) return;
                synchronous = !operation!.Snapshot.RegistrationReady && !operation.Snapshot.SetupSettled;
            });
        OperationCanceledException failure = await Failure<OperationCanceledException>(operation.Start());
        await Finish(operation);
        Check(synchronous && operation.Snapshot.Start == AsyncStartOutcome.Skipped, "Precanceled UnsafeRegister did not exercise callback-before-assignment.");
        Check(failure.CancellationToken == cancel.Token && !operation.Snapshot.NoticeReceived, "Prestart cancellation fabricated a native terminal notice.");
    }

    private static async Task CancellationAndOutcomes()
    {
        foreach (uint status in new uint[] { NativeStatus.Ok, NativeStatus.Canceled, NativeStatus.Timeout })
        {
            using var coordinator = new CallbackCoordinator(1);
            using var cancel = new CancellationTokenSource();
            var calls = new FixtureCalls();
            var operation = new AsyncOperation<NativeBuffer>(Fixture.New(), calls, coordinator, Fixture.TakeBuffer, cancel.Token);
            Task<NativeBuffer> task = operation.Start();
            cancel.Cancel();
            Check(!task.IsCompleted && operation.Snapshot.CancelRequested && calls.Cancels == 1, "Cancellation request prematurely selected the Task outcome.");
            Check(Fixture.Complete(operation, status) == NativeStatus.Ok, "Native terminal commit failed.");
            if (status == NativeStatus.Ok)
            {
                using NativeBuffer result = await task.WaitAsync(Deadline);
                CheckBuffer(result);
            }
            else if (status == NativeStatus.Canceled)
            {
                OperationCanceledException failure = await Failure<OperationCanceledException>(task);
                Check(failure.CancellationToken == cancel.Token, "Terminal cancellation lost its token association.");
            }
            else await Failure<TimeoutException>(task);
            await Finish(operation);
            Check(operation.Snapshot.NativeStatus == status, "Managed cancellation rewrote the native terminal snapshot.");
        }
        using var closeCoordinator = new CallbackCoordinator(1);
        var closing = new AsyncOperation<NativeBuffer>(Fixture.New(4), new FixtureCalls(), closeCoordinator, Fixture.TakeBuffer);
        Task<NativeBuffer> closingTask = closing.Start();
        closing.Dispose(); // cleanup cancel synchronously emits the terminal callback during close
        await Failure<OperationCanceledException>(closingTask);
        await Finish(closing);
        Check(closing.Snapshot.NoticeReceived, "A terminal callback admitted during close was lost.");
    }

    private static async Task CancellationRegistrationDrain()
    {
        using var coordinator = new CallbackCoordinator(1);
        using var cancel = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var exit = new ManualResetEventSlim();
        var operation = new AsyncOperation<NativeBuffer>(Fixture.New(), new FixtureCalls(), coordinator, Fixture.TakeBuffer,
            cancel.Token, testBoundary: boundary =>
            {
                if (boundary != AsyncBoundary.CancelEntered) return;
                entered.Set();
                if (!exit.Wait(Deadline)) throw new TimeoutException("Cancel barrier was not released.");
            });
        Task<NativeBuffer> task = operation.Start();
        Task canceling = Task.Run(cancel.Cancel);
        Check(entered.Wait(Deadline), "Cancellation callback did not enter.");
        Check(Fixture.Spawn(operation) == NativeStatus.Ok, "Native completion worker was not admitted.");
        await Until(() => operation.Snapshot.NoticeReceived, "Native terminal notice missing.");
        Check(!operation.Snapshot.RegistrationDrained && operation.Snapshot.RootLive && !operation.Retirement.IsCompleted,
            "Native completion freed a live managed cancellation registration.");
        using (var waitCancel = new CancellationTokenSource())
        {
            waitCancel.Cancel();
            await Failure<OperationCanceledException>(operation.Retirement.WaitAsync(waitCancel.Token));
        }
        Check(!task.IsCompleted && operation.Snapshot.RootLive, "Canceled close waiter altered retained cleanup ownership.");
        exit.Set();
        await canceling.WaitAsync(Deadline);
        using NativeBuffer result = await task.WaitAsync(Deadline);
        await Finish(operation);
        CheckBuffer(result);
    }

    private static async Task FailedSetupCompensation()
    {
        foreach (uint point in new uint[] { 1, 2 })
        {
            ulong live = Fixture.Count(0), bytes = Fixture.Count(4);
            Check(Fixture.Create(0, [1, 2, 3], point, out nint pointer) != NativeStatus.Ok && pointer == 0,
                "Failed create published an owner.");
            Check(Fixture.Count(0) == live && Fixture.Count(4) == bytes, "Failed create leaked temporary native allocation/input.");
        }
        using var coordinator = new CallbackCoordinator(1);
        foreach (AsyncBoundary boundary in new[] { AsyncBoundary.BeforeRoot, AsyncBoundary.RootAllocated })
        {
            using var owner = Fixture.New();
            var input = new InputLease();
            try
            {
                _ = new AsyncOperation<NativeBuffer>(owner, new FixtureCalls(), coordinator, Fixture.TakeBuffer,
                    inputLifetime: input, testBoundary: value => { if (value == boundary) throw new ApplicationException("construction"); });
                throw new Exception("Construction failure did not execute.");
            }
            catch (ApplicationException) { Check(coordinator.Count == 0 && input.Releases == 0, "Failed construction stole caller input/slot ownership."); }
        }
        var first = new AsyncOperation<NativeBuffer>(Fixture.New(), new FixtureCalls(), coordinator, Fixture.TakeBuffer);
        using (var secondOwner = Fixture.New())
        {
            try
            {
                _ = new AsyncOperation<NativeBuffer>(secondOwner, new FixtureCalls(), coordinator, Fixture.TakeBuffer);
                throw new Exception("Capacity rejection did not execute.");
            }
            catch (InvalidOperationException) { Check(coordinator.Count == 1, "Rejected admission changed bounded slot count."); }
        }
        first.Dispose();
        await Failure<ObjectDisposedException>(first.Task);
        await Finish(first);

        foreach (AsyncBoundary boundary in new[] { AsyncBoundary.BeforeRegister, AsyncBoundary.Registered, AsyncBoundary.BeforeStart, AsyncBoundary.StartReturned })
        {
            using var cancel = new CancellationTokenSource();
            var input = new InputLease();
            var operation = new AsyncOperation<NativeBuffer>(Fixture.New(boundary == AsyncBoundary.StartReturned ? 2u : 0u),
                new FixtureCalls(), coordinator, Fixture.TakeBuffer, cancel.Token, input, 3,
                testBoundary: value => { if (value == boundary) throw new ApplicationException("setup-boundary"); });
            await Failure<ApplicationException>(operation.Start());
            await Finish(operation);
            Check(input.Releases == 1, "Failed setup retained its input owner.");
            Check(operation.Snapshot.Start == (boundary == AsyncBoundary.StartReturned ? AsyncStartOutcome.AcceptedReturned : AsyncStartOutcome.Skipped),
                "Failed setup used the wrong start outcome cleanup path.");
            Check(operation.Snapshot.HasRegistration == (boundary != AsyncBoundary.BeforeRegister), "NoRegistration sentinel did not match actual registration ownership.");
        }
        var rejected = new AsyncOperation<NativeBuffer>(Fixture.New(1), new FixtureCalls(), coordinator, Fixture.TakeBuffer);
        await Failure<ArgumentException>(rejected.Start());
        await Finish(rejected);
        Check(rejected.Snapshot.Start == AsyncStartOutcome.Rejected && !rejected.Snapshot.NoticeReceived, "Rejected start used accepted cleanup.");
    }

    private static async Task DuplicateAndTakeFailures()
    {
        using var coordinator = new CallbackCoordinator(1, TimeSpan.FromMinutes(1));
        var scheduler = new ManualScheduler();
        var calls = new FixtureCalls();
        var operation = new AsyncOperation<NativeBuffer>(Fixture.New(), calls, coordinator, Fixture.TakeBuffer, scheduler: scheduler);
        Task<NativeBuffer> task = operation.Start();
        bool lease = false;
        nint pointer = operation.AcquireControl(ref lease);
        try
        {
            Check(Fixture.CompleteNative(pointer, NativeStatus.Ok) == NativeStatus.Ok, "First completion failed.");
            Check(Fixture.Duplicate(pointer) == NativeStatus.CallbackFailed, "Duplicate terminal notice was accepted twice.");
            Check(Fixture.CompleteNative(pointer, NativeStatus.Canceled) == NativeStatus.InvalidState, "Duplicate native completion changed the terminal state.");
            scheduler.Run();
            Check(!task.IsCompleted && operation.Snapshot.RootLive, "Active native control was reclaimed by terminal delivery.");
        }
        finally { operation.ReleaseControl(ref lease); }
        scheduler.Run();
        using (NativeBuffer result = await task.WaitAsync(Deadline)) CheckBuffer(result);
        await Finish(operation);
        Check(operation.Snapshot.DuplicateNotices == 1 && operation.Snapshot.NativeStatus == NativeStatus.Ok && calls.Poisoned == 1,
            "Duplicate notice did not preserve first result and poison ordinary engine admission.");
        Check(scheduler.MaxQueued == 1, "Coalesced mailbox queued more than one work item.");

        foreach (int mode in new[] { 0, 1, 2 })
        {
            var input = new InputLease();
            var failing = new AsyncOperation<NativeBuffer>(Fixture.New(mode == 2 ? 18u : 2u), new FixtureCalls(), coordinator,
                p => Fixture.TakeBuffer(p, throwAfterTake: mode == 1), inputLifetime: input, inputBytes: 3,
                testBoundary: boundary => { if (mode == 0 && boundary == AsyncBoundary.BeforeTake) throw new ApplicationException("before-take"); });
            if (mode == 2) await Failure<NativeEngineException>(failing.Start());
            else await Failure<ApplicationException>(failing.Start());
            await Finish(failing);
            Check(input.Releases == 1 && Fixture.Count(5) == 0 && Fixture.Count(6) == 0, "Failed take/materialization leaked an owned or untaken result.");
        }
    }

    private static async Task BindingFailureAndRejectedScheduling()
    {
        using var coordinator = new CallbackCoordinator(1, TimeSpan.FromMilliseconds(1));
        var scheduler = new ManualScheduler { RejectRemaining = 4, ThrowRemaining = 2 };
        var calls = new FixtureCalls();
        var operation = new AsyncOperation<NativeBuffer>(Fixture.New(2), calls, coordinator, Fixture.TakeBuffer,
            scheduler: scheduler, testBoundary: boundary =>
            {
                if (boundary == AsyncBoundary.TerminalFieldsCopied) throw new ApplicationException("terminal-copy-binding-fault");
            });
        Task<NativeBuffer> task = operation.Start();
        await Until(() => scheduler.Queued != 0, "Rejected/throwing scheduler lost the terminal latch.");
        scheduler.Run();
        NativeEngineException failure = await Failure<NativeEngineException>(task);
        await Finish(operation);
        Check((uint)failure.Data["NativeTerminalStatus"]! == NativeStatus.Ok && operation.Snapshot.NativeStatus == NativeStatus.Ok,
            "Binding failure overwrote the committed native outcome.");
        Check(operation.Snapshot.BindingFailure && operation.Snapshot.NoticeReceived && operation.SchedulingRejections == 6,
            "Failure-before-notice-publication lost its fixed terminal latch or retry count.");
        Check(calls.Poisoned == 1 && scheduler.MaxQueued == 1, "Binding failure poison or bounded retry invariant failed.");
    }

    private static async Task ResultBeforeNativeQuiet()
    {
        using var coordinator = new CallbackCoordinator(1);
        var input = new InputLease();
        var operation = new AsyncOperation<NativeBuffer>(Fixture.New(), new FixtureCalls(), coordinator, Fixture.TakeBuffer,
            inputLifetime: input, inputBytes: 3);
        Task<NativeBuffer> task = operation.Start();
        using SourceHandle source = Fixture.HoldSource(operation);
        Check(Fixture.Complete(operation) == NativeStatus.Ok, "Controlled native completion failed.");
        using NativeBuffer result = await task.WaitAsync(Deadline);
        AsyncSnapshot retained = operation.Snapshot;
        Check(!retained.NativeQuiet && retained.RootLive && !operation.Retirement.IsCompleted && retained.RetainedInputBytes == 3 && input.Releases == 0,
            "Task publication freed still-live native source/input budget.");
        using (var waitCancel = new CancellationTokenSource())
        {
            waitCancel.Cancel();
            await Failure<OperationCanceledException>(operation.DisposeAsync().AsTask().WaitAsync(waitCancel.Token));
        }
        Check(operation.Snapshot.RootLive && input.Releases == 0, "Finite/canceled close wait fabricated retirement.");
        source.Dispose();
        await Finish(operation);
        Check(input.Releases == 1, "Native quiet did not release retained input exactly once.");
        CheckBuffer(result);
    }

    private static void CheckPendingOwnership(AsyncOperation operation, CallbackCoordinator coordinator,
        InputLease input, ulong liveBefore, ulong releasedBefore)
    {
        AsyncSnapshot state = operation.Snapshot;
        Check(state.ResultProcessed && !state.NativeQuiet && !state.Released && state.RootLive && !state.Quarantined,
            "Pending native retirement lost the published result or retained managed root.");
        Check(state.Controls == 0 && state.CallbackEntries == 0 && state.RetainedInputBytes == 3 && input.Releases == 0,
            "Pending native retirement released input or retained an external control/callback entry.");
        Check(!operation.Retirement.IsCompleted && coordinator.Count == 1 && Fixture.Count(0) == liveBefore + 1 &&
            Fixture.Count(2) == releasedBefore, "Pending native retirement freed its operation owner or bounded slot.");
    }

    private static async Task ImmediateRetirementAfterPending()
    {
        // Critical assertions run synchronously between exact dequeues. The
        // one-minute coordinator timer is never used to advance these tests.
        using var coordinator = new CallbackCoordinator(1, TimeSpan.FromMinutes(1));
        var scheduler = new ManualScheduler();
        var input = new InputLease();
        var calls = new FixtureCalls();
        ulong liveBefore = Fixture.Count(0), releasedBefore = Fixture.Count(2);
        int takes = 0;
        var operation = new AsyncOperation<NativeBuffer>(Fixture.New(), calls, coordinator,
            pointer => { takes++; return Fixture.TakeBuffer(pointer); }, inputLifetime: input, inputBytes: 3, scheduler: scheduler);
        Task<NativeBuffer> task = operation.Start();
        using SourceHandle source = Fixture.HoldSource(operation);
        var statuses = new List<uint>();
        calls.AfterRetire = (_, status) =>
        {
            statuses.Add(status);
            if (calls.Retires != 1) return;
            Check(status == NativeStatus.Pending && operation.Snapshot.ResultProcessed && task.IsCompletedSuccessfully,
                "Immediate retirement retry was not reached after a real post-result Pending verdict.");
            source.Dispose(); // Native WorkLease drop has no callback or managed Signal.
        };
        Check(Fixture.Complete(operation) == NativeStatus.Ok, "Controlled immediate-retry completion failed.");
        Check(scheduler.Queued == 1 && scheduler.RunOne(), "Initial retirement work was not coalesced into one item.");
        Check(task.IsCompletedSuccessfully && takes == 1 && calls.Retires == 1 && source.IsClosed,
            "First retirement pass did not publish exactly one result before the native source became quiet.");
        using NativeBuffer result = task.GetAwaiter().GetResult();
        CheckPendingOwnership(operation, coordinator, input, liveBefore, releasedBefore);
        Check(scheduler.Queued == 1 && scheduler.MaxQueued == 1,
            "Post-result Pending did not queue one bounded follow-up without a new native Signal.");
        Check(scheduler.RunOne(), "The queued immediate retirement follow-up was lost.");
        Check(statuses.SequenceEqual(new[] { NativeStatus.Pending, NativeStatus.Ok }) && takes == 1 &&
            scheduler.Queued == 0 && scheduler.MaxQueued == 1 && operation.Retirement.IsCompletedSuccessfully,
            "The single follow-up did not retire without another signal, duplicated take, or extra queued work.");
        await Finish(operation);
        Check(input.Releases == 1 && coordinator.Count == 0 && Fixture.Count(0) == liveBefore &&
            Fixture.Count(2) == releasedBefore + 1 && calls.Poisoned == 0,
            "Immediate follow-up did not release the exact operation/input/slot ownership once.");
        CheckBuffer(result);
    }

    private static async Task ImmediateRetirementBudgetIsBounded()
    {
        using var coordinator = new CallbackCoordinator(1, TimeSpan.FromMinutes(1));
        var scheduler = new ManualScheduler();
        var input = new InputLease();
        var calls = new FixtureCalls();
        ulong liveBefore = Fixture.Count(0), releasedBefore = Fixture.Count(2);
        int takes = 0;
        var operation = new AsyncOperation<NativeBuffer>(Fixture.New(), calls, coordinator,
            pointer => { takes++; return Fixture.TakeBuffer(pointer); }, inputLifetime: input, inputBytes: 3, scheduler: scheduler);
        Task<NativeBuffer> task = operation.Start();
        using SourceHandle source = Fixture.HoldSource(operation);
        Check(Fixture.Complete(operation) == NativeStatus.Ok, "Controlled bounded-retry completion failed.");
        Check(scheduler.RunOne() && calls.Retires == 1 && scheduler.Queued == 1,
            "The first actual Pending did not schedule its single immediate follow-up.");
        Check(task.IsCompletedSuccessfully && takes == 1, "Result publication did not precede native retirement retries.");
        using NativeBuffer result = task.GetAwaiter().GetResult();
        Check(scheduler.RunOne() && calls.Retires == 2 && scheduler.Queued == 0 && !scheduler.RunOne(),
            "A still-live native source caused unbounded immediate retirement work.");
        CheckPendingOwnership(operation, coordinator, input, liveBefore, releasedBefore);

        operation.Retry(); // The existing timer fallback, advanced deterministically.
        Check(scheduler.Queued == 1 && scheduler.RunOne() && calls.Retires == 3 && scheduler.Queued == 0,
            "An explicit fallback retry reset the per-operation immediate-retry budget.");
        CheckPendingOwnership(operation, coordinator, input, liveBefore, releasedBefore);
        source.Dispose();
        Check(scheduler.Queued == 0 && calls.Retires == 3,
            "Source release unexpectedly fabricated a managed callback or retirement pass.");
        operation.Retry();
        Check(scheduler.Queued == 1 && scheduler.RunOne() && calls.Retires == 4 &&
            operation.Retirement.IsCompletedSuccessfully && scheduler.Queued == 0,
            "The retained timer retry did not recover after the native source became quiet.");
        await Finish(operation);
        Check(takes == 1 && input.Releases == 1 && coordinator.Count == 0 && scheduler.MaxQueued == 1 &&
            Fixture.Count(0) == liveBefore && Fixture.Count(2) == releasedBefore + 1 && calls.Poisoned == 0,
            "Bounded Pending fallback duplicated work or lost operation/input ownership.");
        CheckBuffer(result);
    }

    private static async Task RejectedImmediateRetirementScheduling()
    {
        foreach (bool throwBeforeAcceptance in new[] { false, true })
        {
            using var coordinator = new CallbackCoordinator(1, TimeSpan.FromMinutes(1));
            var scheduler = new ManualScheduler();
            var input = new InputLease();
            var calls = new FixtureCalls();
            ulong liveBefore = Fixture.Count(0), releasedBefore = Fixture.Count(2);
            int takes = 0;
            var operation = new AsyncOperation<NativeBuffer>(Fixture.New(), calls, coordinator,
                pointer => { takes++; return Fixture.TakeBuffer(pointer); }, inputLifetime: input, inputBytes: 3, scheduler: scheduler);
            Task<NativeBuffer> task = operation.Start();
            using SourceHandle source = Fixture.HoldSource(operation);
            calls.AfterRetire = (_, status) =>
            {
                if (calls.Retires != 1) return;
                Check(status == NativeStatus.Pending && operation.Snapshot.ResultProcessed,
                    "Immediate scheduler rejection was not injected after an actual post-result Pending.");
                if (throwBeforeAcceptance) scheduler.ThrowRemaining = 1;
                else scheduler.RejectRemaining = 1;
            };
            Check(Fixture.Complete(operation) == NativeStatus.Ok, "Controlled rejected-retry completion failed.");
            Check(scheduler.RunOne() && task.IsCompletedSuccessfully && takes == 1 && calls.Retires == 1,
                "Rejected immediate follow-up changed initial result publication.");
            using NativeBuffer result = task.GetAwaiter().GetResult();
            Check(operation.SchedulingRejections == 1 && scheduler.Queued == 0 &&
                scheduler.RejectRemaining == 0 && scheduler.ThrowRemaining == 0 && calls.Poisoned == 0,
                "The immediate scheduler rejection/exception was not counted without poisoning safe retry.");
            CheckPendingOwnership(operation, coordinator, input, liveBefore, releasedBefore);

            operation.Retry();
            Check(scheduler.Queued == 1 && scheduler.RunOne() && calls.Retires == 2 && scheduler.Queued == 0,
                "Fallback after rejected scheduling lost dirty state or restored the spent immediate budget.");
            CheckPendingOwnership(operation, coordinator, input, liveBefore, releasedBefore);
            source.Dispose(); // No Signal; only the explicit retry below advances cleanup.
            Check(scheduler.Queued == 0, "Quiet native source unexpectedly scheduled managed cleanup.");
            operation.Retry();
            Check(scheduler.Queued == 1 && scheduler.RunOne() && calls.Retires == 3 &&
                operation.Retirement.IsCompletedSuccessfully && scheduler.Queued == 0,
                "Explicit retry could not recover retained ownership after a scheduler refusal.");
            await Finish(operation);
            Check(takes == 1 && input.Releases == 1 && coordinator.Count == 0 && scheduler.MaxQueued == 1 &&
                operation.SchedulingRejections == 1 && calls.Poisoned == 0 && Fixture.Count(0) == liveBefore &&
                Fixture.Count(2) == releasedBefore + 1, "Rejected immediate retry duplicated take/release or leaked ownership.");
            CheckBuffer(result);
        }
    }

    private static async Task PreterminalPendingPreservesImmediateRetry()
    {
        using var coordinator = new CallbackCoordinator(1, TimeSpan.FromMinutes(1));
        var scheduler = new ManualScheduler();
        var input = new InputLease();
        var calls = new FixtureCalls();
        ulong liveBefore = Fixture.Count(0), releasedBefore = Fixture.Count(2);
        int takes = 0;
        var operation = new AsyncOperation<NativeBuffer>(Fixture.New(), calls, coordinator,
            pointer => { takes++; return Fixture.TakeBuffer(pointer); }, inputLifetime: input, inputBytes: 3, scheduler: scheduler);
        Task<NativeBuffer> task = operation.Start();
        using SourceHandle source = Fixture.HoldSource(operation);
        var statuses = new List<uint>();
        calls.AfterRetire = (pointer, status) =>
        {
            statuses.Add(status);
            if (calls.Retires == 1)
            {
                Check(status == NativeStatus.Pending && !operation.Snapshot.ResultProcessed && !task.IsCompleted &&
                    operation.Snapshot.Controls == 1, "The first native Pending was not before terminal result processing.");
                // This hook still owns TryRetire's cleanup control lease. The
                // real terminal callback supplies the next pass's only Signal.
                Check(Fixture.CompleteNative(pointer, NativeStatus.Ok) == NativeStatus.Ok,
                    "Terminal completion under the retained cleanup control lease failed.");
            }
            else if (calls.Retires == 2)
            {
                Check(status == NativeStatus.Pending && operation.Snapshot.ResultProcessed && task.IsCompletedSuccessfully,
                    "The second native Pending did not follow real result processing.");
                source.Dispose(); // Quiet now, without a second terminal callback or Signal.
            }
        };
        operation.Dispose(); // Request cleanup while native work has not selected its terminal result.
        Check(scheduler.RunOne() && calls.Retires == 1 && !task.IsCompleted && scheduler.Queued == 1,
            "Preterminal Pending lost the subsequent native terminal notification.");
        Check(scheduler.RunOne() && calls.Retires == 2 && task.IsCompletedSuccessfully && takes == 1 && source.IsClosed,
            "Terminal callback did not publish the result while retaining the native source.");
        using NativeBuffer result = task.GetAwaiter().GetResult();
        CheckPendingOwnership(operation, coordinator, input, liveBefore, releasedBefore);
        Check(scheduler.Queued == 1 && scheduler.RunOne() && operation.Retirement.IsCompletedSuccessfully,
            "Preterminal Pending consumed the later post-result immediate-retry budget.");
        Check(statuses.SequenceEqual(new[] { NativeStatus.Pending, NativeStatus.Pending, NativeStatus.Ok }) &&
            scheduler.Queued == 0 && scheduler.MaxQueued == 1 && takes == 1,
            "Preterminal and post-result Pending did not retain a single bounded result/retirement path.");
        await Finish(operation);
        Check(input.Releases == 1 && coordinator.Count == 0 && Fixture.Count(0) == liveBefore &&
            Fixture.Count(2) == releasedBefore + 1 && calls.Poisoned == 0,
            "Preterminal Pending recovery did not release operation/input/slot ownership exactly once.");
        CheckBuffer(result);
    }

    private static async Task BindingDiagnostics()
    {
        using var coordinator = new CallbackCoordinator(1);
        var calls = new FixtureCalls();
        var operation = new AsyncOperation<NativeBuffer>(Fixture.New(), calls, coordinator, Fixture.TakeBuffer,
            testBoundary: boundary => { if (boundary == AsyncBoundary.TerminalFieldsCopied) throw new ApplicationException("postcopy-fault"); });
        Task<NativeBuffer> task = operation.Start();
        Check(Fixture.Complete(operation, NativeStatus.DomainError) == NativeStatus.Ok, "Native DomainError did not commit.");
        NativeEngineException failure = await Failure<NativeEngineException>(task);
        await Finish(operation);
        Check((uint)failure.Data["NativeTerminalStatus"]! == NativeStatus.DomainError && operation.Snapshot.NativeStatus == NativeStatus.DomainError,
            "Binding failure rewrote native DomainError.");
        Check(failure.ErrorDomain == 17 && failure.NativeCode == -71 && failure.CorrelationId == 123,
            "Binding failure discarded the safely copied native domain/code/correlation snapshot.");
        Check(calls.Poisoned == 1, "Binding fault did not isolate ordinary engine admission once.");
    }

    private static async Task RetireFaultQuarantine()
    {
        using var coordinator = new CallbackCoordinator(1, TimeSpan.FromMinutes(1));
        var scheduler = new ManualScheduler();
        var input = new InputLease();
        var calls = new FixtureCalls();
        var operation = new AsyncOperation<NativeBuffer>(Fixture.New(34), calls, coordinator, Fixture.TakeBuffer,
            inputLifetime: input, inputBytes: 3, scheduler: scheduler);
        Task<NativeBuffer> task = operation.Start();
        scheduler.Run(); // all accepted work completes; no timer or later wake can rescue poison
        using NativeBuffer result = await task.WaitAsync(Deadline);
        CheckBuffer(result);
        Check(operation.Snapshot.Quarantined && calls.Poisoned == 1 && scheduler.Queued == 0,
            "Cleanup fault retained the context but failed to poison ordinary engine admission without another wake.");
        Check(!operation.Retirement.IsCompleted && operation.Snapshot.RootLive && input.Releases == 0 && Fixture.Count(0) == 1,
            "Failed native retire falsely freed cleanup ownership.");
        Console.WriteLine($"{{\"mode\":\"--retire-fault\",\"passed\":true,\"poisoned\":{calls.Poisoned},\"retained_native_owners\":1,\"retirement_reported_success\":false,\"assertions\":{s_assertions}}}");
    }

    private static async Task NativeWorkersAndGc()
    {
        using var coordinator = new CallbackCoordinator(2);
        for (int iteration = 0; iteration < 32; iteration++)
        {
            using var cancel = new CancellationTokenSource();
            var operation = new AsyncOperation<NativeBuffer>(Fixture.New(), new FixtureCalls(), coordinator, Fixture.TakeBuffer, cancel.Token);
            Task<NativeBuffer> task = operation.Start();
            uint status = iteration % 2 == 0 ? NativeStatus.Ok : NativeStatus.Canceled;
            Check(Fixture.Spawn(operation, status) == NativeStatus.Ok, "Bounded Rust completion worker start failed.");
            Task canceling = Task.Run(cancel.Cancel);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            if (status == NativeStatus.Ok)
            {
                using NativeBuffer result = await task.WaitAsync(Deadline);
                CheckBuffer(result);
            }
            else await Failure<OperationCanceledException>(task);
            await canceling.WaitAsync(Deadline);
            await Finish(operation);
            Check(operation.Snapshot.NativeStatus == status, "Concurrent cancel/GC changed the chosen native terminal outcome.");
        }
    }

    private static async Task ContextAndSelfWait()
    {
        using var coordinator = new CallbackCoordinator(1);
        Ambient.Value = "awaiting-caller";
        AsyncOperation<NativeBuffer>? operation = null;
        bool prevented = false;
        operation = new(Fixture.New(2), new FixtureCalls(), coordinator, pointer =>
        {
            Check(Ambient.Value is null && !Fixture.InNativeCall, "Internal coordinator captured caller ExecutionContext or ran on native stack.");
            return Fixture.TakeBuffer(pointer);
        }, testBoundary: boundary =>
        {
            if (boundary != AsyncBoundary.BeforeTake) return;
            try { _ = operation!.DisposeAsync(); }
            catch (InvalidOperationException) { prevented = true; }
        });
        using (NativeBuffer result = await operation.Start().WaitAsync(Deadline))
        {
            Check(Ambient.Value == "awaiting-caller", "Application await lost its normal ExecutionContext.");
            CheckBuffer(result);
        }
        await Finish(operation);
        Check(prevented, "Result materializer was allowed to await its own retirement.");

        using var cancel = new CancellationTokenSource();
        string? cancellationContext = null;
        var canceled = new AsyncOperation<NativeBuffer>(Fixture.New(4), new FixtureCalls(), coordinator, Fixture.TakeBuffer,
            cancel.Token, testBoundary: boundary => { if (boundary == AsyncBoundary.CancelEntered) cancellationContext = Ambient.Value; });
        Task<NativeBuffer> task = canceled.Start();
        await Task.Run(() => { Ambient.Value = "canceling-thread"; cancel.Cancel(); });
        await Failure<OperationCanceledException>(task);
        await Finish(canceled);
        Check(cancellationContext == "canceling-thread", "UnsafeRegister captured the registration ExecutionContext.");
        Check(Ambient.Value == "awaiting-caller", "Internal cancellation altered the caller ExecutionContext.");
        Ambient.Value = null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AbandonTask(CallbackCoordinator coordinator)
    {
        var operation = new AsyncOperation<NativeBuffer>(Fixture.New(2), new FixtureCalls(), coordinator, Fixture.TakeBuffer);
        _ = operation.Start();
        return new WeakReference(operation);
    }

    private static async Task DiscardedTaskCleanup()
    {
        using var coordinator = new CallbackCoordinator(1);
        WeakReference context = AbandonTask(coordinator);
        await Until(() => coordinator.Count == 0, "Discarded Task stopped automatic operation retirement.");
        await Until(() =>
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            return !context.IsAlive && Fixture.Count(5) == 0;
        }, "Discarded Task/context/result did not reach nonblocking final release.");
        Check(Fixture.Count(0) == 0 && Fixture.Count(6) == 0, "Discarded Task leaked operation/result backing storage.");
    }

    private static void LostWakeRegression()
    {
        // Exact A19 stale_unschedule counterexample ordering: dequeue/read clean;
        // producer publishes while scheduled; consumer then unschedules. The
        // actual shared A06 slot atomically rechecks dirty with unscheduling.
        // No coordinator/timer is constructed, so polling cannot rescue this.
        var scheduler = new ManualScheduler();
        var probe = new WakeProbe(scheduler);
        probe.Signal();
        scheduler.Run();
        Check(probe.Passes == 2 && scheduler.MaxQueued == 1 && scheduler.Queued == 0,
            "Wake during drain was overwritten by stale unscheduling.");
    }

    private static async Task Quarantine(string mode)
    {
        using var coordinator = new CallbackCoordinator(1);
        var input = new InputLease();
        var calls = new FixtureCalls { ThrowAfterStart = mode == "--indeterminate-start" };
        var operation = new AsyncOperation<NativeBuffer>(Fixture.New(mode == "--callback-then-reject" ? 10u : 0u), calls,
            coordinator, Fixture.TakeBuffer, inputLifetime: input, inputBytes: 3);
        await Failure<Exception>(operation.Start());
        await Until(() => calls.Poisoned == 1, "Indeterminate start did not poison engine admission.");
        GC.Collect(); GC.WaitForPendingFinalizers();
        Check(operation.Snapshot.Quarantined && operation.Snapshot.Start == AsyncStartOutcome.Indeterminate && operation.Snapshot.RootLive,
            "Unknown start acceptance was guessed to be safe never-started cleanup.");
        Check(!operation.Retirement.IsCompleted && input.Releases == 0 && Fixture.Count(0) == 1 && coordinator.Count == 1,
            "Quarantined owner/root/budget was freed or closure was falsely reported.");
        Console.WriteLine($"{{\"mode\":\"{mode}\",\"passed\":true,\"retained_native_owners\":{Fixture.Count(0)},\"retained_input_bytes\":3,\"retained_context_roots\":1,\"retirement_reported_success\":false,\"assertions\":{s_assertions}}}");
    }

    private static async Task Main(string[] args)
    {
        if (args.Contains("--require-aot")) Check(!RuntimeFeature.IsDynamicCodeSupported && !RuntimeFeature.IsDynamicCodeCompiled, "Expected actual Native AOT execution.");
        if (args.Contains("--retire-fault")) { await RetireFaultQuarantine(); return; }
        if (args.Contains("--binding-diagnostics")) { await BindingDiagnostics(); Console.WriteLine("A07 copied binding diagnostics regression passed."); return; }
        string? isolation = args.FirstOrDefault(a => a is "--indeterminate-start" or "--callback-then-reject");
        if (isolation is not null) { await Quarantine(isolation); return; }
        Check(Fixture.Count(0) == 0 && Fixture.Count(5) == 0, "Fixture did not begin at zero live owners.");
        await SynchronousCompletion();
        await PreCanceledRegistration();
        await CancellationAndOutcomes();
        await CancellationRegistrationDrain();
        await FailedSetupCompensation();
        await DuplicateAndTakeFailures();
        await BindingFailureAndRejectedScheduling();
        await BindingDiagnostics();
        await ResultBeforeNativeQuiet();
        await ImmediateRetirementAfterPending();
        await ImmediateRetirementBudgetIsBounded();
        await RejectedImmediateRetirementScheduling();
        await PreterminalPendingPreservesImmediateRetry();
        await NativeWorkersAndGc();
        await ContextAndSelfWait();
        await DiscardedTaskCleanup();
        LostWakeRegression();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check(Fixture.Count(0) == 0 && Fixture.Count(1) == Fixture.Count(2) && Fixture.Count(3) == 0,
            "Native operation owner accounting did not return to baseline.");
        Check(Fixture.Count(4) == 0 && Fixture.Count(5) == 0 && Fixture.Count(6) == 0, "Input/result allocation accounting did not return to baseline.");
        // Explicit text keeps the runtime consumer reflection-free and standalone.
        Console.WriteLine($"{{\"task\":\"A07\",\"passed\":true,\"assertions\":{s_assertions},\"native_owners_created\":{Fixture.Count(1)},\"native_owners_released\":{Fixture.Count(2)},\"live_owners\":0,\"input_bytes\":0,\"result_owners\":0,\"gc_iterations\":32,\"lost_wake_product_regression\":true,\"aot\":{(!RuntimeFeature.IsDynamicCodeSupported).ToString().ToLowerInvariant()}}}");
    }

    private sealed class InputLease : IDisposable
    {
        internal int Releases;
        public void Dispose() => Interlocked.Increment(ref Releases);
    }

    private sealed class ManualScheduler : ICallbackScheduler
    {
        private readonly ConcurrentQueue<IThreadPoolWorkItem> _queue = new();
        private readonly object _gate = new();
        internal int RejectRemaining;
        internal int ThrowRemaining;
        internal int MaxQueued;
        internal int Queued => _queue.Count;
        public bool TryQueue(IThreadPoolWorkItem item)
        {
            lock (_gate)
            {
                if (ThrowRemaining > 0) { ThrowRemaining--; throw new ApplicationException("scheduler rejected before acceptance"); }
                if (RejectRemaining > 0) { RejectRemaining--; return false; }
                _queue.Enqueue(item);
                MaxQueued = Math.Max(MaxQueued, _queue.Count);
                return true;
            }
        }
        internal void Run()
        {
            for (int count = 0; _queue.TryDequeue(out IThreadPoolWorkItem? item); count++)
            {
                if (count > 32) throw new Exception("Unbounded coordinator rescheduling.");
                item.Execute();
            }
        }
        internal bool RunOne()
        {
            if (!_queue.TryDequeue(out IThreadPoolWorkItem? item)) return false;
            item.Execute();
            return true;
        }
    }

    private sealed class WakeProbe(ManualScheduler scheduler) : CoordinatedCallback(scheduler)
    {
        internal int Passes;
        protected override void Process()
        {
            Passes++;
            if (Passes == 1) Task.Run(Signal).GetAwaiter().GetResult();
            else CompleteScheduling();
        }
        protected override void SchedulingFault(Exception failure) => throw new Exception("Lost-wake probe failed.", failure);
    }
}
