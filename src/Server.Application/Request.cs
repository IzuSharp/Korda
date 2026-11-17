using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace NativeWeave.Server;

/// <summary>Borrowed request context, valid through awaited dispatch and cleanup; never capture it in background work.</summary>
public sealed class RequestContext
{
    private readonly NativeServerApplication _application;
    private AsyncServiceScope? _scope;
    private bool _ended;
    private RequestItems? _items;
    private ClaimsPrincipal _user = new(new ClaimsIdentity());
    internal HostRequest Native { get; }
    internal RouteDefinition Route { get; }
    internal ServerConfiguration Configuration => _application.Configuration;
    internal RequestContext(NativeServerApplication application, HostRequest native, RouteDefinition route)
    {
        _application = application; Native = native; Route = route;
        Request = new(this); Response = new(this);
    }
    /// <summary>Gets request metadata and ordinary HTTP body APIs.</summary>
    public ServerRequest Request { get; }
    /// <summary>Gets the response facade; protocol routes restrict raw HTTP access.</summary>
    public ServerResponse Response { get; }
    /// <summary>Gets lazily created request-scoped services.</summary>
    public IServiceProvider Services { get { Alive(); _scope ??= _application.Services.CreateAsyncScope(); return _scope.Value.ServiceProvider; } }
    /// <summary>Gets the authenticated principal, initially empty.</summary>
    public ClaimsPrincipal User { get { Alive(); return _user; } }
    /// <summary>Gets sticky native disconnect, deadline and shutdown cancellation intent.</summary>
    public CancellationToken RequestAborted { get { Alive(); return Native.RequestAborted; } }
    /// <summary>Gets the native monotonic whole-request time remaining.</summary>
    public TimeSpan TimeRemaining { get { Alive(); return Native.TimeRemaining; } }
    /// <summary>Gets a bounded correlation identifier without authorization meaning.</summary>
    public string TraceIdentifier { get { Alive(); return Native.TraceIdentifier; } }
    /// <summary>Gets lazy request storage capped at 128 entries.</summary>
    public IDictionary<object, object?> Items { get { Alive(); return _items ??= new(Alive); } }
    /// <summary>Requests transport abort without claiming native retirement.</summary>
    public void Abort() { Alive(); Native.Abort(); }
    internal void SetUser(ClaimsPrincipal user) { Alive(); _user = user; }
    internal void Alive() { ObjectDisposedException.ThrowIf(_ended, this); }
    internal void Http() { Alive(); if (Native.Kind != 0 && Native.Kind != 2) throw new InvalidOperationException("Raw HTTP access is unavailable after protocol admission."); }
    internal async ValueTask EndScopeAsync()
    { try { if (_scope is { } scope) await scope.DisposeAsync().ConfigureAwait(false); } finally { _ended = true; } }
}

/// <summary>Read-only native HTTP metadata with one bounded body-consumption mode and request-scoped lifetime.</summary>
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
    public string Method { get { _context.Alive(); return _context.Native.Method; } }
    /// <summary>Gets the effective scheme after native trusted-forwarding validation.</summary>
    public string Scheme { get { _context.Alive(); return _context.Native.Scheme; } }
    /// <summary>Gets the validated effective authority.</summary>
    public string Host { get { _context.Alive(); return _context.Native.Host; } }
    /// <summary>Gets the path with encoded separators preserved.</summary>
    public string Path { get { _context.Alive(); return _context.Native.Path; } }
    /// <summary>Gets the raw query string including its leading question mark when nonempty.</summary>
    public string QueryString { get { _context.Alive(); return _context.Native.QueryString; } }
    /// <summary>Gets HTTP/1.1 or HTTP/2.</summary>
    public string Protocol { get { _context.Alive(); return _context.Native.Protocol; } }
    /// <summary>Gets immutable headers preserving repetitions.</summary>
    public HeaderCollection Headers { get; }
    /// <summary>Gets route values decoded once after native matching.</summary>
    public ParameterValues RouteValues { get; }
    /// <summary>Gets decoded query values preserving repetitions.</summary>
    public ParameterValues Query { get; }
    /// <summary>Gets the declared Content-Length without replacing native streaming limits.</summary>
    public long? ContentLength => Headers.GetSingle("Content-Length") is { } value ? long.Parse(value, CultureInfo.InvariantCulture) : null;
    /// <summary>Gets Content-Type when supplied.</summary>
    public string? ContentType => Headers.GetSingle("Content-Type");
    /// <summary>Gets the original direct peer unaffected by forwarding.</summary>
    public IPEndPoint? RemoteEndPoint { get { _context.Alive(); return _context.Native.RemoteEndPoint; } }
    /// <summary>Gets an asynchronous borrowed stream; the first read selects stream mode.</summary>
    public Stream Body { get { _context.Http(); return _stream ??= new ServerReadStream(_context.Native.Body, _gate); } }
    /// <summary>Reads an independently owned chunk or observes EOF; concurrent/mixed reads are rejected.</summary>
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
    public HeaderCollection GetTrailers()
    {
        _context.Http();
        if (!_eof && !(_stream is ServerReadStream stream && stream.EndObserved)) throw new InvalidOperationException("Trailers require observed body EOF.");
        return new(_context.Alive, null, _context.Configuration.Options.MaxHeaderBytes, true, _context.Native.GetTrailers());
    }
}
