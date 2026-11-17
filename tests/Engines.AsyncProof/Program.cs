using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using NativeWeave.Engines.AsyncProof;
using NativeWeave.Interop;

if (args.Contains("--require-aot") && RuntimeFeature.IsDynamicCodeSupported) throw new Exception("Actual Native AOT is required.");
int checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
async Task Wait(Func<bool> value, string label)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (!value()) await Task.Delay(2, deadline.Token).ConfigureAwait(false);
    Check(true, label);
}
async Task Throws<T>(Func<Task> body, string label) where T : Exception
{
    try { await body(); throw new Exception("Missing " + typeof(T).Name + ": " + label); }
    catch (T) { checks++; }
}
async Task CanceledOrTransport(Task task)
{
    try { await task; throw new Exception("Expected canceled/closed native operation."); }
    catch (OperationCanceledException) { checks++; }
    catch (NativeEngineException error) { Check(error.ErrorDomain == 0x48545450, "transport domain preserved"); }
}
async Task Quota(Func<Task> body, string label)
{
    try { await body(); throw new Exception("Missing bounded quota failure: " + label); }
    catch (OutOfMemoryException) { checks++; }
    catch (NativeEngineException error) { Check(error.StatusCode == 5 && error.ErrorDomain == 0x48545450, label); }
}
TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
Task<byte[]> Handler(uint route, CancellationToken _) => route == 99
    ? Task.FromException<byte[]>(new InvalidOperationException("business-handler-failure"))
    : Task.FromResult(Encoding.UTF8.GetBytes($"route:{route}:native-http-body"));
