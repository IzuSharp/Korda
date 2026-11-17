# Typed gRPC API reference

Generated from the actual compiled public API and XML; compared against the exact pinned design declarations. B18 owns the optional static gRPC facade; Tonic and the existing Rust Host own protocol and transport.

## `F:NativeWeave.Server.Grpc.GrpcMethodKind.ClientStreaming`

```csharp
NativeWeave.Server.Grpc.GrpcMethodKind.ClientStreaming = 1
```

Many requests and exactly one response.

Tonic owns framing and transport. Unary inputs are checked before managed decoding; each request has an independent cardinality counter.


## `F:NativeWeave.Server.Grpc.GrpcMethodKind.DuplexStreaming`

```csharp
NativeWeave.Server.Grpc.GrpcMethodKind.DuplexStreaming = 3
```

Independent inbound and outbound message streams.

Tonic owns framing and transport. Unary inputs are checked before managed decoding; each request has an independent cardinality counter.


## `F:NativeWeave.Server.Grpc.GrpcMethodKind.ServerStreaming`

```csharp
NativeWeave.Server.Grpc.GrpcMethodKind.ServerStreaming = 2
```

Exactly one request and many responses.

Tonic owns framing and transport. Unary inputs are checked before managed decoding; each request has an independent cardinality counter.


## `F:NativeWeave.Server.Grpc.GrpcMethodKind.Unary`

```csharp
NativeWeave.Server.Grpc.GrpcMethodKind.Unary = 0
```

Exactly one request and response.

Tonic owns framing and transport. Unary inputs are checked before managed decoding; each request has an independent cardinality counter.


## `F:NativeWeave.Server.Grpc.GrpcStatusCode.Aborted`

```csharp
NativeWeave.Server.Grpc.GrpcStatusCode.Aborted = 10
```

The operation was aborted.

Native diagnostics preserve the actual code. Successful message delivery does not establish terminal status delivery or remote side-effect rollback.


## `F:NativeWeave.Server.Grpc.GrpcStatusCode.AlreadyExists`

```csharp
NativeWeave.Server.Grpc.GrpcStatusCode.AlreadyExists = 6
```

The entity already exists.

Native diagnostics preserve the actual code. Successful message delivery does not establish terminal status delivery or remote side-effect rollback.


## `F:NativeWeave.Server.Grpc.GrpcStatusCode.Cancelled`

```csharp
NativeWeave.Server.Grpc.GrpcStatusCode.Cancelled = 1
```

Cancellation was confirmed.

Native diagnostics preserve the actual code. Successful message delivery does not establish terminal status delivery or remote side-effect rollback.


## `F:NativeWeave.Server.Grpc.GrpcStatusCode.DataLoss`

```csharp
NativeWeave.Server.Grpc.GrpcStatusCode.DataLoss = 15
```

Unrecoverable data loss was reported.

Native diagnostics preserve the actual code. Successful message delivery does not establish terminal status delivery or remote side-effect rollback.


## `F:NativeWeave.Server.Grpc.GrpcStatusCode.DeadlineExceeded`

```csharp
NativeWeave.Server.Grpc.GrpcStatusCode.DeadlineExceeded = 4
```

The native whole-call deadline expired.

Native diagnostics preserve the actual code. Successful message delivery does not establish terminal status delivery or remote side-effect rollback.


## `F:NativeWeave.Server.Grpc.GrpcStatusCode.FailedPrecondition`

```csharp
NativeWeave.Server.Grpc.GrpcStatusCode.FailedPrecondition = 9
```

A required precondition is absent.

Native diagnostics preserve the actual code. Successful message delivery does not establish terminal status delivery or remote side-effect rollback.


## `F:NativeWeave.Server.Grpc.GrpcStatusCode.Internal`

```csharp
NativeWeave.Server.Grpc.GrpcStatusCode.Internal = 13
```

An internal failure occurred; unexpected application details are redacted.

Native diagnostics preserve the actual code. Successful message delivery does not establish terminal status delivery or remote side-effect rollback.


## `F:NativeWeave.Server.Grpc.GrpcStatusCode.InvalidArgument`

```csharp
NativeWeave.Server.Grpc.GrpcStatusCode.InvalidArgument = 3
```

The request argument or unary cardinality is invalid.

Native diagnostics preserve the actual code. Successful message delivery does not establish terminal status delivery or remote side-effect rollback.


