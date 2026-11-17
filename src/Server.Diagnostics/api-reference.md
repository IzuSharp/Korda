# Server diagnostics API contribution

B20 adds no public C# type, overload, handle, configuration object, callback protocol or exporter package. Its implementation is internal and source linked into `NativeWeave.Server`. The complete pinned public declaration inventory remains the B15 server inventory; B20's reference applicability is the integration of the following existing public members and standard .NET extension points.

| Existing surface | Diagnostic behavior |
| --- | --- |
| `RequestContext.TraceIdentifier` | Bounded correlation value. A validated remote D19 context preserves its trace ID; a sampled managed request Activity shares it. It confers no identity or authorization. |
| `RequestContext.TimeRemaining` / `RequestAborted` | Existing native monotonic total request budget and sticky cancellation; diagnostics adds no timeout or CTS. |
| `NativeServerApplication.CloseAsync` | Completed/Pending retains the exact existing request/callback/payload and OS-thread join rules regardless of listeners or exporter health. |
| `NativeServerApplication.DisposeAsync` | Joins business ownership, then gives an optional copied diagnostic queue 250 ms to flush before releasing owned application services. |
| `ActivitySource` / `ActivityListener` | Source name `NativeWeave.Server`; version comes from the managed package. Sampling gates Activity tags; no listener means no Activity. |
| `Meter` / `MeterListener` | Meter name/version match the ActivitySource. Fixed-cardinality request duration and finite, explicitly sampled native snapshot observations. |
| `ILoggerFactory` / `ILogger` | Optional application-provided sink in category `NativeWeave.Server`; structured Warning/Error events contain only bounded normalized metadata. |

`TraceContextV1`, Rust `Diagnostics`/`RequestTrace`, `ServerDiagnosticsController`, queue counters and transport hooks are internal implementation details. They are not a new public ABI or application-facing registration API. The exact existing native request metadata views carry the 32-byte context and optional bounded tracestate. The Core callback table and A07 operation protocol are reused unchanged.

The public-only consumer in `samples/api/server-diagnostics` is compiled and run both disabled and explicitly enabled. The task verifier checks the compiled exported surface for accidental diagnostic plumbing and binds public/XML coverage to the unchanged pinned Server inventory. The guide documents metric names, byte limits, error behavior, activation and shutdown. D19's frozen value layout, actual metadata transfer and malformed input checks are executable tests, not a reproduction of Design acceptance results.
