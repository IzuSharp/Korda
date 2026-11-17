# Server diagnostics, deadlines and shutdown

`NativeWeave.Server` exposes the standard .NET `ActivitySource`, `Meter` and structured `ILogger` integration around the actual Rust Host. Their source/category name is **`NativeWeave.Server`**; ActivitySource and Meter versions come from the managed package version. The application owns its listeners and logging providers. The library installs no listener, meter provider, global Rust subscriber or network exporter, in either Release or Debug.

The [finite consumer](../../samples/api/server-diagnostics) demonstrates both the default mode and an explicit bounded local debugging mode. Ordinary DTO/Handler builds consume the same maintainer-precompiled native asset. There is no diagnostic runtime, managed network server or alternate callback protocol to configure.

## Select diagnostics explicitly

Create an `ActivityListener` whose `ShouldListenTo` selects `NativeWeave.Server`, choose a `Sample` policy, and add it with `ActivitySource.AddActivityListener`. A sampled request has a `Server` Activity named `request`. `PropagationData` can preserve its context without requesting tags; no listener creates no Activity or request diagnostic scope. A MeterListener independently selects the same meter. Installing either listener does not install an exporter.

Register optional logging through the builder's existing `Services.AddLogging`. The bridge emits Warning for normalized cancellation, timeout, binding and quota failures, and Error for an application failure. It checks `IsEnabled` before queueing or formatting. Sampling does not suppress enabled Warning/Error logs. Successful requests do not generate log messages. The bridge never passes an exception object or its message/stack trace to a sink.

The sample's `--local` argument explicitly enables one-in-eight Activity sampling, an observable MeterListener and a bounded console provider. Without that argument its Debug and Release executions both keep diagnostics disabled. An application can use its own OpenTelemetry integration through these standard interfaces; OpenTelemetry and a network exporter are not mandatory dependencies. A third-party listener's own queues and synchronous callbacks remain application-owned configuration.

## Correlation and the exact native value

The existing `nw_server_metadata_read` request metadata carries a D19 `TraceContextV1` as a bounded byte view. Its layout is fixed: size 32 at offset 0, version 1 at offset 4, sampled flag at offset 6, zero reserved byte at 7, 16 W3C trace-ID bytes at 8, and eight parent-span bytes at 24. Zero IDs, unsupported internal flags, malformed size/version/reserved bytes or an invalid native view fail the managed boundary check. No Activity, ExecutionContext, span guard, diagnostic GCHandle or object pointer is serialized across the ABI.

