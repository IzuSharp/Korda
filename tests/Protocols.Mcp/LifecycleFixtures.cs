using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NativeWeave.Server;
using NativeWeave.Server.Mcp;
using NativeWeave.Interop.Async;

internal static class LifecycleFixtures
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var version in new[] { HttpVersion.Version11, HttpVersion.Version20 })
            foreach (bool cancel in new[] { false, true })
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var startPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var failures = new ConcurrentQueue<string>();
                int business = 0, completed = 0, retired = 0, firstStart = 0, startWindow = 0, created = ScopeProbe.Created, disposed = ScopeProbe.Disposed;
                NativeHost? engine = null;
                var builder = NativeServer.CreateBuilder();
                builder.Listen(IPAddress.Loopback, 0, o => o.Protocols = version == HttpVersion.Version20 ? ServerProtocols.Http2 : ServerProtocols.Http1);
                builder.Options.Budget.NativePayloadBytes = 4 * 1024 * 1024;
                builder.Options.Budget.ManagedPayloadBytes = 4 * 1024 * 1024;
                builder.Options.Budget.MaxActiveRequests = 4;
                builder.Options.Budget.MaxQueuedRequests = 0;
                builder.Options.RequestTimeout = TimeSpan.FromSeconds(10);
                builder.Services.AddScoped(_ => new ScopeProbe());
                await using var app = builder.Build();
                app.Use(async (context, next, token) =>
                {
                    engine = ((NativeRequest)context.Native).Engine;
                    engine.TestBoundary = boundary =>
                    {
                        if (boundary != AsyncBoundary.StartReturned || Interlocked.CompareExchange(ref firstStart, 1, 0) != 0) return;
                        Volatile.Write(ref startWindow, 1); startPaused.TrySetResult();
                        if (!entered.Task.Wait(TimeSpan.FromSeconds(5))) failures.Enqueue("Nested continuation could not enter while outer Core start was settling");
                        Volatile.Write(ref startWindow, 0);
                    };
                    engine.TestRetirement = snapshot =>
                    {
                        if (!(snapshot.Released && snapshot.NativeQuiet && !snapshot.RootLive && snapshot.Controls == 0 && snapshot.CallbackEntries == 0 && snapshot.RetainedInputBytes == 0)) failures.Enqueue("A07 ownership remained after retirement");
                        Interlocked.Increment(ref retired);
                    };
                    _ = context.Services.GetRequiredService<ScopeProbe>();
                    context.Response.OnCompleted(() => { Interlocked.Increment(ref completed); return Task.CompletedTask; });
                    await next(context, token);
                });
                var registry = new McpRegistry().AddTool("hold", "Controlled cancellation-ignoring application", Json.Default.Input, Json.Default.Output, async (input, context) =>
                {
                    Interlocked.Increment(ref business);
                    using var registration = context.CancellationToken.Register(() => cancelled.TrySetResult());
                    await startPaused.Task;
                    if (Volatile.Read(ref startWindow) != 1) failures.Enqueue("Nested continuation did not overlap outer Core start settlement");
                    entered.TrySetResult();
                    await release.Task; // Deliberately ignores intent to verify truthful pending ownership.
                    return McpToolResult<Output>.Success(new(input.Text));
                });
                app.MapMcp("/mcp", registry);
                await app.StartAsync();
                using var client = new HttpClient { BaseAddress = app.BoundAddresses.Single(), Timeout = TimeSpan.FromSeconds(12), DefaultRequestVersion = version, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
                using var cts = new CancellationTokenSource();
                using var first = Request(version);
                Task<HttpResponseMessage> active = client.SendAsync(first, cts.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                check(firstStart == 1 && failures.IsEmpty, "Same-ticket continuation enters before outer managed Start settles");
                if (!cancel)
                {
                    using var second = Request(version);
                    using var rejected = await client.SendAsync(second);
                    check(rejected.StatusCode == HttpStatusCode.ServiceUnavailable && business == 1, "Global peak exhaustion rejects a second business request before handler: HTTP/" + version);
                    release.TrySetResult();
                    using var success = await active;
                    check(success.IsSuccessStatusCode && (await success.Content.ReadAsStringAsync()).Contains("structuredContent", StringComparison.Ordinal), "First admitted request completes after global pressure");
                }
                else
                {
                    cts.Cancel();
                    try { using var response = await active; throw new InvalidOperationException("Client cancellation unexpectedly succeeded"); }
                    catch (OperationCanceledException) { check(true, "Unfinished no-event-store response disconnect cancels client"); }
                    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    ServerCloseResult pending = await app.CloseAsync(TimeSpan.Zero);
                    check(pending.Status == ServerCloseStatus.Pending && pending.PendingRequests > 0 && completed == 0 && ScopeProbe.Disposed == disposed, "Canceled nested callback retains request and scope until application returns");
                    check(pending.RetainedNativeBytes > 0 && pending.RetainedManagedBytes > 0, "Cancel does not release live native/managed overlap credits");
                    release.TrySetResult();
                }
                var closed = await app.CloseAsync(TimeSpan.FromSeconds(5));
                check(closed.Status == ServerCloseStatus.Completed && closed.PendingRequests == 0 && closed.RetainedNativeBytes == 0 && closed.RetainedManagedBytes == 0, "Global-pressure/cancel terminal drains both native and managed credits");
                check(retired > 0 && failures.IsEmpty && ScopeProbe.Created - created == completed && ScopeProbe.Disposed - disposed == completed, "Managed A07 retirement and same-scope completion independently drain");
                check(engine is not null, "Lifecycle fixture used actual native Host");
            }
    }
    internal static async Task UndecidedAsync(Action<bool, string> check)
    {
        foreach (var version in new[] { HttpVersion.Version11, HttpVersion.Version20 })
        {
            var auth = new HeldAuthenticator(); int business = 0, completed = 0;
            var builder = NativeServer.CreateBuilder(); builder.Listen(IPAddress.Loopback, 0, o => o.Protocols = version == HttpVersion.Version20 ? ServerProtocols.Http2 : ServerProtocols.Http1);
            builder.UseAuthentication(auth).UseAuthorization(new Authorize()); builder.Services.AddScoped(_ => new ScopeProbe());
            builder.Options.RequestTimeout = TimeSpan.FromSeconds(10);
            int disposed = ScopeProbe.Disposed;
            await using var app = builder.Build();
            app.Use(async (context, next, token) => { _ = context.Services.GetRequiredService<ScopeProbe>(); context.Response.OnCompleted(() => { Interlocked.Increment(ref completed); return Task.CompletedTask; }); await next(context, token); });
            app.MapMcp("/mcp", new McpRegistry().AddTool("hold", "Must never enter", Json.Default.Input, Json.Default.Output, (_, _) => { Interlocked.Increment(ref business); return Task.FromResult(McpToolResult<Output>.Success(new("unexpected"))); })).RequireAuthorization("authenticated");
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = app.BoundAddresses.Single(), Timeout = TimeSpan.FromSeconds(12) };
            using var cts = new CancellationTokenSource(); using var request = Request(version);
            request.Content = new StringContent("{malformed and should never parse", Encoding.UTF8, "application/json");
            Task<HttpResponseMessage> sending = client.SendAsync(request, cts.Token);
            await auth.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cts.Cancel(); try { using var response = await sending; } catch (OperationCanceledException) { }
            await auth.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = await app.CloseAsync(TimeSpan.Zero);
            check(pending.Status == ServerCloseStatus.Pending && pending.PendingRequests > 0 && completed == 0 && ScopeProbe.Disposed == disposed && business == 0, "Cancellation before gate decision retains the sole auth scope and never enters business");
            check(pending.RetainedNativeBytes == 0 && pending.RetainedManagedBytes == 0, "Undecided gate has not admitted a body/SDK/DTO payload");
            auth.Release.TrySetResult();
            var closed = await app.CloseAsync(TimeSpan.FromSeconds(5));
            check(closed.Status == ServerCloseStatus.Completed && closed.PendingRequests == 0 && closed.RetainedNativeBytes == 0 && closed.RetainedManagedBytes == 0 && completed == 1 && ScopeProbe.Disposed == disposed + 1, "Canceled undecided gate retires exactly once after authenticator returns");
        }
    }
    private sealed class HeldAuthenticator : IServerAuthenticator
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<System.Security.Claims.ClaimsPrincipal?> AuthenticateAsync(RequestContext context, CancellationToken token)
        {
            using var registration = token.Register(() => Cancelled.TrySetResult()); Entered.TrySetResult(); await Release.Task; return null;
        }
    }
    internal static HttpRequestMessage Request(Version version)
    {
        const string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"hold\",\"arguments\":{\"text\":\"held\"},\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{}}}}";
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Version = version, VersionPolicy = HttpVersionPolicy.RequestVersionExact, Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Accept", "application/json, text/event-stream"); request.Headers.Add("MCP-Protocol-Version", "2026-07-28"); request.Headers.Add("Mcp-Method", "tools/call"); request.Headers.Add("Mcp-Name", "hold"); return request;
    }
}
