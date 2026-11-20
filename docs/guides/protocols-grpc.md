# Static C# gRPC services on the Rust Server

`NativeWeave.Server.Grpc` maps statically typed C# messages and handlers onto the
existing Rust Server. Tonic owns gRPC framing, method cardinality, statuses and
streaming transport; the same Server instance supplies HTTP/2, rustls, admission,
request scopes and shutdown. The gRPC facade introduces no listener or runtime of
its own and has no Database dependency.

Use the [compiled API reference](../../src/Protocols.Grpc/api-reference.md) for the
exact declarations. The [runnable two-service sample](../../samples/api/protocols-grpc/)
contains minimal and advanced consumers. Its `Grpc.Net.Client` reference is an
independent demonstration client; the listening server uses Rust throughout.
These are component candidate instructions, not a claim that a particular package
version has already been published to the public NuGet feed.

## Packages and static protobuf code

An application references `NativeWeave.Server.Grpc` at the selected coherent
NativeWeave package version. `NativeWeave.Server` and its precompiled native asset
arrive transitively. Maintainers compile that asset; ordinary application builds,
new service registrations and `.proto` edits do not invoke Cargo.

The sample pins `Google.Protobuf` 3.36.2 and `Grpc.Tools` 2.85.0:

```xml
<ItemGroup>
  <PackageReference Include="NativeWeave.Server.Grpc" Version="[CANDIDATE_VERSION]" />
  <PackageReference Include="Google.Protobuf" Version="[3.36.2]" />
  <PackageReference Include="Grpc.Tools" Version="[2.85.0]" PrivateAssets="All" />
  <Protobuf Include="echo.proto" GrpcServices="None" />
</ItemGroup>
```

Replace `CANDIDATE_VERSION` with the exact version supplied by the tested feed.
`Grpc.Tools` runs the managed build's protobuf compiler and generates C# DTOs;
it does not generate or compile Rust. A server-only application needs no
`Grpc.Net.Client` package. The runnable sample uses `GrpcServices="Client"` and
`Grpc.Net.Client` 2.85.0 so its independently generated clients can exercise the
actual HTTP/2 server.

Bind each generated message to explicit static methods:

```csharp
using Google.Protobuf;
using NativeWeave.Server.Grpc;

var request = new GrpcMarshaller<EchoRequest>(
    static (value, output) => value.WriteTo(output),
    static bytes => EchoRequest.Parser.ParseFrom(bytes));
var response = new GrpcMarshaller<EchoReply>(
    static (value, output) => value.WriteTo(output),
    static bytes => EchoReply.Parser.ParseFrom(bytes));
var method = new GrpcMethod<EchoRequest, EchoReply>(
    "/nativeweave.samples.EchoApi/Echo", GrpcMethodKind.Unary,
    request, response);
```

`GrpcMarshaller<T>` is an explicit application extension point. It accepts
complete message bytes, without the five-byte gRPC frame prefix. The decoder
borrows its span only until it returns and must return an independent managed
value. The encoder must write to the supplied scoped writer and must not retain
it. No reflection-based service discovery or runtime schema compiler is involved.
The sample's message classes and client stubs are generated; its small
`IGeneratedGrpcService` mapping classes are explicit application-defined static
adapters. They call the same closed generic mapping methods available directly.

## Start and close one host

Configure an HTTP/2 listener, build the application, register both services and
start it:

```csharp
var builder = NativeServer.CreateBuilder();
builder.Listen(IPAddress.Loopback, 0,
    listener => listener.Protocols = ServerProtocols.Http2);
await using var application = builder.Build();
application.MapGrpc(new EchoService());
application.MapGrpc(new CounterService());
await application.StartAsync();

// Use application.BoundAddresses to connect the client.

var close = await application.CloseAsync(TimeSpan.FromSeconds(5));
```

Registration is single-threaded before startup. Method paths are canonical,
case-sensitive `/package.Service/Method` identities; descriptor cardinality must
match the selected mapping. Duplicate paths, missing HTTP/2 support and a peak
that cannot fit the configured Host budgets reject before serving. The loaded
module must advertise the required gRPC capability.

For TLS, set `listener.Tls = ServerTlsOptions.FromPemFiles(certificatePath,
privateKeyPath)`. The sample's `--tls` mode generates a short-lived local test
certificate and trusts only its exact fingerprint in its own client. Production
applications should use their configured certificates and normal client trust.
No certificate validation bypass is needed in the application server.

