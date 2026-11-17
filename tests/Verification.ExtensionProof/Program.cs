using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeWeave.Interop;
using NativeWeave.Verification.ExtensionProof;

if (args.Contains("--require-aot") && RuntimeFeature.IsDynamicCodeSupported) throw new Exception("Actual Native AOT required.");
int assertions = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); assertions++; }
void Until(Func<bool> predicate, string name)
{
    long end = Environment.TickCount64 + 10000;
    while (!predicate()) { if (Environment.TickCount64 > end) throw new Exception("Timed out: " + name); Thread.Sleep(1); }
}
void Finish(AffinityProbe probe)
{
    probe.Dispose();
    Until(() => probe.CallbacksRetired, "actual A06 managed/native retirement");
    Check(!probe.CallbackRootLive, "managed callback root released");
    Check(probe.ClosePending, "native affinity remains pending without owner progress");
    Check(probe.Pump(1) == 0, "owner drives deferred destructor");
    Check(probe.Snapshot.Retired == 1, "actual Rc retired on owner thread");
    Check(probe.TryFinishClose(), "release thread-safe observer after affine retirement");
}

EngineInfo info = AffinityProbe.Descriptor.Inspect();
Check(info.ThreadingModel == EngineThreadingModel.ExternalLoop && info.Capabilities.Count == 1 && info.Supports(new("affinity.owner.v1")), "actual optional affinity capability");
foreach (string missing in new[] { "gpu.device.v1", "window.surface.v1", "mobile.ios.v1", "mobile.android.v1", "platform.wasm.v1", "platform.static.v1" })
{
    try { info.Require(new(missing)); throw new Exception("Unsupported device/platform capability accepted."); }
    catch (NotSupportedException) { assertions++; }
}
Check(Marshal.SizeOf<AffinitySnapshot>() == 80, "fixed cross-language snapshot layout");

// Owner-only access, bounded successful transfer, queue-full preservation and retry.
using (var probe = new AffinityProbe(capacity: 2))
{
    probe.Start();
    Check(probe.Read(out ulong initial) == 0 && initial == 0, "owner accesses actual Rc/Cell");
    ulong touches = probe.Snapshot.Touches;
    var wrong = Task.Run(() => (probe.Read(out _), probe.Pump())).GetAwaiter().GetResult();
    Check(wrong == (6u, 6u) && probe.Snapshot.Touches == touches, "wrong thread rejected before affine access");
    using var first = new OwnedWork(7); using var second = new OwnedWork(11); using var retained = new OwnedWork(13);
    Check(probe.TryPost(first) == 0 && first.Transferred, "first native ownership transfer");
    Check(probe.TryPost(second) == 0 && second.Transferred, "second native ownership transfer");
    Check(probe.TryPost(retained) == 1 && !retained.Transferred, "full post preserves original owner");
    Check(probe.Snapshot.Queued == 2 && probe.Snapshot.NativeSources == 2, "accepted queue owns A06 source leases");
    Check(probe.Pump(1) == 0 && probe.Snapshot.Completed == 1, "bounded one-item pump");
    Check(probe.TryPost(retained) == 0 && retained.Transferred, "same unconsumed owner can be retried");
    Check(probe.Pump(2) == 0 && probe.Read(out ulong total) == 0 && total == 31, "typed work executes on owner");
    Check(probe.Snapshot.Completed == 3 && probe.Snapshot.Queued == 0, "all accepted work has terminal outcome");
    Finish(probe);
}

// Closing cancels queued commands, releases their source/creator owners, and preserves rejected work.
using (var probe = new AffinityProbe())
{
    probe.Start();
    using var first = new OwnedWork(1); using var second = new OwnedWork(2); using var rejected = new OwnedWork(3);
    Check(probe.TryPost(first) == 0 && probe.TryPost(second) == 0, "queue work before close");
    probe.Dispose();
    Check(probe.TryPost(rejected) == 4 && !rejected.Transferred, "closed managed post retains caller ownership");
    Until(() => probe.CallbacksRetired, "queued work close drain");
    AffinitySnapshot state = probe.Snapshot;
    Check(state.ClosedItems == 2 && state.Completed == 0 && state.NativeSources == 0 && state.Queued == 0, "close terminalizes every accepted queued command");
    Finish(probe);
}

// A real asynchronous managed notification keeps the affine object alive after native quiet.
var entered = new ManualResetEventSlim();
var finishHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using (var probe = new AffinityProbe(notification: value => { entered.Set(); return new ValueTask(finishHandler.Task); }))
{
    probe.Start();
    using var first = new OwnedWork(1); using var second = new OwnedWork(2);
    Check(probe.TryPost(first) == 0 && probe.TryPost(second) == 0 && probe.Pump(2) == 0, "real native callbacks queue managed notifications");
    Until(() => entered.IsSet && probe.RetainedCallbacks == 2, "one active handler plus one retained notification");
    probe.Dispose();
    Until(() => probe.RetainedCallbacks == 1, "queued notification discarded on managed close");
    Check(!probe.CallbacksRetired && probe.CallbackRootLive, "active asynchronous handler retains callback root");
    Check(probe.Pump() == 0 && probe.Snapshot.Retired == 0 && Native.Count(0) == 1, "owner pump cannot free a live managed handler lease");
    finishHandler.SetResult();
    Finish(probe);
}
entered.Dispose();