## `F:NativeWeave.Server.Grpc.GrpcStatusCode.NotFound`

```csharp
NativeWeave.Server.Grpc.GrpcStatusCode.NotFound = 5
```

The requested entity was not found.

Native diagnostics preserve the actual code. Successful message delivery does not establish terminal status delivery or remote side-effect rollback.


## `F:NativeWeave.Server.Grpc.GrpcStatusCode.Ok`

```csharp
NativeWeave.Server.Grpc.GrpcStatusCode.Ok = 0
```

The call completed successfully.

Native diagnostics preserve the actual code. Successful message delivery does not establish terminal status delivery or remote side-effect rollback.


## `F:NativeWeave.Server.Grpc.GrpcStatusCode.OutOfRange`

```csharp
NativeWeave.Server.Grpc.GrpcStatusCode.OutOfRange = 11
```

A value or encoded frame length exceeds the supported range.

Native diagnostics preserve the actual code. Successful message delivery does not establish terminal status delivery or remote side-effect rollback.


## `F:NativeWeave.Server.Grpc.GrpcStatusCode.PermissionDenied`

```csharp
NativeWeave.Server.Grpc.GrpcStatusCode.PermissionDenied = 7
```

The authenticated principal is not permitted.

Native diagnostics preserve the actual code. Successful message delivery does not establish terminal status delivery or remote side-effect rollback.


## `F:NativeWeave.Server.Grpc.GrpcStatusCode.ResourceExhausted`

```csharp
NativeWeave.Server.Grpc.GrpcStatusCode.ResourceExhausted = 8
```

A finite resource or decoded-message limit was reached.

Native diagnostics preserve the actual code. Successful message delivery does not establish terminal status delivery or remote side-effect rollback.


## `F:NativeWeave.Server.Grpc.GrpcStatusCode.Unauthenticated`

```csharp
NativeWeave.Server.Grpc.GrpcStatusCode.Unauthenticated = 16
```

Valid credentials are required.

Native diagnostics preserve the actual code. Successful message delivery does not establish terminal status delivery or remote side-effect rollback.


## `F:NativeWeave.Server.Grpc.GrpcStatusCode.Unavailable`

```csharp
NativeWeave.Server.Grpc.GrpcStatusCode.Unavailable = 14
```

The service is unavailable.

Native diagnostics preserve the actual code. Successful message delivery does not establish terminal status delivery or remote side-effect rollback.


## `F:NativeWeave.Server.Grpc.GrpcStatusCode.Unimplemented`

```csharp
NativeWeave.Server.Grpc.GrpcStatusCode.Unimplemented = 12
```

The requested method is not implemented.

Native diagnostics preserve the actual code. Successful message delivery does not establish terminal status delivery or remote side-effect rollback.


## `F:NativeWeave.Server.Grpc.GrpcStatusCode.Unknown`

```csharp
NativeWeave.Server.Grpc.GrpcStatusCode.Unknown = 2
```

An unclassified failure occurred.

Native diagnostics preserve the actual code. Successful message delivery does not establish terminal status delivery or remote side-effect rollback.


## `M:NativeWeave.Server.Grpc.GrpcCallContext.WriteResponseHeadersAsync(NativeWeave.Server.Grpc.GrpcMetadata,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task NativeWeave.Server.Grpc.GrpcCallContext.WriteResponseHeadersAsync(NativeWeave.Server.Grpc.GrpcMetadata headers, [System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Copies and commits initial response metadata once.

The existing request supplies identity, services, correlation, native deadline/cancellation and lifecycle cleanup. Raw HTTP input/output and OnStarting remain forbidden. Initial metadata commits once; trailing metadata freezes at terminal admission. All native message operations retire before common scope cleanup.

- **headers**: Bounded metadata copied synchronously at admission; later caller edits do not change it.
- **cancellationToken**: Caller cancellation intent; native ownership remains retained through retirement.
- **returns**: An operation that joins native header completion and retirement.

## `M:NativeWeave.Server.Grpc.GrpcEndpointExtensions.MapGrpc(NativeWeave.Server.ServerRoutes,NativeWeave.Server.Grpc.IGeneratedGrpcService)`

```csharp
public static void NativeWeave.Server.Grpc.GrpcEndpointExtensions.MapGrpc(NativeWeave.Server.ServerRoutes routes, NativeWeave.Server.Grpc.IGeneratedGrpcService service)
```

