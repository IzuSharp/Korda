using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Korda.Interop;
using Korda.Interop.Abi;
using Korda.Interop.Async;
using Korda.Interop.Callbacks;

internal static partial class Program
{
    private static async Task DirectHandleChecks()
    {
        int before = s_assertions;
        await DirectConstructionOwnership();
        await DirectAcquisitionFailure();
        await DirectHeldControl();
        await DirectDestructorBoundary();
        Console.WriteLine($"PASS private direct handle: {s_assertions - before} assertions; constructor compensation, physical controls and destructor-before-result");
    }

    private static T DirectFailure<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T failure) { Check(true, typeof(T).Name); return failure; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static async Task DirectConstructionOwnership()
    {
        using var coordinator = new CallbackCoordinator(1);
        var calls = new RetiredCalls();
        var input = new InputLease();
        DirectFailure<ArgumentNullException>(() => _ = new AsyncOperation.RetiredValueTask<NativeBuffer>(
            (SafeHandle)null!, calls, coordinator, Fixture.TakeBuffer, inputLifetime: input));
        using (var invalid = new OperationHandle())
        {
            DirectFailure<ArgumentException>(() => _ = new AsyncOperation.RetiredValueTask<NativeBuffer>(
                invalid, calls, coordinator, Fixture.TakeBuffer, inputLifetime: input));
            Check(!invalid.IsClosed, "An invalid-handle constructor took caller ownership.");
        }
        using (var closed = Fixture.NewHandle())
        {
            closed.Dispose();
            ulong released = Fixture.Count(2);
            DirectFailure<ArgumentException>(() => _ = new AsyncOperation.RetiredValueTask<NativeBuffer>(
                closed, calls, coordinator, Fixture.TakeBuffer, inputLifetime: input));
            Check(Fixture.Count(2) == released, "A closed handle was released twice during rejection.");
        }
        Check(coordinator.Count == 0 && input.Releases == 0,
            "Rejected direct-handle validation reserved capacity or stole input ownership.");
        input.Dispose();

        foreach (var boundary in new[] { AsyncBoundary.BeforeRoot, AsyncBoundary.RootAllocated })
        {
            WeakReference rejected = RejectDirectConstruction(coordinator, boundary);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Check(!rejected.IsAlive, "Failed direct construction retained a callback root/input reference.");
        }

        var firstHandle = Fixture.NewHandle();
        var firstInput = new InputLease();
        var first = new AsyncOperation.RetiredValueTask<NativeBuffer>(firstHandle, calls, coordinator,
            Fixture.TakeBuffer, inputLifetime: firstInput);
        using (var rejected = Fixture.NewHandle())
        {
            var callerInput = new InputLease();
            DirectFailure<InvalidOperationException>(() => _ = new AsyncOperation.RetiredValueTask<NativeBuffer>(
                rejected, calls, coordinator, Fixture.TakeBuffer, inputLifetime: callerInput));
            Check(!rejected.IsClosed && callerInput.Releases == 0 && coordinator.Count == 1,
                "Exhausted direct admission stole ownership or changed the existing reservation.");
            ulong released = Fixture.Count(2);
            rejected.Dispose(); rejected.Dispose();
            Check(Fixture.Count(2) == released + 1, "Rejected direct admission did not leave one caller-owned native handle.");
            callerInput.Dispose();
        }
        await first.DisposeAsync().AsTask().WaitAsync(Deadline);
        await Finish(first);
        Check(firstHandle.IsClosed && firstInput.Releases == 1 && coordinator.Count == 0,
            "A successfully constructed, never-started direct operation did not retire its owners.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RejectDirectConstruction(CallbackCoordinator coordinator, AsyncBoundary boundary)
    {
        using var handle = Fixture.NewHandle();
        var input = new InputLease();
        var original = new ApplicationException("direct-construction-" + boundary);
        var failure = DirectFailure<ApplicationException>(() => _ = new AsyncOperation.RetiredValueTask<NativeBuffer>(
            handle, new RetiredCalls(), coordinator, Fixture.TakeBuffer, inputLifetime: input,
            testBoundary: point => { if (point == boundary) throw original; }));
        Check(ReferenceEquals(failure, original) && !handle.IsClosed && input.Releases == 0 && coordinator.Count == 0,
            "Failed direct construction changed exception identity or transferred caller owners.");
        ulong released = Fixture.Count(2);
        handle.Dispose(); handle.Dispose();
        Check(Fixture.Count(2) == released + 1, "Caller compensation did not release the failed direct handle exactly once.");
        input.Dispose();
        return new WeakReference(input);
    }

    private static async Task DirectAcquisitionFailure()
    {
        using var coordinator = new CallbackCoordinator(1);
        var handle = Fixture.NewHandle();
        var operation = new AsyncOperation.RetiredValueTask<NativeBuffer>(handle, new RetiredCalls(), coordinator, Fixture.TakeBuffer);
        // Deliberate fault injection violates exclusive caller ownership only to
        // make the actual SafeHandle.AddRef fail before native start.
        handle.Dispose();
        bool acquired = false;
        DirectFailure<ObjectDisposedException>(() => _ = operation.AcquireControl(ref acquired));
        Check(!acquired && operation.Snapshot.Controls == 0,
            "Failed physical AddRef left a true caller flag or logical control count.");
        operation.ReleaseControl(ref acquired);
        Check(operation.Snapshot.Controls == 0, "A caller finally decremented an already-compensated control.");
        await operation.DisposeAsync().AsTask().WaitAsync(Deadline);
        await Finish(operation);
    }

    private static async Task DirectHeldControl()
    {
        using var coordinator = new CallbackCoordinator(1, TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();
        var scheduler = new ManualScheduler();
        var input = new InputLease();
        var handle = Fixture.NewHandle();
        var calls = new RetiredCalls();
        var operation = new AsyncOperation.RetiredValueTask<NativeBuffer>(handle, calls, coordinator, Fixture.TakeBuffer,
            cancellation.Token, input, 3, scheduler);
        var pending = operation.Start();
        bool acquired = false;
        nint pointer = operation.AcquireControl(ref acquired);
        try
        {
            DirectFailure<InvalidOperationException>(() => _ = operation.AcquireControl(ref acquired));
            Check(operation.Snapshot.Controls == 1, "A duplicate direct control changed the active count.");
            Check(Fixture.CompleteNative(pointer, NativeStatus.Ok) == NativeStatus.Ok, "Held-control native completion failed.");
            cancellation.Cancel(); operation.Dispose(); scheduler.Run();
            Check(!pending.IsCompleted && !operation.Retirement.IsCompleted && !handle.IsClosed,
                "Native completion/close/cancel released a held direct control or published its result.");
            Check(input.Releases == 0 && coordinator.Count == 1 && operation.Snapshot.RootLive
                && operation.Snapshot.Controls == 1 && calls.Observations == 0,
                "A held direct control lost its input, reservation or callback root.");
            operation.ReleaseControl(ref acquired); operation.ReleaseControl(ref acquired);
            scheduler.Run();
            using var result = await pending.AsTask().WaitAsync(Deadline);
            CheckBuffer(result); await Finish(operation);
            Check(!acquired && handle.IsClosed && input.Releases == 1 && coordinator.Count == 0 && calls.Observations == 1,
                "Direct control release did not complete ownership exactly once.");
        }
        finally { operation.ReleaseControl(ref acquired); }
    }

    private static async Task DirectDestructorBoundary()
    {
        using var coordinator = new CallbackCoordinator(1, TimeSpan.FromMinutes(1));
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var scheduler = new ManualScheduler();
        var input = new InputLease();
        var calls = new RetiredCalls();
        var handle = new BarrierOperationHandle(entered, resume);
        try
        {
            Check(Fixture.Create(2, [1, 2, 3], 0, out nint pointer) == NativeStatus.Ok, "Direct destructor fixture create failed.");
            handle.Adopt(pointer);
        }
        catch { resume.Set(); handle.Dispose(); throw; }
        var operation = new AsyncOperation.RetiredValueTask<NativeBuffer>(handle, calls, coordinator, Fixture.TakeBuffer,
            inputLifetime: input, inputBytes: 3, scheduler: scheduler);
        var pending = operation.Start();
        Task retirement = operation.Retirement;
        Task drain = Task.Run(scheduler.Run);
        try
        {
            Check(entered.Wait(Deadline), "The direct native owner destructor did not enter.");
            var inspection = await Task.Run(() =>
            {
                bool acquired = false;
                DirectFailure<ObjectDisposedException>(() => _ = operation.AcquireControl(ref acquired));
                Check(!acquired, "A closing direct owner admitted a new physical control.");
                return operation.Snapshot;
            }).WaitAsync(Deadline);
            Check(inspection.RootLive && !inspection.Released && inspection.Controls == 0 && coordinator.Count == 1,
                "Destructor execution held the admission lock or discharged root/reservation early.");
            Check(!pending.IsCompleted && !retirement.IsCompleted && input.Releases == 0 && calls.Observations == 0,
                "Retirement/result/input completion preceded the direct destructor's return.");
            GC.Collect(); GC.WaitForPendingFinalizers();
            Check(Fixture.Count(0) == 1, "GC released a direct native owner while its destructor was still active.");
        }
        finally { resume.Set(); }
        await drain.WaitAsync(Deadline);
        using var result = await pending.AsTask().WaitAsync(Deadline);
        CheckBuffer(result); await Finish(operation);
        Check(handle.Releases == 1 && !handle.TimedOut && input.Releases == 1 && coordinator.Count == 0 && calls.Observations == 1,
            "Direct destructor retirement was duplicated, timed out or failed to release the remaining owners.");
    }

    // A deterministic test barrier only: real native destructors remain
    // nonblocking. It proves publication happens after physical Dispose returns.
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
