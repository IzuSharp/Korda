using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using NativeWeave.Data;
using NativeWeave.Server;
using NativeWeave.Server.Grpc;
using NativeWeave.Server.Mcp;

namespace NativeWeave.Verification.EndToEnd;

internal sealed record TinyMessage(byte Value);
internal sealed record McpInput(string Text);
internal sealed record McpOutput(string Text, long Count);
[JsonSerializable(typeof(McpInput)), JsonSerializable(typeof(McpOutput))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, GenerationMode = JsonSourceGenerationMode.Metadata)]
internal partial class ProtocolJson : JsonSerializerContext;

internal static partial class Probe
{
    // Exact application protobuf: message Value { uint32 value = 1; }, bounded to0..127.
    // Tonic owns all gRPC framing, metadata, limits and HTTP2 transport.
    private static readonly GrpcMarshaller<TinyMessage> Tiny = new(
        static (value, writer) => { var span = writer.GetSpan(2); span[0] = 8; span[1] = value.Value; writer.Advance(2); },
        static bytes => bytes.Length == 0 ? new(0) : bytes.Length == 2 && bytes[0] == 8 && bytes[1] < 128
            ? new(bytes[1]) : throw new ArgumentException("Invalid bounded Value protobuf"));

    private static async Task ProtocolComposition()
    {
        await using var source = Source(); var builder = Builder();
        var sseCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = builder.Build();
        async Task<long> Count()
        {
            await using var session = await source.OpenSessionAsync();
            return await session.ScalarAsync(SqlCommand.Create("SELECT COUNT(*) FROM b22_api_tx"), Codecs.Int64);
        }
        var limits = new GrpcMethodOptions { MaxEncodedMessageBytes = 64, MaxDecodedMessageBytes = 64, MaxStreamBytes = 256, Timeout = TimeSpan.FromSeconds(5) };
        app.MapGrpcUnary(new GrpcMethod<TinyMessage, TinyMessage>("/b22.Probe/Metadata", GrpcMethodKind.Unary, Tiny, Tiny), async (message, call) =>
        {
            var decoded = call.RequestHeaders.GetBinaryValues("values-bin");
            var initial = new GrpcMetadata(); initial.Add("decoded-lengths", string.Join(',', decoded.Select(value => value.Length)));
            initial.AddBinary("initial-bin", [0, 3, 255]);
            await call.WriteResponseHeadersAsync(initial);
            call.ResponseTrailers.AddBinary("ending-bin", [4, 5]);
            return new(checked((byte)await Count()));
        }, limits);
        app.MapGrpcUnary(new GrpcMethod<TinyMessage, TinyMessage>("/b22.Probe/Guards", GrpcMethodKind.Unary, Tiny, Tiny), async (message, call) =>
        {
            var context = call.RequestContext; int rejected = 0;
            foreach (Action action in new Action[]
            {
                () => _ = context.Request.Body, () => _ = context.Request.GetTrailers(),
                () => _ = context.Response.Body, () => _ = context.Response.Headers, () => _ = context.Response.Trailers,
                () => _ = context.Response.StatusCode, () => context.Response.StatusCode = 201,
                () => _ = context.Response.ContentType, () => context.Response.ContentType = "text/plain",
                () => _ = context.Response.ContentLength, () => context.Response.ContentLength = 0,
                () => _ = context.Response.HasStarted, () => context.Response.OnStarting(_ => Task.CompletedTask)
            }) { await Expect<InvalidOperationException>(() => { action(); return Task.CompletedTask; }); rejected++; }
            foreach (Func<Task> action in new Func<Task>[]
            {
                async () => { await context.Request.ReadChunkAsync(); },
                async () => { await context.Request.ReadJsonAsync(ProtocolJson.Default.McpInput); },
                () => context.Response.StartAsync(), () => context.Response.WriteAsync(new byte[] { 1 }),
                () => context.Response.FlushAsync(), () => context.Response.CompleteAsync(),
                () => context.Response.WriteJsonAsync(new McpInput("x"), ProtocolJson.Default.McpInput),
                () => Results.Text("x").ExecuteAsync(context),
                () => Results.Json(new McpInput("x"), ProtocolJson.Default.McpInput).ExecuteAsync(context),
                () => Results.Bytes(new byte[] { 1 }).ExecuteAsync(context)
            }) { await Expect<InvalidOperationException>(action); rejected++; }
            Check("grpc-raw-http-route-restrictions", rejected == 23, ("rejectedPublicOperations", rejected));
            return new(checked((byte)await Count()));
        }, limits);
        var registry = new McpRegistry().AddTool("count", "Actual native database count", ProtocolJson.Default.McpInput, ProtocolJson.Default.McpOutput,
            async (input, _) => McpToolResult<McpOutput>.Success(new(input.Text, await Count())));
        app.MapMcp("/mcp", registry);
        app.MapGet("/events", () => Results.ServerSentEvents(Events(sseCancelled)));
        await app.StartAsync(); using var client = Client(app);
        byte[] wire = [0, 0, 0, 0, 2, 8, 7];
        foreach (var row in new[] { ("AQI, /w==", "2,1"), ("", "0"), ("AQI,", "2,0"), (",AQI", "0,2"), ("AQI, ,/w==", "2,0,1") })
        {
            var response = await GrpcWire(client, "Metadata", wire, [("values-bin", row.Item1)]);
            Check("grpc-binary-metadata-" + row.Item2, response.Status == 0 && response.Value == 5
                && response.Headers.GetValueOrDefault("decoded-lengths") == row.Item2
                && BinaryMetadataEquals(response.Headers.GetValueOrDefault("initial-bin"), [0, 3, 255])
                && BinaryMetadataEquals(response.Trailers.GetValueOrDefault("ending-bin"), [4, 5]),
                ("status", response.Status), ("lengths", response.Headers.GetValueOrDefault("decoded-lengths") ?? "missing"),
                ("initialWire", response.Headers.GetValueOrDefault("initial-bin") ?? "missing"),
                ("endingWire", response.Trailers.GetValueOrDefault("ending-bin") ?? "missing"));
        }
        foreach (string invalid in new[] { "%%%", "A===", "YQ=", "A QI" })
            Check("grpc-invalid-binary-metadata-" + invalid, (await GrpcWire(client, "Metadata", wire, [("values-bin", invalid)])).Status == 3);
        Check("grpc-route-guards-preserve-typed-result", (await GrpcWire(client, "Guards", wire)).Value == 5);
        Check("grpc-encoded-cap-native-rejection", (await GrpcWire(client, "Metadata", [0, 0, 0, 0, 65])).Status == 11);
        Check("grpc-identity-only-compressed-frame-rejection", (await GrpcWire(client, "Metadata", [1, 0, 0, 0, 0], [("grpc-encoding", "gzip")])).Status != 0,
            ("profile", "Identity only; no compressed/decompression-support pass is claimed"));
        using (var positive = await client.SendAsync(McpRequest("count")))
        {
            using var json = JsonDocument.Parse(await positive.Content.ReadAsStringAsync());
            Check("mcp-composed-native-database-result", positive.IsSuccessStatusCode && json.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("count").GetInt64() == 5);
        }
        using (var mismatch = McpRequest("count"))
        {
            mismatch.Headers.Remove("MCP-Protocol-Version"); mismatch.Headers.Add("MCP-Protocol-Version", "2099-01-01");
            using var denied = await client.SendAsync(mismatch);
            Check("mcp-protocol-profile-mismatch", denied.StatusCode == HttpStatusCode.BadRequest);
        }
        using (var cancellation = new CancellationTokenSource())
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "events") { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation.Token);
            var buffer = new byte[128]; int read = await stream.ReadAsync(buffer, cancellation.Token);
            Check("sse-positive-wire-event", read > 0 && Encoding.UTF8.GetString(buffer, 0, read).Contains("data:", StringComparison.Ordinal));
            cancellation.Cancel();
        }
        await sseCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check("sse-client-cancel-reaches-generator", true);
        Closed("optional-protocol-composition-close", await app.CloseAsync(TimeSpan.FromSeconds(5)));
    }

    private static async IAsyncEnumerable<ServerEvent> Events(TaskCompletionSource cancelled,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        try { yield return new("positive", "b22"); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        finally { cancelled.TrySetResult(); }
    }

    private sealed record GrpcWireResult(int Status, byte? Value, Dictionary<string, string> Headers, Dictionary<string, string> Trailers);
    private static bool BinaryMetadataEquals(string? wire, ReadOnlySpan<byte> expected)
    {
        if (wire is null) return false;
        // gRPC permits standard base64 with or without padding. The independent
        // client compares decoded bytes while retaining the original wire text.
        try { return Convert.FromBase64String(wire.PadRight((wire.Length + 3) / 4 * 4, '=')).AsSpan().SequenceEqual(expected); }
        catch (FormatException) { return false; }
    }

    private static async Task<GrpcWireResult> GrpcWire(HttpClient client, string method, byte[] bytes, (string, string)[]? extra = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/b22.Probe/" + method)
        { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact, Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("application/grpc"); request.Headers.TryAddWithoutValidation("te", "trailers");
        foreach (var entry in extra ?? []) request.Headers.TryAddWithoutValidation(entry.Item1, entry.Item2);
        using var response = await client.SendAsync(request); var body = await response.Content.ReadAsByteArrayAsync();
        if (response.Version != HttpVersion.Version20 || response.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException("gRPC transport was not actual HTTP2/200");
        var headers = response.Headers.ToDictionary(pair => pair.Key, pair => string.Join(',', pair.Value), StringComparer.OrdinalIgnoreCase);
        var trailers = response.TrailingHeaders.ToDictionary(pair => pair.Key, pair => string.Join(',', pair.Value), StringComparer.OrdinalIgnoreCase);
        string? status = trailers.GetValueOrDefault("grpc-status") ?? headers.GetValueOrDefault("grpc-status");
        if (!int.TryParse(status, out int code)) throw new InvalidOperationException("No actual gRPC terminal status");
        byte? value = body.Length == 7 && body[0] == 0 && BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1)) == 2 && body[5] == 8 ? body[6] : null;
        return new(code, value, headers, trailers);
    }

    private static HttpRequestMessage McpRequest(string name)
    {
        string json = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + name
            + "\",\"arguments\":{\"text\":\"bounded\"},\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{}}}}";
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact, Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("Accept", "application/json, text/event-stream"); request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", "tools/call"); request.Headers.Add("Mcp-Name", name); return request;
    }

    private static async Task McpPressure(bool cancel)
    {
        await using var source = Source();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int business = 0, completed = 0, disposed = ScopeWitness.Disposed;
        var builder = Builder(); builder.Options.Budget.NativePayloadBytes = 4 * 1024 * 1024;
        builder.Options.Budget.ManagedPayloadBytes = 4 * 1024 * 1024;
        builder.Options.Budget.MaxActiveRequests = 4; builder.Options.Budget.MaxQueuedRequests = 0;
        builder.Services.AddScoped(_ => new ScopeWitness());
        await using var app = builder.Build();
        app.Use(async (context, next, token) =>
        {
            _ = context.Services.GetRequiredService<ScopeWitness>();
            context.Response.OnCompleted(() => { Interlocked.Increment(ref completed); return Task.CompletedTask; });
            await next(context, token);
        });
        app.MapMcp("/mcp", new McpRegistry().AddTool("hold", "Hold admitted ownership", ProtocolJson.Default.McpInput, ProtocolJson.Default.McpOutput,
            async (input, context) =>
            {
                Interlocked.Increment(ref business);
                long count; await using (var session = await source.OpenSessionAsync())
                    count = await session.ScalarAsync(SqlCommand.Create("SELECT COUNT(*) FROM b22_api_tx"), Codecs.Int64);
                using var registration = context.CancellationToken.Register(() => cancelled.TrySetResult());
                entered.TrySetResult(); await release.Task;
                return McpToolResult<McpOutput>.Success(new(input.Text, count));
            }));
        await app.StartAsync(); using var client = Client(app); using var cancellation = new CancellationTokenSource();
        using var first = McpRequest("hold"); Task<HttpResponseMessage> active = client.SendAsync(first, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!cancel)
            {
                using var second = McpRequest("hold"); using var denied = await client.SendAsync(second);
                Check("mcp-global-managed-native-peak-immediate-limit", denied.StatusCode == HttpStatusCode.ServiceUnavailable
                    && business == 1 && !release.Task.IsCompleted,
                    ("livenessWitness", "second request rejected while admitted first callback remains behind release barrier"));
            }
            else
            {
                cancellation.Cancel(); await Expect<OperationCanceledException>(async () => { using var response = await active; });
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var pending = await app.CloseAsync(TimeSpan.Zero);
                Check("late-cancellation-keeps-managed-native-overlap", pending.Status == ServerCloseStatus.Pending
                    && pending.PendingRequests > 0 && pending.RetainedManagedBytes > 0 && pending.RetainedNativeBytes > 0
                    && completed == 0 && ScopeWitness.Disposed == disposed,
                    ("managed", pending.RetainedManagedBytes), ("native", pending.RetainedNativeBytes), ("pending", pending.PendingRequests));
            }
        }
        finally { release.TrySetResult(); }
        if (!cancel) { using var response = await active; Check("mcp-positive-pressure-recovery", response.IsSuccessStatusCode); }
        Closed("mcp-pressure-close-" + cancel, await app.CloseAsync(TimeSpan.FromSeconds(5)));
        Check("mcp-request-scope-retirement-" + cancel, completed >= 1 && ScopeWitness.Disposed >= disposed + 1);
    }
}