// Handler failure is observable separately from successful resource retirement.
using (var probe = new AffinityProbe(notification: _ => throw new InvalidOperationException("expected handler failure")))
{
    probe.Start();
    using var work = new OwnedWork(1);
    Check(probe.TryPost(work) == 0 && probe.Pump() == 0, "admit notification before handler fault");
    Until(() => probe.Completion.IsCompleted, "observable failure after callback cleanup");
    Check(probe.Completion.IsFaulted && probe.CallbacksRetired && !probe.CallbackRootLive, "failure channel differs from safe retirement");
    try { probe.Completion.GetAwaiter().GetResult(); throw new Exception("Handler failure was hidden."); }
    catch (InvalidOperationException error) { Check(error.Message == "expected handler failure", "original handler failure retained"); }
    Finish(probe);
}

// The exact A03/A06 control lease delays managed owner release, independently of native quiet.
using (var probe = new AffinityProbe())
using (var controlEntered = new ManualResetEventSlim())
using (var releaseControl = new ManualResetEventSlim())
{
    probe.Start();
    Task control = Task.Run(() => probe.HoldControl(() => { controlEntered.Set(); releaseControl.Wait(); }));
    Until(() => controlEntered.IsSet, "A03 control admitted");
    probe.Dispose();
    Check(probe.Pump() == 0 && probe.Snapshot.ReleaseRequested == 0 && !probe.CallbacksRetired, "control lease prevents owner release");
    releaseControl.Set(); control.GetAwaiter().GetResult();
    Finish(probe);
}

// Concurrent actual posts/pump/close: every accepted command is completed or closed exactly once.
for (int round = 0; round < 8; round++)
{
    using var probe = new AffinityProbe(); probe.Start();
    using var start = new ManualResetEventSlim();
    int accepted = 0;
    Task[] workers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
    {
        start.Wait();
        for (int i = 0; i < 32; i++)
        {
            using var work = new OwnedWork(1);
            uint status = probe.TryPost(work);
            if (status == 0) Interlocked.Increment(ref accepted);
            else if (status is not (1 or 4)) throw new Exception("Unexpected post result.");
        }
    })).ToArray();
    start.Set();
    for (int i = 0; i < 16; i++) { Check(probe.Pump(1) == 0, "bounded concurrent progress"); Thread.Yield(); }
    probe.Dispose(); Task.WaitAll(workers);
    Until(() => probe.CallbacksRetired, "post-close retirement race");
    AffinitySnapshot state = probe.Snapshot;
    Check(state.Completed + state.ClosedItems == (ulong)accepted && state.NativeSources == 0, "no accepted command lost across close");
    Finish(probe);
}

// Direct closed native admission leaves the exact raw owner to its creating allocator.
Check(Native.Create(1, out nint raw, out nint observer) == 0, "unstarted native owner create");
try
{
    Check(Native.NewWork(99, out nint work) == 0, "raw work owner create");
    ulong releases = Native.Count(5);
    try { Check(Native.Stop(raw) == 0 && Native.Post(raw, work) == 4 && Native.Count(5) == releases, "closed native post does not consume work"); }
    finally { Native.ReleaseWork(work); }
    Check(Native.Count(5) == releases + 1, "rejected work creator releases once");
    Native.Release(raw); raw = 0;
    Check(Native.Pump(observer, 1) == 0, "unstarted owner's cleanup still requires owner progress");
}
finally { if (raw != 0) Native.Release(raw); Native.ReleaseObserver(observer); }

// A genuine SafeHandle finalizer runs off-owner and only requests bounded cleanup.
ulong droppedBeforeFinalizer = Native.Count(1);
WeakReference abandoned = FinalizerCase.Create(out observer);
try
{
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    Until(() => !abandoned.IsAlive && Native.Count(2) == 0, "actual SafeHandle finalizer release");
    Check(Native.Snapshot(observer, out AffinitySnapshot pending) == 0 && pending.ReleaseRequested == 1 && pending.Retired == 0, "finalizer leaves ClosePending");
    Check(Native.Count(1) == droppedBeforeFinalizer && Native.Count(0) == 1, "finalizer never destroys Rc on its own thread");
    Check(Native.Pump(observer, 1) == 0 && Native.Count(1) == droppedBeforeFinalizer + 1, "owner pump performs finalizer-requested destruction");
}
finally { Native.ReleaseObserver(observer); }

Check(Native.Count(0) == 0 && Native.Count(2) == 0 && Native.Count(3) == 0 && Native.Count(4) == 0 && Native.Count(6) == 0, "all affine/control/command owners and active native calls retired");
Console.WriteLine($"PASS A23 Linux {(RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "NativeAOT")} {assertions} assertions; affine={Native.Count(0)}; dropped={Native.Count(1)}; handles={Native.Count(2)}; observers={Native.Count(3)}; work={Native.Count(4)}; callbacks/root drain verified");

internal static class FinalizerCase
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static WeakReference Create(out nint observer)
    {
        AffinityProbe.Require(Native.Create(1, out nint main, out observer), "finalizer_create");
        var handle = new ProbeHandle(main, observer: false);
        return new WeakReference(handle);
    }
}
