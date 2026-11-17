# Server application API reference

Generated from the actual compiled public API and XML; compared against the exact pinned design declarations. B15 owns application lifecycle, routes, typed binding and results; B17 owns native transport.

## `F:NativeWeave.Server.ServerCapabilities.Grpc`

```csharp
NativeWeave.Server.ServerCapabilities.Grpc = 16
```

Native Tonic method capability.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `F:NativeWeave.Server.ServerCapabilities.Http1`

```csharp
NativeWeave.Server.ServerCapabilities.Http1 = 1
```

Native HTTP/1.1 capability.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `F:NativeWeave.Server.ServerCapabilities.Http2`

```csharp
NativeWeave.Server.ServerCapabilities.Http2 = 2
```

Native HTTP/2 capability.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `F:NativeWeave.Server.ServerCapabilities.Mcp`

```csharp
NativeWeave.Server.ServerCapabilities.Mcp = 32
```

Native rmcp capability.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `F:NativeWeave.Server.ServerCapabilities.None`

```csharp
NativeWeave.Server.ServerCapabilities.None = 0
```

No capability bits.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `F:NativeWeave.Server.ServerCapabilities.ServerSentEvents`

```csharp
NativeWeave.Server.ServerCapabilities.ServerSentEvents = 64
```

Native Axum SSE formatting capability.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `F:NativeWeave.Server.ServerCapabilities.Tls`

```csharp
NativeWeave.Server.ServerCapabilities.Tls = 4
```

Native rustls capability.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `F:NativeWeave.Server.ServerCapabilities.Trailers`

```csharp
NativeWeave.Server.ServerCapabilities.Trailers = 8
```

Validated HTTP trailer capability.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `F:NativeWeave.Server.ServerCloseStatus.Completed`

```csharp
NativeWeave.Server.ServerCloseStatus.Completed = 0
```

The safe retirement join completed.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `F:NativeWeave.Server.ServerCloseStatus.Pending`

```csharp
NativeWeave.Server.ServerCloseStatus.Pending = 1
```

The wait ended while owners and credits remain retained.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `F:NativeWeave.Server.ServerProtocols.Http1`

```csharp
NativeWeave.Server.ServerProtocols.Http1 = 1
```

HTTP/1.1 transport.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `F:NativeWeave.Server.ServerProtocols.Http1AndHttp2`

```csharp
NativeWeave.Server.ServerProtocols.Http1AndHttp2 = NativeWeave.Server.ServerProtocols.Http1 | NativeWeave.Server.ServerProtocols.Http2
```

HTTP/1.1 and HTTP/2 negotiated by TLS ALPN.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `F:NativeWeave.Server.ServerProtocols.Http2`

```csharp
NativeWeave.Server.ServerProtocols.Http2 = 2
```

HTTP/2 transport.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `F:NativeWeave.Server.ServerProtocols.None`

```csharp
NativeWeave.Server.ServerProtocols.None = 0
```

No protocol selected; invalid for a listener.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `F:NativeWeave.Server.ServerState.Created`

```csharp
NativeWeave.Server.ServerState.Created = 0
```

Configured but no listeners started.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `F:NativeWeave.Server.ServerState.Disposed`

```csharp
NativeWeave.Server.ServerState.Disposed = 6
```

Owned application services and native instance are released.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `F:NativeWeave.Server.ServerState.Faulted`

```csharp
NativeWeave.Server.ServerState.Faulted = 5
```

A host failure has been observed; cleanup still applies.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `F:NativeWeave.Server.ServerState.Running`

```csharp
NativeWeave.Server.ServerState.Running = 2
```

All listeners accept requests.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `F:NativeWeave.Server.ServerState.Starting`

```csharp
NativeWeave.Server.ServerState.Starting = 1
```

Startup has accepted responsibility.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `F:NativeWeave.Server.ServerState.Stopped`

```csharp
NativeWeave.Server.ServerState.Stopped = 4
```

All instance work has retired.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `F:NativeWeave.Server.ServerState.Stopping`

```csharp
NativeWeave.Server.ServerState.Stopping = 3
```

New admission is closed while owners retire.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `M:NativeWeave.Server.HeaderCollection.Append(System.String,System.String)`

```csharp
public void NativeWeave.Server.HeaderCollection.Append(System.String name, System.String value)
```

Appends one response field without combining repeated values.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **name**: The name value described by this operation.
- **value**: The value value described by this operation.

## `M:NativeWeave.Server.HeaderCollection.Contains(System.String)`

```csharp
public System.Boolean NativeWeave.Server.HeaderCollection.Contains(System.String name)
```

Tests for a field name.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **name**: The name value described by this operation.
- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.HeaderCollection.GetSingle(System.String)`

```csharp
public System.String? NativeWeave.Server.HeaderCollection.GetSingle(System.String name)
```

Gets a single value or null; repeated input is rejected without comma folding.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **name**: The name value described by this operation.
- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.HeaderCollection.Remove(System.String)`

```csharp
public System.Boolean NativeWeave.Server.HeaderCollection.Remove(System.String name)
```

Removes a mutable field before commit.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **name**: The name value described by this operation.
- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.HeaderCollection.Set(System.String,System.String)`

```csharp
public void NativeWeave.Server.HeaderCollection.Set(System.String name, System.String value)
```

Replaces response values before commit; request instances are read-only.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **name**: The name value described by this operation.
- **value**: The value value described by this operation.

## `M:NativeWeave.Server.HeaderCollection.ToDictionary`

```csharp
public System.Collections.Generic.IReadOnlyDictionary<System.String, System.Collections.Generic.IReadOnlyList<System.String>> NativeWeave.Server.HeaderCollection.ToDictionary()
```

Copies a bounded caller-owned metadata snapshot.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.HeaderParameterAttribute.#ctor(System.String)`

```csharp
public NativeWeave.Server.HeaderParameterAttribute.HeaderParameterAttribute(System.String name)
```

Uses one validated case-insensitive header name.

Finite annotated static handler whitelist; missing/ambiguous shapes are compile diagnostics. Build-time generation; no per-handler Rust compilation, reflection scan, Expression.Compile or runtime argument object[]. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **name**: The name value described by this operation.

## `M:NativeWeave.Server.Hosting.NativeServerServiceCollectionExtensions.AddNativeWeaveServer(Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Func{System.IServiceProvider,NativeWeave.Server.NativeServerApplication})`

```csharp
public static Microsoft.Extensions.DependencyInjection.IServiceCollection NativeWeave.Server.Hosting.NativeServerServiceCollectionExtensions.AddNativeWeaveServer(Microsoft.Extensions.DependencyInjection.IServiceCollection services, System.Func<System.IServiceProvider, NativeWeave.Server.NativeServerApplication> factory)
```

Registers one application singleton and hosted lifecycle adapter; host async disposal owns the final safe join.

Stop requests bounded close; native retirement can remain pending until async container disposal. The factory must not recursively build or dispose the external Host provider.

- **services**: Explicit service registrations.
- **factory**: Creates the independently owned application from the host provider.
- **returns**: The same service collection.
- **T:System.ArgumentNullException**: A required argument is null.
- **T:System.InvalidOperationException**: An application lifecycle adapter is already registered.

## `M:NativeWeave.Server.IServerAuthenticator.AuthenticateAsync(NativeWeave.Server.RequestContext,System.Threading.CancellationToken)`