`CloseAsync` reports actual pending ownership within its supplied wait budget.
`await using` supplies deterministic cleanup. A timed-out wait never authorizes
freeing callback or message memory still reachable by native work; application
code should inspect the close result instead of inferring reclamation.

## Four method shapes

| Mapping | Handler input and output | Native behavior |
| --- | --- | --- |
| `MapGrpcUnary` | One request to `Task<TResponse>` | One complete request and one response through Tonic unary dispatch |
| `MapGrpcServerStreaming` | One request to `IAsyncEnumerable<TResponse>` | Pull one output message at a time |
| `MapGrpcClientStreaming` | `IAsyncEnumerable<TRequest>` to one response | Read until explicit client half-close or application early disposal |
| `MapGrpcDuplex` | Input and output enumerables | One pending read and one pending write may coexist |

The input enumerable starts reading on its first `MoveNextAsync`. It is single-use
and permits only one pending move. Elements are owned DTOs; the call context and
codec spans are borrowed. Use `call.CancellationToken` in application waits and
`WithCancellation` while consuming input. The adapter also supplies the effective
call token while enumerating output and disposes the enumerator on every exit.

An early input break requests stop for unread messages and joins their retirement.
It does not imply that a peer's already dispatched side effect was rolled back.
Cancellation may race with a successfully completed input EOF; that completed
result is not retroactively rewritten. Observe the call token and eventual close
ownership instead of requiring every racing read to throw.
Do not capture `GrpcCallContext`, its `RequestContext` or a message operation in
unjoined background work. The borrowed scope remains alive through both streaming
directions, protocol completion and required cleanup.

Unary and server-streaming methods require exactly one request frame. One frame
with zero payload bytes is a valid empty protobuf message. Zero frames returns
`Internal`; a second frame returns `InvalidArgument` before the business handler
runs. Each call has a fresh decoder/cardinality state. Client and duplex streaming
allow multiple frames within their finite cumulative limits.

## Limits and deadlines

| Option | Default | Meaning |
| --- | --- | --- |
| `MaxEncodedMessageBytes` | 1 MiB | One encoded message payload; gRPC framing is separate |
| `MaxDecodedMessageBytes` | 1 MiB | One decompressed protobuf payload, not the size of its CLR object graph |
| `MaxStreamBytes` | 64 MiB per direction | Cumulative message payload bytes; excludes HTTP headers and gRPC frame prefixes |
| `Timeout` | 30 seconds | Finite upper bound for the whole call |

Options are copied at registration. The generic route timeout, the method timeout
and the strict client `grpc-timeout` can only shorten the resolved deadline.
Native monotonic timing starts at admission and continues after response headers
and the first output message. Use `call.TimeRemaining` for the current remaining
budget. The client header admits at most one value with one to eight ASCII digits
followed by `H`, `M`, `S`, `m`, `u` or `n`; malformed values reject and zero is
already expired.

The Host's ordinary HTTP body and JSON defaults do not silently cap gRPC. Explicit
HTTP body/JSON limit overrides on a gRPC endpoint are rejected at freeze; configure
the protocol options instead. Identity compression is the implemented profile.
Additional compression, reflection services and a NativeWeave client runtime are
not supplied by this facade.

## Metadata, statuses and permitted request access

`RequestHeaders` is read-only decoded gRPC metadata. Repeated values preserve their
per-name order, and `GetBinaryValues("name-bin")` returns binary values rather than
HTTP base64 text. The Rust adapter performs bounded splitting and decoding of
comma-coalesced binary HTTP fields. Ordinary
`call.RequestContext.Request.Headers` continues to expose HTTP wire text.

`GrpcMetadata.Add` accepts checked ASCII metadata; `AddBinary` copies binary bytes
for a `-bin` name. Metadata is limited to 200 entries and 32 KiB in aggregate.
`WriteResponseHeadersAsync` snapshots and commits initial metadata once.
`ResponseTrailers` remains mutable until terminal admission. Initial headers or
output EOF alone do not prove the peer received final status or trailers.

Throw `GrpcException` for an intentional public failure, such as `NotFound` with
bounded trailing metadata. Codes 1 through 16 are admitted; `Ok` and unknown
numeric values cannot be constructed as failures. Unexpected application
exceptions produce redacted `Internal`. Framed encoded-length excess preserves
Tonic's `OutOfRange`, while decoded/resource excess uses `ResourceExhausted`.
Malformed protobuf, malformed deadlines and excess unary cardinality preserve
their distinct invalid-input paths. Cancellation does not promise write rollback
or make retry safe.

