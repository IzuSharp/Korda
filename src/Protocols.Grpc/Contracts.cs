using System.Buffers;

namespace NativeWeave.Server.Grpc;

/// <summary>Specifies the number of inbound and outbound messages in one gRPC call.</summary>
/// <remarks>Tonic owns framing and transport. Unary inputs are checked before managed decoding; each request has an independent cardinality counter.</remarks>
public enum GrpcMethodKind
{
    /// <summary>Exactly one request and response.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcMethodKind" path="/member/remarks" />
    Unary,
    /// <summary>Many requests and exactly one response.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcMethodKind" path="/member/remarks" />
    ClientStreaming,
    /// <summary>Exactly one request and many responses.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcMethodKind" path="/member/remarks" />
    ServerStreaming,
    /// <summary>Independent inbound and outbound message streams.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcMethodKind" path="/member/remarks" />
    DuplexStreaming
}

/// <summary>Defines the stable wire-compatible gRPC status codes.</summary>
/// <remarks>Native diagnostics preserve the actual code. Successful message delivery does not establish terminal status delivery or remote side-effect rollback.</remarks>
public enum GrpcStatusCode
{
    /// <summary>The call completed successfully.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcStatusCode" path="/member/remarks" />
    Ok = 0,
    /// <summary>Cancellation was confirmed.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcStatusCode" path="/member/remarks" />
    Cancelled = 1,
    /// <summary>An unclassified failure occurred.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcStatusCode" path="/member/remarks" />
    Unknown = 2,
    /// <summary>The request argument or unary cardinality is invalid.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcStatusCode" path="/member/remarks" />
    InvalidArgument = 3,
    /// <summary>The native whole-call deadline expired.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcStatusCode" path="/member/remarks" />
    DeadlineExceeded = 4,
    /// <summary>The requested entity was not found.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcStatusCode" path="/member/remarks" />
    NotFound = 5,
    /// <summary>The entity already exists.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcStatusCode" path="/member/remarks" />
    AlreadyExists = 6,
    /// <summary>The authenticated principal is not permitted.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcStatusCode" path="/member/remarks" />
    PermissionDenied = 7,
    /// <summary>A finite resource or decoded-message limit was reached.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcStatusCode" path="/member/remarks" />
    ResourceExhausted = 8,
    /// <summary>A required precondition is absent.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcStatusCode" path="/member/remarks" />
    FailedPrecondition = 9,
    /// <summary>The operation was aborted.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcStatusCode" path="/member/remarks" />
    Aborted = 10,
    /// <summary>A value or encoded frame length exceeds the supported range.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcStatusCode" path="/member/remarks" />
    OutOfRange = 11,
    /// <summary>The requested method is not implemented.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcStatusCode" path="/member/remarks" />
    Unimplemented = 12,
    /// <summary>An internal failure occurred; unexpected application details are redacted.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcStatusCode" path="/member/remarks" />
    Internal = 13,
    /// <summary>The service is unavailable.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcStatusCode" path="/member/remarks" />
    Unavailable = 14,
    /// <summary>Unrecoverable data loss was reported.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcStatusCode" path="/member/remarks" />
    DataLoss = 15,
    /// <summary>Valid credentials are required.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcStatusCode" path="/member/remarks" />
    Unauthenticated = 16
}

/// <summary>Decodes one complete message into an independently owned managed value.</summary>
/// <remarks>The span is borrowed only for this call and must never escape. Use statically reachable generated protobuf code; no runtime schema discovery is performed. This explicit application callback owns its DTO graph and private allocations. The framework reserves its complete-message byte copy until the callback returns; the decoded limit bounds decompressed payload bytes, not the application's object graph.</remarks>
/// <typeparam name="T">The statically known request or response message type.</typeparam>
/// <param name="payload">Complete protobuf bytes without a gRPC frame header.</param>
/// <returns>The independent managed message.</returns>
public delegate T GrpcDecoder<T>(ReadOnlySpan<byte> payload);

/// <summary>Encodes one complete message into a bounded managed writer.</summary>
/// <remarks>The destination enforces the configured encoded cap before allocation or growth. Tonic adds framing and owns transport. The framework reserves the bounded writer and overlapping native copy; application-owned messages and private codec allocations are outside those payload ledgers.</remarks>
/// <typeparam name="T">The statically known request or response message type.</typeparam>
/// <param name="value">The application-owned message to encode.</param>
/// <param name="destination">The scoped bounded destination; it must not be retained.</param>
public delegate void GrpcEncoder<T>(T value, IBufferWriter<byte> destination);

/// <summary>Binds a statically reachable protobuf encoder and decoder.</summary>
/// <remarks>Immutable and reusable application codec SPI. Ordinary managed message changes do not rebuild the native engine; Rust receives numeric codec identities and complete bytes, never managed Type objects. Supplied codec delegates own their private allocations and independently returned DTOs. Framework byte copies, bounded output writers and native retained owners have separate pregranted capacities; payload caps do not promise a process-heap or arbitrary DTO-graph bound.</remarks>
/// <typeparam name="T">The statically known message type.</typeparam>
public sealed class GrpcMarshaller<T>
{
    internal readonly GrpcEncoder<T> Encode;
    internal readonly GrpcDecoder<T> Decode;
    internal readonly ulong Identity;
    /// <summary>Creates an explicit static codec pair.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcMarshaller`1" path="/member/remarks" />
    /// <param name="encode">The non-null encoder.</param>
    /// <param name="decode">The non-null decoder.</param>
    public GrpcMarshaller(GrpcEncoder<T> encode, GrpcDecoder<T> decode)
    { ArgumentNullException.ThrowIfNull(encode); ArgumentNullException.ThrowIfNull(decode); Encode = encode; Decode = decode; Identity = GrpcIdentities.Next(); }
}