```csharp
System.Threading.Tasks.Task<System.Security.Claims.ClaimsPrincipal?> NativeWeave.Server.IServerAuthenticator.AuthenticateAsync(NativeWeave.Server.RequestContext context, [System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Returns a principal on success or null when no supported credentials are present.

Explicit caller-owned thread-safe auth SPI; application policy, no built-in JWT/session stack. Admitted gRPC denial uses Tonic Unauthenticated16/PermissionDenied7 framing; ordinary HTTP and MCP pre-parse denial use401/403. Bounded registered callbacks; next single-use; exceptions redacted and classified before/after response commit; application service work remains caller cost. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **context**: The current borrowed request context; it cannot escape awaited dispatch and cleanup.
- **cancellationToken**: Caller cancellation intent; cancellation never releases native owners before their retirement join.
- **returns**: An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.

## `M:NativeWeave.Server.IServerAuthorizer.AuthorizeAsync(NativeWeave.Server.RequestContext,System.String,System.Threading.CancellationToken)`

```csharp
System.Threading.Tasks.Task<System.Boolean> NativeWeave.Server.IServerAuthorizer.AuthorizeAsync(NativeWeave.Server.RequestContext context, System.String policy, [System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Returns whether the principal is permitted by the named policy.

Explicit caller-owned thread-safe auth SPI; application policy, no built-in JWT/session stack. Bounded registered callbacks; next single-use; exceptions redacted and classified before/after response commit; application service work remains caller cost. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **context**: The current borrowed request context; it cannot escape awaited dispatch and cleanup.
- **policy**: The policy value described by this operation.
- **cancellationToken**: Caller cancellation intent; cancellation never releases native owners before their retirement join.
- **returns**: An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.

## `M:NativeWeave.Server.NativeServer.CreateBuilder(System.String[])`

```csharp
public static NativeWeave.Server.NativeServerBuilder NativeWeave.Server.NativeServer.CreateBuilder([System.String[]? args = null])
```

Creates a builder with finite loopback defaults; explicit arguments populate configuration.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **args**: The args value described by this operation; omitted calls use null.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.NativeServerApplication.CloseAsync(System.TimeSpan,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<NativeWeave.Server.ServerCloseResult> NativeWeave.Server.NativeServerApplication.CloseAsync(System.TimeSpan timeout, [System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Closes admission and waits a finite interval; Pending retains outstanding roots and credits.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **timeout**: The finite checked wait or request duration specified by this operation.
- **cancellationToken**: Caller cancellation intent; cancellation never releases native owners before their retirement join.
- **returns**: An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.

## `M:NativeWeave.Server.NativeServerApplication.DisposeAsync`

```csharp
public System.Threading.Tasks.ValueTask NativeWeave.Server.NativeServerApplication.DisposeAsync()
```

Closes and joins managed/native retirement without cancellation; self-join from this application's dispatch is rejected.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **returns**: An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.

## `M:NativeWeave.Server.NativeServerApplication.ReloadTlsAsync(System.String,NativeWeave.Server.ServerTlsOptions,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task NativeWeave.Server.NativeServerApplication.ReloadTlsAsync(System.String listenerName, NativeWeave.Server.ServerTlsOptions options, [System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Validates and atomically replaces one native TLS listener snapshot; existing connections retain their original snapshot.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **listenerName**: The listenerName value described by this operation.
- **options**: The options value described by this operation.
- **cancellationToken**: Caller cancellation intent; cancellation never releases native owners before their retirement join.
- **returns**: An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.

## `M:NativeWeave.Server.NativeServerApplication.RunAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task NativeWeave.Server.NativeServerApplication.RunAsync([System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Starts and waits for shutdown, then joins all ownership; Ctrl+C/SIGTERM handlers are scoped to this call.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **cancellationToken**: Caller cancellation intent; cancellation never releases native owners before their retirement join.
- **returns**: An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.

## `M:NativeWeave.Server.NativeServerApplication.StartAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task NativeWeave.Server.NativeServerApplication.StartAsync([System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Freezes all routes and starts once; success means every native listener accepts requests.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **cancellationToken**: Caller cancellation intent; cancellation never releases native owners before their retirement join.
- **returns**: An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.

## `M:NativeWeave.Server.NativeServerApplication.Use(NativeWeave.Server.ServerMiddleware)`

```csharp
public NativeWeave.Server.NativeServerApplication NativeWeave.Server.NativeServerApplication.Use(NativeWeave.Server.ServerMiddleware middleware)
```

Adds middleware before freeze; the first registered middleware is outermost.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **middleware**: The middleware value described by this operation.
- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.NativeServerBuilder.Build`

```csharp
public NativeWeave.Server.NativeServerApplication NativeWeave.Server.NativeServerBuilder.Build()
```

Builds once, validates and snapshots configuration, and loads the native module without starting listeners.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.NativeServerBuilder.Configure(Microsoft.Extensions.Configuration.IConfiguration)`

```csharp
public NativeWeave.Server.NativeServerBuilder NativeWeave.Server.NativeServerBuilder.Configure(Microsoft.Extensions.Configuration.IConfiguration section)
```

Applies supported scalar keys immediately using invariant checked parsing.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **section**: The section value described by this operation.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.NativeServerBuilder.Listen(System.Net.IPAddress,System.Int32,System.Action{NativeWeave.Server.ServerListenOptions})`

```csharp
public NativeWeave.Server.NativeServerBuilder NativeWeave.Server.NativeServerBuilder.Listen(System.Net.IPAddress address, System.Int32 port, [System.Action<NativeWeave.Server.ServerListenOptions>? configure = null])
```

Registers an explicit native listener; the first explicit registration replaces the loopback default.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **address**: The address value described by this operation.
- **port**: The port value described by this operation.
- **configure**: Optional synchronous configuration callback run before the options snapshot is frozen.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.NativeServerBuilder.ListenLocalhost(System.Int32,System.Action{NativeWeave.Server.ServerListenOptions})`

```csharp
public NativeWeave.Server.NativeServerBuilder NativeWeave.Server.NativeServerBuilder.ListenLocalhost([System.Int32 port = 5000], [System.Action<NativeWeave.Server.ServerListenOptions>? configure = null])
```

Registers both IPv4 and IPv6 loopback listeners; startup must bind both.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **port**: The port value described by this operation; omitted calls use 5000.
- **configure**: Optional synchronous configuration callback run before the options snapshot is frozen.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.NativeServerBuilder.UseAuthentication(NativeWeave.Server.IServerAuthenticator)`

```csharp
public NativeWeave.Server.NativeServerBuilder NativeWeave.Server.NativeServerBuilder.UseAuthentication(NativeWeave.Server.IServerAuthenticator authenticator)
```

Registers a caller-owned, thread-safe application authenticator.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **authenticator**: The authenticator value described by this operation.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.NativeServerBuilder.UseAuthorization(NativeWeave.Server.IServerAuthorizer)`

```csharp
public NativeWeave.Server.NativeServerBuilder NativeWeave.Server.NativeServerBuilder.UseAuthorization(NativeWeave.Server.IServerAuthorizer authorizer)
```

Registers a caller-owned, thread-safe named-policy authorizer.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **authorizer**: The authorizer value described by this operation.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.NativeServerBuilder.UseJson(System.Text.Json.Serialization.JsonSerializerContext)`

```csharp
public NativeWeave.Server.NativeServerBuilder NativeWeave.Server.NativeServerBuilder.UseJson(System.Text.Json.Serialization.JsonSerializerContext context)
```

Registers explicit static JSON metadata without runtime serializer generation.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **context**: The current borrowed request context; it cannot escape awaited dispatch and cleanup.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.NativeServerBuilder.UseServiceProvider(System.IServiceProvider,System.Boolean)`

```csharp
public NativeWeave.Server.NativeServerBuilder NativeWeave.Server.NativeServerBuilder.UseServiceProvider(System.IServiceProvider provider, [System.Boolean disposeWithServer = false])
```

Uses an external scope-capable service provider; disposal transfers only when explicitly selected.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **provider**: An explicit scope-capable service provider; ownership transfers only when disposeWithServer is true.
- **disposeWithServer**: Whether application disposal also disposes the supplied provider; false retains caller ownership.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ParameterValues.GetValues(System.String)`

```csharp
public System.Collections.Generic.IReadOnlyList<System.String> NativeWeave.Server.ParameterValues.GetValues(System.String name)
```

Gets raw decoded values; returns an empty list when absent.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **name**: The name value described by this operation.
- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.ParameterValues.Get``1(System.String)`

```csharp
public T NativeWeave.Server.ParameterValues.Get<T>(System.String name) where T : System.IParsable<T>
```

Parses exactly one required value or throws a bounded binding error.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **name**: The name value described by this operation.
- **T**: The statically known result type; JSON paths require explicit or registered static metadata.
- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.ParameterValues.TryGet``1(System.String,``0@)`

```csharp
public System.Boolean NativeWeave.Server.ParameterValues.TryGet<T>(System.String name, out T value) where T : System.IParsable<T>
```

Parses one value; false means absent, repeated or malformed.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **name**: The name value described by this operation.
- **value**: The value value described by this operation.
- **T**: The statically known result type; JSON paths require explicit or registered static metadata.
- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.QueryParameterAttribute.#ctor(System.String)`

```csharp
public NativeWeave.Server.QueryParameterAttribute.QueryParameterAttribute([System.String? name = null])
```

Uses the supplied name, or the parameter name when null.

Finite annotated static handler whitelist; missing/ambiguous shapes are compile diagnostics. Build-time generation; no per-handler Rust compilation, reflection scan, Expression.Compile or runtime argument object[]. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **name**: The name value described by this operation; omitted calls use null.

## `M:NativeWeave.Server.RequestBindingException.#ctor(System.String,System.String)`

```csharp
public NativeWeave.Server.RequestBindingException.RequestBindingException(System.String parameterName, System.String message)
```

Creates a binding error without including the raw input value.

Explicit caller-owned thread-safe auth SPI; application policy, no built-in JWT/session stack. Bounded registered callbacks; next single-use; exceptions redacted and classified before/after response commit; application service work remains caller cost. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **parameterName**: The parameterName value described by this operation.
- **message**: The message value described by this operation.

## `M:NativeWeave.Server.RequestContext.Abort`

```csharp
public void NativeWeave.Server.RequestContext.Abort()
```

Requests transport abort without claiming native retirement.

Borrowed request lifecycle; available cancellation signal, not exact upstream H2 reset certification. gRPC permits metadata/services/identity/Items/cancel/Abort and lifecycle hooks only; raw body/output and OnStarting are rejected. MCP has only its pre-parse HTTP denial exception. Lazy scope/Items (<=128 entries); sticky bounded control notification; TimeRemaining is monotonic; no escaped context after dispatch. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `M:NativeWeave.Server.Results.Bytes(System.ReadOnlySpan{System.Byte},System.String)`

```csharp
public static NativeWeave.Server.ServerResult NativeWeave.Server.Results.Bytes(System.ReadOnlySpan<System.Byte> bytes, [System.String contentType = "application/octet-stream"])
```

Copies at most 1 MiB into a reusable immutable result.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Bounded metadata-only native queue wait followed by nonwaiting request/global payload grant and rollback before copy; serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **bytes**: The payload or finite byte limit described in the summary; application-owned input is not an unbounded framework allocation.
- **contentType**: The contentType value described by this operation; omitted calls use "application/octet-stream".
- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.Results.Created``1(System.String,``0,System.Text.Json.Serialization.Metadata.JsonTypeInfo{``0})`

```csharp
public static NativeWeave.Server.ServerResult NativeWeave.Server.Results.Created<T>(System.String location, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
```

Creates a 201 static JSON result with a validated Location.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Bounded metadata-only native queue wait followed by nonwaiting request/global payload grant and rollback before copy; serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **location**: The location value described by this operation.
- **value**: The value value described by this operation.
- **typeInfo**: Static JSON metadata; nullable registration arguments resolve from UseJson contexts at route freeze, without reflection fallback.
- **T**: The statically known result type; JSON paths require explicit or registered static metadata.
- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.Results.JsonStream``1(System.Collections.Generic.IAsyncEnumerable{``0},System.Text.Json.Serialization.Metadata.JsonTypeInfo{``0})`

```csharp
public static NativeWeave.Server.ServerResult NativeWeave.Server.Results.JsonStream<T>(System.Collections.Generic.IAsyncEnumerable<T> source, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
```

Streams a statically serialized JSON array with one element in flight.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Bounded metadata-only native queue wait followed by nonwaiting request/global payload grant and rollback before copy; serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **source**: The source stream; successful result execution admission transfers disposal responsibility.
- **typeInfo**: Static JSON metadata; nullable registration arguments resolve from UseJson contexts at route freeze, without reflection fallback.
- **T**: The statically known result type; JSON paths require explicit or registered static metadata.
- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.Results.Json``1(``0,System.Text.Json.Serialization.Metadata.JsonTypeInfo{``0},System.Int32)`

```csharp
public static NativeWeave.Server.ServerResult NativeWeave.Server.Results.Json<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, [System.Int32 statusCode = 200])
```

Creates single-execution JSON with explicit static metadata and borrowed DTO state.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Bounded metadata-only native queue wait followed by nonwaiting request/global payload grant and rollback before copy; serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **value**: The value value described by this operation.
- **typeInfo**: Static JSON metadata; nullable registration arguments resolve from UseJson contexts at route freeze, without reflection fallback.
- **statusCode**: A final HTTP status in 200 through 599; bodyless status restrictions apply.
- **T**: The statically known result type; JSON paths require explicit or registered static metadata.
- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.Results.NoContent`

```csharp
public static NativeWeave.Server.ServerResult NativeWeave.Server.Results.NoContent()
```

Creates an empty 204 result.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Bounded metadata-only native queue wait followed by nonwaiting request/global payload grant and rollback before copy; serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.Results.NotFound`

```csharp
public static NativeWeave.Server.ServerResult NativeWeave.Server.Results.NotFound()
```

Creates an empty 404 result.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Bounded metadata-only native queue wait followed by nonwaiting request/global payload grant and rollback before copy; serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.Results.Problem(System.Int32,System.String,System.String,System.String,System.String)`

```csharp
public static NativeWeave.Server.ServerResult NativeWeave.Server.Results.Problem([System.Int32 statusCode = 500], [System.String? title = null], [System.String? detail = null], [System.String? type = null], [System.String? instance = null])
```

Creates a bounded problem response using framework-generated metadata.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Bounded metadata-only native queue wait followed by nonwaiting request/global payload grant and rollback before copy; serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **statusCode**: A final HTTP status in 200 through 599; bodyless status restrictions apply.
- **title**: The title value described by this operation; omitted calls use null.
- **detail**: The detail value described by this operation; omitted calls use null.
- **type**: The type value described by this operation; omitted calls use null.
- **instance**: The instance value described by this operation; omitted calls use null.
- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.Results.Redirect(System.String,System.Boolean,System.Boolean)`

```csharp
public static NativeWeave.Server.ServerResult NativeWeave.Server.Results.Redirect(System.String location, [System.Boolean permanent = false], [System.Boolean preserveMethod = false])
```

Creates a validated redirect; the application explicitly chooses external destinations.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Bounded metadata-only native queue wait followed by nonwaiting request/global payload grant and rollback before copy; serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **location**: The location value described by this operation.
- **permanent**: The permanent value described by this operation; omitted calls use false.
- **preserveMethod**: The preserveMethod value described by this operation; omitted calls use false.
- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.Results.ServerSentEvents(System.Collections.Generic.IAsyncEnumerable{NativeWeave.Server.ServerEvent})`

```csharp
public static NativeWeave.Server.ServerResult NativeWeave.Server.Results.ServerSentEvents(System.Collections.Generic.IAsyncEnumerable<NativeWeave.Server.ServerEvent> source)
```

Streams bounded SSE through native Axum formatting and acknowledges each event before advancing.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Bounded metadata-only native queue wait followed by nonwaiting request/global payload grant and rollback before copy; serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **source**: The source stream; successful result execution admission transfers disposal responsibility.
- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.Results.StatusCode(System.Int32)`

```csharp
public static NativeWeave.Server.ServerResult NativeWeave.Server.Results.StatusCode(System.Int32 statusCode)
```

Creates an empty final result with status 200..599.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Bounded metadata-only native queue wait followed by nonwaiting request/global payload grant and rollback before copy; serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **statusCode**: A final HTTP status in 200 through 599; bodyless status restrictions apply.
- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.Results.Stream(System.Func{System.IO.Stream,System.Threading.CancellationToken,System.Threading.Tasks.Task},System.String)`

```csharp
public static NativeWeave.Server.ServerResult NativeWeave.Server.Results.Stream(System.Func<System.IO.Stream, System.Threading.CancellationToken, System.Threading.Tasks.Task> write, [System.String contentType = "application/octet-stream"])
```

Executes a producer once inside request lifetime with a borrowed asynchronous output stream.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Bounded metadata-only native queue wait followed by nonwaiting request/global payload grant and rollback before copy; serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **write**: The write value described by this operation.
- **contentType**: The contentType value described by this operation; omitted calls use "application/octet-stream".
- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.Results.Stream(System.IO.Stream,System.String,System.Boolean)`

```csharp
public static NativeWeave.Server.ServerResult NativeWeave.Server.Results.Stream(System.IO.Stream source, [System.String contentType = "application/octet-stream"], [System.Boolean leaveOpen = false])
```

Transfers source disposal only after execution admission unless leaveOpen; an unused result transfers nothing.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Bounded metadata-only native queue wait followed by nonwaiting request/global payload grant and rollback before copy; serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **source**: The source stream; successful result execution admission transfers disposal responsibility.
- **contentType**: The contentType value described by this operation; omitted calls use "application/octet-stream".
- **leaveOpen**: The leaveOpen value described by this operation; omitted calls use false.
- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.Results.Text(System.String,System.String,System.Int32)`

```csharp
public static NativeWeave.Server.ServerResult NativeWeave.Server.Results.Text(System.String text, [System.String contentType = "text/plain; charset=utf-8"], [System.Int32 statusCode = 200])
```

Creates reusable immutable text encoded in bounded UTF-8 chunks at execution.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Bounded metadata-only native queue wait followed by nonwaiting request/global payload grant and rollback before copy; serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **text**: The text value described by this operation.
- **contentType**: The contentType value described by this operation; omitted calls use "text/plain; charset=utf-8".
- **statusCode**: A final HTTP status in 200 through 599; bodyless status restrictions apply.
- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.Results.ValidationProblem(System.Collections.Generic.IReadOnlyDictionary{System.String,System.String[]})`

```csharp
public static NativeWeave.Server.ServerResult NativeWeave.Server.Results.ValidationProblem(System.Collections.Generic.IReadOnlyDictionary<System.String, System.String[]> errors)
```

Creates a validation problem with at most 128 fields and 16 bounded messages per field.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Bounded metadata-only native queue wait followed by nonwaiting request/global payload grant and rollback before copy; serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **errors**: The errors value described by this operation.
- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.RouteGroupBuilder.AddFilter(NativeWeave.Server.ServerFilter)`

```csharp
public NativeWeave.Server.RouteGroupBuilder NativeWeave.Server.RouteGroupBuilder.AddFilter(NativeWeave.Server.ServerFilter filter)
```

Adds an inherited result filter; protocol descendants reject filters at freeze.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. A gRPC/MCP endpoint with a local or inherited ServerFilter is rejected at route freeze before listening. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **filter**: The filter value described by this operation.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.RouteGroupBuilder.RequireAuthorization(System.String)`

```csharp
public NativeWeave.Server.RouteGroupBuilder NativeWeave.Server.RouteGroupBuilder.RequireAuthorization(System.String policy)
```

Adds an inherited named authorization requirement.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. A gRPC/MCP endpoint with a local or inherited ServerFilter is rejected at route freeze before listening. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **policy**: The policy value described by this operation.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.RouteGroupBuilder.WithTags(System.String[])`

```csharp
public NativeWeave.Server.RouteGroupBuilder NativeWeave.Server.RouteGroupBuilder.WithTags(params System.String[] tags)
```

Adds bounded inherited documentation tags.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. A gRPC/MCP endpoint with a local or inherited ServerFilter is rejected at route freeze before listening. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **tags**: The tags value described by this operation.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.RouteParameterAttribute.#ctor(System.String)`

```csharp
public NativeWeave.Server.RouteParameterAttribute.RouteParameterAttribute([System.String? name = null])
```

Uses the supplied name, or the parameter name when null.

Finite annotated static handler whitelist; missing/ambiguous shapes are compile diagnostics. Build-time generation; no per-handler Rust compilation, reflection scan, Expression.Compile or runtime argument object[]. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **name**: The name value described by this operation; omitted calls use null.

## `M:NativeWeave.Server.ServerCloseResult.#ctor(NativeWeave.Server.ServerCloseStatus,System.Int32,System.Int64,System.Int64)`

```csharp
public NativeWeave.Server.ServerCloseResult.ServerCloseResult(NativeWeave.Server.ServerCloseStatus Status, System.Int32 PendingRequests, System.Int64 RetainedManagedBytes, System.Int64 RetainedNativeBytes)
```

Close observation; Pending retains roots, native owners and credits.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **Status**: The Status value described by this operation.
- **PendingRequests**: The PendingRequests value described by this operation.
- **RetainedManagedBytes**: The RetainedManagedBytes value described by this operation.
- **RetainedNativeBytes**: The RetainedNativeBytes value described by this operation.

## `M:NativeWeave.Server.ServerEndpointBuilder.AddFilter(NativeWeave.Server.ServerFilter)`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerEndpointBuilder.AddFilter(NativeWeave.Server.ServerFilter filter)
```

Adds an HTTP result filter; protocol routes reject all filters before native startup.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. A gRPC/MCP endpoint with a local or inherited ServerFilter is rejected at route freeze before listening. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **filter**: The filter value described by this operation.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerEndpointBuilder.AllowAnonymous`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerEndpointBuilder.AllowAnonymous()
```

Removes inherited and endpoint authorization requirements.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. A gRPC/MCP endpoint with a local or inherited ServerFilter is rejected at route freeze before listening. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerEndpointBuilder.Produces``1(System.Int32,System.Text.Json.Serialization.Metadata.JsonTypeInfo{``0},System.String)`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerEndpointBuilder.Produces<T>([System.Int32 statusCode = 200], [System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>? typeInfo = null], [System.String contentType = "application/json"])
```

Declares a statically serializable documented response shape.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. A gRPC/MCP endpoint with a local or inherited ServerFilter is rejected at route freeze before listening. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **statusCode**: A final HTTP status in 200 through 599; bodyless status restrictions apply.
- **typeInfo**: Static JSON metadata; nullable registration arguments resolve from UseJson contexts at route freeze, without reflection fallback.
- **contentType**: The contentType value described by this operation; omitted calls use "application/json".
- **T**: The statically known result type; JSON paths require explicit or registered static metadata.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerEndpointBuilder.RequireAuthorization(System.String)`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerEndpointBuilder.RequireAuthorization(System.String policy)
```

Attaches a named authorization policy.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. A gRPC/MCP endpoint with a local or inherited ServerFilter is rejected at route freeze before listening. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **policy**: The policy value described by this operation.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerEndpointBuilder.WithMaxJsonBytes(System.Int32)`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerEndpointBuilder.WithMaxJsonBytes(System.Int32 bytes)
```

Overrides the HTTP JSON wire cap within the existing retained-memory budget.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. A gRPC/MCP endpoint with a local or inherited ServerFilter is rejected at route freeze before listening. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **bytes**: The payload or finite byte limit described in the summary; application-owned input is not an unbounded framework allocation.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerEndpointBuilder.WithMaxRequestBodyBytes(System.Int64)`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerEndpointBuilder.WithMaxRequestBodyBytes(System.Int64 bytes)
```

Overrides the HTTP wire-body cap; protocol routes reject this option at freeze.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. A gRPC/MCP endpoint with a local or inherited ServerFilter is rejected at route freeze before listening. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **bytes**: The payload or finite byte limit described in the summary; application-owned input is not an unbounded framework allocation.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerEndpointBuilder.WithName(System.String)`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerEndpointBuilder.WithName(System.String name)
```

Assigns a unique bounded diagnostic operation name.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. A gRPC/MCP endpoint with a local or inherited ServerFilter is rejected at route freeze before listening. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **name**: The name value described by this operation.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerEndpointBuilder.WithSummary(System.String)`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerEndpointBuilder.WithSummary(System.String summary)
```

Assigns bounded documentation summary text.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. A gRPC/MCP endpoint with a local or inherited ServerFilter is rejected at route freeze before listening. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **summary**: The summary value described by this operation.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerEndpointBuilder.WithTags(System.String[])`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerEndpointBuilder.WithTags(params System.String[] tags)
```

Assigns bounded documentation tags.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. A gRPC/MCP endpoint with a local or inherited ServerFilter is rejected at route freeze before listening. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **tags**: The tags value described by this operation.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerEndpointBuilder.WithTimeout(System.TimeSpan)`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerEndpointBuilder.WithTimeout(System.TimeSpan timeout)
```

Overrides the finite generic whole-request timeout.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. A gRPC/MCP endpoint with a local or inherited ServerFilter is rejected at route freeze before listening. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **timeout**: The finite checked wait or request duration specified by this operation.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerEvent.#ctor(System.String,System.String,System.String,System.Nullable{System.TimeSpan})`

```csharp
public NativeWeave.Server.ServerEvent.ServerEvent(System.String Data, [System.String? Event = null], [System.String? Id = null], [System.TimeSpan? Retry = null])
```

One immutable SSE event; each string is bounded by the endpoint chunk policy.

sse bit6; prevalidated Event/Id; finite whole-stream deadline. One enumerable event in flight; checked conservative expanded UTF-8 reserve; native Axum Event/Sse formatting; no replay retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **Data**: The Data value described by this operation.
- **Event**: The Event value described by this operation; omitted calls use null.
- **Id**: The Id value described by this operation; omitted calls use null.
- **Retry**: The Retry value described by this operation; omitted calls use null.

## `M:NativeWeave.Server.ServerException.#ctor(System.String,System.String,System.Exception)`

```csharp
public NativeWeave.Server.ServerException.ServerException(System.String code, System.String message, [System.Exception? innerException = null])
```

Creates a domain exception with a stable code and optional cause.

Explicit caller-owned thread-safe auth SPI; application policy, no built-in JWT/session stack. Bounded registered callbacks; next single-use; exceptions redacted and classified before/after response commit; application service work remains caller cost. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **code**: The code value described by this operation.
- **message**: The message value described by this operation.
- **innerException**: The innerException value described by this operation; omitted calls use null.

## `M:NativeWeave.Server.ServerRequest.GetTrailers`

```csharp
public NativeWeave.Server.HeaderCollection NativeWeave.Server.ServerRequest.GetTrailers()
```

Gets validated trailers after native EOF; early access is rejected.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Raw Body/ReadChunk/ReadJson/GetTrailers are rejected for gRPC/MCP before consumption or owner movement. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. Native read payload grant is a nonwaiting request/global try; failure returns resource_limit before new payload allocation/retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **returns**: The typed result described in the summary, retaining the ownership policy of its declaring type.

## `M:NativeWeave.Server.ServerRequest.ReadChunkAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<NativeWeave.Server.BodyChunk?> NativeWeave.Server.ServerRequest.ReadChunkAsync([System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Reads an independently owned chunk or observes EOF; concurrent/mixed reads are rejected.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Raw Body/ReadChunk/ReadJson/GetTrailers are rejected for gRPC/MCP before consumption or owner movement. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. Native read payload grant is a nonwaiting request/global try; failure returns resource_limit before new payload allocation/retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **cancellationToken**: Caller cancellation intent; cancellation never releases native owners before their retirement join.
- **returns**: An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.

## `M:NativeWeave.Server.ServerRequest.ReadJsonAsync``1(System.Text.Json.Serialization.Metadata.JsonTypeInfo{``0},System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<T?> NativeWeave.Server.ServerRequest.ReadJsonAsync<T>(System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, [System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Reads bounded JSON with explicit static metadata; a null reference root is preserved.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Raw Body/ReadChunk/ReadJson/GetTrailers are rejected for gRPC/MCP before consumption or owner movement. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. Native read payload grant is a nonwaiting request/global try; failure returns resource_limit before new payload allocation/retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **typeInfo**: Static JSON metadata; nullable registration arguments resolve from UseJson contexts at route freeze, without reflection fallback.
- **cancellationToken**: Caller cancellation intent; cancellation never releases native owners before their retirement join.
- **T**: The statically known result type; JSON paths require explicit or registered static metadata.
- **returns**: An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.

## `M:NativeWeave.Server.ServerResponse.CompleteAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task NativeWeave.Server.ServerResponse.CompleteAsync([System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Joins one terminal attempt; failed completion is retained and never retried as a second response.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Every successful HTTP MapRaw/generated void/custom result/middleware path awaits one CompleteAsync/response_finish before lifecycle cleanup; preserve selected status/headers, untouched200. gRPC/Allowed MCP permit only lifecycle hooks on this facade. Bounded metadata-only queue admission may wait; native request/global payload grant and managed overlap grant are nonwaiting with all-or-none rollback. Serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. Payload-free completion uses reserved control metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **cancellationToken**: Caller cancellation intent; cancellation never releases native owners before their retirement join.
- **returns**: An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.

## `M:NativeWeave.Server.ServerResponse.FlushAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task NativeWeave.Server.ServerResponse.FlushAsync([System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Flushes local native flow-controlled progress without certifying remote consumption.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Every successful HTTP MapRaw/generated void/custom result/middleware path awaits one CompleteAsync/response_finish before lifecycle cleanup; preserve selected status/headers, untouched200. gRPC/Allowed MCP permit only lifecycle hooks on this facade. Bounded metadata-only queue admission may wait; native request/global payload grant and managed overlap grant are nonwaiting with all-or-none rollback. Serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. Payload-free completion uses reserved control metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **cancellationToken**: Caller cancellation intent; cancellation never releases native owners before their retirement join.
- **returns**: An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.

## `M:NativeWeave.Server.ServerResponse.OnCompleted(System.Func{System.Threading.Tasks.Task})`

```csharp
public void NativeWeave.Server.ServerResponse.OnCompleted(System.Func<System.Threading.Tasks.Task> callback)
```

Registers one of at most 32 LIFO completion callbacks, before disposal and request-scope release.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Every successful HTTP MapRaw/generated void/custom result/middleware path awaits one CompleteAsync/response_finish before lifecycle cleanup; preserve selected status/headers, untouched200. gRPC/Allowed MCP permit only lifecycle hooks on this facade. Bounded metadata-only queue admission may wait; native request/global payload grant and managed overlap grant are nonwaiting with all-or-none rollback. Serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. Payload-free completion uses reserved control metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **callback**: An awaited lifecycle callback registered before the corresponding bounded list freezes.

## `M:NativeWeave.Server.ServerResponse.OnStarting(System.Func{System.Threading.CancellationToken,System.Threading.Tasks.Task})`

```csharp
public void NativeWeave.Server.ServerResponse.OnStarting(System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task> callback)
```

Registers one of at most 32 LIFO starting callbacks before starting begins.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Every successful HTTP MapRaw/generated void/custom result/middleware path awaits one CompleteAsync/response_finish before lifecycle cleanup; preserve selected status/headers, untouched200. gRPC/Allowed MCP permit only lifecycle hooks on this facade. Bounded metadata-only queue admission may wait; native request/global payload grant and managed overlap grant are nonwaiting with all-or-none rollback. Serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. Payload-free completion uses reserved control metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **callback**: An awaited lifecycle callback registered before the corresponding bounded list freezes.

## `M:NativeWeave.Server.ServerResponse.RegisterForDisposeAsync(System.IAsyncDisposable)`

```csharp
public void NativeWeave.Server.ServerResponse.RegisterForDisposeAsync(System.IAsyncDisposable resource)
```

Transfers one disposable after successful bounded registration; duplicate identity is rejected.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Every successful HTTP MapRaw/generated void/custom result/middleware path awaits one CompleteAsync/response_finish before lifecycle cleanup; preserve selected status/headers, untouched200. gRPC/Allowed MCP permit only lifecycle hooks on this facade. Bounded metadata-only queue admission may wait; native request/global payload grant and managed overlap grant are nonwaiting with all-or-none rollback. Serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. Payload-free completion uses reserved control metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **resource**: The resource value described by this operation.

## `M:NativeWeave.Server.ServerResponse.StartAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task NativeWeave.Server.ServerResponse.StartAsync([System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Runs starting callbacks in reverse order and commits headers once.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Every successful HTTP MapRaw/generated void/custom result/middleware path awaits one CompleteAsync/response_finish before lifecycle cleanup; preserve selected status/headers, untouched200. gRPC/Allowed MCP permit only lifecycle hooks on this facade. Bounded metadata-only queue admission may wait; native request/global payload grant and managed overlap grant are nonwaiting with all-or-none rollback. Serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. Payload-free completion uses reserved control metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **cancellationToken**: Caller cancellation intent; cancellation never releases native owners before their retirement join.
- **returns**: An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.

## `M:NativeWeave.Server.ServerResponse.WriteAsync(System.ReadOnlyMemory{System.Byte},System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task NativeWeave.Server.ServerResponse.WriteAsync(System.ReadOnlyMemory<System.Byte> data, [System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Writes bounded chunks with one pending writer; native payload admission never waits for byte credit.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Every successful HTTP MapRaw/generated void/custom result/middleware path awaits one CompleteAsync/response_finish before lifecycle cleanup; preserve selected status/headers, untouched200. gRPC/Allowed MCP permit only lifecycle hooks on this facade. Bounded metadata-only queue admission may wait; native request/global payload grant and managed overlap grant are nonwaiting with all-or-none rollback. Serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. Payload-free completion uses reserved control metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **data**: The payload or finite byte limit described in the summary; application-owned input is not an unbounded framework allocation.
- **cancellationToken**: Caller cancellation intent; cancellation never releases native owners before their retirement join.
- **returns**: An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.

## `M:NativeWeave.Server.ServerResponse.WriteJsonAsync``1(``0,System.Text.Json.Serialization.Metadata.JsonTypeInfo{``0},System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task NativeWeave.Server.ServerResponse.WriteJsonAsync<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, [System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Writes JSON using explicit static metadata and finite encoded/peak limits.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Every successful HTTP MapRaw/generated void/custom result/middleware path awaits one CompleteAsync/response_finish before lifecycle cleanup; preserve selected status/headers, untouched200. gRPC/Allowed MCP permit only lifecycle hooks on this facade. Bounded metadata-only queue admission may wait; native request/global payload grant and managed overlap grant are nonwaiting with all-or-none rollback. Serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. Payload-free completion uses reserved control metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **value**: The value value described by this operation.
- **typeInfo**: Static JSON metadata; nullable registration arguments resolve from UseJson contexts at route freeze, without reflection fallback.
- **cancellationToken**: Caller cancellation intent; cancellation never releases native owners before their retirement join.
- **T**: The statically known result type; JSON paths require explicit or registered static metadata.
- **returns**: An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.

## `M:NativeWeave.Server.ServerResult.ExecuteAsync(NativeWeave.Server.RequestContext,System.Threading.CancellationToken)`

```csharp
public abstract System.Threading.Tasks.Task NativeWeave.Server.ServerResult.ExecuteAsync(NativeWeave.Server.RequestContext context, [System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Writes a result using request-owned output; the dispatcher joins unfinished completion.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Bounded metadata-only native queue wait followed by nonwaiting request/global payload grant and rollback before copy; serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **context**: The current borrowed request context; it cannot escape awaited dispatch and cleanup.
- **cancellationToken**: Caller cancellation intent; cancellation never releases native owners before their retirement join.
- **returns**: An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.

## `M:NativeWeave.Server.ServerRouteAttribute.#ctor(System.String,System.String)`

```csharp
public NativeWeave.Server.ServerRouteAttribute.ServerRouteAttribute(System.String method, System.String pattern)
```

Declares one HTTP method and pattern.

Finite annotated static handler whitelist; missing/ambiguous shapes are compile diagnostics. Build-time generation; no per-handler Rust compilation, reflection scan, Expression.Compile or runtime argument object[]. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **method**: The method value described by this operation.
- **pattern**: A validated native route pattern or group prefix, registered before startup.

## `M:NativeWeave.Server.ServerRoutes.MapDelete(System.String,NativeWeave.Server.ResponseHandler)`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerRoutes.MapDelete(System.String pattern, NativeWeave.Server.ResponseHandler handler)
```

Maps an asynchronous DELETE result.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **pattern**: A validated native route pattern or group prefix, registered before startup.
- **handler**: A strongly typed application callback invoked on managed dispatch outside native locks; await its asynchronous work.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerRoutes.MapGet(System.String,NativeWeave.Server.ResponseHandler)`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerRoutes.MapGet(System.String pattern, NativeWeave.Server.ResponseHandler handler)
```

Maps an asynchronous GET result.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **pattern**: A validated native route pattern or group prefix, registered before startup.
- **handler**: A strongly typed application callback invoked on managed dispatch outside native locks; await its asynchronous work.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerRoutes.MapGet(System.String,System.Func{NativeWeave.Server.RequestContext,NativeWeave.Server.ServerResult})`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerRoutes.MapGet(System.String pattern, System.Func<NativeWeave.Server.RequestContext, NativeWeave.Server.ServerResult> handler)
```

Maps a synchronous GET result with explicit request access.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **pattern**: A validated native route pattern or group prefix, registered before startup.
- **handler**: A strongly typed application callback invoked on managed dispatch outside native locks; await its asynchronous work.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerRoutes.MapGet(System.String,System.Func{NativeWeave.Server.ServerResult})`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerRoutes.MapGet(System.String pattern, System.Func<NativeWeave.Server.ServerResult> handler)
```

Maps a synchronous GET result.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **pattern**: A validated native route pattern or group prefix, registered before startup.
- **handler**: A strongly typed application callback invoked on managed dispatch outside native locks; await its asynchronous work.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerRoutes.MapGroup(System.String)`

```csharp
public NativeWeave.Server.RouteGroupBuilder NativeWeave.Server.ServerRoutes.MapGroup(System.String prefix)
```

Creates a prefix group with policies resolved when the registry freezes.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **prefix**: A validated native route pattern or group prefix, registered before startup.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerRoutes.MapJson``2(System.String,System.Collections.Generic.IReadOnlyList{System.String},NativeWeave.Server.JsonRequestHandler{``0,``1},System.Text.Json.Serialization.Metadata.JsonTypeInfo{``0},System.Text.Json.Serialization.Metadata.JsonTypeInfo{``1})`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerRoutes.MapJson<TRequest, TResponse>(System.String pattern, System.Collections.Generic.IReadOnlyList<System.String> methods, NativeWeave.Server.JsonRequestHandler<TRequest, TResponse> handler, [System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest>? requestTypeInfo = null], [System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse>? responseTypeInfo = null])
```

Maps typed JSON for explicit HTTP methods with metadata validated before native startup.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **pattern**: A validated native route pattern or group prefix, registered before startup.
- **methods**: Explicit HTTP method tokens copied into the immutable route configuration.
- **handler**: A strongly typed application callback invoked on managed dispatch outside native locks; await its asynchronous work.
- **requestTypeInfo**: Static JSON metadata; nullable registration arguments resolve from UseJson contexts at route freeze, without reflection fallback.
- **responseTypeInfo**: Static JSON metadata; nullable registration arguments resolve from UseJson contexts at route freeze, without reflection fallback.
- **TRequest**: The statically known request type; JSON paths require explicit or registered static metadata.
- **TResponse**: The statically known result type; JSON paths require explicit or registered static metadata.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerRoutes.MapMethods(System.String,System.Collections.Generic.IReadOnlyList{System.String},NativeWeave.Server.ResponseHandler)`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerRoutes.MapMethods(System.String pattern, System.Collections.Generic.IReadOnlyList<System.String> methods, NativeWeave.Server.ResponseHandler handler)
```

Maps explicit validated HTTP methods; native routing owns matching and dispatch.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **pattern**: A validated native route pattern or group prefix, registered before startup.
- **methods**: Explicit HTTP method tokens copied into the immutable route configuration.
- **handler**: A strongly typed application callback invoked on managed dispatch outside native locks; await its asynchronous work.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerRoutes.MapPatch(System.String,NativeWeave.Server.ResponseHandler)`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerRoutes.MapPatch(System.String pattern, NativeWeave.Server.ResponseHandler handler)
```

Maps an asynchronous PATCH result.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **pattern**: A validated native route pattern or group prefix, registered before startup.
- **handler**: A strongly typed application callback invoked on managed dispatch outside native locks; await its asynchronous work.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerRoutes.MapPost(System.String,NativeWeave.Server.ResponseHandler)`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerRoutes.MapPost(System.String pattern, NativeWeave.Server.ResponseHandler handler)
```

Maps an asynchronous POST result.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **pattern**: A validated native route pattern or group prefix, registered before startup.
- **handler**: A strongly typed application callback invoked on managed dispatch outside native locks; await its asynchronous work.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerRoutes.MapPost(System.String,System.Func{NativeWeave.Server.RequestContext,NativeWeave.Server.ServerResult})`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerRoutes.MapPost(System.String pattern, System.Func<NativeWeave.Server.RequestContext, NativeWeave.Server.ServerResult> handler)
```

Maps a synchronous POST result.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **pattern**: A validated native route pattern or group prefix, registered before startup.
- **handler**: A strongly typed application callback invoked on managed dispatch outside native locks; await its asynchronous work.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerRoutes.MapPost``1(System.String,NativeWeave.Server.JsonResultHandler{``0},System.Text.Json.Serialization.Metadata.JsonTypeInfo{``0})`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerRoutes.MapPost<TRequest>(System.String pattern, NativeWeave.Server.JsonResultHandler<TRequest> handler, [System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest>? requestTypeInfo = null])
```

Maps a JSON POST returning an explicit HTTP result.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **pattern**: A validated native route pattern or group prefix, registered before startup.
- **handler**: A strongly typed application callback invoked on managed dispatch outside native locks; await its asynchronous work.
- **requestTypeInfo**: Static JSON metadata; nullable registration arguments resolve from UseJson contexts at route freeze, without reflection fallback.
- **TRequest**: The statically known request type; JSON paths require explicit or registered static metadata.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerRoutes.MapPost``2(System.String,NativeWeave.Server.JsonRequestHandler{``0,``1},System.Text.Json.Serialization.Metadata.JsonTypeInfo{``0},System.Text.Json.Serialization.Metadata.JsonTypeInfo{``1})`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerRoutes.MapPost<TRequest, TResponse>(System.String pattern, NativeWeave.Server.JsonRequestHandler<TRequest, TResponse> handler, [System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest>? requestTypeInfo = null], [System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse>? responseTypeInfo = null])
```

Maps a JSON POST with request context and static metadata.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **pattern**: A validated native route pattern or group prefix, registered before startup.
- **handler**: A strongly typed application callback invoked on managed dispatch outside native locks; await its asynchronous work.
- **requestTypeInfo**: Static JSON metadata; nullable registration arguments resolve from UseJson contexts at route freeze, without reflection fallback.
- **responseTypeInfo**: Static JSON metadata; nullable registration arguments resolve from UseJson contexts at route freeze, without reflection fallback.
- **TRequest**: The statically known request type; JSON paths require explicit or registered static metadata.
- **TResponse**: The statically known result type; JSON paths require explicit or registered static metadata.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerRoutes.MapPost``2(System.String,System.Func{``0,System.Threading.CancellationToken,System.Threading.Tasks.Task{``1}},System.Text.Json.Serialization.Metadata.JsonTypeInfo{``0},System.Text.Json.Serialization.Metadata.JsonTypeInfo{``1})`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerRoutes.MapPost<TRequest, TResponse>(System.String pattern, System.Func<TRequest, System.Threading.CancellationToken, System.Threading.Tasks.Task<TResponse>> handler, [System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest>? requestTypeInfo = null], [System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse>? responseTypeInfo = null])
```

Maps a statically serialized JSON POST without dynamic delegate binding.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **pattern**: A validated native route pattern or group prefix, registered before startup.
- **handler**: A strongly typed application callback invoked on managed dispatch outside native locks; await its asynchronous work.
- **requestTypeInfo**: Static JSON metadata; nullable registration arguments resolve from UseJson contexts at route freeze, without reflection fallback.
- **responseTypeInfo**: Static JSON metadata; nullable registration arguments resolve from UseJson contexts at route freeze, without reflection fallback.
- **TRequest**: The statically known request type; JSON paths require explicit or registered static metadata.
- **TResponse**: The statically known result type; JSON paths require explicit or registered static metadata.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerRoutes.MapPut(System.String,NativeWeave.Server.ResponseHandler)`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerRoutes.MapPut(System.String pattern, NativeWeave.Server.ResponseHandler handler)
```

Maps an asynchronous PUT result.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **pattern**: A validated native route pattern or group prefix, registered before startup.
- **handler**: A strongly typed application callback invoked on managed dispatch outside native locks; await its asynchronous work.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `M:NativeWeave.Server.ServerRoutes.MapRaw(System.String,System.Collections.Generic.IReadOnlyList{System.String},NativeWeave.Server.RequestHandler)`

```csharp
public NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.ServerRoutes.MapRaw(System.String pattern, System.Collections.Generic.IReadOnlyList<System.String> methods, NativeWeave.Server.RequestHandler handler)
```

Maps direct writes; successful dispatch automatically joins unfinished response completion.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **pattern**: A validated native route pattern or group prefix, registered before startup.
- **methods**: Explicit HTTP method tokens copied into the immutable route configuration.
- **handler**: A strongly typed application callback invoked on managed dispatch outside native locks; await its asynchronous work.
- **returns**: The configured fluent builder; configuration is frozen at application startup.

## `P:NativeWeave.Server.HeaderCollection.Count`

```csharp
public System.Int32 NativeWeave.Server.HeaderCollection.Count | getter=Public;setter=;init=
```

Gets the number of distinct names while the request is alive.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.HeaderCollection.Item(System.String)`

```csharp
public System.Collections.Generic.IReadOnlyList<System.String> NativeWeave.Server.HeaderCollection.this[System.String name] | getter=Public;setter=;init=
```

Gets all field values; missing names return an empty immutable list.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.HeaderParameterAttribute.Name`

```csharp
public System.String NativeWeave.Server.HeaderParameterAttribute.Name | getter=Public;setter=;init=
```

Gets the header name.

Finite annotated static handler whitelist; missing/ambiguous shapes are compile diagnostics. Build-time generation; no per-handler Rust compilation, reflection scan, Expression.Compile or runtime argument object[]. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.NativeServerApplication.BoundAddresses`

```csharp
public System.Collections.Generic.IReadOnlyList<System.Uri> NativeWeave.Server.NativeServerApplication.BoundAddresses | getter=Public;setter=;init=
```

Gets actual listener addresses after successful startup.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.NativeServerApplication.Capabilities`

```csharp
public NativeWeave.Server.ServerCapabilities NativeWeave.Server.NativeServerApplication.Capabilities | getter=Public;setter=;init=
```

Gets negotiated native capabilities.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.NativeServerApplication.Services`

```csharp
public System.IServiceProvider NativeWeave.Server.NativeServerApplication.Services | getter=Public;setter=;init=
```

Gets application services; request-scoped services are resolved through RequestContext.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.NativeServerApplication.State`

```csharp
public NativeWeave.Server.ServerState NativeWeave.Server.NativeServerApplication.State | getter=Public;setter=;init=
```

Gets the atomic lifecycle observation.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.NativeServerBuilder.Configuration`

```csharp
public Microsoft.Extensions.Configuration.ConfigurationManager NativeWeave.Server.NativeServerBuilder.Configuration | getter=Public;setter=;init=
```

Gets explicit configuration; no file watching, environment or secret scanning is installed.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.NativeServerBuilder.Logging`

```csharp
public Microsoft.Extensions.Logging.ILoggingBuilder NativeWeave.Server.NativeServerBuilder.Logging | getter=Public;setter=;init=
```

Gets logging configuration; no sink is installed by default.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.NativeServerBuilder.Options`

```csharp
public NativeWeave.Server.ServerOptions NativeWeave.Server.NativeServerBuilder.Options | getter=Public;setter=;init=
```

Gets mutable configuration copied and validated by Build.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.NativeServerBuilder.Services`

```csharp
public Microsoft.Extensions.DependencyInjection.IServiceCollection NativeWeave.Server.NativeServerBuilder.Services | getter=Public;setter=;init=
```

Gets service registrations; the owned provider is built once.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.QueryParameterAttribute.Name`

```csharp
public System.String? NativeWeave.Server.QueryParameterAttribute.Name | getter=Public;setter=;init=
```

Gets the explicit name.

Finite annotated static handler whitelist; missing/ambiguous shapes are compile diagnostics. Build-time generation; no per-handler Rust compilation, reflection scan, Expression.Compile or runtime argument object[]. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.RequestBindingException.ParameterName`

```csharp
public System.String NativeWeave.Server.RequestBindingException.ParameterName | getter=Public;setter=;init=
```

Gets the safe parameter identifier.

Explicit caller-owned thread-safe auth SPI; application policy, no built-in JWT/session stack. Bounded registered callbacks; next single-use; exceptions redacted and classified before/after response commit; application service work remains caller cost. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.RequestContext.Items`

```csharp
public System.Collections.Generic.IDictionary<System.Object, System.Object?> NativeWeave.Server.RequestContext.Items | getter=Public;setter=;init=
```

Gets lazy request storage capped at 128 entries.

Borrowed request lifecycle; available cancellation signal, not exact upstream H2 reset certification. gRPC permits metadata/services/identity/Items/cancel/Abort and lifecycle hooks only; raw body/output and OnStarting are rejected. MCP has only its pre-parse HTTP denial exception. Lazy scope/Items (<=128 entries); sticky bounded control notification; TimeRemaining is monotonic; no escaped context after dispatch. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.RequestContext.Request`

```csharp
public NativeWeave.Server.ServerRequest NativeWeave.Server.RequestContext.Request | getter=Public;setter=;init=
```

Gets request metadata and ordinary HTTP body APIs.

Borrowed request lifecycle; available cancellation signal, not exact upstream H2 reset certification. gRPC permits metadata/services/identity/Items/cancel/Abort and lifecycle hooks only; raw body/output and OnStarting are rejected. MCP has only its pre-parse HTTP denial exception. Lazy scope/Items (<=128 entries); sticky bounded control notification; TimeRemaining is monotonic; no escaped context after dispatch. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.RequestContext.RequestAborted`

```csharp
public System.Threading.CancellationToken NativeWeave.Server.RequestContext.RequestAborted | getter=Public;setter=;init=
```

Gets sticky native disconnect, deadline and shutdown cancellation intent.

Borrowed request lifecycle; available cancellation signal, not exact upstream H2 reset certification. gRPC permits metadata/services/identity/Items/cancel/Abort and lifecycle hooks only; raw body/output and OnStarting are rejected. MCP has only its pre-parse HTTP denial exception. Lazy scope/Items (<=128 entries); sticky bounded control notification; TimeRemaining is monotonic; no escaped context after dispatch. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.RequestContext.Response`

```csharp
public NativeWeave.Server.ServerResponse NativeWeave.Server.RequestContext.Response | getter=Public;setter=;init=
```

Gets the response facade; protocol routes restrict raw HTTP access.

Borrowed request lifecycle; available cancellation signal, not exact upstream H2 reset certification. gRPC permits metadata/services/identity/Items/cancel/Abort and lifecycle hooks only; raw body/output and OnStarting are rejected. MCP has only its pre-parse HTTP denial exception. Lazy scope/Items (<=128 entries); sticky bounded control notification; TimeRemaining is monotonic; no escaped context after dispatch. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.RequestContext.Services`

```csharp
public System.IServiceProvider NativeWeave.Server.RequestContext.Services | getter=Public;setter=;init=
```

Gets lazily created request-scoped services.

Borrowed request lifecycle; available cancellation signal, not exact upstream H2 reset certification. gRPC permits metadata/services/identity/Items/cancel/Abort and lifecycle hooks only; raw body/output and OnStarting are rejected. MCP has only its pre-parse HTTP denial exception. Lazy scope/Items (<=128 entries); sticky bounded control notification; TimeRemaining is monotonic; no escaped context after dispatch. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.RequestContext.TimeRemaining`

```csharp
public System.TimeSpan NativeWeave.Server.RequestContext.TimeRemaining | getter=Public;setter=;init=
```

Gets the native monotonic whole-request time remaining.

Borrowed request lifecycle; available cancellation signal, not exact upstream H2 reset certification. gRPC permits metadata/services/identity/Items/cancel/Abort and lifecycle hooks only; raw body/output and OnStarting are rejected. MCP has only its pre-parse HTTP denial exception. Lazy scope/Items (<=128 entries); sticky bounded control notification; TimeRemaining is monotonic; no escaped context after dispatch. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.RequestContext.TraceIdentifier`

```csharp
public System.String NativeWeave.Server.RequestContext.TraceIdentifier | getter=Public;setter=;init=
```

Gets a bounded correlation identifier without authorization meaning.

Borrowed request lifecycle; available cancellation signal, not exact upstream H2 reset certification. gRPC permits metadata/services/identity/Items/cancel/Abort and lifecycle hooks only; raw body/output and OnStarting are rejected. MCP has only its pre-parse HTTP denial exception. Lazy scope/Items (<=128 entries); sticky bounded control notification; TimeRemaining is monotonic; no escaped context after dispatch. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.RequestContext.User`

```csharp
public System.Security.Claims.ClaimsPrincipal NativeWeave.Server.RequestContext.User | getter=Public;setter=;init=
```

Gets the authenticated principal, initially empty.

Borrowed request lifecycle; available cancellation signal, not exact upstream H2 reset certification. gRPC permits metadata/services/identity/Items/cancel/Abort and lifecycle hooks only; raw body/output and OnStarting are rejected. MCP has only its pre-parse HTTP denial exception. Lazy scope/Items (<=128 entries); sticky bounded control notification; TimeRemaining is monotonic; no escaped context after dispatch. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.RouteParameterAttribute.Name`

```csharp
public System.String? NativeWeave.Server.RouteParameterAttribute.Name | getter=Public;setter=;init=
```

Gets the explicit name.

Finite annotated static handler whitelist; missing/ambiguous shapes are compile diagnostics. Build-time generation; no per-handler Rust compilation, reflection scan, Expression.Compile or runtime argument object[]. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerBudgetOptions.ManagedPayloadBytes`

```csharp
public System.Int64 NativeWeave.Server.ServerBudgetOptions.ManagedPayloadBytes | getter=Public;setter=Public;init=False
```

Gets or sets managed payload credit, default 64 MiB.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. Native global/request payload grants are atomic and nonwaiting for both reads and writes; queue/network waits are separate, full backing capacity/overlap is charged, and all partial grants roll back without automatic retry. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerBudgetOptions.MaxActiveRequests`

```csharp
public System.Int32 NativeWeave.Server.ServerBudgetOptions.MaxActiveRequests | getter=Public;setter=Public;init=False
```

Gets or sets active and retiring request slots, default 256.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. Native global/request payload grants are atomic and nonwaiting for both reads and writes; queue/network waits are separate, full backing capacity/overlap is charged, and all partial grants roll back without automatic retry. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerBudgetOptions.MaxChunkBytes`

```csharp
public System.Int32 NativeWeave.Server.ServerBudgetOptions.MaxChunkBytes | getter=Public;setter=Public;init=False
```

Gets or sets maximum body chunk capacity, default 32 KiB.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. Native global/request payload grants are atomic and nonwaiting for both reads and writes; queue/network waits are separate, full backing capacity/overlap is charged, and all partial grants roll back without automatic retry. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerBudgetOptions.MaxConnections`

```csharp
public System.Int32 NativeWeave.Server.ServerBudgetOptions.MaxConnections | getter=Public;setter=Public;init=False
```

Gets or sets the connection limit, default 512.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. Native global/request payload grants are atomic and nonwaiting for both reads and writes; queue/network waits are separate, full backing capacity/overlap is charged, and all partial grants roll back without automatic retry. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerBudgetOptions.MaxHttp2StreamsPerConnection`

```csharp
public System.Int32 NativeWeave.Server.ServerBudgetOptions.MaxHttp2StreamsPerConnection | getter=Public;setter=Public;init=False
```

Gets or sets HTTP/2 streams per connection, default 32.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. Native global/request payload grants are atomic and nonwaiting for both reads and writes; queue/network waits are separate, full backing capacity/overlap is charged, and all partial grants roll back without automatic retry. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerBudgetOptions.MaxQueuedRequests`

```csharp
public System.Int32 NativeWeave.Server.ServerBudgetOptions.MaxQueuedRequests | getter=Public;setter=Public;init=False
```

Gets or sets metadata-only admission waiters, default 128.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. Native global/request payload grants are atomic and nonwaiting for both reads and writes; queue/network waits are separate, full backing capacity/overlap is charged, and all partial grants roll back without automatic retry. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerBudgetOptions.NativePayloadBytes`

```csharp
public System.Int64 NativeWeave.Server.ServerBudgetOptions.NativePayloadBytes | getter=Public;setter=Public;init=False
```

Gets or sets native payload credit, default 64 MiB; read/write grants never wait for payload bytes and exhaustion is resource_limit.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. Native global/request payload grants are atomic and nonwaiting for both reads and writes; queue/network waits are separate, full backing capacity/overlap is charged, and all partial grants roll back without automatic retry. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerBudgetOptions.RetainedBytesPerRequest`

```csharp
public System.Int64 NativeWeave.Server.ServerBudgetOptions.RetainedBytesPerRequest | getter=Public;setter=Public;init=False
```

Gets or sets each request's per-side full backing-capacity limit, default 4 MiB; retained input can immediately reject another read or write.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. Native global/request payload grants are atomic and nonwaiting for both reads and writes; queue/network waits are separate, full backing capacity/overlap is charged, and all partial grants roll back without automatic retry. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerCloseResult.PendingRequests`

```csharp
public System.Int32 NativeWeave.Server.ServerCloseResult.PendingRequests | getter=Public;setter=Public;init=True
```

Gets the retained active or retiring request count.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerCloseResult.RetainedManagedBytes`

```csharp
public System.Int64 NativeWeave.Server.ServerCloseResult.RetainedManagedBytes | getter=Public;setter=Public;init=True
```

Gets currently charged framework-owned managed payload bytes.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerCloseResult.RetainedNativeBytes`

```csharp
public System.Int64 NativeWeave.Server.ServerCloseResult.RetainedNativeBytes | getter=Public;setter=Public;init=True
```

Gets currently charged native backing-allocation bytes.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerCloseResult.Status`

```csharp
public NativeWeave.Server.ServerCloseStatus NativeWeave.Server.ServerCloseResult.Status | getter=Public;setter=Public;init=True
```

Gets the observed close status; Pending does not certify retirement.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerEvent.Data`

```csharp
public System.String NativeWeave.Server.ServerEvent.Data | getter=Public;setter=Public;init=True
```

Gets event data; native Axum performs SSE line framing.

sse bit6; prevalidated Event/Id; finite whole-stream deadline. One enumerable event in flight; checked conservative expanded UTF-8 reserve; native Axum Event/Sse formatting; no replay retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerEvent.Event`

```csharp
public System.String? NativeWeave.Server.ServerEvent.Event | getter=Public;setter=Public;init=True
```

Gets the optional event name without CR or LF.

sse bit6; prevalidated Event/Id; finite whole-stream deadline. One enumerable event in flight; checked conservative expanded UTF-8 reserve; native Axum Event/Sse formatting; no replay retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerEvent.Id`

```csharp
public System.String? NativeWeave.Server.ServerEvent.Id | getter=Public;setter=Public;init=True
```

Gets the optional event identifier without CR, LF or NUL.

sse bit6; prevalidated Event/Id; finite whole-stream deadline. One enumerable event in flight; checked conservative expanded UTF-8 reserve; native Axum Event/Sse formatting; no replay retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerEvent.Retry`

```csharp
public System.TimeSpan? NativeWeave.Server.ServerEvent.Retry | getter=Public;setter=Public;init=True
```

Gets an optional whole-millisecond retry interval.

sse bit6; prevalidated Event/Id; finite whole-stream deadline. One enumerable event in flight; checked conservative expanded UTF-8 reserve; native Axum Event/Sse formatting; no replay retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerException.Code`

```csharp
public System.String NativeWeave.Server.ServerException.Code | getter=Public;setter=;init=
```

Gets the stable domain error code.

Explicit caller-owned thread-safe auth SPI; application policy, no built-in JWT/session stack. Bounded registered callbacks; next single-use; exceptions redacted and classified before/after response commit; application service work remains caller cost. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerListenOptions.Name`

```csharp
public System.String? NativeWeave.Server.ServerListenOptions.Name | getter=Public;setter=Public;init=False
```

Gets or sets a unique listener name; generated when null.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerListenOptions.Protocols`

```csharp
public NativeWeave.Server.ServerProtocols NativeWeave.Server.ServerListenOptions.Protocols | getter=Public;setter=Public;init=False
```

Gets or sets protocols; default HTTP/1.1.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerListenOptions.Tls`

```csharp
public NativeWeave.Server.ServerTlsOptions? NativeWeave.Server.ServerListenOptions.Tls | getter=Public;setter=Public;init=False
```

Gets or sets native TLS configuration; null means cleartext.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerOptions.Budget`

```csharp
public NativeWeave.Server.ServerBudgetOptions NativeWeave.Server.ServerOptions.Budget | getter=Public;setter=Public;init=False
```

Gets or sets application payload and admission budgets.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerOptions.HeaderReadTimeout`

```csharp
public System.TimeSpan NativeWeave.Server.ServerOptions.HeaderReadTimeout | getter=Public;setter=Public;init=False
```

Gets or sets the header-receive timeout; default 10 seconds.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerOptions.KeepAliveTimeout`

```csharp
public System.TimeSpan NativeWeave.Server.ServerOptions.KeepAliveTimeout | getter=Public;setter=Public;init=False
```

Gets or sets idle keep-alive timeout; default 60 seconds.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerOptions.MaxHeaderBytes`

```csharp
public System.Int32 NativeWeave.Server.ServerOptions.MaxHeaderBytes | getter=Public;setter=Public;init=False
```

Gets or sets the aggregate header limit; default 32 KiB.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerOptions.MaxJsonBytes`

```csharp
public System.Int32 NativeWeave.Server.ServerOptions.MaxJsonBytes | getter=Public;setter=Public;init=False
```

Gets or sets the HTTP JSON binder wire limit; default 1 MiB. Protocol routes use their own finite limits.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerOptions.MaxRequestBodyBytes`

```csharp
public System.Int64 NativeWeave.Server.ServerOptions.MaxRequestBodyBytes | getter=Public;setter=Public;init=False
```

Gets or sets the default HTTP request-body limit; default 1 MiB. gRPC and MCP use their protocol options.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerOptions.RequestTimeout`

```csharp
public System.TimeSpan NativeWeave.Server.ServerOptions.RequestTimeout | getter=Public;setter=Public;init=False
```

Gets or sets the finite whole-request timeout; default 30 seconds.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerOptions.ShutdownTimeout`

```csharp
public System.TimeSpan NativeWeave.Server.ServerOptions.ShutdownTimeout | getter=Public;setter=Public;init=False
```

Gets or sets the graceful close wait; default 30 seconds.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerOptions.TrustedProxies`

```csharp
public NativeWeave.Server.TrustedProxyOptions? NativeWeave.Server.ServerOptions.TrustedProxies | getter=Public;setter=Public;init=False
```

Gets or sets trusted forwarding policy; null ignores forwarded identity headers.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerRequest.Body`

```csharp
public System.IO.Stream NativeWeave.Server.ServerRequest.Body | getter=Public;setter=;init=
```

Gets an asynchronous borrowed stream; the first read selects stream mode.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Raw Body/ReadChunk/ReadJson/GetTrailers are rejected for gRPC/MCP before consumption or owner movement. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. Native read payload grant is a nonwaiting request/global try; failure returns resource_limit before new payload allocation/retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerRequest.ContentLength`

```csharp
public System.Int64? NativeWeave.Server.ServerRequest.ContentLength | getter=Public;setter=;init=
```

Gets the declared Content-Length without replacing native streaming limits.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Raw Body/ReadChunk/ReadJson/GetTrailers are rejected for gRPC/MCP before consumption or owner movement. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. Native read payload grant is a nonwaiting request/global try; failure returns resource_limit before new payload allocation/retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerRequest.ContentType`

```csharp
public System.String? NativeWeave.Server.ServerRequest.ContentType | getter=Public;setter=;init=
```

Gets Content-Type when supplied.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Raw Body/ReadChunk/ReadJson/GetTrailers are rejected for gRPC/MCP before consumption or owner movement. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. Native read payload grant is a nonwaiting request/global try; failure returns resource_limit before new payload allocation/retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerRequest.Headers`

```csharp
public NativeWeave.Server.HeaderCollection NativeWeave.Server.ServerRequest.Headers | getter=Public;setter=;init=
```

Gets immutable headers preserving repetitions.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Raw Body/ReadChunk/ReadJson/GetTrailers are rejected for gRPC/MCP before consumption or owner movement. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. Native read payload grant is a nonwaiting request/global try; failure returns resource_limit before new payload allocation/retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerRequest.Host`

```csharp
public System.String NativeWeave.Server.ServerRequest.Host | getter=Public;setter=;init=
```

Gets the validated effective authority.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Raw Body/ReadChunk/ReadJson/GetTrailers are rejected for gRPC/MCP before consumption or owner movement. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. Native read payload grant is a nonwaiting request/global try; failure returns resource_limit before new payload allocation/retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerRequest.Method`

```csharp
public System.String NativeWeave.Server.ServerRequest.Method | getter=Public;setter=;init=
```

Gets the validated case-sensitive method token.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Raw Body/ReadChunk/ReadJson/GetTrailers are rejected for gRPC/MCP before consumption or owner movement. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. Native read payload grant is a nonwaiting request/global try; failure returns resource_limit before new payload allocation/retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerRequest.Path`

```csharp
public System.String NativeWeave.Server.ServerRequest.Path | getter=Public;setter=;init=
```

Gets the path with encoded separators preserved.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Raw Body/ReadChunk/ReadJson/GetTrailers are rejected for gRPC/MCP before consumption or owner movement. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. Native read payload grant is a nonwaiting request/global try; failure returns resource_limit before new payload allocation/retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerRequest.Protocol`

```csharp
public System.String NativeWeave.Server.ServerRequest.Protocol | getter=Public;setter=;init=
```

Gets HTTP/1.1 or HTTP/2.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Raw Body/ReadChunk/ReadJson/GetTrailers are rejected for gRPC/MCP before consumption or owner movement. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. Native read payload grant is a nonwaiting request/global try; failure returns resource_limit before new payload allocation/retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerRequest.Query`

```csharp
public NativeWeave.Server.ParameterValues NativeWeave.Server.ServerRequest.Query | getter=Public;setter=;init=
```

Gets decoded query values preserving repetitions.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Raw Body/ReadChunk/ReadJson/GetTrailers are rejected for gRPC/MCP before consumption or owner movement. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. Native read payload grant is a nonwaiting request/global try; failure returns resource_limit before new payload allocation/retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerRequest.QueryString`

```csharp
public System.String NativeWeave.Server.ServerRequest.QueryString | getter=Public;setter=;init=
```

Gets the raw query string including its leading question mark when nonempty.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Raw Body/ReadChunk/ReadJson/GetTrailers are rejected for gRPC/MCP before consumption or owner movement. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. Native read payload grant is a nonwaiting request/global try; failure returns resource_limit before new payload allocation/retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerRequest.RemoteEndPoint`

```csharp
public System.Net.IPEndPoint? NativeWeave.Server.ServerRequest.RemoteEndPoint | getter=Public;setter=;init=
```

Gets the original direct peer unaffected by forwarding.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Raw Body/ReadChunk/ReadJson/GetTrailers are rejected for gRPC/MCP before consumption or owner movement. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. Native read payload grant is a nonwaiting request/global try; failure returns resource_limit before new payload allocation/retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerRequest.RouteValues`

```csharp
public NativeWeave.Server.ParameterValues NativeWeave.Server.ServerRequest.RouteValues | getter=Public;setter=;init=
```

Gets route values decoded once after native matching.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Raw Body/ReadChunk/ReadJson/GetTrailers are rejected for gRPC/MCP before consumption or owner movement. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. Native read payload grant is a nonwaiting request/global try; failure returns resource_limit before new payload allocation/retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerRequest.Scheme`

```csharp
public System.String NativeWeave.Server.ServerRequest.Scheme | getter=Public;setter=;init=
```

Gets the effective scheme after native trusted-forwarding validation.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Raw Body/ReadChunk/ReadJson/GetTrailers are rejected for gRPC/MCP before consumption or owner movement. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. Native read payload grant is a nonwaiting request/global try; failure returns resource_limit before new payload allocation/retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerResponse.Body`

```csharp
public System.IO.Stream NativeWeave.Server.ServerResponse.Body | getter=Public;setter=;init=
```

Gets the borrowed asynchronous output stream.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Every successful HTTP MapRaw/generated void/custom result/middleware path awaits one CompleteAsync/response_finish before lifecycle cleanup; preserve selected status/headers, untouched200. gRPC/Allowed MCP permit only lifecycle hooks on this facade. Bounded metadata-only queue admission may wait; native request/global payload grant and managed overlap grant are nonwaiting with all-or-none rollback. Serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. Payload-free completion uses reserved control metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerResponse.ContentLength`

```csharp
public System.Int64? NativeWeave.Server.ServerResponse.ContentLength | getter=Public;setter=Public;init=False
```

Gets or sets a fixed body length; write mismatch aborts instead of truncating or padding.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Every successful HTTP MapRaw/generated void/custom result/middleware path awaits one CompleteAsync/response_finish before lifecycle cleanup; preserve selected status/headers, untouched200. gRPC/Allowed MCP permit only lifecycle hooks on this facade. Bounded metadata-only queue admission may wait; native request/global payload grant and managed overlap grant are nonwaiting with all-or-none rollback. Serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. Payload-free completion uses reserved control metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerResponse.ContentType`

```csharp
public System.String? NativeWeave.Server.ServerResponse.ContentType | getter=Public;setter=Public;init=False
```

Gets or sets Content-Type in the same header map.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Every successful HTTP MapRaw/generated void/custom result/middleware path awaits one CompleteAsync/response_finish before lifecycle cleanup; preserve selected status/headers, untouched200. gRPC/Allowed MCP permit only lifecycle hooks on this facade. Bounded metadata-only queue admission may wait; native request/global payload grant and managed overlap grant are nonwaiting with all-or-none rollback. Serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. Payload-free completion uses reserved control metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerResponse.HasStarted`

```csharp
public System.Boolean NativeWeave.Server.ServerResponse.HasStarted | getter=Public;setter=;init=
```

Gets whether native final response headers have committed.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Every successful HTTP MapRaw/generated void/custom result/middleware path awaits one CompleteAsync/response_finish before lifecycle cleanup; preserve selected status/headers, untouched200. gRPC/Allowed MCP permit only lifecycle hooks on this facade. Bounded metadata-only queue admission may wait; native request/global payload grant and managed overlap grant are nonwaiting with all-or-none rollback. Serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. Payload-free completion uses reserved control metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerResponse.Headers`

```csharp
public NativeWeave.Server.HeaderCollection NativeWeave.Server.ServerResponse.Headers | getter=Public;setter=;init=
```

Gets response headers mutable before their commit.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Every successful HTTP MapRaw/generated void/custom result/middleware path awaits one CompleteAsync/response_finish before lifecycle cleanup; preserve selected status/headers, untouched200. gRPC/Allowed MCP permit only lifecycle hooks on this facade. Bounded metadata-only queue admission may wait; native request/global payload grant and managed overlap grant are nonwaiting with all-or-none rollback. Serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. Payload-free completion uses reserved control metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerResponse.StatusCode`

```csharp
public System.Int32 NativeWeave.Server.ServerResponse.StatusCode | getter=Public;setter=Public;init=False
```

Gets or sets a final status in 200..599 before headers commit.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Every successful HTTP MapRaw/generated void/custom result/middleware path awaits one CompleteAsync/response_finish before lifecycle cleanup; preserve selected status/headers, untouched200. gRPC/Allowed MCP permit only lifecycle hooks on this facade. Bounded metadata-only queue admission may wait; native request/global payload grant and managed overlap grant are nonwaiting with all-or-none rollback. Serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. Payload-free completion uses reserved control metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerResponse.Trailers`

```csharp
public NativeWeave.Server.HeaderCollection NativeWeave.Server.ServerResponse.Trailers | getter=Public;setter=;init=
```

Gets trailers mutable before completion; transport capability remains native-authoritative.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Every successful HTTP MapRaw/generated void/custom result/middleware path awaits one CompleteAsync/response_finish before lifecycle cleanup; preserve selected status/headers, untouched200. gRPC/Allowed MCP permit only lifecycle hooks on this facade. Bounded metadata-only queue admission may wait; native request/global payload grant and managed overlap grant are nonwaiting with all-or-none rollback. Serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. Payload-free completion uses reserved control metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerRouteAttribute.Method`

```csharp
public System.String NativeWeave.Server.ServerRouteAttribute.Method | getter=Public;setter=;init=
```

Gets the method token.

Finite annotated static handler whitelist; missing/ambiguous shapes are compile diagnostics. Build-time generation; no per-handler Rust compilation, reflection scan, Expression.Compile or runtime argument object[]. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.ServerRouteAttribute.Pattern`

```csharp
public System.String NativeWeave.Server.ServerRouteAttribute.Pattern | getter=Public;setter=;init=
```

Gets the route template.

Finite annotated static handler whitelist; missing/ambiguous shapes are compile diagnostics. Build-time generation; no per-handler Rust compilation, reflection scan, Expression.Compile or runtime argument object[]. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.TrustedProxyOptions.ForwardLimit`

```csharp
public System.Int32 NativeWeave.Server.TrustedProxyOptions.ForwardLimit | getter=Public;setter=Public;init=False
```

Gets or sets the maximum trusted forwarded hops in 1..16, default one; only coherent X-Forwarded-For/Proto/Host tuples apply.

Explicit exact IPs and1..16 right-to-left trusted hops; only coherent X-Forwarded-For/Proto/Host; original peer and MCP wire Host/Origin retained. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `P:NativeWeave.Server.TrustedProxyOptions.KnownProxies`

```csharp
public System.Collections.Generic.IList<System.Net.IPAddress> NativeWeave.Server.TrustedProxyOptions.KnownProxies | getter=Public;setter=;init=
```

Gets exact trusted peer IP addresses.

Explicit exact IPs and1..16 right-to-left trusted hops; only coherent X-Forwarded-For/Proto/Host; original peer and MCP wire Host/Origin retained. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.BodyParameterAttribute`

```csharp
NativeWeave.Server.BodyParameterAttribute | kind=Class;static=False;sealed=True;abstract=False;base=System.Attribute;interfaces=
```

Marks the one statically serialized JSON body parameter.

Finite annotated static handler whitelist; missing/ambiguous shapes are compile diagnostics. Build-time generation; no per-handler Rust compilation, reflection scan, Expression.Compile or runtime argument object[]. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.HeaderCollection`

```csharp
NativeWeave.Server.HeaderCollection | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

Bounded case-insensitive HTTP headers preserving repeated field values.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.HeaderParameterAttribute`

```csharp
NativeWeave.Server.HeaderParameterAttribute | kind=Class;static=False;sealed=True;abstract=False;base=System.Attribute;interfaces=
```

Explicit scalar header binding for generated adapters.

Finite annotated static handler whitelist; missing/ambiguous shapes are compile diagnostics. Build-time generation; no per-handler Rust compilation, reflection scan, Expression.Compile or runtime argument object[]. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.Hosting.NativeServerServiceCollectionExtensions`

```csharp
NativeWeave.Server.Hosting.NativeServerServiceCollectionExtensions | kind=Class;static=True;sealed=False;abstract=False;base=System.Object;interfaces=
```

Optional Generic Host lifecycle integration; Rust remains the only network Host.

Optional Hosting package; one IHostedService; borrows host provider; asynchronous disposal required. One singleton adapter; no Kestrel; stop budget returns explicit Pending and final async disposal joins retained owners. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.IServerAuthenticator`

```csharp
NativeWeave.Server.IServerAuthenticator | kind=Interface;static=False;sealed=False;abstract=True;base=;interfaces=
```

Authentication over parsed application metadata; no transport ownership is transferred.

Explicit caller-owned thread-safe auth SPI; application policy, no built-in JWT/session stack. Admitted gRPC denial uses Tonic Unauthenticated16/PermissionDenied7 framing; ordinary HTTP and MCP pre-parse denial use401/403. Bounded registered callbacks; next single-use; exceptions redacted and classified before/after response commit; application service work remains caller cost. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.IServerAuthorizer`

```csharp
NativeWeave.Server.IServerAuthorizer | kind=Interface;static=False;sealed=False;abstract=True;base=;interfaces=
```

Evaluates application policies after authentication and before body binding.

Explicit caller-owned thread-safe auth SPI; application policy, no built-in JWT/session stack. Bounded registered callbacks; next single-use; exceptions redacted and classified before/after response commit; application service work remains caller cost. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.JsonRequestHandler`2`

```csharp
NativeWeave.Server.JsonRequestHandler<TRequest, TResponse> | kind=Delegate;static=False;sealed=True;abstract=False;base=System.MulticastDelegate;interfaces= | invoke=public virtual System.Threading.Tasks.Task<TResponse> NativeWeave.Server.JsonRequestHandler<TRequest, TResponse>.Invoke(TRequest request, NativeWeave.Server.RequestContext context, System.Threading.CancellationToken cancellationToken)
```

Processes one typed JSON request and response without dynamic binding.

Closed static handler shapes and JsonTypeInfo; no arbitrary Delegate/reflection fallback. Per-request typed invocation/DTO and Task work; no object-array binder; lazy request DI scope only on demand. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **request**: The request value described by this operation.
- **context**: The current borrowed request context; it cannot escape awaited dispatch and cleanup.
- **cancellationToken**: Caller cancellation intent; cancellation never releases native owners before their retirement join.
- **TRequest**: The statically known request type; JSON paths require explicit or registered static metadata.
- **TResponse**: The statically known result type; JSON paths require explicit or registered static metadata.

## `T:NativeWeave.Server.JsonResultHandler`1`

```csharp
NativeWeave.Server.JsonResultHandler<TRequest> | kind=Delegate;static=False;sealed=True;abstract=False;base=System.MulticastDelegate;interfaces= | invoke=public virtual System.Threading.Tasks.Task<NativeWeave.Server.ServerResult> NativeWeave.Server.JsonResultHandler<TRequest>.Invoke(TRequest request, NativeWeave.Server.RequestContext context, System.Threading.CancellationToken cancellationToken)
```

Processes one typed JSON request and chooses an HTTP result.

Closed static handler shapes and JsonTypeInfo; no arbitrary Delegate/reflection fallback. Per-request typed invocation/DTO and Task work; no object-array binder; lazy request DI scope only on demand. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **request**: The request value described by this operation.
- **context**: The current borrowed request context; it cannot escape awaited dispatch and cleanup.
- **cancellationToken**: Caller cancellation intent; cancellation never releases native owners before their retirement join.
- **TRequest**: The statically known request type; JSON paths require explicit or registered static metadata.

## `T:NativeWeave.Server.NativeServer`

```csharp
NativeWeave.Server.NativeServer | kind=Class;static=True;sealed=False;abstract=False;base=System.Object;interfaces=
```

Creates independently owned applications using the required Rust host.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.NativeServerApplication`

```csharp
NativeWeave.Server.NativeServerApplication | kind=Class;static=False;sealed=True;abstract=False;base=NativeWeave.Server.ServerRoutes;interfaces=System.IAsyncDisposable
```

Owns one Rust Host, frozen application routing and optionally owned application services.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.NativeServerBuilder`

```csharp
NativeWeave.Server.NativeServerBuilder | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

Configures one application before Build; configuration is single-threaded and copied at Build.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.ParameterValues`

```csharp
NativeWeave.Server.ParameterValues | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

Invariant typed access to already decoded native route/query values, without reflection.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.QueryParameterAttribute`

```csharp
NativeWeave.Server.QueryParameterAttribute | kind=Class;static=False;sealed=True;abstract=False;base=System.Attribute;interfaces=
```

Explicit scalar or repeated-array query binding for generated adapters.

Finite annotated static handler whitelist; missing/ambiguous shapes are compile diagnostics. Build-time generation; no per-handler Rust compilation, reflection scan, Expression.Compile or runtime argument object[]. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.RequestBindingException`

```csharp
NativeWeave.Server.RequestBindingException | kind=Class;static=False;sealed=True;abstract=False;base=NativeWeave.Server.ServerException;interfaces=
```

Malformed, missing or repeated bound input; default response is HTTP 400.

Explicit caller-owned thread-safe auth SPI; application policy, no built-in JWT/session stack. Bounded registered callbacks; next single-use; exceptions redacted and classified before/after response commit; application service work remains caller cost. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.RequestContext`

```csharp
NativeWeave.Server.RequestContext | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

Borrowed request context, valid through awaited dispatch and cleanup; never capture it in background work.

Borrowed request lifecycle; available cancellation signal, not exact upstream H2 reset certification. gRPC permits metadata/services/identity/Items/cancel/Abort and lifecycle hooks only; raw body/output and OnStarting are rejected. MCP has only its pre-parse HTTP denial exception. Lazy scope/Items (<=128 entries); sticky bounded control notification; TimeRemaining is monotonic; no escaped context after dispatch. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.RequestHandler`

```csharp
NativeWeave.Server.RequestHandler | kind=Delegate;static=False;sealed=True;abstract=False;base=System.MulticastDelegate;interfaces= | invoke=public virtual System.Threading.Tasks.Task NativeWeave.Server.RequestHandler.Invoke(NativeWeave.Server.RequestContext context, System.Threading.CancellationToken cancellationToken)
```

Writes a response directly; successful return is followed by the shared HTTP completion tail before dispatch cleanup.

Closed static handler shapes and JsonTypeInfo; no arbitrary Delegate/reflection fallback. Per-request typed invocation/DTO and Task work; no object-array binder; lazy request DI scope only on demand. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **context**: The current borrowed request context; it cannot escape awaited dispatch and cleanup.
- **cancellationToken**: Caller cancellation intent; cancellation never releases native owners before their retirement join.

## `T:NativeWeave.Server.ResponseHandler`

```csharp
NativeWeave.Server.ResponseHandler | kind=Delegate;static=False;sealed=True;abstract=False;base=System.MulticastDelegate;interfaces= | invoke=public virtual System.Threading.Tasks.Task<NativeWeave.Server.ServerResult> NativeWeave.Server.ResponseHandler.Invoke(NativeWeave.Server.RequestContext context, System.Threading.CancellationToken cancellationToken)
```

Returns one result for execution within the current request lifetime.

Closed static handler shapes and JsonTypeInfo; no arbitrary Delegate/reflection fallback. Per-request typed invocation/DTO and Task work; no object-array binder; lazy request DI scope only on demand. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **context**: The current borrowed request context; it cannot escape awaited dispatch and cleanup.
- **cancellationToken**: Caller cancellation intent; cancellation never releases native owners before their retirement join.

## `T:NativeWeave.Server.Results`

```csharp
NativeWeave.Server.Results | kind=Class;static=True;sealed=False;abstract=False;base=System.Object;interfaces=
```

Creates HTTP results with explicit static serialization and stream ownership.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Bounded metadata-only native queue wait followed by nonwaiting request/global payload grant and rollback before copy; serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.RouteGroupBuilder`

```csharp
NativeWeave.Server.RouteGroupBuilder | kind=Class;static=False;sealed=True;abstract=False;base=NativeWeave.Server.ServerRoutes;interfaces=
```

Groups routes with inherited prefixes, authorization and filters resolved at freeze.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. A gRPC/MCP endpoint with a local or inherited ServerFilter is rejected at route freeze before listening. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.RouteParameterAttribute`

```csharp
NativeWeave.Server.RouteParameterAttribute | kind=Class;static=False;sealed=True;abstract=False;base=System.Attribute;interfaces=
```

Explicit scalar route binding for generated adapters.

Finite annotated static handler whitelist; missing/ambiguous shapes are compile diagnostics. Build-time generation; no per-handler Rust compilation, reflection scan, Expression.Compile or runtime argument object[]. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.ServerBudgetOptions`

```csharp
NativeWeave.Server.ServerBudgetOptions | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

Finite application-owned credits; these are not process RSS limits.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. Native global/request payload grants are atomic and nonwaiting for both reads and writes; queue/network waits are separate, full backing capacity/overlap is charged, and all partial grants roll back without automatic retry. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.ServerCapabilities`

```csharp
NativeWeave.Server.ServerCapabilities | kind=Enum;static=False;sealed=True;abstract=False;base=System.Enum;interfaces=
```

Negotiated optional host capabilities.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.ServerCloseResult`

```csharp
NativeWeave.Server.ServerCloseResult | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=System.IEquatable<NativeWeave.Server.ServerCloseResult>
```

Close observation; Pending retains roots, native owners and credits.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **Status**: The Status value described by this operation.
- **PendingRequests**: The PendingRequests value described by this operation.
- **RetainedManagedBytes**: The RetainedManagedBytes value described by this operation.
- **RetainedNativeBytes**: The RetainedNativeBytes value described by this operation.

## `T:NativeWeave.Server.ServerCloseStatus`

```csharp
NativeWeave.Server.ServerCloseStatus | kind=Enum;static=False;sealed=True;abstract=False;base=System.Enum;interfaces=
```

Whether all managed/native work has retired before close returned.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.ServerEndpointBuilder`

```csharp
NativeWeave.Server.ServerEndpointBuilder | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

Configures metadata of one endpoint before route freeze; not thread safe.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. A gRPC/MCP endpoint with a local or inherited ServerFilter is rejected at route freeze before listening. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.ServerEndpointsAttribute`

```csharp
NativeWeave.Server.ServerEndpointsAttribute | kind=Class;static=False;sealed=True;abstract=False;base=System.Attribute;interfaces=
```

Marks a static partial endpoint class for compile-time binding generation.

Finite annotated static handler whitelist; missing/ambiguous shapes are compile diagnostics. Build-time generation; no per-handler Rust compilation, reflection scan, Expression.Compile or runtime argument object[]. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.ServerEvent`

```csharp
NativeWeave.Server.ServerEvent | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=System.IEquatable<NativeWeave.Server.ServerEvent>
```

One immutable SSE event; each string is bounded by the endpoint chunk policy.

sse bit6; prevalidated Event/Id; finite whole-stream deadline. One enumerable event in flight; checked conservative expanded UTF-8 reserve; native Axum Event/Sse formatting; no replay retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **Data**: The Data value described by this operation.
- **Event**: The Event value described by this operation; omitted calls use null.
- **Id**: The Id value described by this operation; omitted calls use null.
- **Retry**: The Retry value described by this operation; omitted calls use null.

## `T:NativeWeave.Server.ServerException`

```csharp
NativeWeave.Server.ServerException | kind=Class;static=False;sealed=False;abstract=False;base=System.Exception;interfaces=
```

Server-domain failure; native panic/poison/ABI failures use NativeEngineException.

Explicit caller-owned thread-safe auth SPI; application policy, no built-in JWT/session stack. Bounded registered callbacks; next single-use; exceptions redacted and classified before/after response commit; application service work remains caller cost. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.ServerFilter`

```csharp
NativeWeave.Server.ServerFilter | kind=Delegate;static=False;sealed=True;abstract=False;base=System.MulticastDelegate;interfaces= | invoke=public virtual System.Threading.Tasks.Task<NativeWeave.Server.ServerResult> NativeWeave.Server.ServerFilter.Invoke(NativeWeave.Server.RequestContext context, NativeWeave.Server.ResponseHandler next, System.Threading.CancellationToken cancellationToken)
```

Composes HTTP result filters without object-array boxing; gRPC/MCP endpoints and inherited protocol filters are rejected at route freeze.

Explicit caller-owned thread-safe auth SPI; application policy, no built-in JWT/session stack. HTTP ServerResult pipeline only; gRPC/MCP endpoint filters, including inherited group filters, fail route freeze. Bounded registered callbacks; next single-use; exceptions redacted and classified before/after response commit; application service work remains caller cost. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **context**: The current borrowed request context; it cannot escape awaited dispatch and cleanup.
- **next**: The next value described by this operation.
- **cancellationToken**: Caller cancellation intent; cancellation never releases native owners before their retirement join.

## `T:NativeWeave.Server.ServerListenOptions`

```csharp
NativeWeave.Server.ServerListenOptions | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

One listener's mutable startup policy.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.ServerMiddleware`

```csharp
NativeWeave.Server.ServerMiddleware | kind=Delegate;static=False;sealed=True;abstract=False;base=System.MulticastDelegate;interfaces= | invoke=public virtual System.Threading.Tasks.Task NativeWeave.Server.ServerMiddleware.Invoke(NativeWeave.Server.RequestContext context, NativeWeave.Server.RequestHandler next, System.Threading.CancellationToken cancellationToken)
```

Composes asynchronous middleware; next is single-invocation and protocol contexts forbid raw HTTP body/output access.

Explicit caller-owned thread-safe auth SPI; application policy, no built-in JWT/session stack. gRPC middleware uses restricted RequestContext; short circuit without explicit GrpcException maps to Unauthenticated16/PermissionDenied7, never raw HTTP. Bounded registered callbacks; next single-use; exceptions redacted and classified before/after response commit; application service work remains caller cost. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.

- **context**: The current borrowed request context; it cannot escape awaited dispatch and cleanup.
- **next**: The next value described by this operation.
- **cancellationToken**: Caller cancellation intent; cancellation never releases native owners before their retirement join.

## `T:NativeWeave.Server.ServerOptions`

```csharp
NativeWeave.Server.ServerOptions | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

Mutable startup options, frozen by Build.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.ServerProtocols`

```csharp
NativeWeave.Server.ServerProtocols | kind=Enum;static=False;sealed=True;abstract=False;base=System.Enum;interfaces=
```

HTTP protocol selection without implicit upgrades.

Linux x64 verified package; other RID acceptance is separate; base http1/http2/tls/trailers module bits; requested listener capabilities checked at Build. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.ServerRequest`

```csharp
NativeWeave.Server.ServerRequest | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

Read-only native HTTP metadata with one bounded body-consumption mode and request-scoped lifetime.

Static IParsable/JsonTypeInfo and native validated metadata; request collections readonly; trailers bit3 after EOF. Raw Body/ReadChunk/ReadJson/GetTrailers are rejected for gRPC/MCP before consumption or owner movement. Bounded lazy UTF-8 decoding and optional owned snapshots; repeated values preserved; one body mode/reader; no managed HTTP parser. Native read payload grant is a nonwaiting request/global try; failure returns resource_limit before new payload allocation/retention. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.ServerResponse`

```csharp
NativeWeave.Server.ServerResponse | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

Request-scoped HTTP response with one writer, one completion attempt and deterministic cleanup.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Every successful HTTP MapRaw/generated void/custom result/middleware path awaits one CompleteAsync/response_finish before lifecycle cleanup; preserve selected status/headers, untouched200. gRPC/Allowed MCP permit only lifecycle hooks on this facade. Bounded metadata-only queue admission may wait; native request/global payload grant and managed overlap grant are nonwaiting with all-or-none rollback. Serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. Payload-free completion uses reserved control metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.ServerResult`

```csharp
NativeWeave.Server.ServerResult | kind=Class;static=False;sealed=False;abstract=True;base=System.Object;interfaces=
```

Executes one HTTP result inside the active request lifetime without owning transport control.

Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Bounded metadata-only native queue wait followed by nonwaiting request/global payload grant and rollback before copy; serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.ServerRouteAttribute`

```csharp
NativeWeave.Server.ServerRouteAttribute | kind=Class;static=False;sealed=True;abstract=False;base=System.Attribute;interfaces=
```

Declares a route on a supported static method; arbitrary Delegate reflection is prohibited.

Finite annotated static handler whitelist; missing/ambiguous shapes are compile diagnostics. Build-time generation; no per-handler Rust compilation, reflection scan, Expression.Compile or runtime argument object[]. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.ServerRoutes`

```csharp
NativeWeave.Server.ServerRoutes | kind=Class;static=False;sealed=False;abstract=True;base=System.Object;interfaces=
```

Registers typed endpoints before the first Start; no member opens a listener or translates a request path.

HTTP1/HTTP2 route subset; frozen registries, no hot route mutation. O(registration bytes) once; native Axum matching; groups/policies flattened before serving; bounded immutable metadata. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.ServerState`

```csharp
NativeWeave.Server.ServerState | kind=Enum;static=False;sealed=True;abstract=False;base=System.Enum;interfaces=
```

Immutable runtime lifecycle observations.

Single required Rust host; no physical unload; optional capabilities negotiated before use. One app/provider/runtime; Core operation/cancellation records; Pending retains all owners/credits; DisposeAsync joins without a time limit. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.ServiceParameterAttribute`

```csharp
NativeWeave.Server.ServiceParameterAttribute | kind=Class;static=False;sealed=True;abstract=False;base=System.Attribute;interfaces=
```

Marks a required per-request DI service; its type is statically rooted.

Finite annotated static handler whitelist; missing/ambiguous shapes are compile diagnostics. Build-time generation; no per-handler Rust compilation, reflection scan, Expression.Compile or runtime argument object[]. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


## `T:NativeWeave.Server.TrustedProxyOptions`

```csharp
NativeWeave.Server.TrustedProxyOptions | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

Explicit forwarding trust; unlisted direct peers cannot supply effective identity.

Explicit exact IPs and1..16 right-to-left trusted hops; only coherent X-Forwarded-For/Proto/Host; original peer and MCP wire Host/Origin retained. Single-threaded bounded startup snapshots; O(config bytes); options/services freeze at Build, routes separately freeze at first Start; no default global runtime. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.


