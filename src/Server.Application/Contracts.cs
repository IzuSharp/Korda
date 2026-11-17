using System.Net;
using System.Security.Claims;

namespace NativeWeave.Server
{
    /// <summary>Mutable startup options, frozen by Build.</summary>
    /// <remarks>Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    public sealed class ServerOptions
    {
        /// <summary>Gets or sets application payload and admission budgets.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerOptions" path="/member/remarks" />
        public ServerBudgetOptions Budget { get; set; } = new();
        /// <summary>Gets or sets the finite whole-request timeout; default 30 seconds.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerOptions" path="/member/remarks" />
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
        /// <summary>Gets or sets the graceful close wait; default 30 seconds.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerOptions" path="/member/remarks" />
        public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);
        /// <summary>Gets or sets the default HTTP request-body limit; default 1 MiB. gRPC and MCP use their protocol options.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerOptions" path="/member/remarks" />
        public long MaxRequestBodyBytes { get; set; } = 1_048_576;
        /// <summary>Gets or sets the HTTP JSON binder wire limit; default 1 MiB. Protocol routes use their own finite limits.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerOptions" path="/member/remarks" />
        public int MaxJsonBytes { get; set; } = 1_048_576;
        /// <summary>Gets or sets the aggregate header limit; default 32 KiB.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerOptions" path="/member/remarks" />
        public int MaxHeaderBytes { get; set; } = 32_768;
        /// <summary>Gets or sets the header-receive timeout; default 10 seconds.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerOptions" path="/member/remarks" />
        public TimeSpan HeaderReadTimeout { get; set; } = TimeSpan.FromSeconds(10);
        /// <summary>Gets or sets idle keep-alive timeout; default 60 seconds.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerOptions" path="/member/remarks" />
        public TimeSpan KeepAliveTimeout { get; set; } = TimeSpan.FromSeconds(60);
        /// <summary>Gets or sets trusted forwarding policy; null ignores forwarded identity headers.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerOptions" path="/member/remarks" />
        public TrustedProxyOptions? TrustedProxies { get; set; }
    }

    /// <summary>Finite application-owned credits; these are not process RSS limits.</summary>
    /// <remarks>Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. Native global/request payload grants are atomic and nonwaiting for both reads and writes; queue/network waits are separate, full backing capacity/overlap is charged, and all partial grants roll back without automatic retry. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    public sealed class ServerBudgetOptions
    {
        /// <summary>Gets or sets the connection limit, default 512.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerBudgetOptions" path="/member/remarks" />
        public int MaxConnections { get; set; } = 512;
        /// <summary>Gets or sets HTTP/2 streams per connection, default 32.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerBudgetOptions" path="/member/remarks" />
        public int MaxHttp2StreamsPerConnection { get; set; } = 32;
        /// <summary>Gets or sets active and retiring request slots, default 256.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerBudgetOptions" path="/member/remarks" />
        public int MaxActiveRequests { get; set; } = 256;
        /// <summary>Gets or sets metadata-only admission waiters, default 128.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerBudgetOptions" path="/member/remarks" />
        public int MaxQueuedRequests { get; set; } = 128;
        /// <summary>Gets or sets managed payload credit, default 64 MiB.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerBudgetOptions" path="/member/remarks" />
        public long ManagedPayloadBytes { get; set; } = 67_108_864;
        /// <summary>Gets or sets native payload credit, default 64 MiB; read/write grants never wait for payload bytes and exhaustion is resource_limit.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerBudgetOptions" path="/member/remarks" />
        public long NativePayloadBytes { get; set; } = 67_108_864;
        /// <summary>Gets or sets each request's per-side full backing-capacity limit, default 4 MiB; retained input can immediately reject another read or write.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerBudgetOptions" path="/member/remarks" />
        public long RetainedBytesPerRequest { get; set; } = 4_194_304;
        /// <summary>Gets or sets maximum body chunk capacity, default 32 KiB.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerBudgetOptions" path="/member/remarks" />
        public int MaxChunkBytes { get; set; } = 32_768;
    }

    /// <summary>HTTP protocol selection without implicit upgrades.</summary>
    /// <remarks>Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    [Flags] public enum ServerProtocols
    {
        /// <summary>No protocol selected; invalid for a listener.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerProtocols" path="/member/remarks" />
        None = 0,
        /// <summary>HTTP/1.1 transport.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerProtocols" path="/member/remarks" />
        Http1 = 1,
        /// <summary>HTTP/2 transport.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerProtocols" path="/member/remarks" />
        Http2 = 2,
        /// <summary>HTTP/1.1 and HTTP/2 negotiated by TLS ALPN.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerProtocols" path="/member/remarks" />
        Http1AndHttp2 = 3
    }
    /// <summary>Immutable runtime lifecycle observations.</summary>
    /// <remarks>Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    public enum ServerState
    {
        /// <summary>Configured but no listeners started.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerState" path="/member/remarks" />
        Created,
        /// <summary>Startup has accepted responsibility.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerState" path="/member/remarks" />
        Starting,
        /// <summary>All listeners accept requests.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerState" path="/member/remarks" />
        Running,
        /// <summary>New admission is closed while owners retire.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerState" path="/member/remarks" />
        Stopping,
        /// <summary>All instance work has retired.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerState" path="/member/remarks" />
        Stopped,
        /// <summary>A host failure has been observed; cleanup still applies.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerState" path="/member/remarks" />
        Faulted,
        /// <summary>Owned application services and native instance are released.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerState" path="/member/remarks" />
        Disposed
    }
    /// <summary>Whether all managed/native work has retired before close returned.</summary>
    /// <remarks>Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    public enum ServerCloseStatus
    {
        /// <summary>The safe retirement join completed.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerCloseStatus" path="/member/remarks" />
        Completed,
        /// <summary>The wait ended while owners and credits remain retained.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerCloseStatus" path="/member/remarks" />
        Pending
    }
    /// <summary>Negotiated optional host capabilities.</summary>
    /// <remarks>Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    [Flags] public enum ServerCapabilities : ulong
    {
        /// <summary>No capability bits.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerCapabilities" path="/member/remarks" />
        None = 0,
        /// <summary>Native HTTP/1.1 capability.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerCapabilities" path="/member/remarks" />
        Http1 = 1,
        /// <summary>Native HTTP/2 capability.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerCapabilities" path="/member/remarks" />
        Http2 = 2,
        /// <summary>Native rustls capability.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerCapabilities" path="/member/remarks" />
        Tls = 4,
        /// <summary>Validated HTTP trailer capability.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerCapabilities" path="/member/remarks" />
        Trailers = 8,
        /// <summary>Native Tonic method capability.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerCapabilities" path="/member/remarks" />
        Grpc = 16,
        /// <summary>Native rmcp capability.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerCapabilities" path="/member/remarks" />
        Mcp = 32,
        /// <summary>Native Axum SSE formatting capability.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerCapabilities" path="/member/remarks" />
        ServerSentEvents = 64
    }
    /// <summary>One listener's mutable startup policy.</summary>
    /// <remarks>Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    public sealed class ServerListenOptions
    {
        /// <summary>Gets or sets a unique listener name; generated when null.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerListenOptions" path="/member/remarks" />
        public string? Name { get; set; }
        /// <summary>Gets or sets protocols; default HTTP/1.1.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerListenOptions" path="/member/remarks" />
        public ServerProtocols Protocols { get; set; } = ServerProtocols.Http1;
        /// <summary>Gets or sets native TLS configuration; null means cleartext.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerListenOptions" path="/member/remarks" />
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
    /// <remarks>Explicit exact IPs and1..16 right-to-left trusted hops; only coherent X-Forwarded-For/Proto/Host; original peer and MCP wire Host/Origin retained. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    public sealed class TrustedProxyOptions
    {
        /// <summary>Gets exact trusted peer IP addresses.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.TrustedProxyOptions" path="/member/remarks" />
        public IList<IPAddress> KnownProxies { get; } = new List<IPAddress>();
        /// <summary>Gets or sets the maximum trusted forwarded hops in 1..16, default one; only coherent X-Forwarded-For/Proto/Host tuples apply.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.TrustedProxyOptions" path="/member/remarks" />
        public int ForwardLimit { get; set; } = 1;
    }
    /// <summary>Close observation; Pending retains roots, native owners and credits.</summary>
    /// <remarks>Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    /// <param name="Status">The Status value described by this operation.</param>
    /// <param name="PendingRequests">The PendingRequests value described by this operation.</param>
    /// <param name="RetainedManagedBytes">The RetainedManagedBytes value described by this operation.</param>
    /// <param name="RetainedNativeBytes">The RetainedNativeBytes value described by this operation.</param>
    public sealed record ServerCloseResult(ServerCloseStatus Status, int PendingRequests, long RetainedManagedBytes, long RetainedNativeBytes)
    {
        /// <summary>Gets the observed close status; Pending does not certify retirement.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerCloseResult" path="/member/remarks" />
        public ServerCloseStatus Status { get; init; } = Status;
        /// <summary>Gets the retained active or retiring request count.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerCloseResult" path="/member/remarks" />
        public int PendingRequests { get; init; } = PendingRequests;
        /// <summary>Gets currently charged framework-owned managed payload bytes.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerCloseResult" path="/member/remarks" />
        public long RetainedManagedBytes { get; init; } = RetainedManagedBytes;
        /// <summary>Gets currently charged native backing-allocation bytes.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerCloseResult" path="/member/remarks" />
        public long RetainedNativeBytes { get; init; } = RetainedNativeBytes;
    }


    /// <summary>Returns one result for execution within the current request lifetime.</summary>
    /// <remarks>Closed static handler shapes and JsonTypeInfo; no arbitrary Delegate/reflection fallback. Per-request typed invocation/DTO and Task work; no object-array binder; lazy request DI scope only on demand. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    /// <param name="context">The current borrowed request context; it cannot escape awaited dispatch and cleanup.</param>
    /// <param name="cancellationToken">Caller cancellation intent; cancellation never releases native owners before their retirement join.</param>
    public delegate Task<ServerResult> ResponseHandler(RequestContext context, CancellationToken cancellationToken);
    /// <summary>Writes a response directly; successful return is followed by the shared HTTP completion tail before dispatch cleanup.</summary>
    /// <remarks>Closed static handler shapes and JsonTypeInfo; no arbitrary Delegate/reflection fallback. Per-request typed invocation/DTO and Task work; no object-array binder; lazy request DI scope only on demand. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    /// <param name="context">The current borrowed request context; it cannot escape awaited dispatch and cleanup.</param>
    /// <param name="cancellationToken">Caller cancellation intent; cancellation never releases native owners before their retirement join.</param>
    public delegate Task RequestHandler(RequestContext context, CancellationToken cancellationToken);
    /// <summary>Processes one typed JSON request and response without dynamic binding.</summary>
    /// <remarks>Closed static handler shapes and JsonTypeInfo; no arbitrary Delegate/reflection fallback. Per-request typed invocation/DTO and Task work; no object-array binder; lazy request DI scope only on demand. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    /// <param name="request">The request value described by this operation.</param>
    /// <param name="context">The current borrowed request context; it cannot escape awaited dispatch and cleanup.</param>
    /// <param name="cancellationToken">Caller cancellation intent; cancellation never releases native owners before their retirement join.</param>
    /// <typeparam name="TRequest">The statically known request type; JSON paths require explicit or registered static metadata.</typeparam>
    /// <typeparam name="TResponse">The statically known result type; JSON paths require explicit or registered static metadata.</typeparam>
    public delegate Task<TResponse> JsonRequestHandler<TRequest, TResponse>(TRequest request, RequestContext context, CancellationToken cancellationToken);
    /// <summary>Processes one typed JSON request and chooses an HTTP result.</summary>
    /// <remarks>Closed static handler shapes and JsonTypeInfo; no arbitrary Delegate/reflection fallback. Per-request typed invocation/DTO and Task work; no object-array binder; lazy request DI scope only on demand. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    /// <param name="request">The request value described by this operation.</param>
    /// <param name="context">The current borrowed request context; it cannot escape awaited dispatch and cleanup.</param>
    /// <param name="cancellationToken">Caller cancellation intent; cancellation never releases native owners before their retirement join.</param>
    /// <typeparam name="TRequest">The statically known request type; JSON paths require explicit or registered static metadata.</typeparam>
    public delegate Task<ServerResult> JsonResultHandler<TRequest>(TRequest request, RequestContext context, CancellationToken cancellationToken);
    /// <summary>Composes asynchronous middleware; next is single-invocation and protocol contexts forbid raw HTTP body/output access.</summary>
    /// <remarks>Explicit caller-owned thread-safe auth SPI; application policy, no built-in JWT/session stack. gRPC middleware uses restricted RequestContext; short circuit without explicit GrpcException maps to Unauthenticated16/PermissionDenied7, never raw HTTP. Bounded registered callbacks; next single-use; exceptions redacted and classified before/after response commit; application service work remains caller cost. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    /// <param name="context">The current borrowed request context; it cannot escape awaited dispatch and cleanup.</param>
    /// <param name="next">The next value described by this operation.</param>
    /// <param name="cancellationToken">Caller cancellation intent; cancellation never releases native owners before their retirement join.</param>
    public delegate Task ServerMiddleware(RequestContext context, RequestHandler next, CancellationToken cancellationToken);
    /// <summary>Composes HTTP result filters without object-array boxing; gRPC/MCP endpoints and inherited protocol filters are rejected at route freeze.</summary>
    /// <remarks>Explicit caller-owned thread-safe auth SPI; application policy, no built-in JWT/session stack. HTTP ServerResult pipeline only; gRPC/MCP endpoint filters, including inherited group filters, fail route freeze. Bounded registered callbacks; next single-use; exceptions redacted and classified before/after response commit; application service work remains caller cost. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    /// <param name="context">The current borrowed request context; it cannot escape awaited dispatch and cleanup.</param>
    /// <param name="next">The next value described by this operation.</param>
    /// <param name="cancellationToken">Caller cancellation intent; cancellation never releases native owners before their retirement join.</param>
    public delegate Task<ServerResult> ServerFilter(RequestContext context, ResponseHandler next, CancellationToken cancellationToken);


    /// <summary>One immutable SSE event; each string is bounded by the endpoint chunk policy.</summary>
    /// <remarks>sse bit6; prevalidated Event/Id; finite whole-stream deadline. One enumerable event in flight; checked conservative expanded UTF-8 reserve; native Axum Event/Sse formatting; no replay retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    /// <param name="Data">The Data value described by this operation.</param>
    /// <param name="Event">The Event value described by this operation; omitted calls use null.</param>
    /// <param name="Id">The Id value described by this operation; omitted calls use null.</param>
    /// <param name="Retry">The Retry value described by this operation; omitted calls use null.</param>
    public sealed record ServerEvent(string Data, string? Event = null, string? Id = null, TimeSpan? Retry = null)
    {
        /// <summary>Gets event data; native Axum performs SSE line framing.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerEvent" path="/member/remarks" />
        public string Data { get; init; } = Data;
        /// <summary>Gets the optional event name without CR or LF.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerEvent" path="/member/remarks" />
        public string? Event { get; init; } = Event;
        /// <summary>Gets the optional event identifier without CR, LF or NUL.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerEvent" path="/member/remarks" />
        public string? Id { get; init; } = Id;
        /// <summary>Gets an optional whole-millisecond retry interval.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerEvent" path="/member/remarks" />
        public TimeSpan? Retry { get; init; } = Retry;
    }
    /// <summary>Authentication over parsed application metadata; no transport ownership is transferred.</summary>
    /// <remarks>Explicit caller-owned thread-safe auth SPI; application policy, no built-in JWT/session stack. Admitted gRPC denial uses Tonic Unauthenticated16/PermissionDenied7 framing; ordinary HTTP and MCP pre-parse denial use401/403. Bounded registered callbacks; next single-use; exceptions redacted and classified before/after response commit; application service work remains caller cost. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    public interface IServerAuthenticator
    {
        /// <summary>Returns a principal on success or null when no supported credentials are present.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.IServerAuthenticator" path="/member/remarks" />
        /// <param name="context">The current borrowed request context; it cannot escape awaited dispatch and cleanup.</param>
        /// <param name="cancellationToken">Caller cancellation intent; cancellation never releases native owners before their retirement join.</param>
        /// <returns>An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.</returns>
        Task<ClaimsPrincipal?> AuthenticateAsync(RequestContext context, CancellationToken cancellationToken = default);
    }
    /// <summary>Evaluates application policies after authentication and before body binding.</summary>
    /// <remarks>Explicit caller-owned thread-safe auth SPI; application policy, no built-in JWT/session stack. Bounded registered callbacks; next single-use; exceptions redacted and classified before/after response commit; application service work remains caller cost. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    public interface IServerAuthorizer
    {
        /// <summary>Returns whether the principal is permitted by the named policy.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.IServerAuthorizer" path="/member/remarks" />
        /// <param name="context">The current borrowed request context; it cannot escape awaited dispatch and cleanup.</param>
        /// <param name="policy">The policy value described by this operation.</param>
        /// <param name="cancellationToken">Caller cancellation intent; cancellation never releases native owners before their retirement join.</param>
        /// <returns>An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.</returns>
        Task<bool> AuthorizeAsync(RequestContext context, string policy, CancellationToken cancellationToken = default);
    }
    /// <summary>Server-domain failure; native panic/poison/ABI failures use NativeEngineException.</summary>
    /// <remarks>Explicit caller-owned thread-safe auth SPI; application policy, no built-in JWT/session stack. Bounded registered callbacks; next single-use; exceptions redacted and classified before/after response commit; application service work remains caller cost. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    public class ServerException : Exception
    {
        /// <summary>Creates a domain exception with a stable code and optional cause.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerException" path="/member/remarks" />
        /// <param name="code">The code value described by this operation.</param>
        /// <param name="message">The message value described by this operation.</param>
        /// <param name="innerException">The innerException value described by this operation; omitted calls use null.</param>
        public ServerException(string code, string message, Exception? innerException = null) : base(message, innerException) { Code = code; }
        /// <summary>Gets the stable domain error code.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerException" path="/member/remarks" />
        public string Code { get; }
    }
    /// <summary>Malformed, missing or repeated bound input; default response is HTTP 400.</summary>
    /// <remarks>Explicit caller-owned thread-safe auth SPI; application policy, no built-in JWT/session stack. Bounded registered callbacks; next single-use; exceptions redacted and classified before/after response commit; application service work remains caller cost. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    public sealed class RequestBindingException : ServerException
    {
        /// <summary>Creates a binding error without including the raw input value.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.RequestBindingException" path="/member/remarks" />
        /// <param name="parameterName">The parameterName value described by this operation.</param>
        /// <param name="message">The message value described by this operation.</param>
        public RequestBindingException(string parameterName, string message) : base("binding_failed", message) { ParameterName = parameterName; }
        /// <summary>Gets the safe parameter identifier.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.RequestBindingException" path="/member/remarks" />
        public string ParameterName { get; }
    }

    /// <summary>Marks a static partial endpoint class for compile-time binding generation.</summary>
    /// <remarks>Finite annotated static handler whitelist; missing/ambiguous shapes are compile diagnostics. Build-time generation; no per-handler Rust compilation, reflection scan, Expression.Compile or runtime argument object[]. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    [AttributeUsage(AttributeTargets.Class)] public sealed class ServerEndpointsAttribute : Attribute { }
    /// <summary>Declares a route on a supported static method; arbitrary Delegate reflection is prohibited.</summary>
    /// <remarks>Finite annotated static handler whitelist; missing/ambiguous shapes are compile diagnostics. Build-time generation; no per-handler Rust compilation, reflection scan, Expression.Compile or runtime argument object[]. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)] public sealed class ServerRouteAttribute : Attribute
    {
        /// <summary>Declares one HTTP method and pattern.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerRouteAttribute" path="/member/remarks" />
        /// <param name="method">The method value described by this operation.</param>
        /// <param name="pattern">A validated native route pattern or group prefix, registered before startup.</param>
        public ServerRouteAttribute(string method, string pattern) { Method = method; Pattern = pattern; }
        /// <summary>Gets the method token.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerRouteAttribute" path="/member/remarks" />
        public string Method { get; }
        /// <summary>Gets the route template.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.ServerRouteAttribute" path="/member/remarks" />
        public string Pattern { get; }
    }
    /// <summary>Explicit scalar route binding for generated adapters.</summary>
    /// <remarks>Finite annotated static handler whitelist; missing/ambiguous shapes are compile diagnostics. Build-time generation; no per-handler Rust compilation, reflection scan, Expression.Compile or runtime argument object[]. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    [AttributeUsage(AttributeTargets.Parameter)] public sealed class RouteParameterAttribute : Attribute
    {
        /// <summary>Uses the supplied name, or the parameter name when null.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.RouteParameterAttribute" path="/member/remarks" />
        /// <param name="name">The name value described by this operation; omitted calls use null.</param>
        public RouteParameterAttribute(string? name = null) { Name = name; }
        /// <summary>Gets the explicit name.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.RouteParameterAttribute" path="/member/remarks" />
        public string? Name { get; }
    }
    /// <summary>Explicit scalar or repeated-array query binding for generated adapters.</summary>
    /// <remarks>Finite annotated static handler whitelist; missing/ambiguous shapes are compile diagnostics. Build-time generation; no per-handler Rust compilation, reflection scan, Expression.Compile or runtime argument object[]. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    [AttributeUsage(AttributeTargets.Parameter)] public sealed class QueryParameterAttribute : Attribute
    {
        /// <summary>Uses the supplied name, or the parameter name when null.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.QueryParameterAttribute" path="/member/remarks" />
        /// <param name="name">The name value described by this operation; omitted calls use null.</param>
        public QueryParameterAttribute(string? name = null) { Name = name; }
        /// <summary>Gets the explicit name.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.QueryParameterAttribute" path="/member/remarks" />
        public string? Name { get; }
    }
    /// <summary>Explicit scalar header binding for generated adapters.</summary>
    /// <remarks>Finite annotated static handler whitelist; missing/ambiguous shapes are compile diagnostics. Build-time generation; no per-handler Rust compilation, reflection scan, Expression.Compile or runtime argument object[]. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    [AttributeUsage(AttributeTargets.Parameter)] public sealed class HeaderParameterAttribute : Attribute
    {
        /// <summary>Uses one validated case-insensitive header name.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.HeaderParameterAttribute" path="/member/remarks" />
        /// <param name="name">The name value described by this operation.</param>
        public HeaderParameterAttribute(string name) { Name = name; }
        /// <summary>Gets the header name.</summary>
        /// <inheritdoc cref="T:NativeWeave.Server.HeaderParameterAttribute" path="/member/remarks" />
        public string Name { get; }
    }
    /// <summary>Marks the one statically serialized JSON body parameter.</summary>
    /// <remarks>Finite annotated static handler whitelist; missing/ambiguous shapes are compile diagnostics. Build-time generation; no per-handler Rust compilation, reflection scan, Expression.Compile or runtime argument object[]. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    [AttributeUsage(AttributeTargets.Parameter)] public sealed class BodyParameterAttribute : Attribute { }
    /// <summary>Marks a required per-request DI service; its type is statically rooted.</summary>
    /// <remarks>Finite annotated static handler whitelist; missing/ambiguous shapes are compile diagnostics. Build-time generation; no per-handler Rust compilation, reflection scan, Expression.Compile or runtime argument object[]. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
    [AttributeUsage(AttributeTargets.Parameter)] public sealed class ServiceParameterAttribute : Attribute { }
}