Maps a generated service using its explicit static method registrations.

Registration is single-threaded before Start. There is one Host/runtime; no managed gRPC transport or reflection dispatch is installed. Each mapping copies finite options and binds one exact static delegate. Streams are single-use and allow one pending operation per direction.

- **routes**: The existing application or route group.
- **service**: The statically generated service registrar.

## `M:NativeWeave.Server.Grpc.GrpcEndpointExtensions.MapGrpcClientStreaming``2(NativeWeave.Server.ServerRoutes,NativeWeave.Server.Grpc.GrpcMethod{``0,``1},System.Func{System.Collections.Generic.IAsyncEnumerable{``0},NativeWeave.Server.Grpc.GrpcCallContext,System.Threading.Tasks.Task{``1}},NativeWeave.Server.Grpc.GrpcMethodOptions)`

```csharp
public static NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.Grpc.GrpcEndpointExtensions.MapGrpcClientStreaming<TRequest, TResponse>(NativeWeave.Server.ServerRoutes routes, NativeWeave.Server.Grpc.GrpcMethod<TRequest, TResponse> method, System.Func<System.Collections.Generic.IAsyncEnumerable<TRequest>, NativeWeave.Server.Grpc.GrpcCallContext, System.Threading.Tasks.Task<TResponse>> handler, [NativeWeave.Server.Grpc.GrpcMethodOptions? options = null])
```

Maps a single-use inbound stream to one response.

Registration is single-threaded before Start. There is one Host/runtime; no managed gRPC transport or reflection dispatch is installed. Each mapping copies finite options and binds one exact static delegate. Streams are single-use and allow one pending operation per direction.

- **TRequest**: The statically decoded inbound element type.
- **TResponse**: The statically encoded response type.
- **routes**: The existing application or route group.
- **method**: The immutable client-streaming descriptor.
- **handler**: The callback that consumes the input stream and returns one response.
- **options**: Finite options copied now, or the documented defaults.
- **returns**: The ordinary endpoint builder.

## `M:NativeWeave.Server.Grpc.GrpcEndpointExtensions.MapGrpcDuplex``2(NativeWeave.Server.ServerRoutes,NativeWeave.Server.Grpc.GrpcMethod{``0,``1},System.Func{System.Collections.Generic.IAsyncEnumerable{``0},NativeWeave.Server.Grpc.GrpcCallContext,System.Collections.Generic.IAsyncEnumerable{``1}},NativeWeave.Server.Grpc.GrpcMethodOptions)`

```csharp
public static NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.Grpc.GrpcEndpointExtensions.MapGrpcDuplex<TRequest, TResponse>(NativeWeave.Server.ServerRoutes routes, NativeWeave.Server.Grpc.GrpcMethod<TRequest, TResponse> method, System.Func<System.Collections.Generic.IAsyncEnumerable<TRequest>, NativeWeave.Server.Grpc.GrpcCallContext, System.Collections.Generic.IAsyncEnumerable<TResponse>> handler, [NativeWeave.Server.Grpc.GrpcMethodOptions? options = null])
```

Maps independently streaming input and output with one read and one write at a time.

Registration is single-threaded before Start. There is one Host/runtime; no managed gRPC transport or reflection dispatch is installed. Each mapping copies finite options and binds one exact static delegate. Streams are single-use and allow one pending operation per direction.

- **TRequest**: The statically decoded inbound element type.
- **TResponse**: The statically encoded outbound element type.
- **routes**: The existing application or route group.
- **method**: The immutable duplex descriptor.
- **handler**: The callback returning one output enumerable and consuming the input as needed.
- **options**: Finite options copied now, or the documented defaults.
- **returns**: The ordinary endpoint builder.

## `M:NativeWeave.Server.Grpc.GrpcEndpointExtensions.MapGrpcServerStreaming``2(NativeWeave.Server.ServerRoutes,NativeWeave.Server.Grpc.GrpcMethod{``0,``1},System.Func{``0,NativeWeave.Server.Grpc.GrpcCallContext,System.Collections.Generic.IAsyncEnumerable{``1}},NativeWeave.Server.Grpc.GrpcMethodOptions)`