async Task Zero()
{
    await Wait(() => Enumerable.Range(0, 11).All(i => HttpProofHost.Counter((uint)i) == 0) && HttpProofHost.Counter(14) == 0, "all resources and callback/producer queues retired");
    Check(HttpProofHost.Counter(15) == 0, "no unsafe native release attempt");
}
async Task<List<NativeBuffer>> Drain(HttpProofResponse response, bool slow = false)
{
    var result = new List<NativeBuffer>();
    while (await response.ReadChunkAsync() is { } chunk) { result.Add(chunk); if (slow) await Task.Delay(15); }
    return result;
}
byte[] Join(IEnumerable<NativeBuffer> chunks) => chunks.SelectMany(chunk => chunk.ToArray()).ToArray();
async Task NotificationFailure()
{
    var host = new HttpProofHost(Handler);
    host.NotificationProbe = () => throw new InvalidOperationException("notification-fault");
    Task<HttpProofResponse> operation = host.GetAsync(7);
    await CanceledOrTransport(operation);
    await Throws<InvalidOperationException>(() => host.CloseAsync(), "bridge failure survives retirement");
    Check(host.GetSnapshot().ManagedHandlers == 0 && host.GetSnapshot().QueuedNotifications == 0, "failed notification drained");
    await Zero();
}
async Task ObserverBudget()
{
    TaskCompletionSource release = Gate();
    var host = new HttpProofHost(Handler, maxOperations: 1, frameDelayMilliseconds: 0);
    host.RetirementGate = release.Task;
    (await host.GetAsync(1)).Dispose();
    await Wait(() => host.GetSnapshot().Operations == 0 && host.OperationReservations == 1, "native operation retired while cleanup observer is paused");
    await Quota(() => host.GetAsync(2), "managed cleanup reservation remains bounded");
    Check(host.OperationReservations == 1, "at most one operation/root/observer reservation");
    Task close = host.CloseAsync();
    await Wait(() => host.GetSnapshot().RuntimeWorkers == 0 && host.GetSnapshot().RuntimeTasks == 0, "native runtime actually exited before managed bookkeeping release");
    Check(!close.IsCompleted, "host closure joins cleanup bookkeeping as well as native retirement");
    release.TrySetResult(); await close;
    Check(host.OperationReservations == 0, "cleanup metadata reservation released exactly once");
    await Zero();
}
async Task NonblockingCancellationCallbacks()
{
    for (int mode = 0; mode < 2; mode++)
    {
        TaskCompletionSource entered = Gate(), callbackEntered = Gate(), handlerRelease = Gate();
        using var callbackRelease = new ManualResetEventSlim();
        int profile = mode;
        var host = new HttpProofHost(async (_, token) =>
        {
            using var registration = token.Register(() =>
            {
                callbackEntered.TrySetResult();
                callbackRelease.Wait();
                if (profile == 1) throw new InvalidOperationException("cancellation-registration-failure");
            });
            entered.TrySetResult(); await handlerRelease.Task; return [1];
        });
        Task<HttpProofResponse> get = host.GetAsync(1);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var wait = new CancellationTokenSource(50);
        Task? closeWait = null;
        Task invocation = Task.Factory.StartNew(() =>
        {
            if (profile == 0) host.Dispose();
            else closeWait = host.CloseAsync(wait.Token);
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await invocation.WaitAsync(TimeSpan.FromSeconds(2));
            Check(true, "first Dispose/CloseAsync invocation returns while a foreign cancellation callback is blocked");
            if (profile == 1) await Throws<OperationCanceledException>(() => closeWait!, "first close caller can cancel only its wait");
            Check(!host.CloseAsync().IsCompleted, "actual close retains the blocked callback and claimed handler");
        }
        finally { callbackRelease.Set(); handlerRelease.TrySetResult(); }
        await CanceledOrTransport(get);
        if (profile == 1) await Throws<AggregateException>(() => host.CloseAsync(), "asynchronous cancellation callback failure survives cleanup");
        else await host.CloseAsync();
        await Zero();
    }
}
if (args.Contains("--nonblocking-cancel"))
{
    await NonblockingCancellationCallbacks();
    Console.WriteLine($"{{\"case\":\"nonblocking-cancel\",\"checks\":{checks},\"live_resources\":0}}");
    return;
}
if (args.Contains("--observer-budget"))
{
    await ObserverBudget();
    Console.WriteLine($"{{\"case\":\"observer-budget\",\"checks\":{checks},\"live_resources\":0}}");
    return;
}
if (args.Contains("--notification-fault"))
{
    await NotificationFailure();
    Console.WriteLine($"{{\"case\":\"notification-fault\",\"checks\":{checks},\"live_resources\":0}}");
    return;
}

EngineInfo info = HttpProofHost.Descriptor.Inspect();
Check(info.ThreadingModel == EngineThreadingModel.EngineLocalAsync, "runtime is engine local");
Check(info.Capabilities.Count == 1 && info.Supports(new("http.loopback.stream.v1")), "actual advertised HTTP profile");
Check(HttpProofHost.Counter(9) == 0, "descriptor import starts no worker");
foreach (uint route in new uint[] { 7, 99 })
{
    var host = new HttpProofHost(Handler, frameDelayMilliseconds: 15);
    Task<HttpProofResponse> task = host.GetAsync(route);
    HttpProofResponse response = await task;
    Check(ReferenceEquals(await task, response), "A07 Task supports repeated await");
    Check(response.StatusCode == (route == 99 ? 500 : 200), "real HTTP status");
    List<NativeBuffer> chunks = await Drain(response, slow: true);
    byte[] bytes = Join(chunks);
    byte[] expected = route == 99 ? [] : Encoding.UTF8.GetBytes($"route:{route}:native-http-body");
    Check(bytes.SequenceEqual(expected), "native service and compiled managed handler payload");
    string? oracle = args.FirstOrDefault(a => a.StartsWith("--direct-corpus=", StringComparison.Ordinal))?[16..];
    if (oracle is not null)
    {
        using JsonDocument direct = JsonDocument.Parse(File.ReadAllText(Path.Combine(oracle, route + ".json")));
        Check(direct.RootElement.GetProperty("status").GetInt32() == response.StatusCode, "direct Rust status oracle");
        Check(direct.RootElement.GetProperty("body_hex").GetString() == Convert.ToHexString(bytes).ToLowerInvariant(), "direct Rust body oracle");
    }
    response.Dispose();
    await host.CloseAsync();
    HttpProofSnapshot snapshot = host.GetSnapshot();
    Check(snapshot.LiveStreams == 0 && snapshot.Bodies == 0 && snapshot.RuntimeTasks == 0 && snapshot.RuntimeWorkers == 0, "EOF and host join retire live Bodies and producer permits");
    Check(snapshot.ManagedHandlers == 0 && snapshot.QueuedNotifications == 0, "managed delivery drained independently");
    if (chunks.Count != 0)
    {
        Check(snapshot.RetainedBytes > 0 && snapshot.Chunks == (ulong)chunks.Count, "retained Bytes remain charged after stream retirement");
        Check(Join(chunks).SequenceEqual(expected), "held chunk access survives actual host release");
    }
    foreach (NativeBuffer chunk in chunks) chunk.Dispose();
    await Zero();
}

// A managed payload above the exact profile limit becomes an empty HTTP error, never a partial owner.
{
    var host = new HttpProofHost(static (_, _) => Task.FromResult(new byte[1025]), chunkBytes: 1024);
    using HttpProofResponse response = await host.GetAsync(1);
    Check(response.StatusCode == 500, "oversized handler payload is rejected by the bounded service profile");
    Check(await response.ReadChunkAsync() is null, "oversized response exposes no partial chunk");
    response.Dispose(); await host.CloseAsync(); await Zero();
}

// Real early response disposal cancels the pending native Body read.
{
    var host = new HttpProofHost(Handler, frameDelayMilliseconds: 250);
    HttpProofResponse response = await host.GetAsync(8);
    Task<NativeBuffer?> read = response.ReadChunkAsync();
    await Throws<InvalidOperationException>(() => response.ReadChunkAsync(), "one active Body reader");
    response.Dispose();
    await CanceledOrTransport(read);
    await host.CloseAsync(); await Zero();
}
// Pre-admission cancellation creates no operation; committed cancellation retains a claimed handler.
{
    TaskCompletionSource entered = Gate(), release = Gate();
    var host = new HttpProofHost(async (_, _) => { entered.TrySetResult(); await release.Task; return [1, 2, 3]; });
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    await Throws<OperationCanceledException>(() => host.GetAsync(1, canceled.Token), "pre-cancel");
    Check(host.GetSnapshot().Operations == 0, "pre-cancel admits no native owner");
    using var cancellation = new CancellationTokenSource();
    Task<HttpProofResponse> get = host.GetAsync(1, cancellation.Token);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel();
    await CanceledOrTransport(get);
    Check(host.GetSnapshot().ManagedHandlers == 1 && host.GetSnapshot().LiveStreams == 1, "task cancellation is not handler/stream retirement");
    using var waiter = new CancellationTokenSource(30);
    await Throws<OperationCanceledException>(() => host.CloseAsync(waiter.Token), "close timeout only cancels this wait");
    Check(host.GetSnapshot().ManagedHandlers == 1 && host.GetSnapshot().PendingNativeHandlers == 1, "private cleanup root and claimed source remain live");
    GC.Collect(); GC.WaitForPendingFinalizers();
    release.TrySetResult(); await host.CloseAsync(); await Zero();
}
// A claimed foreign handler is still owned after an actual TCP reset.
{
    TaskCompletionSource entered = Gate(), release = Gate();
    var host = new HttpProofHost(async (_, _) => { entered.TrySetResult(); await release.Task; return [4, 5]; });
    using var socket = new TcpClient(); await socket.ConnectAsync("127.0.0.1", host.Port);
    await socket.GetStream().WriteAsync("GET /3 HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"u8.ToArray());
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    socket.Client.LingerState = new LingerOption(true, 0); socket.Dispose();
    Task close = host.CloseAsync();
    Check(!close.IsCompleted && host.GetSnapshot().LiveStreams == 1, "reset cannot release a claimed managed handler source");
    release.TrySetResult(); await close; await Zero();
}
// The operation admission budget is independent from live-stream and held-result budgets.
{
    TaskCompletionSource entered = Gate(), release = Gate();
    var host = new HttpProofHost(async (_, _) => { entered.TrySetResult(); await release.Task; return [1, 2]; }, maxOperations: 1);
    Task<HttpProofResponse> first = host.GetAsync(1); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await Quota(() => host.GetAsync(2), "operation quota");
    release.TrySetResult(); HttpProofResponse response = await first;
    await Wait(() => host.GetSnapshot().Operations == 0 && host.OperationReservations == 0, "completed operation retired separately from held response");
    await Quota(() => host.GetAsync(2), "held response item quota");
    response.Dispose(); await host.CloseAsync(); await Zero();
}
// Retained chunk item budget persists after its read Task and live stream finish.
{
    var host = new HttpProofHost(Handler, maxOperations: 1, frameDelayMilliseconds: 50);
    HttpProofResponse response = await host.GetAsync(3);
    await Wait(() => host.GetSnapshot().Operations == 0 && host.OperationReservations == 0, "get retired before capacity-one read");
    NativeBuffer chunk = (await response.ReadChunkAsync())!;
    await Wait(() => host.GetSnapshot().Operations == 0 && host.OperationReservations == 0, "first read retired while chunk retained");
    await Quota(() => response.ReadChunkAsync(), "held chunk item quota");
    response.Dispose(); await host.CloseAsync();
    Check(chunk.ToArray().Length > 0 && host.GetSnapshot().RetainedBytes > 0, "quota failure keeps earlier chunk independent");
    chunk.Dispose(); await Zero();
}
// Received bytes cannot exceed the finite backing budget while the producer still holds it.
{
    var host = new HttpProofHost(Handler, retainedBytes: 4096, frameDelayMilliseconds: 250);
    HttpProofResponse response = await host.GetAsync(4);
    await Quota(() => response.ReadChunkAsync(), "retained byte quota");
    response.Dispose(); await host.CloseAsync(); await Zero();
}
// A second real HTTP connection sees stream backpressure while the first handler owns its slot.
{
    TaskCompletionSource entered = Gate(), release = Gate();
    var host = new HttpProofHost(async (_, _) => { entered.TrySetResult(); await release.Task; return [7]; }, maxStreams: 1);
    Task<HttpProofResponse> first = host.GetAsync(1); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    using var socket = new TcpClient(); await socket.ConnectAsync("127.0.0.1", host.Port);
    await socket.GetStream().WriteAsync("GET /2 HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"u8.ToArray());
    byte[] data = new byte[512]; int count = await socket.GetStream().ReadAsync(data).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    Check(Encoding.ASCII.GetString(data, 0, count).StartsWith("HTTP/1.1 503", StringComparison.Ordinal), "live stream capacity rejects second native service request");
    release.TrySetResult(); (await first).Dispose(); await host.CloseAsync(); await Zero();
}
// Native parser sees malformed bytes; no managed HTTP implementation takes over.
{
    var host = new HttpProofHost(Handler); ulong errors = HttpProofHost.Counter(11);
    using var socket = new TcpClient(); await socket.ConnectAsync("127.0.0.1", host.Port);
    await socket.GetStream().WriteAsync("not-http\r\n\r\n"u8.ToArray()); socket.Client.Shutdown(SocketShutdown.Send);
    await Wait(() => HttpProofHost.Counter(11) > errors, "actual Hyper protocol rejection");
    await host.CloseAsync(); await Zero();
}
await NotificationFailure();
await ObserverBudget();
await NonblockingCancellationCallbacks();
// The exact A06 handler scope rejects waiting for its own retirement.
{
    HttpProofHost? host = null; bool rejected = false;
    host = new HttpProofHost(async (_, _) =>
    {
        try { await host!.CloseAsync(); }
        catch (InvalidOperationException) { rejected = true; }
        return [1];
    });
    await CanceledOrTransport(host.GetAsync(1)); await host.CloseAsync();
    Check(rejected, "self-wait rejected by actual A06 scope"); await Zero();
}
// Real network completion/cancellation/GC races, with either committed result accepted.
for (int i = 0; i < 12; i++)
{
    var host = new HttpProofHost(Handler, frameDelayMilliseconds: 0);
    using var cancel = new CancellationTokenSource(); Task<HttpProofResponse> task = host.GetAsync((uint)i, cancel.Token);
    if (i % 2 == 0) cancel.Cancel();
    GC.Collect();
    try { (await task).Dispose(); checks++; }
    catch (OperationCanceledException) { checks++; }
    await host.CloseAsync(); await Zero();
}
Console.WriteLine($"{{\"checks\":{checks},\"actual_native_http\":true,\"aot\":{(!RuntimeFeature.IsDynamicCodeSupported).ToString().ToLowerInvariant()},\"races\":12,\"live_resources\":0}}");
