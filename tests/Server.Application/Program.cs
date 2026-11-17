using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NativeWeave.Server;

if (args.Contains("--real")) { await RealApplicationAcceptance.RunAsync(); return; }

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
void Throws<T>(Action action) where T : Exception
{ try { action(); } catch (T) { checks++; return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{ try { await action(); } catch (T) { checks++; return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
var builder = NativeServer.CreateBuilder(["--port=7"]);
Check(builder.Configuration["port"] == "7", "Explicit arguments");
Check(builder.Options.RequestTimeout == TimeSpan.FromSeconds(30), "Timeout default");
_ = ServerConfiguration.Snapshot(builder.Options); checks++;
builder.Configure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MaxJsonBytes"] = "1024", ["Budget:MaxQueuedRequests"] = "0" }).Build());
Check(builder.Options.MaxJsonBytes == 1024 && builder.Options.Budget.MaxQueuedRequests == 0, "Static configuration binding");
Throws<ArgumentException>(() => builder.Configure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Unknown"] = "1" }).Build()));
Throws<ArgumentOutOfRangeException>(() => builder.Listen(IPAddress.Loopback, -1));
Throws<ArgumentException>(() => Results.StatusCode(101));
Throws<ArgumentException>(() => Results.Text("no", statusCode: 204));
Throws<ArgumentException>(() => Results.Redirect("/ok\r\nInjected: yes"));
Throws<ArgumentOutOfRangeException>(() => Results.Bytes(new byte[1048577]));

async Task<(NativeServerApplication app, FakeRequest request)> Execute(Action<NativeServerApplication> map, string method = "GET", byte[]? input = null, int route = 1)
{
    var config = new ServerConfiguration(ServerConfiguration.Snapshot(new ServerOptions()), [], [TestJsonContext.Default], null, null);
    var provider = new ServiceCollection().AddScoped<ScopedProbe>().BuildServiceProvider();
    var host = new FakeHost(); var app = new NativeServerApplication(config, host, provider, true);
    map(app); await app.StartAsync();
    var request = new FakeRequest(route, method, input ?? []); await app.DispatchAsync(request);
    return (app, request);
}

var tail = await Execute(app => app.MapRaw("/", ["GET"], static (_, _) => Task.CompletedTask));
Check(tail.request.Status == 200 && tail.request.Finishes == 1 && tail.request.Completions == 1, "Untouched raw handler must finish exactly once");
await tail.app.DisposeAsync();
var sequence = new List<string>();
RequestContext? escaped = null;
var lifecycle = await Execute(app =>
{
    app.MapRaw("/", ["GET"], (context, _) =>
    {
        escaped = context; context.Response.StatusCode = 201;
        context.Response.OnStarting(_ => { sequence.Add("starting1"); return Task.CompletedTask; });
        context.Response.OnStarting(_ => { sequence.Add("starting2"); return Task.CompletedTask; });
        context.Response.OnCompleted(() => { sequence.Add("completed1"); return Task.CompletedTask; });
        context.Response.OnCompleted(() => { sequence.Add("completed2"); throw new InvalidOperationException("cleanup fault"); });
        context.Response.RegisterForDisposeAsync(new DisposeProbe(() => sequence.Add("dispose1")));
        context.Response.RegisterForDisposeAsync(new DisposeProbe(() => { sequence.Add("dispose2"); throw new InvalidOperationException("dispose fault"); }));
        return Task.CompletedTask;
    });
});
Check(lifecycle.request.Status == 201 && lifecycle.request.Finishes == 1, "Preserve selected 201");
Check(string.Join(',', sequence) == "starting2,starting1,completed2,completed1,dispose2,dispose1", "LIFO isolated cleanup order");
Check(lifecycle.request.Completions == 1, "Always dispatch complete");
Throws<ObjectDisposedException>(() => _ = escaped!.Services);
Throws<ObjectDisposedException>(() => _ = escaped!.Request.Method);
Throws<ObjectDisposedException>(() => _ = escaped!.Response.StatusCode);
await lifecycle.app.DisposeAsync();

var failStarting = await Execute(app => app.MapRaw("/", ["GET"], (context, _) =>
{
    context.Response.OnStarting(_ => throw new InvalidOperationException("start failure"));
    context.Response.OnCompleted(() => { checks++; return Task.CompletedTask; });
    return Task.CompletedTask;
}));
Check(failStarting.request.Status == 500 && failStarting.request.Heads == 1 && failStarting.request.Finishes == 1, "One bounded precommit fallback");
await failStarting.app.DisposeAsync();

var middleware = await Execute(app => { app.Use(static (context, _, _) => { context.Response.StatusCode = 202; return Task.CompletedTask; }); app.MapGet("/", static () => throw new Exception("must not invoke")); });
Check(middleware.request.Status == 202 && middleware.request.Finishes == 1, "Short circuit common tail");
await middleware.app.DisposeAsync();

var count = 0;
var head = await Execute(app => app.MapGet("/", () => Results.Stream((_, _) => { count++; return Task.CompletedTask; })), "HEAD");
Check(count == 0 && head.request.Output.Length == 0 && head.request.Finishes == 1, "HEAD suppresses producer");
await head.app.DisposeAsync();
var source = new TrackedStream("input"u8.ToArray());
var retainedResult = Results.Stream(source);
Check(!source.Disposed, "Unexecuted result retains caller ownership");
var headSource = await Execute(app => app.MapGet("/", () => retainedResult), "HEAD");
Check(source.Disposed && source.Reads == 0, "HEAD admits then disposes source without reading");
await headSource.app.DisposeAsync();

var text = await Execute(app => app.MapGet("/", () => Results.Text(new string('é', 80000))));
Check(text.request.Output.Length == 160000 && text.request.Finishes == 1 && text.request.BodyImpl.PeakManaged <= 65536, "Text uses bounded encoding plus write overlap");
await text.app.DisposeAsync();
var json = await Execute(app => app.MapPost<Message, Message>("/", (value, _) => Task.FromResult(new Message(value.Value + "!")), TestJsonContext.Default.Message, TestJsonContext.Default.Message), "POST", "{\"value\":\"hello\"}"u8.ToArray());
Check(Encoding.UTF8.GetString(json.request.Output.ToArray()).Contains("hello!", StringComparison.Ordinal), "Actual static JSON read/write");
Check(json.request.BodyImpl.CurrentManaged == 0 && json.request.Finishes == 1, "JSON credits balance");
await json.app.DisposeAsync();
var jsonNull = await Execute(app => app.MapPost<Message, Message>("/", (value, _) => Task.FromResult(value), TestJsonContext.Default.Message, TestJsonContext.Default.Message), "POST", "null"u8.ToArray());
Check(jsonNull.request.Status == 400 && jsonNull.request.Heads == 1, "Required JSON null rejects binding");
await jsonNull.app.DisposeAsync();

var caughtBinding = await Execute(app => app.MapPost<Message, Message>("/", (value, _) => Task.FromResult(value), TestJsonContext.Default.Message, TestJsonContext.Default.Message)
    .AddFilter(async (context, next, token) => { try { return await next(context, token); } catch (RequestBindingException) { return Results.Text("custom", statusCode: 422); } }), "POST", "null"u8.ToArray());
Check(caughtBinding.request.Status == 422 && Encoding.UTF8.GetString(caughtBinding.request.Output.ToArray()) == "custom", "Result filter can handle binding failure without rethrow");
await caughtBinding.app.DisposeAsync();
var nextEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var releaseNext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Task? unawaited = null;
var escapedNextRun = Execute(app =>
{
    app.Use((context, next, token) => { unawaited = next(context, token); return Task.CompletedTask; });
    app.MapRaw("/", ["GET"], async (_, _) => { nextEntered.TrySetResult(); await releaseNext.Task; });
});
await nextEntered.Task;
Check(!escapedNextRun.IsCompleted, "Escaped next still retains dispatch lifetime");
releaseNext.TrySetResult(); var escapedNext = await escapedNextRun;
Check(unawaited!.IsCompleted && escapedNext.request.Status == 500 && escapedNext.request.Completions == 1, "Escaped next fails after safe join");
await escapedNext.app.DisposeAsync();

var generated = await Execute(GeneratedEndpoints.Map, "POST", "{\"value\":\"generated\"}"u8.ToArray());
Check(generated.request.Status == 200 && Encoding.UTF8.GetString(generated.request.Output.ToArray()).Contains("generated", StringComparison.Ordinal), "Source-generated body/DTO registration uses declared static context");
await generated.app.DisposeAsync();

var duplicates = new NativeServerApplication(new(ServerConfiguration.Snapshot(new()), [], [], null, null), new FakeHost(), new ServiceCollection().BuildServiceProvider(), true);
duplicates.MapGet("/item/{id}", static () => Results.NoContent());
Throws<InvalidOperationException>(() => duplicates.MapGet("/item/{name}", static () => Results.NoContent()));
Throws<ArgumentException>(() => duplicates.MapGet("/item/{id:int}", static () => Results.NoContent()));
await duplicates.StartAsync(); Throws<InvalidOperationException>(() => duplicates.MapGet("/later", static () => Results.NoContent()));
await ThrowsAsync<InvalidOperationException>(() => duplicates.StartAsync());
await duplicates.DisposeAsync();

var repeated = new ParameterValues(static () => { }, new Dictionary<string, IReadOnlyList<string>> { ["one"] = ["12"], ["many"] = ["1", "2"], ["bad"] = ["no"] });
Check(repeated.Get<int>("one") == 12, "Invariant typed parameter");
Check(!repeated.TryGet<int>("many", out _) && !repeated.TryGet<int>("bad", out _), "Repeated/malformed TryGet");
Throws<RequestBindingException>(() => repeated.Get<int>("none"));

var self = await Execute(app => app.MapRaw("/", ["GET"], async (_, _) => await app.DisposeAsync()));
Check(self.request.Status == 500 && self.request.Completions == 1, "Self dispose fails without deadlock");
await self.app.DisposeAsync();
Console.WriteLine($"B15 managed application component: {checks} assertions passed; fake transport scope, actual B17 acceptance required.");

internal sealed class FakeHost : ServerHost
{
    private readonly TaskCompletionSource _lifetime = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal override Task Lifetime => _lifetime.Task;
    internal override ServerCapabilities Capabilities => ServerCapabilities.Http1 | ServerCapabilities.Http2 | ServerCapabilities.Trailers;
    internal override IReadOnlyList<Uri> BoundAddresses => [];
    internal override Task StartAsync(IReadOnlyList<RouteDefinition> routes, Func<HostRequest, Task> dispatch, CancellationToken cancellationToken) => Task.CompletedTask;
    internal override Task<ServerCloseResult> CloseAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(new ServerCloseResult(ServerCloseStatus.Completed, 0, 0, 0));
    internal override Task ReloadTlsAsync(string listenerName, TlsSnapshot options, CancellationToken cancellationToken) => Task.CompletedTask;
    internal override void ReleaseUnstarted() { }
    public override ValueTask DisposeAsync() { _lifetime.TrySetResult(); return ValueTask.CompletedTask; }
}
internal sealed class FakeRequest(long route, string method, byte[] input) : HostRequest
{
    internal int Status, Heads, Finishes, Completions;
    internal MemoryStream Output { get; } = new();
    private FakeBody? _body;
    internal FakeBody BodyImpl => _body ??= new(this, input);
    internal override ServerBodyTransport Body => BodyImpl;
    internal override long RouteId => route;
    internal override int Kind => 0;
    internal override string Method => method;
    internal override string Scheme => "http";
    internal override string Host => "localhost";
    internal override string Path => "/";
    internal override string QueryString => "";
    internal override string Protocol => "HTTP/1.1";
    internal override IPEndPoint? RemoteEndPoint => new(IPAddress.Loopback, 1234);
    internal override IReadOnlyDictionary<string, IReadOnlyList<string>> Headers => new Dictionary<string, IReadOnlyList<string>> { ["Content-Type"] = ["application/json"] };
    internal override IReadOnlyDictionary<string, IReadOnlyList<string>> RouteValues => new Dictionary<string, IReadOnlyList<string>>();
    internal override IReadOnlyDictionary<string, IReadOnlyList<string>> Query => new Dictionary<string, IReadOnlyList<string>>();
    internal override CancellationToken RequestAborted => default;
    internal override TimeSpan TimeRemaining => TimeSpan.FromSeconds(30);
    internal override string TraceIdentifier => "component";
    internal override IReadOnlyDictionary<string, IReadOnlyList<string>> GetTrailers() => new Dictionary<string, IReadOnlyList<string>>();
    internal override Task CommitHeadersAsync(int status, IReadOnlyDictionary<string, IReadOnlyList<string>> headers, CancellationToken cancellationToken) { Status = status; Heads++; return Task.CompletedTask; }
    internal override Task CommitTrailersAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> trailers, CancellationToken cancellationToken) => Task.CompletedTask;
    internal override Task WriteServerEventAsync(ServerEvent value, CancellationToken cancellationToken) => Task.CompletedTask;
    internal override void Abort() { }
    internal override Task CompleteDispatchAsync() { Completions++; BodyImpl.Ended = true; return Task.CompletedTask; }
}
internal sealed class FakeBody(FakeRequest request, byte[] input) : ServerBodyTransport
{
    internal bool Ended;
    internal int CurrentManaged, PeakManaged;
    private int _offset;
    internal override int MaxChunkBytes => 32768;
    internal override void ThrowIfUnavailable() => ObjectDisposedException.ThrowIf(Ended, this);
    internal override Task<BodyChunk?> ReadChunkAsync(int maximumBytes, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable(); cancellationToken.ThrowIfCancellationRequested();
        if (_offset == input.Length) return Task.FromResult<BodyChunk?>(null);
        int count = Math.Min(maximumBytes, input.Length - _offset);
        var chunk = new BodyChunk(new ManagedChunkOwner(input.AsSpan(_offset, count).ToArray(), this)); _offset += count;
        return Task.FromResult<BodyChunk?>(chunk);
    }
    internal override Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) { request.Output.Write(data.Span); return Task.CompletedTask; }
    internal override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    internal override Task FinishAsync(CancellationToken cancellationToken) { request.Finishes++; return Task.CompletedTask; }
    internal override IDisposable AcquireManagedCredit(int capacity)
    { if (capacity > 4194304 - CurrentManaged) throw new InvalidOperationException("credit exhausted"); CurrentManaged += capacity; PeakManaged = Math.Max(PeakManaged, CurrentManaged); return new Credit(() => CurrentManaged -= capacity); }
    internal override void CloseRead() { }
    internal override void CloseWrite() { }
    private sealed class Credit(Action release) : IDisposable { public void Dispose() => release(); }
}
internal sealed class ManagedChunkOwner : BodyChunkOwner
{
    private readonly byte[] _bytes; private readonly FakeBody _body;
    internal ManagedChunkOwner(byte[] bytes, FakeBody body) : base(new TestHandle(), bytes.Length) { _bytes = bytes; _body = body; }
    protected override void CopyLeased(nint pointer, Span<byte> destination) => _bytes.CopyTo(destination);
    protected override IDisposable AcquireManagedCreditLeased(nint pointer, int capacity) => _body.AcquireManagedCredit(capacity);
    private sealed class TestHandle : System.Runtime.InteropServices.SafeHandle
    { internal TestHandle() : base(0, true) => SetHandle(1); public override bool IsInvalid => handle == 0; protected override bool ReleaseHandle() { handle = 0; return true; } }
}
internal sealed class DisposeProbe(Action action) : IAsyncDisposable { public ValueTask DisposeAsync() { action(); return ValueTask.CompletedTask; } }
internal sealed class ScopedProbe;
internal sealed class TrackedStream(byte[] bytes) : MemoryStream(bytes)
{
    internal bool Disposed; internal int Reads;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { Reads++; return base.ReadAsync(buffer, cancellationToken); }
    public override ValueTask DisposeAsync() { Disposed = true; return base.DisposeAsync(); }
}
internal sealed record Message([property: JsonPropertyName("value")] string Value);
[JsonSerializable(typeof(Message))]
internal partial class TestJsonContext : JsonSerializerContext;

[ServerEndpoints]
internal static partial class GeneratedEndpoints
{
    [ServerRoute("POST", "/")]
    private static Message Echo([BodyParameter] Message input, CancellationToken token) { token.ThrowIfCancellationRequested(); return input; }
}