```csharp
public static NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.Grpc.GrpcEndpointExtensions.MapGrpcServerStreaming<TRequest, TResponse>(NativeWeave.Server.ServerRoutes routes, NativeWeave.Server.Grpc.GrpcMethod<TRequest, TResponse> method, System.Func<TRequest, NativeWeave.Server.Grpc.GrpcCallContext, System.Collections.Generic.IAsyncEnumerable<TResponse>> handler, [NativeWeave.Server.Grpc.GrpcMethodOptions? options = null])
```

Maps one request and a bounded pull-based response stream.

Registration is single-threaded before Start. There is one Host/runtime; no managed gRPC transport or reflection dispatch is installed. Each mapping copies finite options and binds one exact static delegate. Streams are single-use and allow one pending operation per direction.

- **TRequest**: The statically decoded request type.
- **TResponse**: The statically encoded response element type.
- **routes**: The existing application or route group.
- **method**: The immutable server-streaming descriptor.
- **handler**: The callback returning the single-use output enumerable.
- **options**: Finite options copied now, or the documented defaults.
- **returns**: The ordinary endpoint builder.

## `M:NativeWeave.Server.Grpc.GrpcEndpointExtensions.MapGrpcUnary``2(NativeWeave.Server.ServerRoutes,NativeWeave.Server.Grpc.GrpcMethod{``0,``1},System.Func{``0,NativeWeave.Server.Grpc.GrpcCallContext,System.Threading.Tasks.Task{``1}},NativeWeave.Server.Grpc.GrpcMethodOptions)`

```csharp
public static NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.Grpc.GrpcEndpointExtensions.MapGrpcUnary<TRequest, TResponse>(NativeWeave.Server.ServerRoutes routes, NativeWeave.Server.Grpc.GrpcMethod<TRequest, TResponse> method, System.Func<TRequest, NativeWeave.Server.Grpc.GrpcCallContext, System.Threading.Tasks.Task<TResponse>> handler, [NativeWeave.Server.Grpc.GrpcMethodOptions? options = null])
```

Maps one unary request and response.

Registration is single-threaded before Start. There is one Host/runtime; no managed gRPC transport or reflection dispatch is installed. Each mapping copies finite options and binds one exact static delegate. Streams are single-use and allow one pending operation per direction.

- **TRequest**: The statically decoded request type.
- **TResponse**: The statically encoded response type.
- **routes**: The existing application or route group.
- **method**: The immutable unary method descriptor.
- **handler**: The typed application callback; its returned task must join its work.
- **options**: Finite options copied now, or the documented defaults.
- **returns**: The ordinary endpoint builder for metadata, authorization and a shorter timeout.

## `M:NativeWeave.Server.Grpc.GrpcException.#ctor(NativeWeave.Server.Grpc.GrpcStatusCode,System.String,NativeWeave.Server.Grpc.GrpcMetadata)`

```csharp
public NativeWeave.Server.Grpc.GrpcException.GrpcException(NativeWeave.Server.Grpc.GrpcStatusCode statusCode, System.String detail, [NativeWeave.Server.Grpc.GrpcMetadata? trailers = null])
```

Creates a failure with a non-OK status.

Only codes 1..16 are accepted. Detail is finite UTF-8 protocol text, and optional metadata obeys the same bounded profile. Unexpected application exceptions are redacted independently.

- **statusCode**: The admitted failure code; Ok and unknown numeric values are rejected.
- **detail**: The bounded public error detail.
- **trailers**: Optional bounded trailing metadata copied at terminal admission.

## `M:NativeWeave.Server.Grpc.GrpcMarshaller`1.#ctor(NativeWeave.Server.Grpc.GrpcEncoder{`0},NativeWeave.Server.Grpc.GrpcDecoder{`0})`

```csharp
public NativeWeave.Server.Grpc.GrpcMarshaller<T>.GrpcMarshaller(NativeWeave.Server.Grpc.GrpcEncoder<T> encode, NativeWeave.Server.Grpc.GrpcDecoder<T> decode)
```

Creates an explicit static codec pair.

Immutable and reusable application codec SPI. Ordinary managed message changes do not rebuild the native engine; Rust receives numeric codec identities and complete bytes, never managed Type objects. Supplied codec delegates own their private allocations and independently returned DTOs. Framework byte copies, bounded output writers and native retained owners have separate pregranted capacities; payload caps do not promise a process-heap or arbitrary DTO-graph bound.

- **encode**: The non-null encoder.
- **decode**: The non-null decoder.

## `M:NativeWeave.Server.Grpc.GrpcMetadata.#ctor`

