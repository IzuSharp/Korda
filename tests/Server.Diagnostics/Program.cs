using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NativeWeave.Server;

internal static class Program
{
    internal const string Parent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
    internal const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private static int _assertions;
    internal static void Check(bool success, string name)
    { if (!success) throw new InvalidOperationException(name); Interlocked.Increment(ref _assertions); Console.WriteLine("PASS " + name); }
    internal static async Task Eventually(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(5, timeout.Token); }
    private static async Task Main(string[] args)
    {
        if (args.Contains("--cost", StringComparer.Ordinal)) { await Costs.RunAsync(args.Last()); return; }
        await DefaultsAsync();
        if (!args.Contains("--defaults", StringComparer.Ordinal))
        {
            Layout(); await QueueAsync();
            await ActualContextAsync(ServerProtocols.Http1, HttpVersion.Version11);
            await ActualContextAsync(ServerProtocols.Http2, HttpVersion.Version20);
            await LifetimeAsync(); await CancellationRacesAsync(); await ListenerFaultAsync(); await ExporterStallAsync();
        }
        Console.WriteLine($"B20 actual diagnostics: {_assertions} assertions, dynamic={RuntimeFeature.IsDynamicCodeSupported}, configuration={Configuration}.");
    }
    internal static string Configuration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif
    internal static HttpClient Client(NativeServerApplication app, Version? version = null) => new(new SocketsHttpHandler { ActivityHeadersPropagator = null })
    { BaseAddress = app.BoundAddresses.Single(), DefaultRequestVersion = version ?? HttpVersion.Version11, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact, Timeout = TimeSpan.FromSeconds(10) };
    private static async Task DefaultsAsync()
    {
        var builder = NativeServer.CreateBuilder().Listen(IPAddress.Loopback, 0);
        await using var app = builder.Build();
        app.MapGet("/default", context => { Check(Activity.Current is null, "Default callback has no managed Activity."); return Results.Text(context.TraceIdentifier); });
        Check(!ServerDiagnostics.Activities.HasListeners() && !ServerDiagnostics.RequestDuration.Enabled, "No default Activity/Meter listener in " + Configuration + ".");
        Check(Interlocked.Read(ref DiagnosticCounters.TotalRetainedEntries) == 0, "No default exporter queue.");
        await app.StartAsync(); using var client = Client(app);
        using var request = new HttpRequestMessage(HttpMethod.Get, "default"); request.Headers.Add("traceparent", Parent);
        using var response = await client.SendAsync(request);
        Check(await response.Content.ReadAsStringAsync() == TraceId, "No-listener fixed parent propagation through actual native metadata.");
        var close = await app.CloseAsync(TimeSpan.FromSeconds(2));
        Check(close.Status == ServerCloseStatus.Completed && close.PendingRequests == 0 && close.RetainedNativeBytes == 0 && close.RetainedManagedBytes == 0, "Default diagnostics preserve complete ownership join.");
    }
    private static void Layout()
    {
        Check(Marshal.SizeOf<TraceContextV1>() == 32 && Marshal.OffsetOf<TraceContextV1>(nameof(TraceContextV1.TraceId)) == 8 && Marshal.OffsetOf<TraceContextV1>(nameof(TraceContextV1.ParentSpanId)) == 24, "Managed D19 size and fixed offsets.");
        byte[] golden = Convert.FromHexString(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "trace-golden.txt")).Trim());
        var context = DiagnosticContext.Decode(golden, "vendor=value");
        Check(context.TraceId.ToHexString() == TraceId && context.SpanId.ToHexString() == "00f067aa0ba902b7" && context.IsRemote && context.TraceFlags == ActivityTraceFlags.Recorded, "Shared Rust/C# golden byte order and flags.");
        foreach (int offset in new[] { 0, 4, 6, 7 }) { byte[] bad = golden.ToArray(); bad[offset] = 255; Reject(() => DiagnosticContext.Decode(bad, "")); }
        foreach (var range in new[] { (8, 16), (24, 8) }) { byte[] bad = golden.ToArray(); Array.Clear(bad, range.Item1, range.Item2); Reject(() => DiagnosticContext.Decode(bad, "")); }
        Reject(() => DiagnosticContext.Decode(golden.AsSpan(0, 31), ""));
        foreach (string state in new[] { "UPPER=value", "vendor=a=b", "vendor=", "vendor=x,vendor=y", "vendor=é", string.Join(',', Enumerable.Repeat(" ", 33)), "vendor=" + new string('x', 257) }) Reject(() => DiagnosticContext.Decode(golden, state));
        Check(DiagnosticContext.ValidState(",vendor=value,, ") && DiagnosticContext.ValidState("tenant@system=value, vendor=other"), "Complete bounded tracestate grammar.");
        Check(ServerDiagnostics.Bounded(new string('é', 256)).Length == 128 && Encoding.UTF8.GetByteCount(ServerDiagnostics.Bounded(new string('x', 257))) == 256, "Attribute UTF-8 byte cap.");
        Check(ServerDiagnostics.Category(new NativeWeave.Interop.NativeEngineException("server", "operation", 9, 0, 0, 0, "SECRET")) == "native_panic" && ServerDiagnostics.Category(new ServerException("transport_failed", "SECRET")) == "transport", "Native error metadata maps to finite categories without message fields.");
        static void Reject(Action action) { try { action(); } catch (InvalidOperationException) { Check(true, "Malformed internal fixed context rejected."); return; } throw new Exception("Malformed fixed context accepted."); }
    }
    private static async Task QueueAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var counters = new DiagnosticCounters();
        var value = new DiagnosticEvent("application_failure", "/fixed/{id}", "HTTP/1.1", "5xx");
        var queue = new BoundedDiagnostics(async (_, _) => { entered.TrySetResult(); await release.Task; }, counters, entries: 2, bytes: 1024, itemBytes: 512);
        Check(queue.TryWrite(value), "First bounded diagnostic item admitted."); await entered.Task;
        Check(queue.TryWrite(value) && !queue.TryWrite(value), "In-flight plus queued items share the entry bound.");
        Check(counters.RetainedEntries == 2 && counters.RetainedBytes == 2 * value.Bytes && counters.Dropped == 1, "Exact conservative string/backing credit and full-queue drop count.");
        var watch = Stopwatch.StartNew(); await queue.StopAsync(TimeSpan.FromMilliseconds(25));
        Check(watch.Elapsed < TimeSpan.FromSeconds(1) && counters.RetainedEntries == 1 && counters.RetainedBytes == value.Bytes, "Finite flush preserves stalled in-flight credit and drops pending metadata.");
        Check(!queue.TryWrite(value), "Closed exporter admission stays closed."); release.TrySetResult(); await queue.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Check(counters.RetainedEntries == 0 && counters.RetainedBytes == 0, "Credit releases only after exporter returns.");
        var bytesCounters = new DiagnosticCounters();
        var byteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var byteQueue = new BoundedDiagnostics(async (_, _) => await byteGate.Task, bytesCounters, entries: 20, bytes: value.Bytes, itemBytes: value.Bytes);
        Check(byteQueue.TryWrite(value) && !byteQueue.TryWrite(value), "Byte budget denies before entry budget."); byteGate.TrySetResult(); await byteQueue.StopAsync(TimeSpan.FromSeconds(1));
        var failCounters = new DiagnosticCounters();
        var failed = new BoundedDiagnostics((_, _) => throw new IOException("secret exporter disconnect"), failCounters);
        Check(failed.TryWrite(value), "Disconnect fault row admitted."); await failed.StopAsync(TimeSpan.FromSeconds(1));
        Check(failCounters.ExportFailures == 1 && failCounters.Dropped == 1 && failCounters.RetainedBytes == 0, "Exporter disconnect counts and releases.");
        var cancellationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stuckRegistration = new BoundedDiagnostics(async (_, token) =>
        {
            using var registration = token.Register(() => registrationGate.Task.GetAwaiter().GetResult());
            cancellationEntered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, new());
        stuckRegistration.TryWrite(value); await cancellationEntered.Task;
        watch.Restart(); await stuckRegistration.StopAsync(TimeSpan.FromMilliseconds(25));
        Check(watch.Elapsed < TimeSpan.FromSeconds(1), "A stalled exporter cancellation registration cannot extend flush timeout.");
        registrationGate.TrySetResult(); await stuckRegistration.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Check(Interlocked.Read(ref DiagnosticCounters.TotalRetainedBytes) == 0, "All diagnostic test queues drain independently of business resources.");
    }
    private static async Task ActualContextAsync(ServerProtocols protocols, Version version)
    {
        using var traces = new TraceCollector(ActivitySamplingResult.AllDataAndRecorded);
        using var metrics = new MetricCollector();
        var seen = new ConcurrentQueue<string>(); var cleanup = new ConcurrentQueue<string>();
        var framed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new CaptureLogger();
        var builder = NativeServer.CreateBuilder().Listen(IPAddress.Loopback, 0, listener => listener.Protocols = protocols);
        builder.Services.AddLogging(logging => { logging.SetMinimumLevel(LogLevel.Warning); logging.AddProvider(logger); });
        await using var app = builder.Build();
        app.MapRaw("/context/{id}", ["GET"], async (context, token) =>
        {
            var native = (NativeRequest)context.Native;
            string initial = Activity.Current?.TraceId.ToHexString() ?? "none";
            context.Response.OnCompleted(() => { cleanup.Enqueue(Activity.Current?.TraceId.ToHexString() ?? "none"); return Task.CompletedTask; });
            await Task.Yield();
            await Task.Run(async () => { await Task.Delay(1, token); if (Activity.Current?.TraceId.ToHexString() != initial) throw new InvalidOperationException("Managed worker migration lost the request scope."); }, token);
            string state = native.DiagnosticParent.TraceState ?? "";
            seen.Enqueue(initial + "|" + native.DiagnosticParent.TraceId.ToHexString() + "|" + native.DiagnosticParent.SpanId.ToHexString() + "|" + state + "|" + context.TraceIdentifier);
            Check(Activity.Current?.Baggage.Any() != true, "Baggage is not propagated through native metadata.");
            await context.Response.WriteAsync("ok"u8.ToArray(), token);
        });
        app.MapRaw("/fixed-length", ["GET"], async (context, token) =>
        {
            context.Response.ContentLength = 4;
            await context.Response.WriteAsync("body"u8.ToArray(), token);
            // Force Hyper to consume/drop the exact declared body before the
            // producer asks to enqueue its otherwise redundant End control.
            await Task.Delay(25, token);
            try { await context.Response.CompleteAsync(token); framed.TrySetResult(null); }
            catch (Exception failure) { framed.TrySetResult(failure); throw; }
        });
        app.MapGet("/fail/{id}", static () => throw new InvalidOperationException("SECRET SQL SELECT password; Authorization bearer; -----BEGIN PRIVATE KEY-----"));
        await app.StartAsync(); metrics.Observe();
        using var client = Client(app, version);
        using var unrelated = new Activity("unrelated-caller").Start();
        var prior = Activity.Current;
        async Task Send(string? parent, params string[] states)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "context/private-value?token=SECRET") { Version = version, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            if (parent is not null) request.Headers.TryAddWithoutValidation("traceparent", parent);
            foreach (string state in states) request.Headers.TryAddWithoutValidation("tracestate", state);
            request.Headers.Add("baggage", "credential=SECRET"); request.Headers.Add("Authorization", "Bearer SECRET"); request.Headers.Add("Cookie", "session=SECRET");
            using var response = await client.SendAsync(request); Check(response.Version == version && await response.Content.ReadAsStringAsync() == "ok", "Actual " + version + " context callback preserves business output.");
        }
        await Send(Parent, "vendor=value", "other=opaque");
        await Eventually(() => seen.Count == 1 && traces.Completed.Count == 1);
        string valid = seen.ToArray()[0];
        Check(valid == TraceId + "|" + TraceId + "|00f067aa0ba902b7|vendor=value, other=opaque|" + TraceId || valid == TraceId + "|" + TraceId + "|00f067aa0ba902b7|vendor=value,other=opaque|" + TraceId, "Native callback transfers exact parent ID, span ID and combined state.");
        Check(traces.Completed.Single().ParentSpan == "00f067aa0ba902b7" && traces.Completed.Single().Trace == TraceId, "Managed Activity shares native parent correlation.");
        await Send(Parent, "vendor=one,vendor=two");
        await Send("00-00000000000000000000000000000000-00f067aa0ba902b7-01");
        await Send("00-4bf92f3577b34da6a3ce929d0e0e4736-0000000000000000-01");
        await Send(Parent + "," + Parent);
        await Send(null);
        await Eventually(() => seen.Count == 6 && traces.Completed.Count == 6 && cleanup.Count == 6);
        var captures = seen.ToArray();
        Check(captures[1].StartsWith(TraceId + "|" + TraceId, StringComparison.Ordinal) && captures[1].Contains("||", StringComparison.Ordinal), "Malformed tracestate is dropped without breaking a valid parent.");
        Check(captures.Skip(2).All(value => !value.StartsWith(TraceId, StringComparison.Ordinal) && !value.StartsWith(prior!.TraceId.ToHexString(), StringComparison.Ordinal)), "Malformed/absent external parents create fresh roots, never an unrelated managed parent.");
        Check(cleanup.SequenceEqual(captures.Select(value => value.Split('|')[0])), "Request Activity survives awaited cleanup and is restored per callback.");
        Check(ReferenceEquals(Activity.Current, prior), "Caller Activity restored after asynchronous actual Host calls.");
        using (var failed = await client.GetAsync("fail/private-value?password=SECRET")) Check((int)failed.StatusCode == 500, "Failure diagnostics preserve actual HTTP result.");
        await Eventually(() => logger.Messages.Count > 0 && traces.Completed.Count == 7);
        Check(logger.Messages.All(message => !message.Contains("SECRET", StringComparison.Ordinal) && !message.Contains("private-value", StringComparison.Ordinal) && !message.Contains("SELECT", StringComparison.Ordinal) && !message.Contains("PRIVATE KEY", StringComparison.Ordinal)), "Structured logs exclude URL/query/body/credentials/SQL/key/exception content.");
        Check(traces.Completed.SelectMany(trace => trace.Tags).All(pair => !pair.Value.Contains("SECRET", StringComparison.Ordinal) && !pair.Value.Contains("private-value", StringComparison.Ordinal)), "Trace attributes contain bounded route templates and normalized categories.");
        Check(metrics.Measurements.Any(value => value.Name == "nativeweave.server.request.duration") && metrics.Measurements.All(value => value.Tags.All(pair => pair.Key is "http.route" or "network.protocol.version" or "http.response.status_class" or "error.type")), "Metrics use only declared fixed-cardinality dimensions.");
        await Task.Delay(1050); metrics.Observe();
        Check(metrics.Latest("nativeweave.server.diagnostics.dropped") >= 4, "Malformed external contexts are counted in actual native snapshot.");
        Check(metrics.Latest("nativeweave.server.callback.duration.max") > 0, "Actual raw callback scalar latency is observable without invoking sinks in FFI.");
        Check(await client.GetStringAsync("fixed-length") == "body", "Known-length response reaches local Hyper framing completion.");
        Exception? framingFailure = await framed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(framingFailure is null, "Native finish accepts an already-consumed exact Content-Length body without a false failure: " + framingFailure?.GetType().Name);
        await Eventually(() => traces.Completed.Count == 8);
        Check(traces.Completed.Last().Tags.Any(pair => pair.Key == "http.response.status_class" && pair.Value == "2xx"), "Successful final framing records the actual success status.");
        Check((await app.CloseAsync(TimeSpan.FromSeconds(2))).Status == ServerCloseStatus.Completed, "Context test native/managed retirement joined.");
    }
    private static async Task LifetimeAsync()
    {
        using var traces = new TraceCollector(ActivitySamplingResult.AllDataAndRecorded);
        using var metrics = new MetricCollector();
        var retained = new TaskCompletionSource<BodyChunk>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = NativeServer.CreateBuilder().Listen(IPAddress.Loopback, 0); builder.Options.RequestTimeout = TimeSpan.FromSeconds(5);
        var app = builder.Build();
        app.MapRaw("/retain", ["POST"], async (context, token) => { retained.SetResult((await context.Request.ReadChunkAsync(token))!); await context.Response.WriteAsync("retained"u8.ToArray(), token); });
        app.MapRaw("/deadline", ["GET"], async (context, token) =>
        {
            TimeSpan before = context.TimeRemaining; await Task.Delay(15, token); Check(context.TimeRemaining < before, "Deadline budget decreases across managed awaits.");
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); } catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
        }).WithTimeout(TimeSpan.FromMilliseconds(70));
        await app.StartAsync(); using var client = Client(app); metrics.Observe();
        using (var response = await client.GetAsync("deadline")) Check((int)response.StatusCode is 408 or 500, "Single native deadline terminates whole request.");
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using (var response = await client.PostAsync("retain", new ByteArrayContent("body"u8.ToArray()))) Check(await response.Content.ReadAsStringAsync() == "retained", "Retained payload request completes business response.");
        var chunk = await retained.Task; await Eventually(() => traces.Completed.Count == 2);
        Check(chunk.ToArray().SequenceEqual("body"u8.ToArray()), "Independently retained chunk survives callback Activity completion.");
        var pending = await app.CloseAsync(TimeSpan.Zero);
        Check(pending.Status == ServerCloseStatus.Pending && pending.RetainedNativeBytes > 0, "ClosePending preserves independent payload ownership after diagnostics complete.");
        Check(metrics.Measurements.Any(value => value.Tags.Any(pair => pair.Key == "error.type" && pair.Value == "timeout")), "Native timeout is distinguished from client cancellation without another timer.");
        chunk.Dispose(); var complete = await app.CloseAsync(TimeSpan.FromSeconds(2));
        Check(complete.Status == ServerCloseStatus.Completed && complete.RetainedNativeBytes == 0 && complete.RetainedManagedBytes == 0, "Retained payload release permits final native OS-thread join.");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => app.DisposeAsync().AsTask()));
        Check(app.State == ServerState.Disposed, "Concurrent disposal has one joined teardown.");
    }
    private static async Task CancellationRacesAsync()
    {
        using var traces = new TraceCollector(ActivitySamplingResult.AllDataAndRecorded);
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0, canceled = 0, cleaned = 0;
        var app = NativeServer.CreateBuilder().Listen(IPAddress.Loopback, 0).Build();
        app.MapRaw("/race/{id}", ["GET"], async (context, token) =>
        {
            context.Response.OnCompleted(() => { if (Activity.Current is not null) Interlocked.Increment(ref cleaned); return Task.CompletedTask; });
            if (Interlocked.Increment(ref entered) == 12) allEntered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { Interlocked.Increment(ref canceled); throw; }
        });
        await app.StartAsync(); using var client = Client(app);
        var tokens = Enumerable.Range(0, 12).Select(_ => new CancellationTokenSource()).ToArray();
        var calls = tokens.Select((token, index) => Observe(client.GetAsync("race/" + index.ToString(CultureInfo.InvariantCulture), token.Token))).ToArray();
        await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task<ServerCloseResult> close = app.CloseAsync(TimeSpan.Zero);
        await Task.WhenAll(tokens.Select(token => token.CancelAsync()));
        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(3)); await close;
        var joined = await app.CloseAsync(TimeSpan.FromSeconds(3));
        Check(joined.Status == ServerCloseStatus.Completed && joined.PendingRequests == 0 && joined.RetainedNativeBytes == 0 && joined.RetainedManagedBytes == 0, "Repeated cancellation/close callbacks join all native ownership with tracing enabled.");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => app.DisposeAsync().AsTask()));
        Check(canceled == 12 && cleaned == 12 && traces.Completed.Count == 12, "Twelve racing requests each cancel, clean up and stop their Activity once.");
        Check(traces.Completed.Select(trace => trace.Trace).Distinct().Count() == 12, "Concurrent callbacks never share an accidental request Activity.");
        foreach (var token in tokens) token.Dispose();
        static async Task Observe(Task<HttpResponseMessage> call)
        { try { using var response = await call; } catch (OperationCanceledException) { } catch (HttpRequestException) { } }
    }
    private static async Task ListenerFaultAsync()
    {
        using var listener = new ActivityListener { ShouldListenTo = source => source.Name == "NativeWeave.Server", Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded, ActivityStopped = _ => throw new IOException("secret broken sink") };
        ActivitySource.AddActivityListener(listener);
        var builder = NativeServer.CreateBuilder().Listen(IPAddress.Loopback, 0); await using var app = builder.Build(); app.MapGet("/ok", static () => Results.Text("ok"));
        await app.StartAsync(); using var client = Client(app); long drops = Interlocked.Read(ref DiagnosticCounters.TotalDropped);
        Check(await client.GetStringAsync("ok") == "ok", "Throwing ActivityStopped listener cannot alter business output.");
        await Eventually(() => Interlocked.Read(ref DiagnosticCounters.TotalDropped) > drops);
        Check((await app.CloseAsync(TimeSpan.FromSeconds(2))).Status == ServerCloseStatus.Completed, "Throwing listener cannot prevent request/root retirement.");
    }
    private static async Task ExporterStallAsync()
    {
        using var metrics = new MetricCollector(); var logger = new CaptureLogger { Block = true };
        var builder = NativeServer.CreateBuilder().Listen(IPAddress.Loopback, 0); builder.Options.RequestTimeout = TimeSpan.FromSeconds(10);
        builder.Services.AddLogging(logging => { logging.SetMinimumLevel(LogLevel.Warning); logging.AddProvider(logger); });
        var app = builder.Build(); app.MapGet("/fault", static () => throw new InvalidOperationException("SECRET"));
        await app.StartAsync(); using var client = Client(app); metrics.Observe(); long beforeDrops = Interlocked.Read(ref DiagnosticCounters.TotalDropped);
        var cpu = Process.GetCurrentProcess().TotalProcessorTime; var watch = Stopwatch.StartNew();
        for (int i = 0; i < 2100; i++) { using var response = await client.GetAsync("fault"); if ((int)response.StatusCode != 500) throw new Exception("Exporter affected HTTP output."); }
        await logger.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(Interlocked.Read(ref DiagnosticCounters.TotalRetainedEntries) <= 2048 && Interlocked.Read(ref DiagnosticCounters.TotalRetainedBytes) <= 524_288, "Actual blocked sink queue has finite entry and byte budgets.");
        await Eventually(() => Interlocked.Read(ref DiagnosticCounters.TotalDropped) > beforeDrops);
        Check(Interlocked.Read(ref DiagnosticCounters.TotalDropped) > beforeDrops, "Actual exporter saturation drops diagnostics while all requests complete.");
        double cpuMilliseconds = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
        metrics.Observe(); double callback = metrics.Latest("nativeweave.server.callback.duration.max");
        Console.WriteLine(FormattableString.Invariant($"EXPORTER_FAULT requests=2100 elapsed_ms={watch.Elapsed.TotalMilliseconds:F3} cpu_ms={cpuMilliseconds:F3} callback_max_seconds={callback:R}"));
        var close = await app.CloseAsync(TimeSpan.FromSeconds(3));
        Check(close.Status == ServerCloseStatus.Completed && close.PendingRequests == 0 && close.RetainedNativeBytes == 0 && close.RetainedManagedBytes == 0, "Business owners and OS join finish while exporter is still blocked.");
        watch.Restart(); await app.DisposeAsync();
        Check(watch.Elapsed < TimeSpan.FromSeconds(2) && app.State == ServerState.Disposed, "Independent finite exporter flush cannot keep application disposal pending.");
        Check(Interlocked.Read(ref DiagnosticCounters.TotalRetainedEntries) == 1, "Only bounded metadata remains with the stuck exporter.");
        logger.Release.TrySetResult(); await Eventually(() => Interlocked.Read(ref DiagnosticCounters.TotalRetainedEntries) == 0);
        Check(logger.Messages.All(message => !message.Contains("SECRET", StringComparison.Ordinal)), "Stalled exporter retains only redacted metadata.");
    }
}
internal sealed record TraceRecord(string Trace, string ParentSpan, string State, IReadOnlyList<KeyValuePair<string, string>> Tags);
internal sealed class TraceCollector : IDisposable
{
    private readonly ActivityListener _listener;
    internal ConcurrentQueue<TraceRecord> Completed { get; } = new();
    internal TraceCollector(ActivitySamplingResult result, int every = 1)
    {
        int sampled = 0;
        _listener = new() { ShouldListenTo = source => source.Name == "NativeWeave.Server", Sample = (ref ActivityCreationOptions<ActivityContext> _) => Interlocked.Increment(ref sampled) % every == 0 ? result : ActivitySamplingResult.None,
            ActivityStopped = activity => Completed.Enqueue(new(activity.TraceId.ToHexString(), activity.ParentSpanId.ToHexString(), activity.TraceStateString ?? "", activity.TagObjects.Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.ToString() ?? "")).ToArray())) };
        ActivitySource.AddActivityListener(_listener);
    }
    public void Dispose() => _listener.Dispose();
}
internal sealed record Measurement(string Name, double Value, IReadOnlyList<KeyValuePair<string, string>> Tags);
internal sealed class MetricCollector : IDisposable
{
    private readonly MeterListener _listener = new();
    internal ConcurrentQueue<Measurement> Measurements { get; } = new();
    internal MetricCollector()
    {
        _listener.InstrumentPublished = (instrument, listener) => { if (instrument.Meter.Name == "NativeWeave.Server") listener.EnableMeasurementEvents(instrument); };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Capture(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Capture(instrument, value, tags)); _listener.Start();
    }
    private void Capture(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    { List<KeyValuePair<string, string>> copied = []; foreach (var tag in tags) copied.Add(KeyValuePair.Create(tag.Key, tag.Value?.ToString() ?? "")); Measurements.Enqueue(new(instrument.Name, value, copied)); }
    internal void Observe() => _listener.RecordObservableInstruments();
    internal double Latest(string name) => Measurements.LastOrDefault(value => value.Name == name)?.Value ?? 0;
    public void Dispose() => _listener.Dispose();
}
internal sealed class CaptureLogger : ILoggerProvider, ILogger
{
    internal bool Block, Throw;
    internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ConcurrentQueue<string> Messages { get; } = new();
    public ILogger CreateLogger(string categoryName) => this;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> formatter)
    { Entered.TrySetResult(); if (Block) Release.Task.GetAwaiter().GetResult(); if (Throw) throw new IOException("Disconnected diagnostic sink."); Messages.Enqueue(formatter(state, error)); }
    public void Dispose() { }
}
