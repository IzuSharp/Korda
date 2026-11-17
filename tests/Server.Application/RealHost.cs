using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NativeWeave.Server;
using NativeWeave.Server.Hosting;

internal static class RealApplicationAcceptance
{
    internal static async Task RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks++; Console.WriteLine("PASS " + name); }
        async Task Throws<T>(Func<Task> work) where T : Exception { try { await work(); } catch (T) { checks++; return; } throw new Exception("Expected " + typeof(T).Name); }
        var native = Path.Combine(AppContext.BaseDirectory, "runtimes/linux-x64/native/libnativeweave_server.so");
        string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(native)));
        var rows = new List<object>();
        foreach (var protocol in new[] { (ServerProtocols.Http1, HttpVersion.Version11), (ServerProtocols.Http2, HttpVersion.Version20) })
        {
            var sequence = new ConcurrentQueue<string>();
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var scopeIds = new ConcurrentBag<int>();
            var streams = new ConcurrentBag<AcceptanceStream>();
            var builder = NativeServer.CreateBuilder();
            builder.Listen(IPAddress.Loopback, 0, listener => listener.Protocols = protocol.Item1);
            builder.UseJson(ProductJson.Default).UseAuthentication(new ProductAuthenticator()).UseAuthorization(new ProductAuthorizer());
            builder.Options.RequestTimeout = TimeSpan.FromSeconds(5);
            builder.Options.ShutdownTimeout = TimeSpan.FromSeconds(1);
            builder.Options.MaxJsonBytes = 1024;
            builder.Services.AddScoped(_ => new ProductScope(sequence));
            await using var app = builder.Build();
            Check(app.State == ServerState.Created && app.BoundAddresses.Count == 0, "Build is listener-free.");
            app.Use(async (context, next, token) =>
            {
                if (context.Request.Path == "/short") { context.Response.StatusCode = 202; return; }
                await next(context, token);
            });
            app.MapPost<ProductInput, ProductReply>("/typed", static (value, _) => Task.FromResult(new ProductReply(value.Name.ToUpperInvariant(), value.Count + 1)));
            ProductEndpoints.Map(app);
            app.MapRaw("/raw", ["GET"], (context, _) => { context.Response.StatusCode = 201; return Task.CompletedTask; });
            app.MapRaw("/untouched", ["GET"], static (_, _) => Task.CompletedTask);
            app.MapRaw("/short", ["GET"], static (_, _) => throw new Exception("Short circuit failed."));
            app.MapGet("/custom", static () => new IncompleteResult());
            app.MapGet("/scope", context =>
            {
                var scope = context.Services.GetRequiredService<ProductScope>();
                if (!ReferenceEquals(scope, context.Services.GetRequiredService<ProductScope>())) throw new Exception("Scoped singleton mismatch.");
                scopeIds.Add(scope.Id);
                context.Response.OnStarting(_ => { sequence.Enqueue("start1"); return Task.CompletedTask; });
                context.Response.OnStarting(_ => { sequence.Enqueue("start2"); return Task.CompletedTask; });
                context.Response.OnCompleted(() => { sequence.Enqueue("complete1"); return Task.CompletedTask; });
                context.Response.OnCompleted(() => { sequence.Enqueue("complete2"); throw new InvalidOperationException("cleanup exception"); });
                context.Response.RegisterForDisposeAsync(new AcceptanceDispose(sequence, "dispose1", false));
                context.Response.RegisterForDisposeAsync(new AcceptanceDispose(sequence, "dispose2", true));
                return Results.Text("scope");
            });
            app.MapGet("/failure", static () => throw new InvalidOperationException("secret handler payload"));
            app.MapRaw("/after", ["GET"], async (context, token) => { await context.Response.WriteAsync("prefix"u8.ToArray(), token); throw new InvalidOperationException("after commit"); });
            app.MapGet("/stream", () => { var stream = new AcceptanceStream("stream"u8.ToArray()); streams.Add(stream); return Results.Stream(stream); });
            app.MapGet("/events", static () => Results.ServerSentEvents(Events()));
            app.MapGet("/jsonstream", static () => Results.JsonStream(Replies(), ProductJson.Default.ProductReply));
            app.MapRaw("/cancel", ["GET"], async (_, token) => { try { await Task.Delay(Timeout.InfiniteTimeSpan, token); } catch (OperationCanceledException) { cancelled.TrySetResult(); throw; } });
            var group = app.MapGroup("/private").RequireAuthorization("signed");
            group.MapGet("/admin", static () => Results.Text("admin")).RequireAuthorization("admin");
            group.MapGet("/public", static () => Results.Text("public")).AllowAnonymous();
            app.MapGet("/identity", context => Results.Text(context.Request.Scheme + "|" + context.Request.Host + "|" + context.Request.RemoteEndPoint!.Address));
            app.MapGet("/bodyless", static () => Results.NoContent());
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = app.BoundAddresses.Single(), DefaultRequestVersion = protocol.Item2, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact, Timeout = TimeSpan.FromSeconds(10) };
            Check(app.State == ServerState.Running, "Running state.");
            using (var response = await client.PostAsync("typed", Json("{\"name\":\"linux\",\"count\":4}")))
            { var value = await response.Content.ReadAsStringAsync(); Check(response.Version == protocol.Item2 && value.Contains("LINUX", StringComparison.Ordinal) && value.Contains('5'), "Typed static JSON request/result."); }
            foreach (string invalid in new[] { "null", "", "{broken", "{\"name\":null,\"count\":4}" })
            {
                using var response = await client.PostAsync("typed", Json(invalid));
                Check(response.StatusCode == HttpStatusCode.BadRequest, "Required or malformed JSON rejected: " + invalid);
            }
            using (var response = await client.PostAsync("typed", Json("{\"name\":\"" + new string('x', 1100) + "\",\"count\":0}"))) Check((int)response.StatusCode == 413, "Bounded JSON rejects oversized body.");
            using (var request = new HttpRequestMessage(HttpMethod.Get, "generated/7?tags=1&tags=2") { Version = protocol.Item2, VersionPolicy = HttpVersionPolicy.RequestVersionExact })
            {
                request.Headers.Add("X-Label", "static"); using var response = await client.SendAsync(request);
                Check((await response.Content.ReadAsStringAsync()).Contains("static:7:3", StringComparison.Ordinal), "Generated route/query/header/service/DTO binding.");
            }
            Check(await client.GetStringAsync("generated-defaults") == "True|True|True|True|True", "Generated optional defaults retain decimal precision and floating constants.");
            foreach (string invalid in new[] { "generated/no?tags=1", "generated/1?tags=x" }) { using var response = await client.GetAsync(invalid); Check(response.StatusCode == HttpStatusCode.BadRequest, "Generated typed parameter failure."); }
            foreach (var row in new[] { ("raw", 201), ("untouched", 200), ("short", 202), ("custom", 202), ("generated-void", 203), ("bodyless", 204) })
            { using var response = await client.GetAsync(row.Item1); Check((int)response.StatusCode == row.Item2 && (await response.Content.ReadAsByteArrayAsync()).Length == 0, "Automatic final framing: " + row.Item1); }
            await Eventually(() => sequence.Contains("scope-dispose"));
            sequence.Clear();
            Check(await client.GetStringAsync("scope") == "scope", "Actual scoped dispatch.");
            await Eventually(() => sequence.Contains("scope-dispose"));
            Check(string.Join(',', sequence) == "start2,start1,complete2,complete1,dispose2,dispose1,scope-dispose", "LIFO callbacks, isolated failures, request scope last.");
            sequence.Clear(); await client.GetStringAsync("scope"); await Eventually(() => sequence.Contains("scope-dispose"));
            Check(scopeIds.Distinct().Count() == 2, "Separate request scopes.");
            using (var response = await client.GetAsync("failure")) Check((int)response.StatusCode == 500 && !(await response.Content.ReadAsStringAsync()).Contains("secret", StringComparison.Ordinal), "Bounded precommit handler failure.");
            try { using var response = await client.GetAsync("after", HttpCompletionOption.ResponseHeadersRead); Check((int)response.StatusCode == 200, "Committed status is not rewritten."); await response.Content.ReadAsByteArrayAsync(); }
            catch (HttpRequestException) { checks++; }
            Check(await client.GetStringAsync("stream") == "stream", "Owned stream result.");
            using (var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "stream") { Version = protocol.Item2, VersionPolicy = HttpVersionPolicy.RequestVersionExact })) Check((await response.Content.ReadAsByteArrayAsync()).Length == 0, "HEAD suppresses stream body.");
            await Eventually(() => streams.Count == 2 && streams.All(x => x.Disposed));
            Check(streams.Count(x => x.ReadCount == 0) == 1, "HEAD disposes admitted source without reading.");
            Check((await client.GetStringAsync("events")).Contains("data: native\n", StringComparison.Ordinal), "Actual SSE result.");
            Check((await client.GetStringAsync("jsonstream")).Contains("second", StringComparison.Ordinal), "Bounded static JSON sequence.");
            using (var response = await client.GetAsync("private/admin")) Check((int)response.StatusCode == 401, "Anonymous authorization denial.");
            using (var request = new HttpRequestMessage(HttpMethod.Get, "private/admin") { Version = protocol.Item2, VersionPolicy = HttpVersionPolicy.RequestVersionExact })
            { request.Headers.Add("X-User", "reader"); using var response = await client.SendAsync(request); Check((int)response.StatusCode == 403, "Named policies combine with AND."); }
            client.DefaultRequestHeaders.Add("X-User", "admin"); Check(await client.GetStringAsync("private/admin") == "admin", "Authenticated policy success."); client.DefaultRequestHeaders.Remove("X-User");
            Check(await client.GetStringAsync("private/public") == "public", "Explicit anonymous route.");
            using (var request = new HttpRequestMessage(HttpMethod.Get, "identity") { Version = protocol.Item2, VersionPolicy = HttpVersionPolicy.RequestVersionExact })
            { request.Headers.Add("X-Forwarded-For", "192.0.2.1"); request.Headers.Add("X-Forwarded-Proto", "https"); request.Headers.Add("X-Forwarded-Host", "forged.test"); using var response = await client.SendAsync(request); Check((await response.Content.ReadAsStringAsync()).StartsWith("http|", StringComparison.Ordinal), "Untrusted forwarding is ignored."); }
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100))) await Throws<OperationCanceledException>(async () => { using var response = await client.GetAsync("cancel", cancel.Token); });
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3)); checks++;
            await Throws<InvalidOperationException>(() => { app.MapGet("/late", static () => Results.NoContent()); return Task.CompletedTask; });
            var close = await app.CloseAsync(TimeSpan.FromSeconds(2));
            Check(close.Status == ServerCloseStatus.Completed && close.PendingRequests == 0 && close.RetainedManagedBytes == 0 && close.RetainedNativeBytes == 0, "All application/native ownership joined.");
            await app.DisposeAsync(); Check(app.State == ServerState.Disposed, "Disposed application.");
            await Throws<InvalidOperationException>(() => app.StartAsync());
            rows.Add(new { http = protocol.Item2.ToString(), assertions = checks, close });
        }
        // A distinct application/DTO uses the same precompiled native bytes.
        var second = NativeServer.CreateBuilder(); second.Listen(IPAddress.Loopback, 0); second.UseJson(ProductJson.Default);
        second.Options.TrustedProxies = new TrustedProxyOptions(); second.Options.TrustedProxies.KnownProxies.Add(IPAddress.Loopback);
        await using (var app = second.Build())
        {
            app.MapGet("/other", static () => Results.Json(new OtherProduct(Guid.Empty, "second-app"), ProductJson.Default.OtherProduct));
            app.MapGet("/identity", context => Results.Text(context.Request.Scheme + "|" + context.Request.Host + "|" + context.Request.RemoteEndPoint!.Address));
            await app.StartAsync(); using var client = new HttpClient { BaseAddress = app.BoundAddresses.Single() };
            Check((await client.GetStringAsync("other")).Contains("second-app", StringComparison.Ordinal), "Second application and DTO.");
            using var request = new HttpRequestMessage(HttpMethod.Get, "identity"); request.Headers.Add("X-Forwarded-For", "192.0.2.1"); request.Headers.Add("X-Forwarded-Proto", "https"); request.Headers.Add("X-Forwarded-Host", "edge.test");
            using var response = await client.SendAsync(request); Check(await response.Content.ReadAsStringAsync() == "https|edge.test|127.0.0.1", "Trusted coherent forwarding preserves original transport peer.");
            using var incoherent = new HttpRequestMessage(HttpMethod.Get, "identity"); incoherent.Headers.Add("X-Forwarded-For", "192.0.2.1, 192.0.2.2"); incoherent.Headers.Add("X-Forwarded-Proto", "https"); incoherent.Headers.Add("X-Forwarded-Host", "edge.test");
            using var bad = await client.SendAsync(incoherent); Check((int)bad.StatusCode == 400, "Malformed trusted forwarding tuple rejects request.");
        }
        var services = new ServiceCollection();
        services.AddNativeWeaveServer(provider => { var builder = NativeServer.CreateBuilder().Listen(IPAddress.Loopback, 0).UseServiceProvider(provider); var app = builder.Build(); app.MapGet("/hosted", static () => Results.Text("hosted")); return app; });
        await using (var provider = services.BuildServiceProvider())
        {
            var hosted = provider.GetRequiredService<IHostedService>(); await hosted.StartAsync(default);
            var app = provider.GetRequiredService<NativeServerApplication>(); using var client = new HttpClient { BaseAddress = app.BoundAddresses.Single() };
            Check(await client.GetStringAsync("hosted") == "hosted", "Optional Generic Host lifecycle uses actual Rust Host."); await hosted.StopAsync(default);
        }
        string after = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(native))); Check(before == after, "Native SHA unchanged across Handler/DTO applications.");
        Console.WriteLine($"B15 actual Rust Host: {checks} assertions, HTTP1/HTTP2, two DTO applications, native SHA {after}, dynamic={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}.");
    }
    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");
    private static async Task Eventually(Func<bool> predicate) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!predicate()) await Task.Delay(5, timeout.Token); }
    private static async IAsyncEnumerable<ServerEvent> Events() { yield return new("native"); await Task.CompletedTask; }
    private static async IAsyncEnumerable<ProductReply> Replies() { yield return new("first", 1); await Task.CompletedTask; yield return new("second", 2); }
}
internal sealed class ProductScope(ConcurrentQueue<string> sequence) : IAsyncDisposable
{
    private static int _next;
    internal int Id { get; } = Interlocked.Increment(ref _next);
    public ValueTask DisposeAsync() { sequence.Enqueue("scope-dispose"); return ValueTask.CompletedTask; }
}
internal sealed class AcceptanceDispose(ConcurrentQueue<string> sequence, string name, bool fail) : IAsyncDisposable
{ public ValueTask DisposeAsync() { sequence.Enqueue(name); return fail ? ValueTask.FromException(new InvalidOperationException("isolated disposal fault")) : ValueTask.CompletedTask; } }
internal sealed class AcceptanceStream(byte[] data) : MemoryStream(data)
{
    internal bool Disposed; internal int ReadCount;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { ReadCount++; return base.ReadAsync(buffer, cancellationToken); }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}
