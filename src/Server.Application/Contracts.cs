using System.Net;
using System.Security.Claims;

namespace NativeWeave.Server
{
    /// <summary>Mutable startup options, frozen by Build.</summary>
    public sealed class ServerOptions
    {
        /// <summary>Gets or sets application payload and admission budgets.</summary>
        public ServerBudgetOptions Budget { get; set; } = new();
        /// <summary>Gets or sets the finite whole-request timeout; default 30 seconds.</summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
        /// <summary>Gets or sets the graceful close wait; default 30 seconds.</summary>
        public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);
        /// <summary>Gets or sets the default HTTP request-body limit; default 1 MiB. gRPC and MCP use their protocol options.</summary>
        public long MaxRequestBodyBytes { get; set; } = 1_048_576;
        /// <summary>Gets or sets the HTTP JSON binder wire limit; default 1 MiB. Protocol routes use their own finite limits.</summary>
        public int MaxJsonBytes { get; set; } = 1_048_576;
        /// <summary>Gets or sets the aggregate header limit; default 32 KiB.</summary>
        public int MaxHeaderBytes { get; set; } = 32_768;
        /// <summary>Gets or sets the header-receive timeout; default 10 seconds.</summary>
        public TimeSpan HeaderReadTimeout { get; set; } = TimeSpan.FromSeconds(10);
        /// <summary>Gets or sets idle keep-alive timeout; default 60 seconds.</summary>
        public TimeSpan KeepAliveTimeout { get; set; } = TimeSpan.FromSeconds(60);
        /// <summary>Gets or sets trusted forwarding policy; null ignores forwarded identity headers.</summary>
        public TrustedProxyOptions? TrustedProxies { get; set; }
    }

    /// <summary>Finite application-owned credits; these are not process RSS limits.</summary>
    public sealed class ServerBudgetOptions
    {
        /// <summary>Gets or sets the connection limit, default 512.</summary>
        public int MaxConnections { get; set; } = 512;
        /// <summary>Gets or sets HTTP/2 streams per connection, default 32.</summary>
        public int MaxHttp2StreamsPerConnection { get; set; } = 32;
        /// <summary>Gets or sets active and retiring request slots, default 256.</summary>
        public int MaxActiveRequests { get; set; } = 256;
        /// <summary>Gets or sets metadata-only admission waiters, default 128.</summary>
        public int MaxQueuedRequests { get; set; } = 128;
        /// <summary>Gets or sets managed payload credit, default 64 MiB.</summary>
        public long ManagedPayloadBytes { get; set; } = 67_108_864;
        /// <summary>Gets or sets native payload credit, default 64 MiB; read/write grants never wait for payload bytes and exhaustion is resource_limit.</summary>
        public long NativePayloadBytes { get; set; } = 67_108_864;
        /// <summary>Gets or sets each request's per-side full backing-capacity limit, default 4 MiB; retained input can immediately reject another read or write.</summary>
        public long RetainedBytesPerRequest { get; set; } = 4_194_304;
        /// <summary>Gets or sets maximum body chunk capacity, default 32 KiB.</summary>
        public int MaxChunkBytes { get; set; } = 32_768;
    }

    /// <summary>HTTP protocol selection without implicit upgrades.</summary>
    [Flags] public enum ServerProtocols
    {
        /// <summary>No protocol selected; invalid for a listener.</summary>
        None = 0,
        /// <summary>HTTP/1.1 transport.</summary>
        Http1 = 1,
        /// <summary>HTTP/2 transport.</summary>
        Http2 = 2,
        /// <summary>HTTP/1.1 and HTTP/2 negotiated by TLS ALPN.</summary>
        Http1AndHttp2 = 3
    }
    /// <summary>Immutable runtime lifecycle observations.</summary>
    public enum ServerState
    {
        /// <summary>Configured but no listeners started.</summary>
        Created,
        /// <summary>Startup has accepted responsibility.</summary>
        Starting,
        /// <summary>All listeners accept requests.</summary>
        Running,
        /// <summary>New admission is closed while owners retire.</summary>
        Stopping,
        /// <summary>All instance work has retired.</summary>
        Stopped,
        /// <summary>A host failure has been observed; cleanup still applies.</summary>
        Faulted,
        /// <summary>Owned application services and native instance are released.</summary>
        Disposed
    }
    /// <summary>Whether all managed/native work has retired before close returned.</summary>
    public enum ServerCloseStatus
    {
        /// <summary>The safe retirement join completed.</summary>
        Completed,
        /// <summary>The wait ended while owners and credits remain retained.</summary>
        Pending
    }
    /// <summary>Negotiated optional host capabilities.</summary>
    [Flags] public enum ServerCapabilities : ulong
    {
        /// <summary>No capability bits.</summary>
        None = 0,
        /// <summary>Native HTTP/1.1 capability.</summary>
        Http1 = 1,
        /// <summary>Native HTTP/2 capability.</summary>
        Http2 = 2,
        /// <summary>Native rustls capability.</summary>
        Tls = 4,
        /// <summary>Validated HTTP trailer capability.</summary>
        Trailers = 8,
        /// <summary>Native Tonic method capability.</summary>
        Grpc = 16,
        /// <summary>Native rmcp capability.</summary>
        Mcp = 32,
        /// <summary>Native Axum SSE formatting capability.</summary>
        ServerSentEvents = 64
    }
    /// <summary>One listener's mutable startup policy.</summary>
    public sealed class ServerListenOptions
    {
        /// <summary>Gets or sets a unique listener name; generated when null.</summary>
        public string? Name { get; set; }
        /// <summary>Gets or sets protocols; default HTTP/1.1.</summary>
        public ServerProtocols Protocols { get; set; } = ServerProtocols.Http1;
        /// <summary>Gets or sets native TLS configuration; null means cleartext.</summary>
        public ServerTlsOptions? Tls { get; set; }
    }
    /// <summary>Native rustls configuration; no managed TLS transport is created.</summary>
    public sealed class ServerTlsOptions
    {
        /// <summary>Creates PEM-file configuration; paths must resolve to readable regular files.</summary>
        public static ServerTlsOptions FromPemFiles(string certificateChainFile, string privateKeyFile)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(certificateChainFile);
            ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyFile);
            return new ServerTlsOptions { CertificateChainFile = certificateChainFile, PrivateKeyFile = privateKeyFile };
        }
        internal string CertificateChainFile { get; private init; } = string.Empty;
        internal string PrivateKeyFile { get; private init; } = string.Empty;
        private ServerTlsOptions() { }
        /// <summary>Gets or sets the handshake timeout; default 10 seconds.</summary>
        public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);
        /// <summary>Gets or sets concurrent handshakes; default 64.</summary>
        public int MaxConcurrentHandshakes { get; set; } = 64;
        /// <summary>Gets or sets a PEM client trust-root file; null disables client-certificate authentication.</summary>
        public string? ClientCertificateAuthorityFile { get; set; }
    }
    /// <summary>Explicit forwarding trust; unlisted direct peers cannot supply effective identity.</summary>
    public sealed class TrustedProxyOptions
    {
        /// <summary>Gets exact trusted peer IP addresses.</summary>
        public IList<IPAddress> KnownProxies { get; } = new List<IPAddress>();
        /// <summary>Gets or sets the maximum trusted forwarded hops in 1..16, default one; only coherent X-Forwarded-For/Proto/Host tuples apply.</summary>
        public int ForwardLimit { get; set; } = 1;
    }
    /// <summary>Close observation; Pending retains roots, native owners and credits.</summary>
    public sealed record ServerCloseResult(ServerCloseStatus Status, int PendingRequests, long RetainedManagedBytes, long RetainedNativeBytes);


    /// <summary>Returns one result for execution within the current request lifetime.</summary>
    public delegate Task<ServerResult> ResponseHandler(RequestContext context, CancellationToken cancellationToken);
    /// <summary>Writes a response directly; successful return is followed by the shared HTTP completion tail before dispatch cleanup.</summary>
    public delegate Task RequestHandler(RequestContext context, CancellationToken cancellationToken);
    /// <summary>Processes one typed JSON request and response without dynamic binding.</summary>
    public delegate Task<TResponse> JsonRequestHandler<TRequest, TResponse>(TRequest request, RequestContext context, CancellationToken cancellationToken);
    /// <summary>Processes one typed JSON request and chooses an HTTP result.</summary>
    public delegate Task<ServerResult> JsonResultHandler<TRequest>(TRequest request, RequestContext context, CancellationToken cancellationToken);
    /// <summary>Composes asynchronous middleware; next is single-invocation and protocol contexts forbid raw HTTP body/output access.</summary>
    public delegate Task ServerMiddleware(RequestContext context, RequestHandler next, CancellationToken cancellationToken);
    /// <summary>Composes HTTP result filters without object-array boxing; gRPC/MCP endpoints and inherited protocol filters are rejected at route freeze.</summary>
    public delegate Task<ServerResult> ServerFilter(RequestContext context, ResponseHandler next, CancellationToken cancellationToken);


    /// <summary>One immutable SSE event; each string is bounded by the endpoint chunk policy.</summary>
    public sealed record ServerEvent(string Data, string? Event = null, string? Id = null, TimeSpan? Retry = null);
    /// <summary>Authentication over parsed application metadata; no transport ownership is transferred.</summary>
    public interface IServerAuthenticator
    {
        /// <summary>Returns a principal on success or null when no supported credentials are present.</summary>
        Task<ClaimsPrincipal?> AuthenticateAsync(RequestContext context, CancellationToken cancellationToken = default);
    }
    /// <summary>Evaluates application policies after authentication and before body binding.</summary>
    public interface IServerAuthorizer
    {
        /// <summary>Returns whether the principal is permitted by the named policy.</summary>
        Task<bool> AuthorizeAsync(RequestContext context, string policy, CancellationToken cancellationToken = default);
    }
    /// <summary>Server-domain failure; native panic/poison/ABI failures use NativeEngineException.</summary>
    public class ServerException : Exception
    {
        /// <summary>Creates a domain exception with a stable code and optional cause.</summary>
        public ServerException(string code, string message, Exception? innerException = null) : base(message, innerException) { Code = code; }
        /// <summary>Gets the stable domain error code.</summary>
        public string Code { get; }
    }
    /// <summary>Malformed, missing or repeated bound input; default response is HTTP 400.</summary>
    public sealed class RequestBindingException : ServerException
    {
        /// <summary>Creates a binding error without including the raw input value.</summary>
        public RequestBindingException(string parameterName, string message) : base("binding_failed", message) { ParameterName = parameterName; }
        /// <summary>Gets the safe parameter identifier.</summary>
        public string ParameterName { get; }
    }

    /// <summary>Marks a static partial endpoint class for compile-time binding generation.</summary>
    [AttributeUsage(AttributeTargets.Class)] public sealed class ServerEndpointsAttribute : Attribute { }
    /// <summary>Declares a route on a supported static method; arbitrary Delegate reflection is prohibited.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)] public sealed class ServerRouteAttribute : Attribute
    {
        /// <summary>Declares one HTTP method and pattern.</summary>
        public ServerRouteAttribute(string method, string pattern) { Method = method; Pattern = pattern; }
        /// <summary>Gets the method token.</summary>
        public string Method { get; }
        /// <summary>Gets the route template.</summary>
        public string Pattern { get; }
    }
    /// <summary>Explicit scalar route binding for generated adapters.</summary>
    [AttributeUsage(AttributeTargets.Parameter)] public sealed class RouteParameterAttribute : Attribute
    {
        /// <summary>Uses the supplied name, or the parameter name when null.</summary>
        public RouteParameterAttribute(string? name = null) { Name = name; }
        /// <summary>Gets the explicit name.</summary>
        public string? Name { get; }
    }
    /// <summary>Explicit scalar or repeated-array query binding for generated adapters.</summary>
    [AttributeUsage(AttributeTargets.Parameter)] public sealed class QueryParameterAttribute : Attribute
    {
        /// <summary>Uses the supplied name, or the parameter name when null.</summary>
        public QueryParameterAttribute(string? name = null) { Name = name; }
        /// <summary>Gets the explicit name.</summary>
        public string? Name { get; }
    }
    /// <summary>Explicit scalar header binding for generated adapters.</summary>
    [AttributeUsage(AttributeTargets.Parameter)] public sealed class HeaderParameterAttribute : Attribute
    {
        /// <summary>Uses one validated case-insensitive header name.</summary>
        public HeaderParameterAttribute(string name) { Name = name; }
        /// <summary>Gets the header name.</summary>
        public string Name { get; }
    }
    /// <summary>Marks the one statically serialized JSON body parameter.</summary>
    [AttributeUsage(AttributeTargets.Parameter)] public sealed class BodyParameterAttribute : Attribute { }
    /// <summary>Marks a required per-request DI service; its type is statically rooted.</summary>
    [AttributeUsage(AttributeTargets.Parameter)] public sealed class ServiceParameterAttribute : Attribute { }
}
