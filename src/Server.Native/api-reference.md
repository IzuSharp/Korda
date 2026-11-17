# Native Server implementation contribution

B17 adds no public C# API. `NativeHost`, `NativeRequest`, exact wire records, copied-input scopes, typed native-call tables and owner handles are internal source linked into `NativeWeave.Server`. Consumers use B15's public assembly surface and B16's `BodyChunk`.

| Public responsibility | Required native behavior |
| --- | --- |
| NativeServerBuilder / NativeServerApplication | Immutable creation; capability negotiation; atomic listener bind; one native runtime; actual native join and completed/pending close |
| ServerListenOptions / ServerTlsOptions | Native protocol validation, HTTP/1.1 / HTTP/2 / ALPN, whole-snapshot TLS reload, bounded handshakes |
| ServerRoutes / ServerEndpointBuilder | Axum route validation and method admission; bounded ticket dispatch outside unmanaged callbacks |
| ServerRequest / BodyChunk | Validated immutable metadata, sticky cancellation, monotonic remaining budget, independent retained buffer owners |
| ServerResponse / Results | Exact begin/reserve/prepare/write/flush/finish/abort operations, final framing, source-bound Axum SSE formatting |
| ServerException / ServerCapabilities | Negotiated capability correspondence and typed native errors under the Core cleanup lease |

The pinned public declaration inventory is B15's `public-api.expected.json`; B17's exact native signature inventory and supported A10/A11 projection live in `tests/Server.Native/Bindings`. No opaque handle, function pointer, runtime or raw native byte view is public application API. Samples in `samples/api/server-native` compile against the actual product assembly and run on the required native Host.
