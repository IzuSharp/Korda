using System.Diagnostics;
using System.Net;
using NativeWeave.Server;

internal static class RetirementFixtures
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Empty = new Dictionary<string, IReadOnlyList<string>>();

    internal static async Task RunAsync(Action<bool, string> check,
        Func<ServerProtocols, ServerOptions, (NativeHost Host, RouteDefinition[] Routes)> create,
        Func<NativeHost, Version, HttpClient> client)
    {
        foreach (Version version in new[] { HttpVersion.Version11, HttpVersion.Version20 })
        {
            ServerProtocols protocol = version == HttpVersion.Version11 ? ServerProtocols.Http1 : ServerProtocols.Http2;
            await DispatcherTailAsync(check, create, client, protocol, version);
            await FinalLeaseAsync(check, create, client, protocol, version);
        }
        Console.WriteLine("PASS request admission and final-lease retirement HTTP/1.1 + HTTP/2 TLS");
    }

    private static ServerOptions Options()
    {
        var options = new ServerOptions { RequestTimeout = TimeSpan.FromSeconds(10), ShutdownTimeout = TimeSpan.FromMilliseconds(100) };
        options.Budget.MaxActiveRequests = 1;
        options.Budget.MaxQueuedRequests = 1;
        return options;
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        long started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(3)) throw new TimeoutException("Request retirement boundary");
            await Task.Delay(5);
        }
    }

    private static async Task EmptyResponseAsync(HostRequest request)
    {
        await request.CommitHeadersAsync(204, Empty, request.RequestAborted);
        await request.Body.FinishAsync(request.RequestAborted);
    }

    private static async Task DispatcherTailAsync(Action<bool, string> check,
        Func<ServerProtocols, ServerOptions, (NativeHost Host, RouteDefinition[] Routes)> create,
        Func<NativeHost, Version, HttpClient> client, ServerProtocols protocol, Version version)
    {
        var firstReady = new TaskCompletionSource<NativeRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new TaskCompletionSource<NativeRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        var (host, routes) = create(protocol, Options());
        await using (host)
        {
            await host.StartAsync(routes, async request =>
            {
                int ordinal = Interlocked.Increment(ref entered);
                await EmptyResponseAsync(request);
                await request.CompleteDispatchAsync();
                if (ordinal == 1)
                {
                    firstReady.TrySetResult((NativeRequest)request);
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(3));
                }
                else secondReady.TrySetResult((NativeRequest)request);
            }, default);
            using var firstClient = client(host, version);
            using var secondClient = client(host, version);
            try
            {
                using var firstResponse = await firstClient.GetAsync("hello/first").WaitAsync(TimeSpan.FromSeconds(3));
                NativeRequest first = await firstReady.Task.WaitAsync(TimeSpan.FromSeconds(3));
                check(firstResponse.StatusCode == HttpStatusCode.NoContent && firstResponse.Version == version && first.ReadyToRelease,
                    "Request dispatch quiesces after a real bodyless TLS response");
                check(!first.Owner.IsClosing && !first.Owner.Retirement.IsCompleted,
                    "The native admission owner remains live until the complete managed dispatcher returns");
                Task<HttpResponseMessage> secondResponseTask = secondClient.GetAsync("hello/second");
                await UntilAsync(() => host.Snapshot().queued_requests == 1);
                check(Volatile.Read(ref entered) == 1 && !secondResponseTask.IsCompleted,
                    "A new independent client queues behind the managed tail without a false dispatch rejection");
                release.TrySetResult();
                await first.Owner.Retirement.WaitAsync(TimeSpan.FromSeconds(3));
                using var secondResponse = await secondResponseTask.WaitAsync(TimeSpan.FromSeconds(3));
                NativeRequest second = await secondReady.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await second.Owner.Retirement.WaitAsync(TimeSpan.FromSeconds(3));
                check(secondResponse.StatusCode == HttpStatusCode.NoContent && secondResponse.Version == version && Volatile.Read(ref entered) == 2,
                    "Final request-owner retirement makes its native and managed admission slots reusable together");
                var close = await host.CloseAsync(TimeSpan.FromSeconds(3), default);
                check(close.Status == ServerCloseStatus.Completed && close.PendingRequests == 0 && close.RetainedManagedBytes == 0 && close.RetainedNativeBytes == 0,
                    "Dispatcher-tail requests and their live TLS clients fully drain");
            }
            finally { release.TrySetResult(); }
        }
    }

    private static async Task FinalLeaseAsync(Action<bool, string> check,
        Func<ServerProtocols, ServerOptions, (NativeHost Host, RouteDefinition[] Routes)> create,
        Func<NativeHost, Version, HttpClient> client, ServerProtocols protocol, Version version)
    {
        NativeRequest? held = null;
        bool lease = false;
        CancellationTokenRegistration registration = default;
        var taken = new TaskCompletionSource<NativeRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, routes) = create(protocol, Options());
        await using (host)
        {
            await host.StartAsync(routes, async request =>
            {
                held = (NativeRequest)request;
                held.Owner.Acquire(ref lease);
                registration = request.RequestAborted.Register(static () => throw new InvalidOperationException("retirement cancellation fixture"));
                await EmptyResponseAsync(request);
                taken.TrySetResult(held);
                held.NotifyCancellation(2);
                await held.CompleteDispatchAsync();
            }, default);
            using var connection = client(host, version);
            try
            {
                using var response = await connection.GetAsync("hello/lease").WaitAsync(TimeSpan.FromSeconds(3));
                NativeRequest request = await taken.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await UntilAsync(() => request.Owner.IsClosing);
                check(response.StatusCode == HttpStatusCode.NoContent && request.ReadyToRelease && request.Owner.ActiveLeases == 1 && !request.Owner.Retirement.IsCompleted,
                    "Joined faulted cancellation still arms final release while an admitted physical lease retains ownership");
                check(host.Lifetime.IsFaulted && host.Lifetime.Exception!.Flatten().InnerExceptions.Any(error =>
                    error is InvalidOperationException && error.Message == "retirement cancellation fixture"),
                    "A throwing cancellation registration remains an observable host fault after its join");
                long nativeStarted = Stopwatch.GetTimestamp();
                while (true)
                {
                    await host.CloseAsync(TimeSpan.Zero, default);
                    var completed = host.Snapshot();
                    if (completed.state == 4 && completed.active_requests == 0 && completed.retained_managed_bytes == 0 && completed.retained_native_bytes == 0) break;
                    if (Stopwatch.GetElapsedTime(nativeStarted) >= TimeSpan.FromSeconds(3)) throw new TimeoutException("Native completion before final-lease close wait");
                    await Task.Delay(2);
                }
                long closeStarted = Stopwatch.GetTimestamp();
                var pending = await host.CloseAsync(TimeSpan.FromMilliseconds(100), default);
                TimeSpan elapsed = Stopwatch.GetElapsedTime(closeStarted);
                var native = host.Snapshot();
                check(native.state == 4 && native.active_requests == 0 && native.retained_managed_bytes == 0 && native.retained_native_bytes == 0 &&
                    pending.Status == ServerCloseStatus.Pending && pending.PendingRequests == 1 && elapsed >= TimeSpan.FromMilliseconds(80),
                    "Native completion and zero payload do not remove the managed close obligation before the last physical lease");
                Task<ServerCloseResult> closing = host.CloseAsync(TimeSpan.FromSeconds(3), default);
                await Task.WhenAny(closing, Task.Delay(25));
                check(!closing.IsCompleted, "Close continues waiting for the actual delayed SafeHandle release");
                request.Owner.Release(ref lease);
                await request.Owner.Retirement.WaitAsync(TimeSpan.FromSeconds(3));
                var closed = await closing.WaitAsync(TimeSpan.FromSeconds(3));
                check(closed.Status == ServerCloseStatus.Completed && closed.PendingRequests == 0 && closed.RetainedManagedBytes == 0 && closed.RetainedNativeBytes == 0,
                    "Final lease release removes its exact ticket and completes the original finite close wait");
            }
            finally { held?.Owner.Release(ref lease); registration.Dispose(); }
        }
    }
}