The existing request scope supplies `Services`, `User`, `Items`, trace identity,
request metadata and permitted completion/disposal hooks. Authentication and
authorization denial produce framed `Unauthenticated` or `PermissionDenied`.
An inherited or endpoint `ServerFilter` is rejected for a gRPC route. Global
middleware can inspect allowed metadata and await `next` exactly once.

Raw HTTP request bodies/trailers and raw response status, headers, trailers, body,
JSON writes, flush/completion and `OnStarting` are unavailable on gRPC requests.
The managed wrapper rejects before exposing a mutable object or selecting a body
mode; the native ABI independently rejects the raw operations. Use typed messages,
gRPC metadata and protocol status instead. No second HTTP/JSON response is appended
after a protocol response has committed.

## Ownership and boundary cost

Response headers and trailers retain their complete native metadata grant until
their transmitted values and clones are released. The HTTP/2 library's per-value
sensitive flag prevents insertion of these request-owned values into its HPACK
dynamic table, so a completed request's grant and admission slot cannot be held
by that connection-level cache. An existing exact or static index may still be
used without retaining this value's owner. Header names and values are unchanged;
the sensitive flag remains set on every retained value and its clones. Global
HTTP/2 header compression remains enabled, but these values can require more
header bytes on repeated calls. This lifetime
rule does not change payload compression: the implemented gRPC profile remains
identity only. No public API or native ABI changes are required.

The framework grants bounded storage for complete-message copies, its outbound
writer and the independently retained native message buffers before allocation.
Metadata and overlapping native/frame/output storage have their own admission
accounting. HTTP/2 flow-control windows do not replace those memory grants.

The application decoder's returned DTO graph and allocations internal to custom
codecs are application-owned. `MaxDecodedMessageBytes` limits decompressed payload
bytes; it cannot bound an arbitrary CLR object graph or a custom decoder's private
allocations. Keep generated/static codecs appropriate for the application schema.
The borrowed input span must not escape into that graph.

Each complete inbound message requires native read/take/retirement, a bounded
framework copy and static decode. Outbound work serializes to the granted writer,
reserves native write storage, copies complete bytes and joins completion and
retirement. Header/status operations add separate native ownership. This is not a
zero-copy or zero-allocation claim; there is no measured performance advantage in
this component guide.

## Run the component examples

Maintainers supply the precompiled Server asset through the existing
`NativeWeaveServerAssetPath` property. Ordinary NuGet consumers receive that asset
through restore and use the standard build/publish commands.

```sh
dotnet run --project samples/api/protocols-grpc -- --minimal
dotnet run --project samples/api/protocols-grpc -- --tls
dotnet publish samples/api/protocols-grpc -c Release -r linux-x64 -p:PublishAot=true
```

The sample's advanced mode exercises all four cardinalities, input half-close,
binary metadata, explicit error trailers and native timeout after committed
headers. Its two `.proto` services use distinct generated DTO pairs with one
unchanged native asset. Add `--require-aot` when running the published executable
to verify that runtime dynamic-code support and compilation are disabled.

`tests/Protocols.Grpc/package_probe.py` composes the current tested packages using
the existing A14 packer, then builds and executes the copied sample from ordinary
`PackageReference` entries with independent NuGet state and no accessible Rust
toolchain. It records exact package, SDK-selected native, input and executable
hashes, and checks a managed `.proto`/handler edit against the same native bytes.
This is scoped component package consumption, not a claim of the final pristine
machine/release gates. This task's actual validation is Linux x64 only.

`tests/Protocols.Grpc/verify.py` creates the assembled component receipt. Supply
`--native`, `--expected-native-sha256` and `--native-source-commit` for the exact
reviewed build, plus `--native-component-receipt`, `--design`, `--sdk` and a fresh
`--output` directory. It freezes the native file, checks all 48 exports and the
pinned layout, verifies the compiled public API/XML and negative consumers, then
runs the actual transport tests in JIT and NativeAOT. The native component receipt
is reused without rebuilding Rust. An optional `--package-summary` reuses the
ordinary-consumer proof only after checking unchanged producer/sample inputs,
native identity, command logs and retained artifacts. The receipt distinguishes
the managed source commit from the commit that produced the native binary.

The fixed design is [server API §7](https://github.com/IzuSharp/NativeWeave-Design/blob/c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9/docs/api/server.md)
and [the protocol contract](https://github.com/IzuSharp/NativeWeave-Design/blob/c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9/docs/server/protocols-and-observability.md).
