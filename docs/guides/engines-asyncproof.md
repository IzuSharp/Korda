# Actual asynchronous HTTP engine proof (A18)

`NativeWeave.Engines.AsyncProof` is a bounded verification package for Linux x64. It exercises real loopback TCP, Hyper HTTP/1 service/Body, an engine-owned Tokio runtime, a statically compiled C# handler and actual A06/A07 lifetime machinery. It establishes this profile's integration behavior; it is not the final `NativeWeave.Server` API or a production server recommendation. Windows is outside this run.

## Install and consume

Maintainers build the locked native asset and run `tests/Engines.AsyncProof/verify.py`. Its local feed contains `NativeWeave.Core` and `NativeWeave.Engines.AsyncProof` version `0.1.0-proof.1`. An ordinary application references the proof package, then uses normal `dotnet build`/`dotnet run`. Native AOT uses `dotnet publish -c Release -r linux-x64 -p:PublishAot=true`. The package supplies the precompiled native asset and A16 build metadata; handler changes never invoke Rust. The feed is a validation artifact, not a public NuGet release.

```csharp
await using var host = new HttpProofHost(
    static (route, cancellationToken) => Task.FromResult(
        System.Text.Encoding.UTF8.GetBytes($"Hello {route}")));
using HttpProofResponse response = await host.GetAsync(42);
while (await response.ReadChunkAsync() is { } chunk)
{
    using (chunk) Console.WriteLine(System.Text.Encoding.UTF8.GetString(chunk.ToArray()));
}
```

The executable [minimal and advanced consumers](../../samples/api/engines-asyncproof/Program.cs) also show a retained chunk surviving early response disposal and actual host closure. The [generated reference](../../src/Engines.AsyncProof/api-reference.md) comes from the compiled assembly/XML and its public signature baseline.

## Native capability and typed operations

The immutable descriptor advertises `http.loopback.stream.v1` at capability bit zero, ABI 1.0, engine-local asynchronous threading and x86-64 baseline. Inspect negotiates through `nw_asyncproof_get_abi_v1`; merely reading/importing the descriptor starts no runtime. This proof host binds `127.0.0.1` on an ephemeral port. Numeric routes are application data; the handler delegate and callback entry points are statically compiled and AOT safe. The smallest profile uses one request per HTTP/1 connection and a bounded payload, not a general router or request-body API.

The A09 inventory parses the actual adapter, and A10/A11 validate its exact linked export set, generated C# imports, Rust function/layout assertions and C header. Core owns the nongeneric unmanaged callback functions. Generated create/start/take wrappers use A03 physical leases and A07's actual setup executor; the engine does not duplicate the Task state machine. Native errors preserve status and fixed HTTP domain `0x48545450`, native code and operation correlation. Ordinary application-handler exceptions intentionally produce HTTP 500 with an empty Body. A callback/notification infrastructure failure instead faults actual host close after safe cleanup.

## Capacity and ownership

| Resource | Finite profile | Release predicate |
| --- | --- | --- |
| Operations and managed cleanup reservations | `maxOperations`, 1–64 | Actual A07 native quiet plus managed controls/setup/result processing and retirement-observer completion |
| Held response items | Separate `maxOperations` quota | Response disposal and all already admitted read-source references end |
| Held nonempty chunk items | Separate `maxOperations` quota | Last creating-owner release; an EOF take transfers an immediately released empty sentinel |
| Live stream slots | `maxStreams`, 1–8 | Last server Body, producer and claimed managed-handler source drops |
| Retained backing bytes | `retainedBytes`, up to one MiB | Last `Bytes` backing owner drops; each nonempty allocation conservatively charges `chunkBytes` |
| Notification queue | At most `maxStreams` items / `12 * maxStreams` copied bytes, including active handlers | Active handler's final acknowledgement or discard before claim |
| Producer queue | One native frame per live Body | Item-owned accounting refunds on consumption or any receiver/channel destruction, including publication through an already-issued permit after close |
| Connections | `4 * maxStreams` admitted sockets | Native connection task destruction |

`chunkBytes` is 1–4096 and bounds the complete handler response and each received frame. A response producer emits at most two frames, optionally delayed by 0–1000 ms. Hyper/Tokio own their bounded connection buffers and fixed metadata; these application quotas are not a whole-process RSS bound. Requests exceeding stream capacity receive HTTP 503. Operation admission may throw `OutOfMemoryException`; a quota failure after an operation is accepted is a `NativeEngineException` with status 5. A failed/canceled read closes the remaining Body.

The backing-byte and stream permits are deliberately independent. EOF, early Body disposal or host close can retire all streams while a caller still holds `NativeBuffer` chunks and their byte quota. Native chunk read leases remain usable after host disposal. Copying to managed arrays is explicit; no raw pointer, escaping span or native-backed `Memory` is public.

## Cancellation, reset and closure

A cancellation token requests cooperative native cancellation; the committed native outcome wins and successful completion remains legal. Task completion does not imply native source retirement. Each response permits one active read. Early response disposal cancels that read while its native operation still holds a separate response Arc.

`Dispose` closes admission and starts cleanup. `CloseAsync`/`DisposeAsync` join the native OS worker, native callback source quiet, active managed handlers, setup/controls, operation retirement and bounded managed retirement bookkeeping. A caller's canceled close wait leaves the cleanup owner intact. Foreign cancellation registrations run through the BCL asynchronous cancellation owner, so they cannot block the initial Dispose/CloseAsync call; actual close still joins them and preserves their failures after cleanup. A handler cannot await its own host retirement; A06 detects this exact scope. A handler that ignores cancellation retains its claim/root/stream until it eventually returns. TCP reset cannot manufacture that acknowledgement. Queued notifications never claimed by managed code are removed on close and do not leak a source.

Response registration and host closure use the same admission gate. Headers arriving after closure are dropped before a live Body can be published behind the drain. Unknown worker panic payloads go through A05 containment without invoking arbitrary payload destructors; such a worker failure permanently quarantines the host, reports native panic, and does not claim normal retirement. The isolated native regression records the deliberately retained unknown-size payload and host.

## Source, costs and verification

Exact crate versions, features, upstream VCS commits, licenses and cached-primary-source correspondences are in `native/engines/async-proof/upstream.json`; `Cargo.lock` fixes transitive dependencies. The real native components are Tokio, Hyper, hyper-util and Bytes. Core gains no public runtime, HTTP type or mandatory executor dependency.

Every get/read has a native owner, one Task/root, one retirement observer bounded through its own completion, fixed scalar setup/cancel/retire calls and a typed result take. Handler notifications copy 12 bytes into the bounded queue. Handler output is copied once into native owned Bytes; each received native frame is copied into its accounted chunk backing. Application copies are optional and explicit. There is one native OS/runtime worker per host and no global startup. `GetSnapshot` performs ten scalar native reads and is a non-atomic diagnostic view.

The receipt records native/managed/package/executable hashes, exact source and lock identity, native direct-oracle responses, real JIT/AOT consumers, failure-before-fix regressions, and static incremental code/dependency/asset costs against the accepted A17 receipt. These are integration costs, not comparable performance benchmarks or a claim of lower RSS/latency than managed networking. Final pristine-machine/public-release acceptance remains A16/B24; this component's isolated local package run uses Rust invocation traps and documents its actual environment.
