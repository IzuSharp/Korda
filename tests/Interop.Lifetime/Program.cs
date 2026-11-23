using System.Runtime.CompilerServices;
using Korda.Interop.Lifetime;

internal static class Program
{
    private static int _assertions;
    private static void Check(bool condition, string message)
    {
        Interlocked.Increment(ref _assertions);
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { Check(true, typeof(T).Name); return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static void Clean() => Check(Fixture.Counter(0) == 0 && Fixture.Counter(2) == 0, "native owners and parent states returned to zero");

    public static async Task Main(string[] args)
    {
        if (args.Contains("--require-aot")) Check(!RuntimeFeature.IsDynamicCodeSupported, "actual Native AOT execution");
        Clean();
        Basic();
        foreach (LeaseBoundary boundary in Enum.GetValues<LeaseBoundary>())
        {
            await AcquireBeforeClose(boundary);
            Rollback(boundary);
        }
        await CloseBeforeAcquire();
        await RetirementAndGc();
        ParentChild();
        PairAcquisitionFailure();
        MapperFailure();
        FinalizerFallback();
        await ReentrantDispose();
        HotPathAllocations();
        Clean();
        Console.WriteLine($"A03 PASS assertions={_assertions} owners={Fixture.Counter(0)} releases={Fixture.Counter(1)} parent_states={Fixture.Counter(2)} parent_releases={Fixture.Counter(3)} calls={Fixture.Counter(4)}");
        Console.WriteLine("Schedules: open-check/close, logical-token/close, AddRef/close, pointer-read/close, close-before-acquire, owner-drop/last-release, GC/in-flight; bounds <=3 threads, <=2 calls, <=8 forced phase switches per schedule; finite coverage, not exhaustive interleavings.");
    }

    private static void Basic()
    {
        ulong releases = Fixture.Counter(1);
        using var owner = Fixture.New();
        Check(!owner.IsClosing && Fixture.Invoke(owner) == 42, "open owner");
        owner.Dispose(); owner.Dispose();
        Check(owner.IsClosing && owner.Retirement.IsCompletedSuccessfully, "physical retirement after synchronous dispose");
        Throws<ObjectDisposedException>(() => Fixture.Invoke(owner));
        Check(Fixture.Counter(1) == releases + 1, "idempotent managed disposal, one creator release");
        using var invalid = new FixtureHandle(0);
        Throws<ArgumentException>(() => new NativeResourceOwner(invalid));
        Check(!invalid.IsClosed, "failed adoption retains caller ownership");
    }

    private static async Task AcquireBeforeClose(LeaseBoundary boundary)
    {
        using var atBoundary = new ManualResetEventSlim();
        using var continueAcquire = new ManualResetEventSlim();
        using var admitted = new ManualResetEventSlim();
        using var finishCall = new ManualResetEventSlim();
        using var closeStarted = new ManualResetEventSlim();
        var owner = Fixture.New(123);
        ulong releases = Fixture.Counter(1);
        owner.TestBoundary = point =>
        {
            if (point == boundary) { atBoundary.Set(); Check(continueAcquire.Wait(TimeSpan.FromSeconds(10)), "controlled acquisition resumed"); }
        };
        Task call = Task.Run(() =>
        {
            bool lease = false;
            try
            {
                nint p = owner.Acquire(ref lease);
                admitted.Set();
                Check(finishCall.Wait(TimeSpan.FromSeconds(10)), "controlled admitted call resumed");
                Check(Fixture.Read(p, out ulong value, out _) == 0 && value == 123, "pre-close lease executes native after Closing");
            }
            finally { owner.Release(ref lease); owner.Release(ref lease); }
        });
        Check(atBoundary.Wait(TimeSpan.FromSeconds(10)), "hit named actual acquisition boundary");
        Task close = Task.Run(() => { closeStarted.Set(); owner.Dispose(); });
        Check(closeStarted.Wait(TimeSpan.FromSeconds(10)), "close attempt scheduled");
        Check(!close.IsCompleted, "close cannot split open-check/AddRef critical section");
        continueAcquire.Set();
        Check(admitted.Wait(TimeSpan.FromSeconds(10)), "lease admitted");
        await close.WaitAsync(TimeSpan.FromSeconds(10));
        Check(owner.IsClosing && !owner.Retirement.IsCompleted, "logical close precedes retirement");
        Check(Fixture.Counter(1) == releases && Fixture.Counter(0) == 1, "live actual native owner during Closing");
        owner.TestBoundary = null;
        Throws<ObjectDisposedException>(() => Fixture.Invoke(owner));
        finishCall.Set();
        await call.WaitAsync(TimeSpan.FromSeconds(10));
        await owner.Retirement.WaitAsync(TimeSpan.FromSeconds(10));
        Check(Fixture.Counter(1) == releases + 1, "one release after final admitted call");
        Clean();
    }

    private static void Rollback(LeaseBoundary boundary)
    {
        using var owner = Fixture.New();
        owner.TestBoundary = point => { if (point == boundary) throw new OutOfMemoryException("controlled allocation failure"); };
        Throws<OutOfMemoryException>(() => Fixture.Invoke(owner));
        Check(owner.ActiveLeases == 0 && Fixture.Counter(0) == 1, "failed acquire rolls logical/physical increments back");
        owner.TestBoundary = null;
        Check(Fixture.Invoke(owner) == 42, "owner remains usable after acquisition allocation failure");
        owner.Dispose(); Clean();
    }

    private static async Task CloseBeforeAcquire()
    {
        var owner = Fixture.New();
        await owner.DisposeAsync();
        Throws<ObjectDisposedException>(() => Fixture.Invoke(owner));
        Check(owner.ActiveLeases == 0, "post-close operation never increments admission");
        Clean();
    }

    private static async Task RetirementAndGc()
    {
        var owner = Fixture.New();
        bool lease = false;
        nint p = owner.Acquire(ref lease);
        ValueTask retired = owner.DisposeAsync();
        Check(!retired.IsCompleted, "DisposeAsync observes true lease retirement");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check(Fixture.Read(p, out ulong value, out _) == 0 && value == 42 && Fixture.Counter(0) == 1, "GC cannot release live admitted native owner");
        owner.Release(ref lease);
        await retired;
        Check(owner.Retirement.IsCompletedSuccessfully, "physical release observed after final lease");
        Clean();
    }

    private static void ParentChild()
    {
        var parent = Fixture.New(91);
        bool lease = false;
        nint child;
        try
        {
            nint p = parent.Acquire(ref lease);
            Check(Fixture.Child(p, 1, out nint failed) == 5 && failed == 0, "failure after native Arc retain publishes no child");
            Check(Fixture.Child(p, 0, out child) == 0, "native child creation");
        }
        finally { parent.Release(ref lease); }
        using var childOwner = new NativeResourceOwner(new FixtureHandle(child));
        parent.Dispose();
        Check(Fixture.Counter(0) == 1 && Fixture.Counter(2) == 1, "native child alone retains parent state");
        Check(Fixture.Invoke(childOwner) == 91, "child remains independently usable after parent closure");
        Throws<ObjectDisposedException>(() => Fixture.Invoke(parent));
        childOwner.Dispose(); Clean();
    }

    private static void PairAcquisitionFailure()
    {
        using var first = Fixture.New(); using var second = Fixture.New();
        second.TestBoundary = boundary => { if (boundary == LeaseBoundary.PhysicalAcquired) throw new OutOfMemoryException("second acquisition"); };
        Throws<OutOfMemoryException>(() =>
        {
            bool one = false, two = false;
            try { _ = first.Acquire(ref one); _ = second.Acquire(ref two); }
            finally { second.Release(ref two); first.Release(ref one); }
        });
        Check(first.ActiveLeases == 0 && second.ActiveLeases == 0, "all earlier acquisitions roll back");
        first.Dispose(); second.Dispose(); Clean();
    }

    private static void MapperFailure()
    {
        using var owner = Fixture.New();
        Throws<FormatException>(() =>
        {
            bool lease = false;
            try { _ = owner.Acquire(ref lease); throw new FormatException("managed mapper"); }
            finally { owner.Release(ref lease); }
        });
        Check(owner.ActiveLeases == 0, "mapping exception releases admitted lease");
        owner.Dispose(); Clean();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Abandon()
    {
        NativeResourceOwner owner = Fixture.New();
        Check(Fixture.Invoke(owner) == 42, "abandoned owner has actual native resource");
        return new WeakReference(owner);
    }
    private static void FinalizerFallback()
    {
        ulong before = Fixture.Counter(1);
        WeakReference abandoned = Abandon();
        for (int i = 0; i < 10 && Fixture.Counter(1) == before; i++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Check(!abandoned.IsAlive && Fixture.Counter(1) == before + 1, "SafeHandle-only finalizer performs one bounded release");
        Clean();
    }

    private static async Task ReentrantDispose()
    {
        var owner = Fixture.New(); bool lease = false;
        try
        {
            nint p = owner.Acquire(ref lease);
            owner.Dispose();
            Check(Fixture.Read(p, out _, out _) == 0, "reader may dispose its own owner and continue existing lease");
        }
        finally { owner.Release(ref lease); }
        await owner.Retirement; Clean();
    }
    private static void HotPathAllocations()
    {
        using var owner = Fixture.New();
        for (int i = 0; i < 64; i++) Fixture.Invoke(owner);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1024; i++) Fixture.Invoke(owner);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0, "steady-state synchronous admission allocates no managed lease objects");
        Console.WriteLine("Synchronous lease measured managed allocation bytes=" + allocated + " calls=1024");
    }
}
