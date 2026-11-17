using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace NativeWeave.Server;

/// <summary>Borrowed request context, valid through awaited dispatch and cleanup; never capture it in background work.</summary>
/// <remarks>Borrowed request lifecycle; available cancellation signal, not exact upstream H2 reset certification. gRPC permits metadata/services/identity/Items/cancel/Abort and lifecycle hooks only; raw body/output and OnStarting are rejected. MCP has only its pre-parse HTTP denial exception. Lazy scope/Items (&lt;=128 entries); sticky bounded control notification; TimeRemaining is monotonic; no escaped context after dispatch. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
public sealed class RequestContext
{
    private readonly NativeServerApplication _application;
    private AsyncServiceScope? _scope;
    private bool _ended;
    private RequestItems? _items;
    private ClaimsPrincipal _user = new(new ClaimsIdentity());
    internal HostRequest Native { get; }
    internal RouteDefinition Route { get; }
    internal object? ProtocolState { get; set; }
    internal bool ProtocolHttpAllowed { get; set; } = true;
    internal ServerConfiguration Configuration => _application.Configuration;
    internal RequestContext(NativeServerApplication application, HostRequest native, RouteDefinition route)
    {
        _application = application; Native = native; Route = route;
        Request = new(this); Response = new(this);
    }
    /// <summary>Gets request metadata and ordinary HTTP body APIs.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.RequestContext" path="/member/remarks" />
    public ServerRequest Request { get; }
    /// <summary>Gets the response facade; protocol routes restrict raw HTTP access.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.RequestContext" path="/member/remarks" />
    public ServerResponse Response { get; }
    /// <summary>Gets lazily created request-scoped services.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.RequestContext" path="/member/remarks" />
    public IServiceProvider Services { get { Alive(); _scope ??= _application.Services.CreateAsyncScope(); return _scope.Value.ServiceProvider; } }
    /// <summary>Gets the authenticated principal, initially empty.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.RequestContext" path="/member/remarks" />
    public ClaimsPrincipal User { get { Alive(); return _user; } }
    /// <summary>Gets sticky native disconnect, deadline and shutdown cancellation intent.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.RequestContext" path="/member/remarks" />
    public CancellationToken RequestAborted { get { Alive(); return Native.RequestAborted; } }
    /// <summary>Gets the native monotonic whole-request time remaining.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.RequestContext" path="/member/remarks" />
    public TimeSpan TimeRemaining { get { Alive(); return Native.TimeRemaining; } }
    /// <summary>Gets a bounded correlation identifier without authorization meaning.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.RequestContext" path="/member/remarks" />
    public string TraceIdentifier { get { Alive(); return Native.TraceIdentifier; } }
    /// <summary>Gets lazy request storage capped at 128 entries.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.RequestContext" path="/member/remarks" />
    public IDictionary<object, object?> Items { get { Alive(); return _items ??= new(Alive); } }
    /// <summary>Requests transport abort without claiming native retirement.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.RequestContext" path="/member/remarks" />
    public void Abort() { Alive(); Native.Abort(); }
    internal void SetUser(ClaimsPrincipal user) { Alive(); _user = user; }
    internal void Alive() { ObjectDisposedException.ThrowIf(_ended, this); }
    internal void Http() { Alive(); if (Native.Kind != 0) throw new InvalidOperationException("Raw HTTP input is unavailable for protocol requests."); }
    internal void HttpResponse() { Alive(); if (Native.Kind != 0 && !(Native.Kind == 2 && ProtocolHttpAllowed)) throw new InvalidOperationException("Raw HTTP output is unavailable after protocol admission."); }
    internal async ValueTask EndScopeAsync()
    { try { if (_scope is { } scope) await scope.DisposeAsync().ConfigureAwait(false); } finally { _ended = true; } }
}

