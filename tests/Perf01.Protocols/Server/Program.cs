using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using NativeWeave.Perf01;
using NativeWeave.Perf01.Wire;
using NativeWeave.Server;
using NativeWeave.Server.Grpc;

var config = JsonSerializer.Deserialize(File.ReadAllText(Inputs.Argument(args, "--config")), WireJson.Default.ServerConfig)!;
string output = Inputs.Argument(args, "--output");
Directory.CreateDirectory(output);
if (RuntimeFeature.IsDynamicCodeSupported || RuntimeFeature.IsDynamicCodeCompiled)
    throw new InvalidOperationException("Perf-01 server measurements require an actual Native AOT executable.");
var builder = NativeServer.CreateBuilder();
foreach (var protocol in new[] { ServerProtocols.Http1, ServerProtocols.Http2 })
    builder.Listen(IPAddress.Loopback, 0, options =>
    {
        options.Protocols = protocol;
        options.Tls = ServerTlsOptions.FromPemFiles(config.Certificate, config.PrivateKey);
    });
builder.Options.MaxRequestBodyBytes = config.MaxStreamBytes;
builder.Options.MaxJsonBytes = config.MaxJsonBytes;
builder.Options.RequestTimeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds);
builder.Options.ShutdownTimeout = TimeSpan.FromSeconds(config.ShutdownTimeoutSeconds);
builder.Options.Budget = new()
{
    MaxChunkBytes = config.ChunkBytes, MaxConnections = config.MaxConnections,
    MaxHttp2StreamsPerConnection = config.MaxHttp2StreamsPerConnection,
    MaxActiveRequests = config.MaxActiveRequests, MaxQueuedRequests = config.MaxQueuedRequests,
    ManagedPayloadBytes = config.ManagedPayloadBytes, NativePayloadBytes = config.NativePayloadBytes,
    RetainedBytesPerRequest = config.RetainedBytesPerRequest
};
builder.UseAuthentication(new Authentication()).UseAuthorization(new Authorization());
await using var application = builder.Build();
byte[] tiny = "0123456789abcdef"u8.ToArray();
ServerResult tinyResult = Results.Bytes(tiny);
application.MapGet("/tiny", context => { context.Response.ContentLength = tiny.Length; return tinyResult; }).RequireAuthorization("benchmark");
application.MapGet("/json", () => Results.Json(new SmallReply("perf01", 42), WireJson.Default.SmallReply)).RequireAuthorization("benchmark");
foreach (int size in new[] { 1048576, 8388608 })
{
    byte[] payload = Inputs.Bytes(size);
    string digest = Convert.ToHexStringLower(SHA256.HashData(payload));
    application.MapGet("/download/" + size.ToString(CultureInfo.InvariantCulture), context =>
    {
        context.Response.ContentLength = payload.Length;
        context.Response.Headers.Set("x-content-sha256", digest);
        return Results.Stream(new MemoryStream(payload, writable: false));
    }).RequireAuthorization("benchmark");
}
application.MapPut("/upload", async (context, token) =>
{
    if (!long.TryParse(context.Request.Headers.GetSingle("content-length"), CultureInfo.InvariantCulture, out long expected)
        || expected < 0 || expected > config.MaxStreamBytes) return Results.StatusCode(400);
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    byte[] buffer = new byte[config.ChunkBytes]; long total = 0; int read;
    while ((read = await context.Request.Body.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
    {
        total = checked(total + read);
        if (total > expected) return Results.StatusCode(400);
        hash.AppendData(buffer, 0, read);
    }
    if (total != expected) return Results.StatusCode(400);
    return Results.Json(new TransferReply(total, Convert.ToHexStringLower(hash.GetHashAndReset())), WireJson.Default.TransferReply);
}).RequireAuthorization("benchmark");
new GrpcService(config).Map(application);
using var observations = new Observations(Path.Combine(output, "managed-resources.jsonl"));
await application.StartAsync();
var loaded = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (string line in File.ReadLines("/proc/self/maps"))
{
    int separator = line.IndexOf('/');
    if (separator < 0) continue;
    string path = line[separator..];
    if (Path.GetFileName(path).Contains("nativeweave", StringComparison.Ordinal) && File.Exists(path))
        loaded[path] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
if (!loaded.Keys.Any(path => Path.GetFileName(path) == "libnativeweave_server.so"))
    throw new InvalidOperationException("Loaded Server module was not found in /proc/self/maps.");
var ready = new Ready(application.BoundAddresses.Select(uri => uri.ToString()).ToArray(), RuntimeFeature.IsDynamicCodeSupported, loaded);
Console.WriteLine("READY " + JsonSerializer.Serialize(ready, WireJson.Default.Ready));
Console.Out.Flush();
observations.Capture("ready");
using var sampling = new CancellationTokenSource();
Task sample = observations.SampleAsync(sampling.Token);
while (await Console.In.ReadLineAsync() is { } command)
{
    if (command == "stop") break;
    observations.Phase = command;
    observations.Capture(command);
}
observations.Phase = "drain";
var close = await application.CloseAsync(TimeSpan.FromSeconds(config.ShutdownTimeoutSeconds));
await application.DisposeAsync();
await sampling.CancelAsync();
await sample;
observations.Capture("disposed");
var closed = new Closed(close.Status.ToString(), close.PendingRequests, close.RetainedManagedBytes, close.RetainedNativeBytes, application.State.ToString());
File.WriteAllText(Path.Combine(output, "closed.json"), JsonSerializer.Serialize(closed, WireJson.Default.Closed));
Console.WriteLine("CLOSED " + JsonSerializer.Serialize(closed, WireJson.Default.Closed));
if (close.Status != ServerCloseStatus.Completed || close.PendingRequests != 0 || close.RetainedManagedBytes != 0 || close.RetainedNativeBytes != 0)
    throw new InvalidOperationException("Server close did not prove retirement.");

internal sealed class Authentication : IServerAuthenticator
{
    private static readonly byte[] Expected = Encoding.UTF8.GetBytes(Inputs.Bearer);
    public Task<ClaimsPrincipal?> AuthenticateAsync(RequestContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? value = context.Request.Headers.GetSingle("authorization");
        bool valid = value is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(value), Expected);
        return Task.FromResult(valid ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "perf01")], "synthetic-bearer")) : null);
    }
}
internal sealed class Authorization : IServerAuthorizer
{
    public Task<bool> AuthorizeAsync(RequestContext context, string policy, CancellationToken cancellationToken = default)
        => Task.FromResult(policy == "benchmark" && context.User.Identity?.IsAuthenticated == true);
}
internal sealed class GrpcService(ServerConfig config) : IGeneratedGrpcService
{
    private static readonly GrpcMarshaller<Payload> Codec = new(static (value, writer) => value.WriteTo(writer), static bytes => Payload.Parser.ParseFrom(bytes));
    private static GrpcMethod<Payload, Payload> Method(string name, GrpcMethodKind kind)
        => new("/nativeweave.perf01.Benchmark/" + name, kind, Codec, Codec);
    private GrpcMethodOptions Limits() => new()
    { MaxEncodedMessageBytes = config.MaxMessageBytes, MaxDecodedMessageBytes = config.MaxMessageBytes, MaxStreamBytes = config.MaxStreamBytes, Timeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds) };
    public void Map(ServerRoutes routes)
    {
        routes.MapGrpcUnary(Method("Unary", GrpcMethodKind.Unary), static (value, call) =>
        { Finish(call, 1, 1); return Task.FromResult(value); }, Limits()).RequireAuthorization("benchmark");
        routes.MapGrpcServerStreaming(Method("Server", GrpcMethodKind.ServerStreaming), Server, Limits()).RequireAuthorization("benchmark");
        routes.MapGrpcClientStreaming(Method("Client", GrpcMethodKind.ClientStreaming), Client, Limits()).RequireAuthorization("benchmark");
        routes.MapGrpcDuplex(Method("Duplex", GrpcMethodKind.DuplexStreaming), Duplex, Limits()).RequireAuthorization("benchmark");
        routes.MapGrpcServerStreaming(Method("Wait", GrpcMethodKind.ServerStreaming), Wait, Limits()).RequireAuthorization("benchmark");
    }
    private static void Finish(GrpcCallContext call, long inputs, long outputs)
    {
        call.ResponseTrailers.Add("perf-completed", "yes");
        call.ResponseTrailers.Add("perf-inputs", inputs.ToString(CultureInfo.InvariantCulture));
        call.ResponseTrailers.Add("perf-outputs", outputs.ToString(CultureInfo.InvariantCulture));
    }
    private static async IAsyncEnumerable<Payload> Server(Payload value, GrpcCallContext call)
    {
        if (value.Count is < 1 or > 32) throw new GrpcException(GrpcStatusCode.InvalidArgument, "Count is outside the benchmark profile.");
        for (int i = 0; i < value.Count; i++)
        {
            call.CancellationToken.ThrowIfCancellationRequested();
            yield return new Payload { Sequence = i, Data = value.Data, Count = value.Count };
        }
        Finish(call, 1, value.Count);
        await Task.CompletedTask;
    }
    private static async Task<Payload> Client(IAsyncEnumerable<Payload> values, GrpcCallContext call)
    {
        long total = 0, count = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await foreach (var value in values.WithCancellation(call.CancellationToken).ConfigureAwait(false))
        {
            if (value.Sequence != count || count >= 32) throw new GrpcException(GrpcStatusCode.InvalidArgument, "Message ordering or count differs.");
            hash.AppendData(value.Data.Span); total += value.Data.Length; count++;
        }
        Finish(call, count, 1);
        return new Payload { Count = checked((int)count), TotalBytes = total, Digest = Convert.ToHexStringLower(hash.GetHashAndReset()) };
    }
    private static async IAsyncEnumerable<Payload> Duplex(IAsyncEnumerable<Payload> values, GrpcCallContext call)
    {
        long count = 0;
        await foreach (var value in values.WithCancellation(call.CancellationToken).ConfigureAwait(false))
        {
            if (value.Sequence != count || count >= 32) throw new GrpcException(GrpcStatusCode.InvalidArgument, "Message ordering or count differs.");
            count++; yield return value;
        }
        Finish(call, count, count);
    }
    private static async IAsyncEnumerable<Payload> Wait(Payload value, GrpcCallContext call)
    {
        var headers = new GrpcMetadata(); headers.Add("perf-started", "yes");
        await call.WriteResponseHeadersAsync(headers, call.CancellationToken);
        yield return value;
        await Task.Delay(Timeout.InfiniteTimeSpan, call.CancellationToken);
    }
}
internal sealed class Observations : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, long> _gauges = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    internal string Phase = "startup";
    internal Observations(string path)
    {
        _writer = new StreamWriter(path) { AutoFlush = true };
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "NativeWeave.Server" && instrument is ObservableGauge<long> or ObservableCounter<long>)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => { lock (_gate) _gauges[instrument.Name] = value; });
        _listener.Start();
    }
    internal void Capture(string phase)
    {
        lock (_gate)
        {
            _listener.RecordObservableInstruments();
            var memory = GC.GetGCMemoryInfo();
            var snapshot = new ResourceSnapshot(Stopwatch.GetTimestamp(), Stopwatch.Frequency, DateTimeOffset.UtcNow.ToString("O"), GC.GetTotalAllocatedBytes(false),
                memory.HeapSizeBytes, memory.TotalCommittedBytes, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), new(_gauges), phase);
            _writer.WriteLine(JsonSerializer.Serialize(snapshot, WireJson.Default.ResourceSnapshot));
        }
    }
    internal async Task SampleAsync(CancellationToken token)
    {
        try { using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1)); while (await timer.WaitForNextTickAsync(token)) Capture(Phase); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    public void Dispose() { _listener.Dispose(); _writer.Dispose(); }
}
