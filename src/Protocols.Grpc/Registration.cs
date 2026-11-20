using System.Buffers;
using System.Text.Json;

namespace NativeWeave.Server.Grpc;

/// <summary>Registers statically typed Tonic-backed methods on the existing Rust Host.</summary>
/// <remarks>Registration is single-threaded before Start. There is one Host/runtime; no managed gRPC transport or reflection dispatch is installed. Each mapping copies finite options and binds one exact static delegate. Streams are single-use and allow one pending operation per direction.</remarks>
public static class GrpcEndpointExtensions
{
    /// <summary>Maps a generated service using its explicit static method registrations.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcEndpointExtensions" path="/member/remarks" />
    /// <param name="routes">The existing application or route group.</param>
    /// <param name="service">The statically generated service registrar.</param>
    public static void MapGrpc(this ServerRoutes routes, IGeneratedGrpcService service)
    { ArgumentNullException.ThrowIfNull(routes); ArgumentNullException.ThrowIfNull(service); routes.Registry.Mutable(); service.Map(routes); }
    /// <summary>Maps one unary request and response.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcEndpointExtensions" path="/member/remarks" />
    /// <typeparam name="TRequest">The statically decoded request type.</typeparam>
    /// <typeparam name="TResponse">The statically encoded response type.</typeparam>
    /// <param name="routes">The existing application or route group.</param>
    /// <param name="method">The immutable unary method descriptor.</param>
    /// <param name="handler">The typed application callback; its returned task must join its work.</param>
    /// <param name="options">Finite options copied now, or the documented defaults.</param>
    /// <returns>The ordinary endpoint builder for metadata, authorization and a shorter timeout.</returns>
    public static ServerEndpointBuilder MapGrpcUnary<TRequest, TResponse>(this ServerRoutes routes, GrpcMethod<TRequest, TResponse> method, Func<TRequest, GrpcCallContext, Task<TResponse>> handler, GrpcMethodOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Map(routes, method, GrpcMethodKind.Unary, options, async call =>
        { var request = await Single(call, method.RequestMarshaller).ConfigureAwait(false); var result = await handler(request, call).ConfigureAwait(false); await call.Transport.WriteAsync(result, method.ResponseMarshaller, call.CancellationToken).ConfigureAwait(false); });
    }
    /// <summary>Maps one request and a bounded pull-based response stream.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcEndpointExtensions" path="/member/remarks" />
    /// <typeparam name="TRequest">The statically decoded request type.</typeparam>
    /// <typeparam name="TResponse">The statically encoded response element type.</typeparam>
    /// <param name="routes">The existing application or route group.</param>
    /// <param name="method">The immutable server-streaming descriptor.</param>
    /// <param name="handler">The callback returning the single-use output enumerable.</param>
    /// <param name="options">Finite options copied now, or the documented defaults.</param>
    /// <returns>The ordinary endpoint builder.</returns>
    public static ServerEndpointBuilder MapGrpcServerStreaming<TRequest, TResponse>(this ServerRoutes routes, GrpcMethod<TRequest, TResponse> method, Func<TRequest, GrpcCallContext, IAsyncEnumerable<TResponse>> handler, GrpcMethodOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Map(routes, method, GrpcMethodKind.ServerStreaming, options, async call =>
        { var request = await Single(call, method.RequestMarshaller).ConfigureAwait(false); await WriteAll(call, method.ResponseMarshaller, handler(request, call)).ConfigureAwait(false); });
    }
    /// <summary>Maps a single-use inbound stream to one response.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcEndpointExtensions" path="/member/remarks" />
    /// <typeparam name="TRequest">The statically decoded inbound element type.</typeparam>
    /// <typeparam name="TResponse">The statically encoded response type.</typeparam>
    /// <param name="routes">The existing application or route group.</param>
    /// <param name="method">The immutable client-streaming descriptor.</param>
    /// <param name="handler">The callback that consumes the input stream and returns one response.</param>
    /// <param name="options">Finite options copied now, or the documented defaults.</param>
    /// <returns>The ordinary endpoint builder.</returns>
    public static ServerEndpointBuilder MapGrpcClientStreaming<TRequest, TResponse>(this ServerRoutes routes, GrpcMethod<TRequest, TResponse> method, Func<IAsyncEnumerable<TRequest>, GrpcCallContext, Task<TResponse>> handler, GrpcMethodOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Map(routes, method, GrpcMethodKind.ClientStreaming, options, async call =>
        {
            var stream = new GrpcMessageStream<TRequest>(call, method.RequestMarshaller); call.RegisterInbound(stream.DisposeAsync);
            var result = await handler(stream, call).ConfigureAwait(false);
            await call.Transport.WriteAsync(result, method.ResponseMarshaller, call.CancellationToken).ConfigureAwait(false);
        });
    }
    /// <summary>Maps independently streaming input and output with one read and one write at a time.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcEndpointExtensions" path="/member/remarks" />
    /// <typeparam name="TRequest">The statically decoded inbound element type.</typeparam>
    /// <typeparam name="TResponse">The statically encoded outbound element type.</typeparam>
    /// <param name="routes">The existing application or route group.</param>
    /// <param name="method">The immutable duplex descriptor.</param>
    /// <param name="handler">The callback returning one output enumerable and consuming the input as needed.</param>
    /// <param name="options">Finite options copied now, or the documented defaults.</param>
    /// <returns>The ordinary endpoint builder.</returns>
    public static ServerEndpointBuilder MapGrpcDuplex<TRequest, TResponse>(this ServerRoutes routes, GrpcMethod<TRequest, TResponse> method, Func<IAsyncEnumerable<TRequest>, GrpcCallContext, IAsyncEnumerable<TResponse>> handler, GrpcMethodOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Map(routes, method, GrpcMethodKind.DuplexStreaming, options, async call =>
        {
            var stream = new GrpcMessageStream<TRequest>(call, method.RequestMarshaller); call.RegisterInbound(stream.DisposeAsync);
            await WriteAll(call, method.ResponseMarshaller, handler(stream, call)).ConfigureAwait(false);
        });
    }
    private static async Task<T> Single<T>(GrpcCallContext call, GrpcMarshaller<T> marshaller)
    {
        var first = await call.Transport.ReadAsync(marshaller, call.CancellationToken).ConfigureAwait(false);
        if (!first.HasValue) throw new GrpcException(GrpcStatusCode.Internal, "The unary request message is missing.");
        var second = await call.Transport.ReadAsync(marshaller, call.CancellationToken).ConfigureAwait(false);
        if (second.HasValue) throw new GrpcException(GrpcStatusCode.InvalidArgument, "The unary request contains more than one message.");
        return first.Value;
    }
    private static async Task WriteAll<T>(GrpcCallContext call, GrpcMarshaller<T> marshaller, IAsyncEnumerable<T> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        await foreach (var value in output.WithCancellation(call.CancellationToken).ConfigureAwait(false))
            await call.Transport.WriteAsync(value, marshaller, call.CancellationToken).ConfigureAwait(false);
    }
    private static ServerEndpointBuilder Map<TRequest, TResponse>(ServerRoutes routes, GrpcMethod<TRequest, TResponse> method, GrpcMethodKind kind, GrpcMethodOptions? options, Func<GrpcCallContext, Task> invoke)
    {
        ArgumentNullException.ThrowIfNull(routes); ArgumentNullException.ThrowIfNull(method);
        if (method.Kind != kind) throw new ArgumentException("The method cardinality does not match this mapping.", nameof(method));
        var descriptor = new GrpcDescriptor<TRequest, TResponse>(method, options ?? new(), invoke);
        // A canonical method has exactly two path components. Group policy may be
        // inherited when the resulting full path is still that exact method.
        string prefix = routes.Group.Prefix;
        if (!method.FullName.StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException("The method is outside this route group.", nameof(method));
        string relative = method.FullName[prefix.Length..];
        if (!relative.StartsWith('/')) throw new ArgumentException("The route group must end at the service component.", nameof(method));
        var route = routes.Registry.Add(routes.Group, relative, ["POST"], null, descriptor.InvokeAsync);
        route.Kind = 1; route.ProtocolDescriptor = descriptor;
        return new(routes.Registry, route);
    }
}

internal sealed class GrpcDescriptor<TRequest, TResponse> : INativeServerProtocolDescriptor
{
    private readonly GrpcMethod<TRequest, TResponse> _method;
    private readonly int _encoded, _decoded;
    private readonly long _stream;
    private readonly TimeSpan _timeout;
    private readonly Func<GrpcCallContext, Task> _invoke;
    private readonly GrpcWriteScratch? _writeScratch;
    public byte[] RegistrationUtf8 { get; private set; } = [];
    internal GrpcDescriptor(GrpcMethod<TRequest, TResponse> method, GrpcMethodOptions options, Func<GrpcCallContext, Task> invoke)
    {
        _method = method; _encoded = options.MaxEncodedMessageBytes; _decoded = options.MaxDecodedMessageBytes; _stream = options.MaxStreamBytes; _timeout = options.Timeout; _invoke = invoke;
        if (_encoded <= 0 || _decoded <= 0 || _stream <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        ServerValidation.Duration(_timeout, false, nameof(options));
        if (method.Kind is GrpcMethodKind.Unary or GrpcMethodKind.ClientStreaming)
            _writeScratch = new(_encoded);
    }
    public void Freeze(RouteDefinition route, ServerConfiguration configuration)
    {
        if (!configuration.Listeners.Any(listener => (listener.Protocols & ServerProtocols.Http2) != 0)) throw new InvalidOperationException("gRPC requires a configured HTTP/2 listener.");
        var budget = configuration.Options.Budget;
        long maximum = Math.Min(budget.ManagedPayloadBytes, budget.RetainedBytesPerRequest);
        long nativeFloor = checked(2L * _encoded + 2L * budget.MaxChunkBytes + 10
            + 2L * Math.Min(configuration.Options.MaxHeaderBytes, 32_768) + 200L * 48 + 2_048);
        if (_encoded > maximum || _decoded > maximum || nativeFloor > Math.Min(budget.NativePayloadBytes, budget.RetainedBytesPerRequest))
            throw new InvalidOperationException("The message peak cannot fit the selected Host budgets.");
        if (route.Pattern != _method.FullName) throw new InvalidOperationException("The gRPC route differs from its canonical method descriptor.");
        if (_timeout < route.EffectiveTimeout) route.EffectiveTimeout = _timeout;
        route.EffectiveMaxRequestBytes = _stream; route.EffectiveMaxJsonBytes = 0;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteNumber("version", 1); writer.WriteString("kind", "grpc"); writer.WriteNumber("handler_id", checked((ulong)route.Id));
            writer.WriteString("method", _method.FullName); writer.WriteString("method_kind", _method.Kind switch { GrpcMethodKind.Unary => "unary", GrpcMethodKind.ClientStreaming => "client_streaming", GrpcMethodKind.ServerStreaming => "server_streaming", _ => "duplex" });
            writer.WriteNumber("request_codec_id", _method.RequestMarshaller.Identity); writer.WriteNumber("response_codec_id", _method.ResponseMarshaller.Identity);
            writer.WriteNumber("max_encoded_message_bytes", _encoded); writer.WriteNumber("max_decoded_message_bytes", _decoded); writer.WriteNumber("max_stream_bytes", checked((ulong)_stream));
            writer.WriteNumber("timeout_ns", Native.NativeCodec.Nanoseconds(route.EffectiveTimeout)); writer.WriteString("compression", "identity"); writer.WriteEndObject();
        }
        RegistrationUtf8 = buffer.WrittenSpan.ToArray();
    }
    private GrpcCallContext Context(RequestContext context)
    {
        if (context.ProtocolState is GrpcCallContext existing) return existing;
        var call = new GrpcCallContext(context, _method.FullName, _encoded, _decoded, _stream, _writeScratch); context.ProtocolState = call; return call;
    }
    internal Task InvokeAsync(RequestContext context, CancellationToken token)
    { token.ThrowIfCancellationRequested(); var call = Context(context); call.Invoked = true; return _invoke(call); }
    public Task CompleteDispatchAsync(RequestContext context, Exception? failure) => Context(context).CompleteAsync(failure);
}
