# Perf-01: isolated per-message gRPC buffer experiment

**Source-only, uncompiled and unexecuted. This candidate is not retained.** Its base is selected product `2f40348c3db80a4fbee7a99e721b374a597b16df`, and its isolated branch is `task/Perf-01-grpc-buffer-linux`. The full encoded-limit array allocation is a source-confirmed cost; the root's final004 profile motivated this experiment. No profile, latency improvement, correctness pass or accepted duration is asserted here.

## One bounded implementation change

`src/Protocols.Grpc/NativeTransport.cs` keeps the existing native write reservation and the complete `encodedLimit` managed grant before invoking the application encoder exactly once. The private writer allocates its first array only when the encoder requests a span or memory. Let `E` be the encoded limit and `I` be 16 KiB.

| First request / later behavior | Framework arrays held | Managed grant held by this writer |
| --- | --- | --- |
| No buffer requested, empty encoding | None | E |
| E <= I, or first size hint > I | E | E |
| E > I, first hint <= I, optional I grant succeeds | I | E + I |
| Small mode later needs more space | Old I plus new E | E + I |
| Optional I grant returns only `resource_limit` | E, original eager path | E |

The optional overlap grant is acquired before the first small array exists. Only an exact `ServerException` with code `resource_limit` selects the original full-size fallback; cancellation, closing, poisoning and other errors are not swallowed. No further budget acquisition occurs during growth. It copies only committed bytes once into a zeroed E-sized array; the old I array remains referenced and charged until the original synchronous native-copy scope ends. Allocation failure unwinds a still-temporary optional grant. Disposal invalidates the writer, clears its array/request references and releases its unique optional grant once. The original outer E grant retains its existing lifetime.

Every requested span/memory is nonempty and at least the requested size, within the unchanged E cap. `Advance` validates the actual current writable extent as well as the encoded cap; `Advance(0)` is valid before a first buffer request. No old buffer is promised usable after Advance or a subsequent buffer request. No array pool, weak reference cache, cross-message reuse or cross-call reuse is introduced.

The native ABI, native sources/assets, public marshaller shape, dependency versions, message/stream caps, scheduling and completion/retirement order are unchanged. All selected HPACK metadata-owner, admission-gate, request-release and graceful-close corrections remain.

## Costs and retention decision

A small-mode output adds one synchronous native credit acquisition and one release. **The existing `NwServerManagedCredit::drop` also calls `host.reap()`** (`native/server/host/src/exports.rs:52–58`), so this is an extra synchronous request-registry scan, not merely two cheap ABI crossings. No Rust reaper optimization is included in this experiment.

Each active small-mode output reserves an additional 16 KiB even if it never grows. This increases concurrent admission pressure within the same unchanged global and per-request limits. A large message whose encoder supplies no size hint can allocate E + 16 KiB and copy its committed prefix, exceeding the old E-only allocation/copy cost. A denied optional grant also creates and handles the existing resource-limit exception before the eager fallback; first-chance diagnostics may observe that internal exception even though the RPC can succeed. Source inspection shows that this ordinary quota failure neither changes quota counters nor poisons EngineBoundary; the new real-gauge fixture must still verify the actual healthy host/callback state.

Compare this candidate against current 2f and the original EA5 baseline using the same common driver, existing native hashes, payloads, message counts, TLS/connections, arrival schedule, resources, noise policy and error/tail criteria. Report whole-profile allocation and CPU separately from call latency and p99. If the extra credit/reap, concurrency grant, or growth cost erases the benefit or produces a veto, reject/revert this candidate. A later native-credit-drop investigation would be a separate evidence-led change. No retention conclusion follows from reduced array sizes alone.

## Existing and added correctness coverage

All existing assertions in `WriterContracts.cs` and every byte of `WriterAcrossCalls.cs` are preserved. The sole change to the existing contract fixture is a call to the new `PerMessageWriterBuffers` fixture. In particular, full-E credit still precedes encoder entry, and a first `GetMemory(E)` still exposes the original exact zeroed E array without an optional overlap grant.

The added `WriterBuffers.cs` fixture uses a real TLS/H2 gRPC service and independent client. It covers small default-hint storage, deterministic one-growth encoding at the non-power-of-two exact cap, actual managed gauges before/after both allocations, tight-budget optional-grant fallback, one encoder invocation, exceptions before and after growth, actual native-create rejection after growth, repeated disposal, collectable old/new backings, empty protobuf output, recovery messages, protobuf ordering/status and final zero-owner drain. It also checks fallback does not add a native callback failure or fault the host. These cases have been written, **not executed**.

The existing B18 JIT/Native AOT, package/API and supplied-native functional entry points remain the later acceptance route. New aggregate assertion totals must come from actual logs. Do not reuse the old binary's counts or performance results for this candidate. Build and run only through the established finite owner and exclusive timing lock after the root permits it; no new controller or load recipe is introduced here.

## Actual upstream source inspection

The consumers already lock Google.Protobuf 3.36.2 and Grpc.Tools 2.85.0. The product `NativeWeave.Server.Grpc` package itself has no Google.Protobuf dependency, and `GrpcMarshaller<T>` currently carries only the explicit encoder/decoder pair. The repository's C# binding generator emits ABI adapters, not protobuf message-size adapters. Adding automatic `IMessage.CalculateSize` dispatch would therefore require a dependency or a different codec SPI; retrying a stateful encoder with a counting writer would change single-invocation semantics.

The actually read protobuf v36.2 `MessageExtensions.WriteTo(IMessage, IBufferWriter<byte>)` uses `WriteContext` without a size prepass; `WriteBufferHelper.RefreshBuffer` calls `GetSpan()` without a hint. Exact sizing exists on `IMessage` and the independent `ToByteArray` path. The .NET 11 `IBufferWriter` source specifies minimum capacity, permits changing buffer identity/capacity and invalidates previous writable buffers after Advance. These observations support a small first buffer without changing application DTOs or invoking the encoder twice.

[upstream-sources.json](upstream-sources.json) records the exact versioned URLs, source locations and SHA-256 of the UTF-8 source content actually read. No third-party implementation was copied, no new dependency was added and no third-party code is newly distributed; existing LICENSE/NOTICE files are unchanged.

## Source preparation boundary

[sources.json](sources.json) binds the exact candidate files, relevant source/lock inputs and unchanged native tree. Static diff, source identity and JSON checks are distinct from C# compilation and runtime correctness. No generated files, restored graph, fresh binary manifest or execution receipt exists as a result of this source preparation. The selected product and frozen formal source/index/binding remain untouched.
