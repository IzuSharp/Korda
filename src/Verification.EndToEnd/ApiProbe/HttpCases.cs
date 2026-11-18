using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NativeWeave.Data;
using NativeWeave.Database;
using NativeWeave.Server;

namespace NativeWeave.Verification.EndToEnd;

internal static partial class Probe
{
    private static async Task HttpComposition()
    {
        await using var source = Source();
        var builder = Builder(); builder.Services.AddScoped(_ => new ScopeWitness());
        await using var app = builder.Build();
        app.Use(async (context, next, token) =>
        {
            _ = context.Services.GetRequiredService<ScopeWitness>();
            if (context.Request.Path == "/short") { context.Response.StatusCode = 202; return; }
            await next(context, token);
        });
        app.MapRaw("/raw", ["GET"], (context, _) => { context.Response.StatusCode = 201; return Task.CompletedTask; });
        app.MapRaw("/untouched", ["GET"], (_, _) => Task.CompletedTask);
        app.MapRaw("/short", ["GET"], (_, _) => throw new InvalidOperationException("short circuit did not stop"));
        app.MapGet("/custom", () => new PartialResult());
        GeneratedProbe.Map(app);
        app.MapGet("/relations", async (_, token) =>
        {
            var session = await source.OpenSessionAsync(token);
            try
            {
                var related = await session.LoadRelatedAsync(Relations.Parents.Key(Relations.Tenant, 7L).And(Relations.Id, 1L), Relations.Optional, token);
                using var query = session.From(Relations.Children);
                var missing = await query.WhereIsNull(Relations.ChildTenant).OrderBy(Relations.ChildId).SelectAll().ToListAsync(token);
                using var nullEquality = session.From(Relations.Children);
                long equalNull = await nullEquality.WhereEqual(Relations.ChildTenant, (long?)null).CountAsync(token);
                return Results.Text(string.Join(',', related.Select(item => item.Id)) + "|" + string.Join(',', missing.Select(item => item.Id)) + "|" + equalNull);
            }
            finally { await session.DisposeAsync(); }
        });
        app.MapGet("/handler-failure", () => throw new InvalidOperationException("private application detail"));
        await app.StartAsync();
        foreach (Version version in new[] { HttpVersion.Version11, HttpVersion.Version20 })
        {
            using var client = Client(app, version);
            foreach (var row in new[] { ("raw", 201, ""), ("untouched", 200, ""), ("short", 202, ""), ("custom", 207, "custom"), ("generated-void", 203, "") })
            {
                using var response = await client.GetAsync(row.Item1);
                Check("automatic-http-completion-" + version + "-" + row.Item1,
                    response.Version == version && (int)response.StatusCode == row.Item2 && await response.Content.ReadAsStringAsync() == row.Item3);
            }
            string relatedBody = await client.GetStringAsync("relations");
            Check("nullable-foreign-key-query-http-" + version, relatedBody == "1|2,4|0", ("body", relatedBody));
            using var failure = await client.GetAsync("handler-failure");
            Check("handler-fault-redacted-" + version, (int)failure.StatusCode == 500 && !(await failure.Content.ReadAsStringAsync()).Contains("private application detail", StringComparison.Ordinal));
        }
        Closed("http-composition-close", await app.CloseAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task RetainedChunk()
    {
        var builder = Builder(); builder.Services.AddScoped(_ => new ScopeWitness());
        builder.Options.Budget.MaxActiveRequests = 1; builder.Options.Budget.MaxQueuedRequests = 0;
        var held = new TaskCompletionSource<BodyChunk>(TaskCreationOptions.RunContinuationsAsynchronously);
        var weakScope = new TaskCompletionSource<WeakReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResponse = new TaskCompletionSource<ServerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool eof = false; int disposed = ScopeWitness.Disposed;
        await using var app = builder.Build();
        app.MapRaw("/hold", ["POST"], async (context, token) =>
        {
            weakScope.TrySetResult(new(context.Services.GetRequiredService<ScopeWitness>()));
            BodyChunk chunk = await context.Request.ReadChunkAsync(token) ?? throw new InvalidOperationException("No positive input chunk");
            eof = await context.Request.ReadChunkAsync(token) is null;
            held.TrySetResult(chunk); oldResponse.TrySetResult(context.Response);
        });
        app.MapGet("/other", () => Results.NoContent());
        await app.StartAsync(); using var client = Client(app);
        using var response = await client.PostAsync("hold", new ByteArrayContent("kept beyond EOF"u8.ToArray()));
        using BodyChunk retained = await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check("retained-chunk-after-eof", response.IsSuccessStatusCode && eof && Encoding.UTF8.GetString(retained.ToArray()) == "kept beyond EOF");
        await Until(() => ScopeWitness.Disposed == disposed + 1, "Ended request scope did not dispose");
        ServerResponse? stale = await oldResponse.Task;
        await Expect<InvalidOperationException>(() => stale.WriteAsync(new byte[] { 1 }));
        Check("ended-response-rejects-old-send-obligation", true); stale = null; oldResponse = null!;
        using var denied = await client.GetAsync("other");
        Check("retained-owner-keeps-active-permit", denied.StatusCode == HttpStatusCode.ServiceUnavailable);
        var pending = await app.CloseAsync(TimeSpan.Zero);
        Check("pending-close-keeps-charged-owner", pending.Status == ServerCloseStatus.Pending && pending.PendingRequests == 1
            && pending.RetainedNativeBytes == builder.Options.Budget.MaxChunkBytes,
            ("nativeBytes", pending.RetainedNativeBytes), ("pending", pending.PendingRequests));
        Task join = app.DisposeAsync().AsTask();
        Check("dispose-join-not-completed-before-owner-release", !join.IsCompleted);
        retained.Dispose(); retained.Dispose();
        await join.WaitAsync(TimeSpan.FromSeconds(5));
        Check("actual-dispose-joins-after-owner-release", app.State == ServerState.Disposed);
        WeakReference weak = await weakScope.Task;
        for (int i = 0; i < 5 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(20); }
        Check("ended-request-scope-weak-reference-released", !weak.IsAlive,
            ("observation", "one ended DI scope object; not aggregate managed roots"));
    }

    private static async Task CreditExhaustion(bool global)
    {
        var builder = Builder(); builder.Options.MaxJsonBytes = 64;
        builder.Options.Budget.NativePayloadBytes = global ? 65_536 : 131_072;
        builder.Options.Budget.ManagedPayloadBytes = 131_072;
        builder.Options.Budget.RetainedBytesPerRequest = global ? 131_072 : 65_536;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcome = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = builder.Build();
        app.MapRaw("/fill", ["POST"], async (context, token) =>
        {
            var chunks = new List<BodyChunk>();
            try
            {
                chunks.Add(await context.Request.ReadChunkAsync(token) ?? throw new InvalidOperationException("chunk1"));
                chunks.Add(await context.Request.ReadChunkAsync(token) ?? throw new InvalidOperationException("chunk2"));
                entered.TrySetResult();
                if (!global)
                {
                    try { await context.Response.WriteAsync(new byte[] { 1 }, token); outcome.TrySetResult("unexpected success"); }
                    catch (ServerException error) { outcome.TrySetResult(error.Code); }
                }
                await release.Task;
            }
            finally { foreach (var chunk in chunks) chunk.Dispose(); }
            // HTTP2 may split the upload into smaller frames than MaxChunkBytes.
            // Drain any remaining real request bytes after releasing the two owners,
            // so an early response/reset is not confused with the credit oracle.
            while (await context.Request.ReadChunkAsync(token) is { } remaining) remaining.Dispose();
        });
        app.MapRaw("/victim", ["GET"], async (context, token) =>
        {
            try { await context.Response.WriteAsync(new byte[] { 1 }, token); outcome.TrySetResult("unexpected success"); }
            catch (ServerException error) { outcome.TrySetResult(error.Code); }
        });
        app.MapGet("/control", () => Results.NoContent());
        await app.StartAsync(); using var client = Client(app);
        Task<HttpResponseMessage> filling = client.PostAsync("fill", new ByteArrayContent(new byte[65_536]));
        Task<HttpResponseMessage>? victim = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (global) victim = client.GetAsync("victim");
            string result = await outcome.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check((global ? "global" : "request") + "-immediate-resource-limit", result == "resource_limit" && !release.Task.IsCompleted,
                ("observedError", result), ("livenessWitness", "error delivered while all charged chunks are still held; no timeout substituted"));
            if (victim is not null)
            {
                // WriteAsync starts headers before its payload reservation. The failed
                // write therefore aborts that response by contract. A separate bodyless
                // response proves that the reserved control path still makes progress.
                using var control = await client.GetAsync("control").WaitAsync(TimeSpan.FromSeconds(3));
                Check("payload-free-completion-under-full-global-credit", control.StatusCode == HttpStatusCode.NoContent && !release.Task.IsCompleted);
            }
        }
        finally { release.TrySetResult(); }
        if (global) { using var done = await filling; Check("global-credit-holder-drains-after-release", done.IsSuccessStatusCode); }
        else await ExpectedCommittedWriteReset("request-credit-write", filling);
        if (victim is not null) await ExpectedCommittedWriteReset("global-credit-write", victim);
        Closed((global ? "global" : "request") + "-credit-retirement", await app.CloseAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task ExpectedCommittedWriteReset(string id, Task<HttpResponseMessage> request)
    {
        var failure = await Expect<HttpRequestException>(async () => { using var response = await request; });
        Check(id + "-post-header-abort", failure.InnerException is HttpProtocolException { ErrorCode: 2 },
            ("transport", "Actual HTTP2 INTERNAL_ERROR reset after headers; this is not a successful payload response"));
    }
}

internal sealed class PartialResult : ServerResult
{
    public override async Task ExecuteAsync(RequestContext context, CancellationToken cancellationToken = default)
    { context.Response.StatusCode = 207; await context.Response.WriteAsync("custom"u8.ToArray(), cancellationToken); }
}

[ServerEndpoints]
internal static partial class GeneratedProbe
{
    [ServerRoute("GET", "/generated-void")]
    private static Task Empty(RequestContext context) { context.Response.StatusCode = 203; return Task.CompletedTask; }
}
