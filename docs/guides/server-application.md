# C# Server application

`NativeWeave.Server` supplies typed application routing, handler invocation, static JSON, lifecycle and DI scopes. The independently built Rust Server module supplies the single Tokio runtime, Axum routing, Hyper HTTP/1 and HTTP/2, rustls transport, admission, deadlines and bounded Body ownership. Adding a C# handler or DTO changes managed code and descriptors, not the native library.

The public contract is checked against Design commit `c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9`. See the generated [API reference](../../src/Server.Application/api-reference.md), [static examples](../../samples/api/server-application/), and [stream ownership](server-streaming.md). This task's current execution row is Linux x64; other platform acceptance is separate. Example evidence is bound by the task verifier, not inferred from compiling a declaration fixture.

## Minimal application

```csharp
using System.Net;
using NativeWeave.Server;

await using var app = NativeServer.CreateBuilder()
    .Listen(IPAddress.Loopback, 5000)
    .Build();
app.MapGet("/hello", () => Results.Text("Hello from C# and Rust."));
await app.RunAsync();
```

The default is one loopback HTTP/1 listener on port5000. The first explicit `Listen` replaces it. `ListenLocalhost` explicitly requires both IPv4 and IPv6 to bind successfully. `Build` copies configuration and inspects the packaged native module; `StartAsync` separately freezes all route/group configuration and succeeds only when every selected listener is accepting. There is no global initializer or per-handler runtime.

Package consumers use normal `dotnet build`, `dotnet run` and `dotnet publish -r linux-x64 -p:PublishAot=true`. Only the maintainer builds Rust and constructs a validated package closure. The package's existing SDK-output task records the selected native destination and hash. Consumer project imports do not run Cargo or guess a NuGet cache path. Optional `NativeWeave.Server.Hosting` registers the same application as an `IHostedService`; it creates no alternate listener or root service provider.

## Typed JSON and generated endpoints

Register a source-declared `JsonSerializerContext` using `UseJson`, or supply the exact `JsonTypeInfo<T>` to a typed mapping or result. Missing metadata fails before listener admission. An arbitrary runtime object type does not cause reflection serialization or dynamic serializer generation. JSON request binding validates uncompressed JSON media types, the route's encoded byte cap and conservative managed materialization credit. Required empty/malformed/null input is400, unsupported media/encoding is415, and over-cap input is413.

An optional `[ServerEndpoints]` static partial class receives `Map(ServerRoutes)`. The generator emits ordinary existing `MapRaw`/`MapMethods` calls and direct typed binding. It recognizes explicit route/query/header values implementing `IParsable<T>`, query arrays, one JSON body, explicit DI service parameters, `RequestContext` and `CancellationToken`. A static input context with `[JsonSerializable]` supplies DTO metadata; generated code does not read another generator's output to discover a model. Ordinary result returns, typed DTO returns and their `Task<T>` forms are supported. `void`/`Task` requires explicit context access for direct output.

Unsupported ref/out/open/ref-struct/dynamic forms, ambiguous bindings and absent JSON metadata fail with the exact `NWAPI-S002`, `NWAPI-S003` and `NWAPI-S001` contract identifiers. Roslyn's generator diagnostic callback rejects hyphenated IDs; the implementation uses compiler-supported `ObsoleteAttribute.DiagnosticId` on inaccessible generated error sentinels with mapped source locations. Actual negative consumers verify these compiler errors. Valid consumers contain no sentinel and expose no extra runtime API. No diagnostic identifier is silently renumbered.

## Request and result ownership

A request context is borrowed through awaited dispatch and cleanup. Its request scope and Items dictionary are lazy. Do not capture the context for detached work. Middleware/filter continuations are single-use; an already-started continuation retains the request until it completes even when the caller forgot to await it. A filter can catch a binding exception and return a custom response; this does not rethrow an already-handled downstream failure.

Request body consumption chooses exactly one of stream, owned-chunk or static-JSON mode on first use. A `BodyChunk` is independently owned and disposable; keeping it after dispatch retains its full native allocation credit. Borrowed streams are asynchronous only. Read cancellation is separate from native retirement; retained memory is never freed by merely completing a caller task.

Only one response write may be pending. Raw `ReadOnlyMemory<byte>` is already application-owned and is not falsely charged as a second managed framework copy. Framework UTF-8/JSON staging is separately credited; the native adapter reserves queue/payload credit before its own copy or protocol formatting. `StartAsync` and `CompleteAsync` each coalesce one attempt. Starting callbacks run LIFO; completion callbacks then registered asynchronous disposables run LIFO with exception isolation, before the request scope is disposed.

Every successful incomplete HTTP response is completed by the common dispatch tail. An untouched raw/generated-void handler or middleware short circuit yields empty200; a selected201 is preserved. A failed precommit response gets at most one bounded fallback. A failure after final headers aborts the transport instead of sending another final response. HEAD suppresses producers and body bytes; stream ownership still transfers at successful result execution admission and is then disposed according to `leaveOpen`.204/205/304 and Content-Length mismatches are checked explicitly.

## Shutdown and capabilities

`CloseAsync(timeout)` closes admission and returns an honest completed/pending observation. Pending retains request slots, callbacks and all still-live managed/native byte credits. `DisposeAsync` joins actual retirement without cancellation; invoking it from the same application's handler/cleanup is rejected to prevent self-join. `RunAsync` scopes Ctrl+C/SIGTERM registration and joins ownership on native lifetime failure or cancellation before rethrowing a host fault.

Capabilities are inspected before use. gRPC/MCP adapters own their typed protocol surfaces and share the same Rust Host. Raw HTTP access is restricted after protocol admission. Trusted forwarding is an explicit frozen native policy; arbitrary forwarding headers do not establish identity. No JWT validator, implicit proxy trust or cookie/session subsystem is installed.

## Verification and costs

The component verifier compares278 exact compiled symbols, including nullability, optional defaults, generic constraints, record/delegate API and accessors; explicit symbols have XML parameters and resolvable lifetime/cost remarks in the generated reference. It compiles generated positive/negative consumers and tests application cleanup, static JSON, bodyless output and continuation retention. Real Host, package and actual NativeAOT execution are separate recorded gates.

Configuration allocates finite copied descriptors once. A request uses one native ticket and a managed context, with a scope only when services are requested. Typed dispatch avoids an object-array binder. JSON, UTF-8, callback and source-stream buffers are measured separately from application DTO graphs, driver allocations and process RSS. No performance advantage or end-to-end zero-copy claim is inferred from these ownership checks.