internal sealed class IncompleteResult : ServerResult
{ public override Task ExecuteAsync(RequestContext context, CancellationToken cancellationToken = default) { context.Response.StatusCode = 202; return Task.CompletedTask; } }
internal sealed class ProductAuthenticator : IServerAuthenticator
{ public Task<ClaimsPrincipal?> AuthenticateAsync(RequestContext context, CancellationToken cancellationToken = default) => Task.FromResult(context.Request.Headers.GetSingle("X-User") is { } user ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "test")) : null); }
internal sealed class ProductAuthorizer : IServerAuthorizer
{ public Task<bool> AuthorizeAsync(RequestContext context, string policy, CancellationToken cancellationToken = default) => Task.FromResult(context.User.Identity?.IsAuthenticated == true && (policy == "signed" || context.User.Identity.Name == "admin")); }
internal sealed record ProductInput(string Name, int Count);
internal sealed record ProductReply(string Label, int Count);
internal sealed record OtherProduct(Guid Id, string Name);
[JsonSerializable(typeof(ProductInput)), JsonSerializable(typeof(ProductReply)), JsonSerializable(typeof(OtherProduct))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
internal partial class ProductJson : JsonSerializerContext { }
[ServerEndpoints]
internal static partial class ProductEndpoints
{
    [ServerRoute("GET", "/generated/{id}")]
    private static ProductReply Read([RouteParameter] int id, [QueryParameter] int[] tags, [HeaderParameter("X-Label")] string? label, [ServiceParameter] ProductScope scope) => new((label ?? "none") + ":" + id + ":" + tags.Sum(), scope.Id);
    [ServerRoute("GET", "/generated-defaults")]
    private static ServerResult Defaults([QueryParameter] decimal precise = 1.2345678901234567890123456789m, [QueryParameter] float single = float.NaN, [QueryParameter] double negative = double.NegativeInfinity, [QueryParameter] double positive = double.PositiveInfinity, [QueryParameter] ulong large = ulong.MaxValue)
        => Results.Text($"{precise == 1.2345678901234567890123456789m}|{float.IsNaN(single)}|{double.IsNegativeInfinity(negative)}|{double.IsPositiveInfinity(positive)}|{large == ulong.MaxValue}");
    [ServerRoute("GET", "/generated-void")]
    private static Task Empty(RequestContext context) { context.Response.StatusCode = 203; return Task.CompletedTask; }
}
