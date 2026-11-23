using Korda.Interop;
using Korda.Interop.Abi;
using Korda.Interop.Async;
using Korda.Interop.Callbacks;

namespace Korda.Verification.Interop;

/// <summary>Deliberately invalid protocol controls run only in their own bounded child process.</summary>
internal static class ChildCases
{
    internal static async Task<int> Run(string mode)
    {
        if (mode == "--child-indeterminate") return await Indeterminate();
        if (mode != "--child-stale-cookie") throw new ArgumentException("Unknown isolated child mode.");
        using var coordinator = new CallbackCoordinator(1);
        var oldCalls = new OperationCalls();
        var oldScheduler = new ManualScheduler();
        var old = new AsyncOperation<NativeBuffer>(AsyncNative.New(2), oldCalls, coordinator, AsyncNative.TakeBuffer, scheduler: oldScheduler);
        Task<NativeBuffer> oldResult = old.Start();
        // Keep the actual GCHandle allocation/free/reallocation on this actor.
        // Cross-thread allocator caches otherwise make this counterexample depend
        // on unrelated ThreadPool reuse rather than the selected protocol rule.
        oldScheduler.Run();
        using (NativeBuffer result = await oldResult.WaitAsync(TimeSpan.FromSeconds(10))) Check.That(result.Length == 3, "old positive control");
        await old.Retirement.WaitAsync(TimeSpan.FromSeconds(10));
        Check.That(!old.Snapshot.RootLive, "old cookie retired before negative control");
        for (int i = 0; i < 1024; i++)
        {
            var calls = new OperationCalls(); var scheduler = new ManualScheduler();
            var current = new AsyncOperation<NativeBuffer>(AsyncNative.New(4), calls, coordinator, AsyncNative.TakeBuffer, scheduler: scheduler);
            _ = current.Start();
            if (calls.Captured.Cookie == oldCalls.Captured.Cookie)
            {
                // Deliberate protocol violation: the old producer should have
                // stopped before its cookie was freed. Never call this in-process.
                uint result = Invoke(oldCalls.Captured);
                Check.That(result == NativeStatus.Ok && current.Snapshot.NoticeReceived && !old.Snapshot.RootLive, "stale cookie negative demonstrates wrong-target delivery");
                Console.WriteLine("EXPECTED_PROTOCOL_VIOLATION stale-cookie-reuse reached a different live context; native source-closure is necessary; retained-native-owner=1; no safe-retirement claim");
                return 42;
            }
            current.Dispose(); scheduler.Run();
            try { using NativeBuffer unused = await current.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
            await current.Retirement.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Console.Error.WriteLine("INCONCLUSIVE no cookie reuse within 1024 attempts"); return 78;
    }
    private static unsafe uint Invoke(NwOperationCallbacksV1 table) => Native.Notify(&table);

    private static async Task<int> Indeterminate()
    {
        using var coordinator = new CallbackCoordinator(1);
        var calls = new OperationCalls();
        var operation = new AsyncOperation<NativeBuffer>(AsyncNative.New(10), calls, coordinator, AsyncNative.TakeBuffer);
        try { using NativeBuffer unexpected = await operation.Start().WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("Expected indeterminate start failure."); }
        catch (NativeEngineException) { }
        Check.That(operation.Snapshot.Quarantined && operation.Snapshot.RootLive && !operation.Retirement.IsCompleted && AsyncNative.Count(0) == 1, "indeterminate startup must retain uncertain obligations");
        Console.WriteLine("EXPECTED_QUARANTINE rejected-after-callback; native-owner=1 managed-root=1 successful-retirement=0; isolated process ends retention");
        return 43;
    }
}