Native admission validates external `traceparent` and `tracestate` according to the pinned [W3C Trace Context Recommendation](https://www.w3.org/TR/2021/REC-trace-context-1-20211123/) sections 3.2/3.3. A malformed traceparent is counted and starts a new root when tracing is selected. Invalid tracestate is counted and discarded while preserving a valid traceparent. Multiple tracestate fields combine in received order; the resulting ASCII/UTF-8 value is at most 512 bytes and 32 list members. Empty members are accepted, keys are unique, and values and keys follow the bounded grammar. Future traceparent extension fields are not interpreted. Baggage is not forwarded into diagnostic context.

The native context is copied while its metadata owner is leased. It becomes the explicit remote parent of the managed Activity; an unrelated ThreadPool Activity is never selected as an implicit parent. The Activity flows through awaited handlers, filters and cleanup, then is disposed after native callback retirement. The previous managed scope is restored even if a listener throws. `RequestContext.TraceIdentifier` is correlation metadata and never authentication or authorization evidence.

Rust captures an already application-owned `tracing` dispatcher when the Host is created. Native futures use `Instrumented` plus owned `WithDispatch` on each poll, including a change of worker thread. Synchronous body polls/callbacks enter and leave their scope in the same call. No `Span::enter` guard crosses an await and no library call seizes the process global subscriber. When a native embedding selects a subscriber, its request span includes bounded route/protocol fields and structural parent IDs; these IDs are never metric dimensions. The native owner-retirement observation may include independent payload retention, while the managed callback-retirement instrument below stops at the request callback join.

## Instruments and bounds

| Instrument | Unit and meaning |
| --- | --- |
| `nativeweave.server.request.duration` | Seconds, native admission to managed response completion; cleanup/retirement is observed separately. |
| `nativeweave.server.request.callback_retirement.duration` | Seconds, native admission to the request callback/root retirement join. Independently retained BodyChunk payload can outlive it. |
| `nativeweave.server.requests.active`, `.queued`, `.retiring` | Native bounded request counts. |
| `nativeweave.server.payload.managed.bytes`, `.native.bytes` | Actual existing payload ownership ledger totals, including retained backing credit. |
| `nativeweave.server.requests.rejected` | Cumulative admission rejection count. |
| `nativeweave.server.callbacks.failed` | Cumulative native callback failure count, independent of observer activation. |
| `nativeweave.server.callback.duration.mean`, `.max` | Seconds for the small raw managed notification callback. Only scalar clocks/atomics execute there; no sink, formatter or user handler runs inside FFI. Sampling enables this scalar observation. |
| `nativeweave.server.diagnostics.queue.bytes`, `.entries` | Metadata currently retained by the optional diagnostic queue, including an in-flight blocked item. |
| `nativeweave.server.diagnostics.dropped` | Malformed contexts, full queues, export failures or isolated listener faults. |

Duration dimensions are the registered route template, HTTP protocol profile, status class and normalized error category. Route strings are limited to 256 UTF-8 bytes. There are no URL/query values, request/operation IDs, arbitrary tool arguments, credentials, SQL or certificate/key values in metric dimensions or framework log events. Current request Activities use at most four bounded fields; exception descriptions use a fixed category. The maximum-callback observation is a process lifetime maximum, not a histogram percentile.

Observable instruments read `nw_server_get_snapshot` at most once per second across enabled observations, or at a lifecycle boundary. The library starts no background polling timer. Active/retiring/payload admission and release counters remain mandatory even when no diagnostic observer exists. Observer sampling never gates a correctness check or resource release.

The optional logging/export worker accepts at most 2,048 entries, 524,288 charged bytes total and 1,024 charged bytes per entry. A charge includes a fixed metadata allowance plus the larger of each string's UTF-8 bytes and UTF-16 storage. The in-flight item still consumes entry and byte credit. Queue admission is nonblocking: a full queue drops the event and increments the counter. All stored items contain only redacted category, route template, protocol and status class. The worker does not capture the request's ExecutionContext, Activity, DI scope, native owner or callback root.

## Deadlines and two-stage shutdown

The Host's existing monotonic deadline covers admission, managed work and body operations. `TimeRemaining` decreases across awaits; diagnostics creates no second timer. Native cancellation cause distinguishes timeout from client cancellation without replacing the existing sticky notification. Database outcomes are not inferred from cancellation: an Unknown commit remains Unknown, and a request trace must not claim rollback or success on that basis.

`CloseAsync` still reports Pending while callbacks, operations, independently owned payload or the native OS worker remain live. Pending closes only the caller's bounded wait. Owners and credit remain retained, and `DisposeAsync` keeps joining until safe retirement. A completed request Activity does not authorize releasing a retained BodyChunk.

Only after business/native owners have closed does application disposal give the optional metadata exporter a separate **250 ms** flush. A blocked export or a blocked exporter cancellation registration cannot extend that deadline. Queued metadata is dropped; a still-executing exporter retains its bounded in-flight charge until it returns. It retains no framework business owners. Logger exceptions are counted and isolated, and a throwing Activity listener cannot skip response cleanup or native retirement. Raw FFI callbacks perform no export I/O and hold no business lock while invoking a sink.

## Evidence and practical interpretation

`tests/Server.Diagnostics` runs actual Linux x64 HTTP/1.1 and HTTP/2 callbacks, the shared Rust/C# golden layout, malformed context cases, async managed parent restoration, Rust per-poll dispatcher migration, native TCP subscriber/metadata retirement, exporter entry/byte saturation, disconnect, blocked export and blocked cancellation registration, no-secret checks, deadline cancellation, retained-body ClosePending and repeated disposal. It exercises Release and Debug defaults separately and publishes/runs the actual NativeAOT consumer. Its bounded sample also runs against locally produced NuGet packages with a consumer PATH that has no Cargo/rustc.

The four matched cost rows use the same finite failing HTTP workload with diagnostics disabled, one-in-eight Activities, full Activities, and an exporter fault. They report whole-process allocations/CPU/RSS and request latency, including the test client; they are not isolated framework allocation claims or a performance-superiority claim. Disabled callback timing is deliberately unobserved because the scalar meter is disabled. Enabled/fault rows observe the actual raw callback maximum. Immutable source, lock, asset and executable hashes and exact command logs accompany the receipt. Windows and final release-wide pristine consumption remain separate platform/task gates.