```csharp
public NativeWeave.Server.Grpc.GrpcMetadata.GrpcMetadata()
```

Creates an empty mutable metadata collection.

At most 200 entries and 32 KiB including count/length prefixes and repeated names. Binary names end in -bin. Borrowed request collections are read-only and expire with the call. Response headers copy at admission; trailers freeze at terminal admission. Binary getters return caller-owned copies.


## `M:NativeWeave.Server.Grpc.GrpcMetadata.Add(System.String,System.String)`

```csharp
public void NativeWeave.Server.Grpc.GrpcMetadata.Add(System.String name, System.String value)
```

Appends one validated ASCII text value.

At most 200 entries and 32 KiB including count/length prefixes and repeated names. Binary names end in -bin. Borrowed request collections are read-only and expire with the call. Response headers copy at admission; trailers freeze at terminal admission. Binary getters return caller-owned copies.

- **name**: A lowercase ASCII metadata name without the -bin suffix.
- **value**: Printable ASCII text; empty values are retained.

## `M:NativeWeave.Server.Grpc.GrpcMetadata.AddBinary(System.String,System.ReadOnlySpan{System.Byte})`

```csharp
public void NativeWeave.Server.Grpc.GrpcMetadata.AddBinary(System.String name, System.ReadOnlySpan<System.Byte> value)
```

Copies and appends one binary metadata value.

At most 200 entries and 32 KiB including count/length prefixes and repeated names. Binary names end in -bin. Borrowed request collections are read-only and expire with the call. Response headers copy at admission; trailers freeze at terminal admission. Binary getters return caller-owned copies.

- **name**: A lowercase ASCII metadata name ending in -bin.
- **value**: Raw bytes; no managed base64 conversion is performed.

## `M:NativeWeave.Server.Grpc.GrpcMetadata.GetBinaryValues(System.String)`

```csharp
public System.Collections.Generic.IReadOnlyList<System.Byte[]> NativeWeave.Server.Grpc.GrpcMetadata.GetBinaryValues(System.String name)
```

Gets independent copies of repeated raw binary values.

At most 200 entries and 32 KiB including count/length prefixes and repeated names. Binary names end in -bin. Borrowed request collections are read-only and expire with the call. Response headers copy at admission; trailers freeze at terminal admission. Binary getters return caller-owned copies.

- **name**: The lowercase metadata name ending in -bin.
- **returns**: An immutable list of caller-owned byte arrays, including distinct empty values.

## `M:NativeWeave.Server.Grpc.GrpcMetadata.GetValues(System.String)`

```csharp
public System.Collections.Generic.IReadOnlyList<System.String> NativeWeave.Server.Grpc.GrpcMetadata.GetValues(System.String name)
```

Gets repeated text values in per-name order.

At most 200 entries and 32 KiB including count/length prefixes and repeated names. Binary names end in -bin. Borrowed request collections are read-only and expire with the call. Response headers copy at admission; trailers freeze at terminal admission. Binary getters return caller-owned copies.

- **name**: The lowercase non-binary metadata name.
- **returns**: An immutable list of caller-owned strings; missing names return an empty list.

## `M:NativeWeave.Server.Grpc.GrpcMethod`2.#ctor(System.String,NativeWeave.Server.Grpc.GrpcMethodKind,NativeWeave.Server.Grpc.GrpcMarshaller{`0},NativeWeave.Server.Grpc.GrpcMarshaller{`1})`

```csharp
public NativeWeave.Server.Grpc.GrpcMethod<TRequest, TResponse>.GrpcMethod(System.String fullName, NativeWeave.Server.Grpc.GrpcMethodKind kind, NativeWeave.Server.Grpc.GrpcMarshaller<TRequest> requestMarshaller, NativeWeave.Server.Grpc.GrpcMarshaller<TResponse> responseMarshaller)
```

Creates a method with a canonical /package.Service/Method path.

Registration verifies the canonical path, cardinality, finite caps and configured HTTP/2 listener before startup. The descriptor introduces no runtime or native owner.

- **fullName**: The case-sensitive canonical method path.
- **kind**: The declared request/response cardinality.
- **requestMarshaller**: The static request codec.
- **responseMarshaller**: The static response codec.

## `M:NativeWeave.Server.Grpc.IGeneratedGrpcService.Map(NativeWeave.Server.ServerRoutes)`