/// <summary>Read-only native HTTP metadata with one bounded body-consumption mode and request-scoped lifetime.</summary>
/// <remarks>Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Raw Body/ReadChunk/ReadJson/GetTrailers are rejected for gRPC/MCP before consumption or owner movement. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. Native read payload grant is a nonwaiting request/global try; failure returns resource_limit before new payload allocation/retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
public sealed class ServerRequest
{
    private readonly RequestContext _context;
    private readonly ServerBodyReadGate _gate = new();
    private Stream? _stream;
    private bool _eof;
    internal ServerRequest(RequestContext context)
    {
        _context = context;
        Headers = new(context.Alive, null, context.Configuration.Options.MaxHeaderBytes, values: context.Native.Headers);
        RouteValues = new(context.Alive, context.Native.RouteValues); Query = new(context.Alive, context.Native.Query);
    }
    /// <summary>Gets the validated case-sensitive method token.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.ServerRequest" path="/member/remarks" />
    public string Method { get { _context.Alive(); return _context.Native.Method; } }
    /// <summary>Gets the effective scheme after native trusted-forwarding validation.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.ServerRequest" path="/member/remarks" />
    public string Scheme { get { _context.Alive(); return _context.Native.Scheme; } }
    /// <summary>Gets the validated effective authority.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.ServerRequest" path="/member/remarks" />
    public string Host { get { _context.Alive(); return _context.Native.Host; } }
    /// <summary>Gets the path with encoded separators preserved.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.ServerRequest" path="/member/remarks" />
    public string Path { get { _context.Alive(); return _context.Native.Path; } }
    /// <summary>Gets the raw query string including its leading question mark when nonempty.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.ServerRequest" path="/member/remarks" />
    public string QueryString { get { _context.Alive(); return _context.Native.QueryString; } }
    /// <summary>Gets HTTP/1.1 or HTTP/2.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.ServerRequest" path="/member/remarks" />
    public string Protocol { get { _context.Alive(); return _context.Native.Protocol; } }
    /// <summary>Gets immutable headers preserving repetitions.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.ServerRequest" path="/member/remarks" />
    public HeaderCollection Headers { get; }
    /// <summary>Gets route values decoded once after native matching.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.ServerRequest" path="/member/remarks" />
    public ParameterValues RouteValues { get; }
    /// <summary>Gets decoded query values preserving repetitions.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.ServerRequest" path="/member/remarks" />
    public ParameterValues Query { get; }
    /// <summary>Gets the declared Content-Length without replacing native streaming limits.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.ServerRequest" path="/member/remarks" />
    public long? ContentLength => Headers.GetSingle("Content-Length") is { } value ? long.Parse(value, CultureInfo.InvariantCulture) : null;
    /// <summary>Gets Content-Type when supplied.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.ServerRequest" path="/member/remarks" />
    public string? ContentType => Headers.GetSingle("Content-Type");
    /// <summary>Gets the original direct peer unaffected by forwarding.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.ServerRequest" path="/member/remarks" />
    public IPEndPoint? RemoteEndPoint { get { _context.Alive(); return _context.Native.RemoteEndPoint; } }
    /// <summary>Gets an asynchronous borrowed stream; the first read selects stream mode.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.ServerRequest" path="/member/remarks" />
    public Stream Body { get { _context.Http(); return _stream ??= new ServerReadStream(_context.Native.Body, _gate); } }
    /// <summary>Reads an independently owned chunk or observes EOF; concurrent/mixed reads are rejected.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.ServerRequest" path="/member/remarks" />
    /// <param name="cancellationToken">Caller cancellation intent; cancellation never releases native owners before their retirement join.</param>
    /// <returns>An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.</returns>
    public Task<BodyChunk?> ReadChunkAsync(CancellationToken cancellationToken = default)
    {
        _context.Http(); cancellationToken.ThrowIfCancellationRequested(); _gate.Enter(2);
        return ReadChunkCoreAsync(cancellationToken);
    }
    private async Task<BodyChunk?> ReadChunkCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var chunk = await _context.Native.Body.ReadChunkAsync(_context.Native.Body.MaxChunkBytes, cancellationToken).ConfigureAwait(false);
            if (chunk is null) _eof = true;
            return chunk;
        }
        finally { _gate.Exit(); }
    }
    /// <summary>Reads bounded JSON with explicit static metadata; a null reference root is preserved.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.ServerRequest" path="/member/remarks" />
    /// <param name="typeInfo">Static JSON metadata; nullable registration arguments resolve from UseJson contexts at route freeze, without reflection fallback.</param>
    /// <param name="cancellationToken">Caller cancellation intent; cancellation never releases native owners before their retirement join.</param>
    /// <typeparam name="T">The statically known result type; JSON paths require explicit or registered static metadata.</typeparam>
    /// <returns>An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.</returns>
    public Task<T?> ReadJsonAsync<T>(JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        _context.Http(); ArgumentNullException.ThrowIfNull(typeInfo); cancellationToken.ThrowIfCancellationRequested();
        _gate.Enter(3); return ReadJsonCoreAsync(typeInfo, cancellationToken);
    }
    internal async Task<T> ReadRequiredJsonAsync<T>(JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        string? media = ContentType?.Split(';')[0].Trim();
        if (media is null || !(media.Equals("application/json", StringComparison.OrdinalIgnoreCase) || media.StartsWith("application/", StringComparison.OrdinalIgnoreCase) && media.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) ||
            Headers.GetSingle("Content-Encoding") is { } encoding && !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
            throw new ServerException("unsupported_media_type", "Static JSON binding requires an uncompressed JSON media type.");
        var value = await ReadJsonAsync(typeInfo, cancellationToken).ConfigureAwait(false);
        return value is null ? throw new RequestBindingException("body", "A non-null JSON request body is required.") : value;
    }
    private async Task<T?> ReadJsonCoreAsync<T>(JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        try
        {
            int maximum = _context.Route.EffectiveMaxJsonBytes;
            using var credit = _context.Native.Body.AcquireManagedCredit(checked(maximum * 3));
            byte[] bytes = new byte[maximum]; int count = 0;
            while (true)
            {
                using var chunk = await _context.Native.Body.ReadChunkAsync(_context.Native.Body.MaxChunkBytes, cancellationToken).ConfigureAwait(false);
                if (chunk is null) { _eof = true; break; }
                if (chunk.Length > maximum - count) throw new ServerException("body_limit_exceeded", "The JSON body exceeds its finite route cap.");
                chunk.CopyTo(bytes.AsSpan(count)); count += chunk.Length;
            }
            try { return JsonSerializer.Deserialize(bytes.AsSpan(0, count), typeInfo); }
            catch (JsonException) { throw new RequestBindingException("body", "The request JSON is malformed or does not match its static contract."); }
        }
        finally { _gate.Exit(); }
    }
    /// <summary>Gets validated trailers after native EOF; early access is rejected.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.ServerRequest" path="/member/remarks" />
    /// <returns>The typed result described in the summary, retaining the ownership policy of its declaring type.</returns>
    public HeaderCollection GetTrailers()
    {
        _context.Http();
        if (!_eof && !(_stream is ServerReadStream stream && stream.EndObserved)) throw new InvalidOperationException("Trailers require observed body EOF.");
        return new(_context.Alive, null, _context.Configuration.Options.MaxHeaderBytes, true, _context.Native.GetTrailers());
    }
}