internal static class GrpcIdentities
{
    private static long _next;
    internal static ulong Next() => checked((ulong)Interlocked.Increment(ref _next));
}

/// <summary>Describes one immutable typed gRPC method.</summary>
/// <remarks>Registration verifies the canonical path, cardinality, finite caps and configured HTTP/2 listener before startup. The descriptor introduces no runtime or native owner.</remarks>
/// <typeparam name="TRequest">The static request message type.</typeparam>
/// <typeparam name="TResponse">The static response message type.</typeparam>
public sealed class GrpcMethod<TRequest, TResponse>
{
    internal readonly string FullName;
    internal readonly GrpcMethodKind Kind;
    internal readonly GrpcMarshaller<TRequest> RequestMarshaller;
    internal readonly GrpcMarshaller<TResponse> ResponseMarshaller;
    /// <summary>Creates a method with a canonical /package.Service/Method path.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcMethod`2" path="/member/remarks" />
    /// <param name="fullName">The case-sensitive canonical method path.</param>
    /// <param name="kind">The declared request/response cardinality.</param>
    /// <param name="requestMarshaller">The static request codec.</param>
    /// <param name="responseMarshaller">The static response codec.</param>
    public GrpcMethod(string fullName, GrpcMethodKind kind, GrpcMarshaller<TRequest> requestMarshaller, GrpcMarshaller<TResponse> responseMarshaller)
    {
        GrpcValidation.Method(fullName); ArgumentNullException.ThrowIfNull(requestMarshaller); ArgumentNullException.ThrowIfNull(responseMarshaller);
        if (kind is < GrpcMethodKind.Unary or > GrpcMethodKind.DuplexStreaming) throw new ArgumentOutOfRangeException(nameof(kind));
        FullName = fullName; Kind = kind; RequestMarshaller = requestMarshaller; ResponseMarshaller = responseMarshaller;
    }
}

/// <summary>Configures finite message, stream and whole-call limits.</summary>
/// <remarks>Copied at registration. Message and stream limits must be positive and fit checked host peak accounting; the method timeout, generic route timeout and strict client deadline can only shorten the native deadline. Identity compression is the initial profile.</remarks>
public sealed class GrpcMethodOptions
{
    /// <summary>Gets or sets the encoded message cap; default 1 MiB.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcMethodOptions" path="/member/remarks" />
    public int MaxEncodedMessageBytes { get; set; } = 1_048_576;
    /// <summary>Gets or sets the decoded message cap; default 1 MiB.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcMethodOptions" path="/member/remarks" />
    public int MaxDecodedMessageBytes { get; set; } = 1_048_576;
    /// <summary>Gets or sets cumulative payload bytes per direction; default 64 MiB, excluding framing.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcMethodOptions" path="/member/remarks" />
    public long MaxStreamBytes { get; set; } = 67_108_864;
    /// <summary>Gets or sets the finite whole-call upper bound; default 30 seconds.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcMethodOptions" path="/member/remarks" />
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Maps statically generated service descriptors and typed callbacks.</summary>
/// <remarks>The application calls this before Start. Implementations directly call closed generic MapGrpc methods; assembly scanning and runtime schema compilation are unnecessary.</remarks>
public interface IGeneratedGrpcService
{
    /// <summary>Maps the generated methods before the route registry freezes.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.IGeneratedGrpcService" path="/member/remarks" />
    /// <param name="routes">The existing application or route group.</param>
    void Map(ServerRoutes routes);
}

/// <summary>Represents an explicit application-selected gRPC failure.</summary>
/// <remarks>Only codes 1..16 are accepted. Detail is finite UTF-8 protocol text, and optional metadata obeys the same bounded profile. Unexpected application exceptions are redacted independently.</remarks>
public sealed class GrpcException : Exception
{
    /// <summary>Creates a failure with a non-OK status.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcException" path="/member/remarks" />
    /// <param name="statusCode">The admitted failure code; Ok and unknown numeric values are rejected.</param>
    /// <param name="detail">The bounded public error detail.</param>
    /// <param name="trailers">Optional bounded trailing metadata copied at terminal admission.</param>
    public GrpcException(GrpcStatusCode statusCode, string detail, GrpcMetadata? trailers = null) : base(detail)
    {
        if (statusCode is < GrpcStatusCode.Cancelled or > GrpcStatusCode.Unauthenticated) throw new ArgumentOutOfRangeException(nameof(statusCode));
        ArgumentNullException.ThrowIfNull(detail); if (Native.NativeCodec.Utf8.GetByteCount(detail) > 4096) throw new ArgumentOutOfRangeException(nameof(detail));
        StatusCode = statusCode; Trailers = trailers;
    }
    /// <summary>Gets the original admitted protocol status.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcException" path="/member/remarks" />
    public GrpcStatusCode StatusCode { get; }
    /// <summary>Gets optional bounded trailing metadata.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcException" path="/member/remarks" />
    public GrpcMetadata? Trailers { get; }
}

internal static class GrpcValidation
{
    internal static void Method(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 4096 || name[0] != '/') throw new ArgumentException("A canonical gRPC method path is required.", nameof(name));
        int separator = name.IndexOf('/', 1);
        if (separator <= 1 || separator == name.Length - 1 || name.IndexOf('/', separator + 1) != -1) throw new ArgumentException("A canonical gRPC method path is required.", nameof(name));
        foreach (string identifier in name[1..separator].Split('.')) Identifier(identifier);
        Identifier(name[(separator + 1)..]);
    }
    private static void Identifier(string value)
    {
        if (value.Length == 0 || !(char.IsAsciiLetter(value[0]) || value[0] == '_') || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("gRPC service and method identifiers must use the protobuf identifier grammar.");
    }
}
