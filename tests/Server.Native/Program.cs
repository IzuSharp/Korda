using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NativeWeave.Server;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
async Task ThrowsAsync<T>(Func<Task> work) where T : Exception
{ try { await work(); } catch (T) { checks++; return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
async Task Until(Func<bool> condition)
{ var end = Stopwatch.StartNew(); while (!condition()) { if (end.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Retirement did not settle."); await Task.Delay(5); } }
var empty = new Dictionary<string, IReadOnlyList<string>>();
string output = Environment.GetEnvironmentVariable("NW_SERVER_EVIDENCE") ?? Path.Combine(Path.GetTempPath(), "nativeweave-server-" + Environment.ProcessId);
Directory.CreateDirectory(output);
using var key = RSA.Create(2048);
var certificateRequest = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
certificateRequest.CertificateExtensions.Add(names.Build());
certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
string certFile = Path.Combine(output, "server-cert.pem"), keyFile = Path.Combine(output, "server-key.pem");
File.WriteAllText(certFile, certificate.ExportCertificatePem()); File.WriteAllText(keyFile, key.ExportPkcs8PrivateKeyPem());
var tls = ServerTlsOptions.FromPemFiles(certFile, keyFile);

(NativeHost host, RouteDefinition[] routes) Create(ServerProtocols protocol, ServerTlsOptions? tlsOptions = null, ServerOptions? options = null)
{
    options ??= new() { RequestTimeout = TimeSpan.FromSeconds(2), ShutdownTimeout = TimeSpan.FromMilliseconds(100) };
    var config = new ServerConfiguration(ServerConfiguration.Snapshot(options), [new(IPAddress.Loopback, 0, "test", protocol, tlsOptions is null ? null : TlsSnapshot.Create(tlsOptions))], [], null, null);
    var host = new NativeHost(config); var group = new GroupPolicy(null, "");
    var routes = new[] { new RouteDefinition(1, group, "/hello/{name}", ["GET"], null, null), new RouteDefinition(2, group, "/echo", ["POST"], null, null), new RouteDefinition(3, group, "/status/{code}", ["GET"], null, null), new RouteDefinition(4, group, "/hold", ["POST"], null, null), new RouteDefinition(5, group, "/deadline", ["GET"], null, null), new RouteDefinition(6, group, "/quota", ["POST"], null, null), new RouteDefinition(7, group, "/unread", ["POST"], null, null), new RouteDefinition(8, group, "/sse", ["GET"], null, null) };
    foreach (var route in routes) route.Validate(config);
    return (host, routes);
}
HttpClient Client(NativeHost host, Version version, bool trusted = true)
{
    var handler = new HttpClientHandler();
    if (trusted) handler.ServerCertificateCustomValidationCallback = (_, received, _, errors) => received?.Thumbprint == certificate.Thumbprint && (errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) == 0;
    return new(handler) { BaseAddress = host.BoundAddresses[0], DefaultRequestVersion = version, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact, Timeout = TimeSpan.FromSeconds(8) };
}
TaskCompletionSource<BodyChunk> retained = new(TaskCreationOptions.RunContinuationsAsynchronously);
TaskCompletionSource<string> quota = new(TaskCreationOptions.RunContinuationsAsynchronously);
TaskCompletionSource cancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
int dispatches = 0;
async Task Dispatch(HostRequest request)
{
    Interlocked.Increment(ref dispatches);
    switch (request.RouteId)
    {
        case 1:
            await request.CommitHeadersAsync(200, empty, request.RequestAborted);
            if (request.Method != "HEAD") await request.Body.WriteAsync(Encoding.UTF8.GetBytes("Hello " + request.RouteValues["name"][0]), request.RequestAborted);
            await request.Body.FinishAsync(request.RequestAborted); break;
        case 2:
            await request.CommitHeadersAsync(200, empty, request.RequestAborted);
            while (await request.Body.ReadChunkAsync(request.Body.MaxChunkBytes, request.RequestAborted) is { } chunk)
            { using (chunk) await request.Body.WriteAsync(chunk.ToArray(), request.RequestAborted); }
            await request.Body.FinishAsync(request.RequestAborted); break;
        case 3:
            await request.CommitHeadersAsync(int.Parse(request.RouteValues["code"][0]), empty, request.RequestAborted);
            await request.Body.FinishAsync(request.RequestAborted); break;
        case 4:
            var held = await request.Body.ReadChunkAsync(request.Body.MaxChunkBytes, request.RequestAborted) ?? throw new InvalidOperationException("Missing held input.");
            retained.TrySetResult(held);
            await request.CommitHeadersAsync(200, empty, request.RequestAborted);
            await request.Body.FinishAsync(request.RequestAborted); break;
        case 5:
            try { await Task.Delay(Timeout.InfiniteTimeSpan, request.RequestAborted); }
            catch (OperationCanceledException) { cancellation.TrySetResult(); }
            break;
        case 6:
            var chunks = new List<BodyChunk>();
            try
            {
                for (int i = 0; i < 2; i++) chunks.Add(await request.Body.ReadChunkAsync(request.Body.MaxChunkBytes, request.RequestAborted) ?? throw new InvalidOperationException("Missing quota input."));
                await request.CommitHeadersAsync(200, empty, request.RequestAborted);
                var time = Stopwatch.StartNew();
                try { await request.Body.WriteAsync(new byte[] { 1 }, request.RequestAborted); quota.TrySetResult("unexpected-success"); }
                catch (ServerException error) { quota.TrySetResult(error.Code + ":" + (time.Elapsed < TimeSpan.FromSeconds(1))); }
            }
            finally { foreach (var chunk in chunks) chunk.Dispose(); }
            request.Abort(); break;
        case 8:
            await request.CommitHeadersAsync(200, new Dictionary<string, IReadOnlyList<string>> { ["content-type"] = new[] { "text/event-stream" } }, request.RequestAborted);
            await request.WriteServerEventAsync(new ServerEvent("one\ntwo", "update", "7", TimeSpan.FromMilliseconds(5)), request.RequestAborted);
            await request.Body.FinishAsync(request.RequestAborted); break;
        case 7:
            await request.CommitHeadersAsync(200, empty, request.RequestAborted); await request.Body.FinishAsync(request.RequestAborted); break;
    }
}

foreach (var row in new[] { (ServerProtocols.Http1, (ServerTlsOptions?)null, HttpVersion.Version11), (ServerProtocols.Http2, (ServerTlsOptions?)null, HttpVersion.Version20), (ServerProtocols.Http1AndHttp2, tls, HttpVersion.Version20), (ServerProtocols.Http1AndHttp2, tls, HttpVersion.Version11) })
{
    var (host, routes) = Create(row.Item1, row.Item2);
    await using (host)
    {
        Check(host.Snapshot().state == 0 && host.BoundAddresses.Count == 0, "Build is runtime/listener-free.");
        Check((host.Capabilities & (ServerCapabilities)15) == (ServerCapabilities)15, "Negotiated required capabilities.");
        await host.StartAsync(routes, Dispatch, default).WaitAsync(TimeSpan.FromSeconds(10));
        Check(host.BoundAddresses.Count == 1 && host.BoundAddresses[0].Port != 0, "Actual bound ephemeral address.");
        using var client = Client(host, row.Item3);
        using var response = await client.GetAsync("hello/Linux");
        Check(response.Version == row.Item3 && await response.Content.ReadAsStringAsync() == "Hello Linux", "Real HTTP version and static managed dispatch.");
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "hello/Linux") { Version = row.Item3, VersionPolicy = HttpVersionPolicy.RequestVersionExact });
        Check(head.StatusCode == HttpStatusCode.OK && (await head.Content.ReadAsByteArrayAsync()).Length == 0, "HEAD body suppression.");
        using var missing = await client.GetAsync("missing"); Check(missing.StatusCode == HttpStatusCode.NotFound, "Native Axum 404.");
        using var method = await client.PostAsync("hello/Linux", new ByteArrayContent([])); Check(method.StatusCode == HttpStatusCode.MethodNotAllowed && method.Content.Headers.Allow.Contains("GET"), "Native method/Allow gate.");
        byte[] payload = Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray();
        using var echo = await client.PostAsync("echo", new ByteArrayContent(payload));
        Check((await echo.Content.ReadAsByteArrayAsync()).SequenceEqual(payload), "Real bounded duplex input/output and retained copies.");
        foreach (int code in new[] { 204, 205, 304 })
        { using var status = await client.GetAsync("status/" + code); Check((int)status.StatusCode == code && (await status.Content.ReadAsByteArrayAsync()).Length == 0, "Bodyless final status " + code); }
        string events = await client.GetStringAsync("sse");
        Check(events == "data: one\ndata: two\nevent: update\nid: 7\nretry: 5\n\n", "Axum SSE formatter output and bounded ownership.");
        using var unread = await client.PostAsync("unread", new ByteArrayContent(new byte[10_000])); Check(unread.StatusCode == HttpStatusCode.OK, "Bounded early unread body close.");
        if (row.Item2 is not null)
        {
            using var untrusted = Client(host, row.Item3, false);
            await ThrowsAsync<HttpRequestException>(async () => await untrusted.GetAsync("hello/trust"));
            await host.ReloadTlsAsync("test", TlsSnapshot.Create(tls), default);
            Check(await client.GetStringAsync("hello/reloaded") == "Hello reloaded", "Existing TLS connection survives atomic snapshot reload.");
        }
        await Until(() => { var snap = host.Snapshot(); return snap.active_requests == 0 && snap.retiring_requests == 0 && snap.retained_native_bytes == 0 && snap.retained_managed_bytes == 0; });
        var close = await host.CloseAsync(TimeSpan.FromSeconds(1), default).WaitAsync(TimeSpan.FromSeconds(5));
        Check(close.Status == ServerCloseStatus.Completed && close.RetainedNativeBytes == 0, "Close joins HTTP work, callbacks, payload and runtime worker.");
    }
    Console.WriteLine("PASS transport " + row.Item1 + " TLS=" + (row.Item2 is not null) + " HTTP=" + row.Item3);
}

var heldOptions = new ServerOptions { RequestTimeout = TimeSpan.FromSeconds(3), ShutdownTimeout = TimeSpan.FromMilliseconds(20) };
heldOptions.Budget.MaxActiveRequests = 1; heldOptions.Budget.MaxQueuedRequests = 0;
var heldHost = Create(ServerProtocols.Http1, options: heldOptions);
await using (heldHost.host)
{
    await heldHost.host.StartAsync(heldHost.routes, Dispatch, default);
    using var client = Client(heldHost.host, HttpVersion.Version11);
    using var response = await client.PostAsync("hold", new ByteArrayContent("retained"u8.ToArray()));
    using BodyChunk chunk = await retained.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(response.StatusCode == HttpStatusCode.OK && Encoding.UTF8.GetString(chunk.ToArray()) == "retained", "Owned chunk survives successful dispatch.");
    await Until(() => heldHost.host.Snapshot().retiring_requests == 1);
    using var saturated = await client.GetAsync("hello/saturated"); Check(saturated.StatusCode == HttpStatusCode.ServiceUnavailable, "Retiring chunk holds active request slot.");
    var pending = await heldHost.host.CloseAsync(TimeSpan.Zero, default);
    Check(pending.Status == ServerCloseStatus.Pending && pending.RetainedNativeBytes == heldOptions.Budget.MaxChunkBytes, "Zero-wait close reports retained full backing credit.");
    Check(chunk.ToArray().SequenceEqual("retained"u8.ToArray()), "Retained anchor copy remains valid after close intent.");
    chunk.Dispose();
}

var quotaOptions = new ServerOptions { RequestTimeout = TimeSpan.FromSeconds(3), ShutdownTimeout = TimeSpan.FromMilliseconds(20), MaxJsonBytes = 64 };
quotaOptions.Budget.NativePayloadBytes = quotaOptions.Budget.ManagedPayloadBytes = quotaOptions.Budget.RetainedBytesPerRequest = 65_536;
var quotaHost = Create(ServerProtocols.Http1, options: quotaOptions);
await using (quotaHost.host)
{
    await quotaHost.host.StartAsync(quotaHost.routes, Dispatch, default);
    using var client = Client(quotaHost.host, HttpVersion.Version11);
    Task<HttpResponseMessage> response = client.PostAsync("quota", new ByteArrayContent(new byte[65_536]));
    Check(await quota.Task.WaitAsync(TimeSpan.FromSeconds(5)) == "resource_limit:True", "Full inbound credit rejects next write without waiting or hidden retry.");
    try { using var completed = await response; } catch (HttpRequestException) { checks++; }
    await Until(() => quotaHost.host.Snapshot().retained_native_bytes == 0);
}

var shortOptions = new ServerOptions { RequestTimeout = TimeSpan.FromMilliseconds(80), ShutdownTimeout = TimeSpan.FromMilliseconds(20) };
var deadlineHost = Create(ServerProtocols.Http2, options: shortOptions);
await using (deadlineHost.host)
{
    await deadlineHost.host.StartAsync(deadlineHost.routes, Dispatch, default);
    using var client = Client(deadlineHost.host, HttpVersion.Version20);
    using var response = await client.GetAsync("deadline");
    Check(response.StatusCode == HttpStatusCode.RequestTimeout, "Native total deadline includes handler wait.");
    await cancellation.Task.WaitAsync(TimeSpan.FromSeconds(5)); checks++;
}

// This dispatcher uses no B15 matching, middleware or binder. The final row tests composition.
var builder = NativeServer.CreateBuilder(); builder.Options.ShutdownTimeout = TimeSpan.FromMilliseconds(30); builder.Listen(IPAddress.Loopback, 0);
await using (var app = builder.Build())
{
    app.MapGet("/hello/{name}", context => Results.Text("Hello " + context.Request.RouteValues.Get<string>("name")));
    await app.StartAsync(); using var client = new HttpClient { BaseAddress = app.BoundAddresses[0] };
    Check(await client.GetStringAsync("hello/application") == "Hello application", "B15/B17 composed native host.");
}
Console.WriteLine("PASS B17 checks=" + checks + " dispatches=" + dispatches + " dynamic=" + System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported);
