using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Korda.Interop;
using Korda.Interop.Abi;
using Korda.Interop.Async;
using Korda.Interop.Callbacks;
using Korda.Interop.Lifetime;

internal static partial class Program
{
    private static async Task OperationOwnershipChecks()
    {
        int before = s_assertions;
        await OperationConstructionOwnership();
        await OperationAcquisitionFailure();
        await OperationHeldControl();
        await OperationDestructorBoundary();
        await IndependentDelayedResults();
        await EnrichedCanceledDiagnostic();
        Console.WriteLine($"PASS operation ownership: {s_assertions - before} assertions; actual resource-owner controls, constructor compensation, physical retirement and independent Task results");
    }

    private static T OwnershipFailure<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T failure) { Check(true, typeof(T).Name); return failure; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static async Task OperationConstructionOwnership()
    {
        using var coordinator = new CallbackCoordinator(1);
        var input = new InputLease();
        OwnershipFailure<ArgumentNullException>(() => _ = new AsyncOperation<NativeBuffer>(
            null!, new FixtureCalls(), coordinator, Fixture.TakeBuffer, inputLifetime: input));
        // Handle validation belongs to the real resource-owner constructor.
        OwnershipFailure<ArgumentNullException>(() => _ = new NativeResourceOwner(null!));
        using (var invalid = new OperationHandle())
        {
            OwnershipFailure<ArgumentException>(() => _ = new NativeResourceOwner(invalid));
            Check(!invalid.IsClosed, "Rejected resource construction stole the caller's invalid handle.");
        }
        using (var closed = Fixture.NewHandle())
        {
            closed.Dispose();
            ulong released = Fixture.Count(2);
            OwnershipFailure<ArgumentException>(() => _ = new NativeResourceOwner(closed));
            Check(Fixture.Count(2) == released, "A rejected closed resource was physically released twice.");
        }
        Check(coordinator.Count == 0 && input.Releases == 0,
            "Rejected operation construction reserved capacity or stole input ownership.");
        input.Dispose();

        foreach (var boundary in new[] { AsyncBoundary.BeforeRoot, AsyncBoundary.RootAllocated })
        {
            WeakReference rejected = RejectOperationConstruction(coordinator, boundary);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Check(!rejected.IsAlive, "Failed operation construction retained a callback root/input reference.");
        }

        var firstOwner = Fixture.New();
        var firstInput = new InputLease();
        var first = new AsyncOperation<NativeBuffer>(firstOwner, new FixtureCalls(), coordinator,
            Fixture.TakeBuffer, inputLifetime: firstInput);
        using (var rejected = Fixture.New())
        {
            var callerInput = new InputLease();
            OwnershipFailure<InvalidOperationException>(() => _ = new AsyncOperation<NativeBuffer>(
                rejected, new FixtureCalls(), coordinator, Fixture.TakeBuffer, inputLifetime: callerInput));
            Check(!rejected.IsClosing && callerInput.Releases == 0 && coordinator.Count == 1,
                "Exhausted operation admission stole ownership or changed the existing reservation.");
            ulong released = Fixture.Count(2);
            rejected.Dispose(); rejected.Dispose();
            Check(Fixture.Count(2) == released + 1, "Rejected operation admission did not retain one caller-owned resource.");
            callerInput.Dispose();
        }
        await first.DisposeAsync().AsTask().WaitAsync(Deadline);
        await Failure<ObjectDisposedException>(first.Task);
        await Finish(first);
        Check(firstOwner.IsClosing && firstOwner.Retirement.IsCompletedSuccessfully
            && firstInput.Releases == 1 && coordinator.Count == 0,
            "A successfully constructed, never-started operation did not retire its owners.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RejectOperationConstruction(CallbackCoordinator coordinator, AsyncBoundary boundary)
    {
        using var owner = Fixture.New();
        var input = new InputLease();
        var original = new ApplicationException("operation-construction-" + boundary);
        var failure = OwnershipFailure<ApplicationException>(() => _ = new AsyncOperation<NativeBuffer>(
            owner, new FixtureCalls(), coordinator, Fixture.TakeBuffer, inputLifetime: input,
            testBoundary: point => { if (point == boundary) throw original; }));
        Check(ReferenceEquals(failure, original) && !owner.IsClosing && input.Releases == 0 && coordinator.Count == 0,
            "Failed operation construction changed exception identity or transferred caller owners.");
        ulong released = Fixture.Count(2);
        owner.Dispose(); owner.Dispose();
        Check(Fixture.Count(2) == released + 1, "Caller compensation did not release its failed operation owner once.");
        input.Dispose();
        return new WeakReference(input);
    }

    private static async Task OperationAcquisitionFailure()
    {
        foreach (var boundary in new[] { LeaseBoundary.OpenChecked, LeaseBoundary.LogicalAcquired,
            LeaseBoundary.PhysicalAcquired, LeaseBoundary.PointerRead })
        {
            using var coordinator = new CallbackCoordinator(1);
            var owner = Fixture.New();
            var operation = new AsyncOperation<NativeBuffer>(owner, new FixtureCalls(), coordinator, Fixture.TakeBuffer);
            var original = new ApplicationException("operation-control-" + boundary);
            owner.TestBoundary = point => { if (point == boundary) throw original; };
            bool acquired = false;
            var failure = OwnershipFailure<ApplicationException>(() => _ = operation.AcquireControl(ref acquired));
            Check(ReferenceEquals(failure, original) && !acquired && operation.Snapshot.Controls == 0
                && owner.ActiveLeases == 0,
                "Failed resource acquisition left a caller flag, logical count or physical reference obligation.");
            operation.ReleaseControl(ref acquired);
            Check(operation.Snapshot.Controls == 0 && owner.ActiveLeases == 0,
                "The caller's finally decremented an already-compensated operation control.");
            owner.TestBoundary = null;
            await operation.DisposeAsync().AsTask().WaitAsync(Deadline);
            await Failure<ObjectDisposedException>(operation.Task);
            await Finish(operation);
        }
    }

    private static async Task OperationHeldControl()
    {
        using var coordinator = new CallbackCoordinator(1, TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();
        var scheduler = new ManualScheduler();
        var input = new InputLease();
        var owner = Fixture.New();
        var calls = new FixtureCalls();
        var operation = new AsyncOperation<NativeBuffer>(owner, calls, coordinator, Fixture.TakeBuffer,
            cancellation.Token, input, 3, scheduler);
        var pending = operation.Start();
        bool acquired = false;
        nint pointer = operation.AcquireControl(ref acquired);
        try
        {
            OwnershipFailure<InvalidOperationException>(() => _ = operation.AcquireControl(ref acquired));
            Check(operation.Snapshot.Controls == 1 && owner.ActiveLeases == 1,
                "A duplicate control changed the active operation/resource count.");
            Check(Fixture.CompleteNative(pointer, NativeStatus.Ok) == NativeStatus.Ok, "Held-control native completion failed.");
            cancellation.Cancel(); operation.Dispose(); scheduler.Run();
            Check(!pending.IsCompleted && !operation.Retirement.IsCompleted && !owner.IsClosing,
                "Native completion/close/cancel released a held control or exposed its result.");
            Check(input.Releases == 0 && coordinator.Count == 1 && operation.Snapshot.RootLive
                && operation.Snapshot.Controls == 1 && owner.ActiveLeases == 1 && calls.Retires == 0,
                "A held control lost its input, reservation, callback root or physical lease.");
            operation.ReleaseControl(ref acquired); operation.ReleaseControl(ref acquired);
            scheduler.Run();
            using var result = await pending.WaitAsync(Deadline);
            CheckBuffer(result); await Finish(operation);
            Check(!acquired && owner.IsClosing && owner.ActiveLeases == 0 && owner.Retirement.IsCompletedSuccessfully
                && input.Releases == 1 && coordinator.Count == 0,
                "Control release did not complete operation/resource ownership exactly once.");
        }
        finally { operation.ReleaseControl(ref acquired); }
    }

    private static async Task OperationDestructorBoundary()
    {
        using var coordinator = new CallbackCoordinator(1, TimeSpan.FromMinutes(1));
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var scheduler = new ManualScheduler();
        var input = new InputLease();
        var handle = new BarrierOperationHandle(entered, resume);
        NativeResourceOwner owner;
        try
        {
            Check(Fixture.Create(2, [1, 2, 3], 0, out nint pointer) == NativeStatus.Ok, "Destructor fixture create failed.");
            handle.Adopt(pointer);
            owner = new(handle);
        }
        catch { resume.Set(); handle.Dispose(); throw; }
        var operation = new AsyncOperation<NativeBuffer>(owner, new FixtureCalls(), coordinator, Fixture.TakeBuffer,
            inputLifetime: input, inputBytes: 3, scheduler: scheduler);
        var pending = operation.Start();
        Task retirement = operation.Retirement;
        Task drain = Task.Run(scheduler.Run);
        try
        {
            Check(entered.Wait(Deadline), "The native owner destructor did not enter.");
            var inspection = await Task.Run(() =>
            {
                bool acquired = false;
                OwnershipFailure<ObjectDisposedException>(() => _ = operation.AcquireControl(ref acquired));
                Check(!acquired, "A retiring operation admitted another physical control.");
                return operation.Snapshot;
            }).WaitAsync(Deadline);
            Check(inspection.RootLive && !inspection.Released && inspection.Controls == 0 && coordinator.Count == 1,
                "Destructor execution held the admission lock or discharged root/reservation early.");
            Check(pending.IsCompletedSuccessfully && !retirement.IsCompleted && !owner.Retirement.IsCompleted
                && input.Releases == 0,
                "The independent Task result and actual physical retirement were conflated.");
            Check(ReferenceEquals(retirement, operation.Retirement), "Pending retirement observers lost their shared completion.");
            GC.Collect(); GC.WaitForPendingFinalizers();
            Check(Fixture.Count(0) == 1, "GC released the native owner while its destructor was active.");
        }
        finally { resume.Set(); }
        await drain.WaitAsync(Deadline);
        using var result = await pending.WaitAsync(Deadline);
        CheckBuffer(result); await Finish(operation);
        Check(handle.Releases == 1 && !handle.TimedOut && owner.Retirement.IsCompletedSuccessfully
            && input.Releases == 1 && coordinator.Count == 0,
            "Physical retirement was duplicated, timed out or failed to release its remaining owners.");
    }

    private static async Task IndependentDelayedResults()
    {
        using var coordinator = new CallbackCoordinator(1);
        var first = new AsyncOperation<NativeBuffer>(Fixture.New(2), new FixtureCalls(), coordinator, Fixture.TakeBuffer);
        var unconsumed = first.Start();
        await Finish(first);
        var second = new AsyncOperation<NativeBuffer>(Fixture.New(2), new FixtureCalls(), coordinator, Fixture.TakeBuffer);
        using var secondResult = await second.Start().WaitAsync(Deadline);
        await Finish(second);
        using var firstResult = await unconsumed.WaitAsync(Deadline);
        Check(ReferenceEquals(firstResult, await unconsumed) && !ReferenceEquals(firstResult, secondResult),
            "A later operation overwrote a delayed independent result or its Task identity.");
        CheckBuffer(firstResult); CheckBuffer(secondResult);
    }

    private static async Task EnrichedCanceledDiagnostic()
    {
        using var coordinator = new CallbackCoordinator(1);
        using var cancellation = new CancellationTokenSource();
        var cleanup = new ApplicationException("retained-cleanup-diagnostic");
        var enriched = new OperationCanceledException("native-canceled-with-cleanup", cleanup, cancellation.Token);
        var calls = new FixtureCalls { MappedFailure = enriched };
        var operation = new AsyncOperation<NativeBuffer>(Fixture.New(), calls, coordinator, Fixture.TakeBuffer, cancellation.Token);
        var pending = operation.Start();
        Check(Fixture.Complete(operation, NativeStatus.Canceled) == NativeStatus.Ok, "Enriched cancellation did not commit.");
        await Until(() => pending.IsCompleted, "The canceled Task did not publish its outcome.");
        // Observe the original Task directly: WaitAsync may create a proxy Task
        // whose TaskCanceledException correctly refers to that proxy instead.
        var failure = OwnershipFailure<TaskCanceledException>(() => pending.GetAwaiter().GetResult());
        Check(failure.CancellationToken == cancellation.Token && ReferenceEquals(failure.Task, pending) && pending.IsCanceled,
            "The Task cancellation lost its token or Task identity.");
        Check(ReferenceEquals(operation.TerminalFailure, enriched) && ReferenceEquals(operation.TerminalFailure?.InnerException, cleanup),
            "The domain completion adapter cannot recover the original owned cancellation cleanup diagnostic.");
        await Finish(operation);
    }

    // A deterministic test barrier only: real native destructors remain
    // nonblocking. It proves retirement follows the physical Dispose return.
    private sealed class BarrierOperationHandle(ManualResetEventSlim entered, ManualResetEventSlim resume) : SafeHandle(0, true)
    {
        internal int Releases;
        internal bool TimedOut;
        internal void Adopt(nint value) => SetHandle(value);
        public override bool IsInvalid => handle == 0;
        protected override bool ReleaseHandle()
        {
            entered.Set();
            TimedOut = !resume.Wait(Deadline);
            Fixture.Release(handle);
            Interlocked.Increment(ref Releases);
            return true;
        }
    }
}
