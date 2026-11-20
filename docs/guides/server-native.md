# Required native Server Host

The `NativeWeave.Server` package runs the network stack in its maintainer-built Rust asset. C# owns application handlers, scopes and static serializers. Adding a handler requires an ordinary `dotnet build`; it does not change or recompile the native module. The Linux x64 asset composes Tokio 1.54.1, Hyper 1.12.0, hyper-util 0.1.21, Tower 0.5.3, Axum 0.8.9 and rustls 0.23.46. Cargo.lock records exact checksums and feature selection. Kestrel appears only in the comparison project.

## Start and stop

```csharp
using System.Net;
using NativeWeave.Server;

var builder = NativeServer.CreateBuilder();
builder.Listen(IPAddress.Loopback, 0);
await using var app = builder.Build();
app.MapGet("/hello/{name}", context =>
    Results.Text("Hello " + context.Request.RouteValues.Get<string>("name")));
await app.RunAsync();
```

`Build` validates and copies the finite native configuration and negotiates the process-resident engine through the Core loader. It opens no listening sockets and starts no Tokio worker. `StartAsync` freezes the route/handler registry and binds every listener before publishing addresses. Port zero chooses an ephemeral port. A failed later bind drops earlier listeners and joins the worker before the managed startup failure is returned.

Each Host has one native runtime. Axum matches paths and native code resolves methods and implicit GET-to-HEAD behavior. An unmanaged callback publishes only a bounded ticket; the shared Core callback coordinator schedules managed work. Typed request take and metadata copying happen outside that callback. Handlers and cancellation registrations never execute on its stack.

A close request stops admission, drains work until its monotonic deadline, then requests cancellation and transport abort. `Completed` requires request/buffer/operation/callback retirement and an actual native worker join. `Pending` is a truthful snapshot: retained body owners or executing managed work still exist. Keep the application alive and await disposal; a timeout never permits forced native unload or premature callback-cookie release. Independently retained `BodyChunk` values remain usable and disposable after dispatch. Their full backing allocations keep request/global credit until the last owner releases them.

Request admission remains owned until the dispatcher, protocol continuation and cancellation callbacks have all joined. The request's final native handle release and managed ticket removal share a separate admission gate, including when an admitted physical lease delays that release. A new request therefore cannot observe a returned native slot paired with the previous managed ticket. Close continues to count that ticket until physical release completes, even when the native worker has already stopped and no payload bytes remain. The host control gate is not held across the native destructor, preserving cancellation delivery during close. This adds no public API, ABI entry or per-request delegate allocation.

## HTTP and TLS

Cleartext listeners select HTTP/1.1 or HTTP/2 prior knowledge. Combined HTTP/1.1-and-HTTP/2 listeners require TLS and use ALPN. Native validation repeats managed configuration checks before any listener or callback admission. Fixed configuration bounds cover connections, HTTP/2 streams, header bytes, active/queued requests and concurrent TLS handshakes. Request queueing retains metadata only.

Use `ServerTlsOptions.FromPemFiles` and the builder's listener options to select a complete TLS configuration. The native parser validates certificate, key, optional client CA, handshake timeout and handshake limit together. TLS reload constructs a replacement immutable snapshot before creating the operation; publication is atomic. Existing handshakes and connections retain their original configuration. New handshakes capture the replacement configuration and its handshake admission policy. A failed reload preserves the old snapshot. Private key bytes are never part of diagnostics.

The native outer gate limits and validates request metadata before invoking C#. The optional trusted-proxy policy accepts a coherent forwarded chain only from an explicitly trusted socket peer; the original peer remains available. It does not enable forwarding implicitly. HTTP transfer framing remains native: 404/405, HEAD, 204, 205 and 304 handling, content length agreement, trailers and connection cancellation are verified over real TCP.

## Bodies and SSE

Request reads expose B16-owned chunks. Each read reserves full backing capacity before polling native input; partial slices do not lower the charge. Raw response writes wait for a bounded queue slot while holding no newly allocated payload, then try native request/global credit without waiting. A full retained inbound budget therefore fails a new write promptly with `resource_limit`. `WriteAsync` copies call-borrowed input during native operation creation, before returning, and transfers that prepared allocation to the body queue only on operation start. Never-started release rolls it back.

SSE uses Axum's public `Event` and `Sse` formatter. The conservative design admission bound is `6 * UTF8(data).Length + UTF8(event).Length + UTF8(id).Length + 128`. Native admission also checks the exact source-derived newline expansion bound. A single event must fit `MaxChunkBytes`; it is never split into new events. Queue and native peak credit are granted before formatter allocation. Only the three temporary UTF-8 bridge arrays consume additional managed credit; application-owned strings do not. The full formatter growth allocation stays charged through all later `Bytes` clones and slices. The pinned source proof is [formatter-ownership.md](../../tests/Server.Native/formatter-ownership.md).

## Errors and capabilities

`ServerCapabilities` comes from the negotiated native asset before callback registration. This implementation advertises HTTP/1.1, HTTP/2, TLS, trailers and SSE. gRPC and MCP entries remain exact declared exports returning `NotSupported` until their task-owned adapters install those capabilities; their presence is not a capability claim.

Native status/error fields are copied inside the shared Core operation cleanup lease. Listener bind, TLS configuration, payload admission, body-limit and content-length errors map to stable `ServerException.Code` values. Invalid state, cancellation, deadline and unsupported capability remain distinct. Native Host failure signals `Lifetime`, allowing `RunAsync` to report it after safe cleanup. Cancellation is sticky before request take as well as after it. A faulted Host still permits all trusted owner-retirement and release operations.

## Exact binding and documentation coverage

B17 exposes no new public C# types. Its internal source is linked into the documented B15 `NativeWeave.Server` assembly; it implements the host/body operation correspondence of those public APIs. The public XML and exact API reference remain owned by the assemblies containing the public declarations. The B17 [API applicability](../../src/Server.Native/api-reference.md) identifies that contribution.

All 48 normative exports and eight Server records are checked against the SHA-pinned design header. The C compiler, Rust constant function-pointer/layout assertions and NativeAOT-managed layout assertions cover 103 record fields. A10/A11 regenerate 34 supported lifecycle and pointer-form exports. The other 14 contain exact by-value/nested-record forms outside A09's scalar-record profile; checked direct bindings preserve their declared ABI. `Bindings/coverage.json` records every included and explicit export, without changing signatures or pretending that A09 supports a different profile.

## Verify and interpret costs

Run `python3 tests/Server.Native/verify.py` with the repository's fixed toolchain. Its evidence includes strict native Clippy, targeted native tests, deterministic headers/bindings/layouts, JIT and Linux x64 NativeAOT execution, source/native/executable hashes, compiled minimal and advanced consumers, and the matched HTTP comparison row. No Windows or other-platform acceptance is implied.

The comparison measures one HTTP/1.1 keep-alive workload with a fixed 16-byte body against direct Axum/Hyper and Kestrel. It reports end-to-end native-plus-FFI costs, latency, process memory and managed allocation totals. It does not isolate a single FFI call or establish throughput, memory or language superiority across other workloads. It is not a long-running-load result. A16/B24 own the final pristine no-Rust package-consumption gates; B17 owns the actual component/Host rows reported here.