```csharp
void NativeWeave.Server.Grpc.IGeneratedGrpcService.Map(NativeWeave.Server.ServerRoutes routes)
```

Maps the generated methods before the route registry freezes.

The application calls this before Start. Implementations directly call closed generic MapGrpc methods; assembly scanning and runtime schema compilation are unnecessary.

- **routes**: The existing application or route group.

## `P:NativeWeave.Server.Grpc.GrpcCallContext.CancellationToken`

```csharp
public System.Threading.CancellationToken NativeWeave.Server.Grpc.GrpcCallContext.CancellationToken | getter=Public;setter=;init=
```

Gets cancellation from the native whole-call lifetime.

The existing request supplies identity, services, correlation, native deadline/cancellation and lifecycle cleanup. Raw HTTP input/output and OnStarting remain forbidden. Initial metadata commits once; trailing metadata freezes at terminal admission. All native message operations retire before common scope cleanup.


## `P:NativeWeave.Server.Grpc.GrpcCallContext.Method`

```csharp
public System.String NativeWeave.Server.Grpc.GrpcCallContext.Method | getter=Public;setter=;init=
```

Gets the canonical method path.

The existing request supplies identity, services, correlation, native deadline/cancellation and lifecycle cleanup. Raw HTTP input/output and OnStarting remain forbidden. Initial metadata commits once; trailing metadata freezes at terminal admission. All native message operations retire before common scope cleanup.


## `P:NativeWeave.Server.Grpc.GrpcCallContext.RequestContext`

```csharp
public NativeWeave.Server.RequestContext NativeWeave.Server.Grpc.GrpcCallContext.RequestContext | getter=Public;setter=;init=
```

Gets the same request, service scope, identity and permitted lifecycle hooks.

The existing request supplies identity, services, correlation, native deadline/cancellation and lifecycle cleanup. Raw HTTP input/output and OnStarting remain forbidden. Initial metadata commits once; trailing metadata freezes at terminal admission. All native message operations retire before common scope cleanup.


## `P:NativeWeave.Server.Grpc.GrpcCallContext.RequestHeaders`

```csharp
public NativeWeave.Server.Grpc.GrpcMetadata NativeWeave.Server.Grpc.GrpcCallContext.RequestHeaders | getter=Public;setter=;init=
```

Gets the read-only native-decoded metadata; ordinary HTTP headers retain wire text.

The existing request supplies identity, services, correlation, native deadline/cancellation and lifecycle cleanup. Raw HTTP input/output and OnStarting remain forbidden. Initial metadata commits once; trailing metadata freezes at terminal admission. All native message operations retire before common scope cleanup.


## `P:NativeWeave.Server.Grpc.GrpcCallContext.ResponseTrailers`

```csharp
public NativeWeave.Server.Grpc.GrpcMetadata NativeWeave.Server.Grpc.GrpcCallContext.ResponseTrailers | getter=Public;setter=;init=
```

Gets trailing metadata mutable until terminal admission.

The existing request supplies identity, services, correlation, native deadline/cancellation and lifecycle cleanup. Raw HTTP input/output and OnStarting remain forbidden. Initial metadata commits once; trailing metadata freezes at terminal admission. All native message operations retire before common scope cleanup.


## `P:NativeWeave.Server.Grpc.GrpcCallContext.TimeRemaining`

```csharp
public System.TimeSpan NativeWeave.Server.Grpc.GrpcCallContext.TimeRemaining | getter=Public;setter=;init=
```

Gets the remaining monotonic native deadline.

The existing request supplies identity, services, correlation, native deadline/cancellation and lifecycle cleanup. Raw HTTP input/output and OnStarting remain forbidden. Initial metadata commits once; trailing metadata freezes at terminal admission. All native message operations retire before common scope cleanup.


## `P:NativeWeave.Server.Grpc.GrpcException.StatusCode`

```csharp
public NativeWeave.Server.Grpc.GrpcStatusCode NativeWeave.Server.Grpc.GrpcException.StatusCode | getter=Public;setter=;init=
```

Gets the original admitted protocol status.

Only codes 1..16 are accepted. Detail is finite UTF-8 protocol text, and optional metadata obeys the same bounded profile. Unexpected application exceptions are redacted independently.


## `P:NativeWeave.Server.Grpc.GrpcException.Trailers`

```csharp
public NativeWeave.Server.Grpc.GrpcMetadata? NativeWeave.Server.Grpc.GrpcException.Trailers | getter=Public;setter=;init=
```

Gets optional bounded trailing metadata.

Only codes 1..16 are accepted. Detail is finite UTF-8 protocol text, and optional metadata obeys the same bounded profile. Unexpected application exceptions are redacted independently.


## `P:NativeWeave.Server.Grpc.GrpcMethodOptions.MaxDecodedMessageBytes`

```csharp
public System.Int32 NativeWeave.Server.Grpc.GrpcMethodOptions.MaxDecodedMessageBytes | getter=Public;setter=Public;init=False
```

Gets or sets the decoded message cap; default 1 MiB.

Copied at registration. Message and stream limits must be positive and fit checked host peak accounting; the method timeout, generic route timeout and strict client deadline can only shorten the native deadline. Identity compression is the initial profile.


## `P:NativeWeave.Server.Grpc.GrpcMethodOptions.MaxEncodedMessageBytes`

```csharp
public System.Int32 NativeWeave.Server.Grpc.GrpcMethodOptions.MaxEncodedMessageBytes | getter=Public;setter=Public;init=False
```

Gets or sets the encoded message cap; default 1 MiB.

Copied at registration. Message and stream limits must be positive and fit checked host peak accounting; the method timeout, generic route timeout and strict client deadline can only shorten the native deadline. Identity compression is the initial profile.


## `P:NativeWeave.Server.Grpc.GrpcMethodOptions.MaxStreamBytes`

```csharp
public System.Int64 NativeWeave.Server.Grpc.GrpcMethodOptions.MaxStreamBytes | getter=Public;setter=Public;init=False
```

Gets or sets cumulative payload bytes per direction; default 64 MiB, excluding framing.

Copied at registration. Message and stream limits must be positive and fit checked host peak accounting; the method timeout, generic route timeout and strict client deadline can only shorten the native deadline. Identity compression is the initial profile.


## `P:NativeWeave.Server.Grpc.GrpcMethodOptions.Timeout`

```csharp
public System.TimeSpan NativeWeave.Server.Grpc.GrpcMethodOptions.Timeout | getter=Public;setter=Public;init=False
```

Gets or sets the finite whole-call upper bound; default 30 seconds.

Copied at registration. Message and stream limits must be positive and fit checked host peak accounting; the method timeout, generic route timeout and strict client deadline can only shorten the native deadline. Identity compression is the initial profile.


## `T:NativeWeave.Server.Grpc.GrpcCallContext`

```csharp
NativeWeave.Server.Grpc.GrpcCallContext | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

Provides typed protocol state borrowed for one complete gRPC call.

The existing request supplies identity, services, correlation, native deadline/cancellation and lifecycle cleanup. Raw HTTP input/output and OnStarting remain forbidden. Initial metadata commits once; trailing metadata freezes at terminal admission. All native message operations retire before common scope cleanup.


## `T:NativeWeave.Server.Grpc.GrpcDecoder`1`

```csharp
NativeWeave.Server.Grpc.GrpcDecoder<T> | kind=Delegate;static=False;sealed=True;abstract=False;base=System.MulticastDelegate;interfaces= | invoke=public virtual T NativeWeave.Server.Grpc.GrpcDecoder<T>.Invoke(System.ReadOnlySpan<System.Byte> payload)
```

Decodes one complete message into an independently owned managed value.

The span is borrowed only for this call and must never escape. Use statically reachable generated protobuf code; no runtime schema discovery is performed. This explicit application callback owns its DTO graph and private allocations. The framework reserves its complete-message byte copy until the callback returns; the decoded limit bounds decompressed payload bytes, not the application's object graph.

- **T**: The statically known request or response message type.
- **payload**: Complete protobuf bytes without a gRPC frame header.
- **returns**: The independent managed message.

## `T:NativeWeave.Server.Grpc.GrpcEncoder`1`

```csharp
NativeWeave.Server.Grpc.GrpcEncoder<T> | kind=Delegate;static=False;sealed=True;abstract=False;base=System.MulticastDelegate;interfaces= | invoke=public virtual void NativeWeave.Server.Grpc.GrpcEncoder<T>.Invoke(T value, System.Buffers.IBufferWriter<System.Byte> destination)
```

Encodes one complete message into a bounded managed writer.

The destination enforces the configured encoded cap before allocation or growth. Tonic adds framing and owns transport. The framework reserves the bounded writer and overlapping native copy; application-owned messages and private codec allocations are outside those payload ledgers.

- **T**: The statically known request or response message type.
- **value**: The application-owned message to encode.
- **destination**: The scoped bounded destination; it must not be retained.

## `T:NativeWeave.Server.Grpc.GrpcEndpointExtensions`

```csharp
NativeWeave.Server.Grpc.GrpcEndpointExtensions | kind=Class;static=True;sealed=False;abstract=False;base=System.Object;interfaces=
```

Registers statically typed Tonic-backed methods on the existing Rust Host.

Registration is single-threaded before Start. There is one Host/runtime; no managed gRPC transport or reflection dispatch is installed. Each mapping copies finite options and binds one exact static delegate. Streams are single-use and allow one pending operation per direction.


## `T:NativeWeave.Server.Grpc.GrpcException`

```csharp
NativeWeave.Server.Grpc.GrpcException | kind=Class;static=False;sealed=True;abstract=False;base=System.Exception;interfaces=
```

Represents an explicit application-selected gRPC failure.

Only codes 1..16 are accepted. Detail is finite UTF-8 protocol text, and optional metadata obeys the same bounded profile. Unexpected application exceptions are redacted independently.


## `T:NativeWeave.Server.Grpc.GrpcMarshaller`1`

```csharp
NativeWeave.Server.Grpc.GrpcMarshaller<T> | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

Binds a statically reachable protobuf encoder and decoder.

Immutable and reusable application codec SPI. Ordinary managed message changes do not rebuild the native engine; Rust receives numeric codec identities and complete bytes, never managed Type objects. Supplied codec delegates own their private allocations and independently returned DTOs. Framework byte copies, bounded output writers and native retained owners have separate pregranted capacities; payload caps do not promise a process-heap or arbitrary DTO-graph bound.

- **T**: The statically known message type.

## `T:NativeWeave.Server.Grpc.GrpcMetadata`

```csharp
NativeWeave.Server.Grpc.GrpcMetadata | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

Stores bounded repeated ASCII text or copied binary gRPC metadata.

At most 200 entries and 32 KiB including count/length prefixes and repeated names. Binary names end in -bin. Borrowed request collections are read-only and expire with the call. Response headers copy at admission; trailers freeze at terminal admission. Binary getters return caller-owned copies.


## `T:NativeWeave.Server.Grpc.GrpcMethodKind`

```csharp
NativeWeave.Server.Grpc.GrpcMethodKind | kind=Enum;static=False;sealed=True;abstract=False;base=System.Enum;interfaces=
```

Specifies the number of inbound and outbound messages in one gRPC call.

Tonic owns framing and transport. Unary inputs are checked before managed decoding; each request has an independent cardinality counter.


## `T:NativeWeave.Server.Grpc.GrpcMethodOptions`

```csharp
NativeWeave.Server.Grpc.GrpcMethodOptions | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

Configures finite message, stream and whole-call limits.

Copied at registration. Message and stream limits must be positive and fit checked host peak accounting; the method timeout, generic route timeout and strict client deadline can only shorten the native deadline. Identity compression is the initial profile.


## `T:NativeWeave.Server.Grpc.GrpcMethod`2`

```csharp
NativeWeave.Server.Grpc.GrpcMethod<TRequest, TResponse> | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

Describes one immutable typed gRPC method.

Registration verifies the canonical path, cardinality, finite caps and configured HTTP/2 listener before startup. The descriptor introduces no runtime or native owner.

- **TRequest**: The static request message type.
- **TResponse**: The static response message type.

## `T:NativeWeave.Server.Grpc.GrpcStatusCode`

```csharp
NativeWeave.Server.Grpc.GrpcStatusCode | kind=Enum;static=False;sealed=True;abstract=False;base=System.Enum;interfaces=
```

Defines the stable wire-compatible gRPC status codes.

Native diagnostics preserve the actual code. Successful message delivery does not establish terminal status delivery or remote side-effect rollback.


## `T:NativeWeave.Server.Grpc.IGeneratedGrpcService`

```csharp
NativeWeave.Server.Grpc.IGeneratedGrpcService | kind=Interface;static=False;sealed=False;abstract=True;base=;interfaces=
```

Maps statically generated service descriptors and typed callbacks.

The application calls this before Start. Implementations directly call closed generic MapGrpc methods; assembly scanning and runtime schema compilation are unnecessary.


