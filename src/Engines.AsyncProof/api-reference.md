# NativeWeave asynchronous HTTP proof-engine API

Generated from the compiled product assembly and XML by `src/Build.Integration/ApiReference`.

A18 bounded real Hyper/Tokio service, static foreign Handler and owned streaming profile. Linux x64 only; no final Server API or all-platform claim.

## `M:NativeWeave.Engines.AsyncProof.HttpProofHost.#ctor(System.Func{System.UInt32,System.Threading.CancellationToken,System.Threading.Tasks.Task{System.Byte[]}},System.Int32,System.Int32,System.Int32,System.Int32,System.Int32)`

```csharp
public NativeWeave.Engines.AsyncProof.HttpProofHost.HttpProofHost(System.Func<uint, System.Threading.CancellationToken, System.Threading.Tasks.Task<byte[]>> handler, int maxStreams = 2, int maxOperations = 8, int retainedBytes = 16384, int chunkBytes = 4096, int frameDelayMilliseconds = 5)
```

**summary:** Loads the precompiled engine, binds an ephemeral loopback port and starts its native worker.

**param handler:** A statically compiled managed handler for a numeric route. Its bounded result becomes HTTP 200; an ordinary handler exception becomes HTTP 500 with an empty body.

**param maxStreams:** Maximum live Body/producer/claimed-handler groups, from one through eight.

**param maxOperations:** Maximum accepted native operations and each independent held response/chunk item quota, from one through 64.

**param retainedBytes:** Maximum conservatively charged native backing bytes, from chunkBytes through one MiB.

**param chunkBytes:** Maximum response payload and received frame, from one through 4096 bytes. Each nonempty backing reserves this full amount until its last Bytes owner drops.

**param frameDelayMilliseconds:** Native producer delay before each of at most two frames, from zero through 1000 milliseconds.

**exception:** The handler is null.

**exception:** A finite profile limit is invalid.

**exception:** The prebuilt asset fails negotiated preflight.

**exception:** Native bind/start fails.

**remarks:** Creates a bounded coordinator, a rooted static callback, one native host and one native worker. Handler execution occurs outside the native callback; ordinary handler/route changes do not compile Rust. Native frame buffering is capacity one. The limits are payload/item accounting, not a whole-process RSS bound.

**Inherited type policy:** This proof package binds loopback HTTP/1 GET routes to one foreign Service implementation. It is not the final NativeWeave.Server API. A host owns one Tokio runtime/OS worker; importing the assembly alone starts none. Operation, live-stream, result-item and retained-byte limits are separate. The current prebuilt asset is Linux x64. All public methods are safe for concurrent calls except concurrent reads of the same response, which are rejected. Dispose requests closure; DisposeAsync joins native worker, callbacks, managed handlers and operation retirement. Owned chunks remain valid.

## `M:NativeWeave.Engines.AsyncProof.HttpProofHost.CloseAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task NativeWeave.Engines.AsyncProof.HttpProofHost.CloseAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Joins native OS-worker exit, callback/handler drain and all accepted operation retirement.

**param cancellationToken:** Cancels only this caller's wait; it does not free the background cleanup owner.

**returns:** The shared actual-close completion, optionally wrapped in a cancellable wait.

**exception:** The caller stopped waiting before closure completed.

**exception:** A handler attempts to await its own host retirement.

**remarks:** Early response disposal and host close request cancellation. Held NativeBuffer chunks remain valid and keep their independent byte/item quota until disposal or final release. Close never reports success from a timeout.

**Inherited type policy:** This proof package binds loopback HTTP/1 GET routes to one foreign Service implementation. It is not the final NativeWeave.Server API. A host owns one Tokio runtime/OS worker; importing the assembly alone starts none. Operation, live-stream, result-item and retained-byte limits are separate. The current prebuilt asset is Linux x64. All public methods are safe for concurrent calls except concurrent reads of the same response, which are rejected. Dispose requests closure; DisposeAsync joins native worker, callbacks, managed handlers and operation retirement. Owned chunks remain valid.

## `M:NativeWeave.Engines.AsyncProof.HttpProofHost.Dispose`

```csharp
public void NativeWeave.Engines.AsyncProof.HttpProofHost.Dispose()
```

**summary:** Closes ordinary admission and requests native/managed cleanup without waiting.

**remarks:** Safe inside a handler. The background owner remains until native and managed retirement; this method never destroys a live worker, callback or retained result.

**Inherited type policy:** This proof package binds loopback HTTP/1 GET routes to one foreign Service implementation. It is not the final NativeWeave.Server API. A host owns one Tokio runtime/OS worker; importing the assembly alone starts none. Operation, live-stream, result-item and retained-byte limits are separate. The current prebuilt asset is Linux x64. All public methods are safe for concurrent calls except concurrent reads of the same response, which are rejected. Dispose requests closure; DisposeAsync joins native worker, callbacks, managed handlers and operation retirement. Owned chunks remain valid.

## `M:NativeWeave.Engines.AsyncProof.HttpProofHost.DisposeAsync`

```csharp
public System.Threading.Tasks.ValueTask NativeWeave.Engines.AsyncProof.HttpProofHost.DisposeAsync()
```

**summary:** Joins actual closure for await using.

**returns:** A ValueTask over the shared retirement promise; each call may be independently awaited.

**exception:** A handler attempts to await its own retirement.

**Inherited type policy:** This proof package binds loopback HTTP/1 GET routes to one foreign Service implementation. It is not the final NativeWeave.Server API. A host owns one Tokio runtime/OS worker; importing the assembly alone starts none. Operation, live-stream, result-item and retained-byte limits are separate. The current prebuilt asset is Linux x64. All public methods are safe for concurrent calls except concurrent reads of the same response, which are rejected. Dispose requests closure; DisposeAsync joins native worker, callbacks, managed handlers and operation retirement. Owned chunks remain valid.

## `M:NativeWeave.Engines.AsyncProof.HttpProofHost.GetAsync(System.UInt32,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<NativeWeave.Engines.AsyncProof.HttpProofResponse> NativeWeave.Engines.AsyncProof.HttpProofHost.GetAsync(uint route, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Issues a real native Hyper GET request to this host and asynchronously acquires its response Body.

**param route:** Numeric route delivered as data to the statically compiled managed handler.

**param cancellationToken:** Requests cooperative native cancellation. Success may win; cancellation does not acknowledge retirement.

**returns:** A multiply awaitable Task whose response must be disposed, including non-200 HTTP status responses.

**exception:** Host ordinary admission is closed.

**exception:** A finite operation/result quota is exhausted before admission.

**exception:** Pre-admission or committed native cancellation occurs.

**exception:** Native transport, accepted-operation quota, contract or panic failure occurs.

**remarks:** Uses native create/register/start and one typed take through A07. There is no managed HTTP core. Input is a copied scalar route. Headers may complete before Body/producer/handler retirement. Each operation adds a Task/root and one bounded retirement observer; chunks are acquired separately.

**Inherited type policy:** This proof package binds loopback HTTP/1 GET routes to one foreign Service implementation. It is not the final NativeWeave.Server API. A host owns one Tokio runtime/OS worker; importing the assembly alone starts none. Operation, live-stream, result-item and retained-byte limits are separate. The current prebuilt asset is Linux x64. All public methods are safe for concurrent calls except concurrent reads of the same response, which are rejected. Dispose requests closure; DisposeAsync joins native worker, callbacks, managed handlers and operation retirement. Owned chunks remain valid.

## `M:NativeWeave.Engines.AsyncProof.HttpProofHost.GetSnapshot`

```csharp
public NativeWeave.Engines.AsyncProof.HttpProofSnapshot NativeWeave.Engines.AsyncProof.HttpProofHost.GetSnapshot()
```

**summary:** Returns bounded module-wide native counters and this host's managed handler queue state.

**returns:** A non-atomic diagnostic snapshot; simultaneous operations may advance between scalar reads.

**remarks:** Performs ten fixed scalar FFI reads. Native values cover all proof hosts in the process. Retained bytes and chunks may remain positive after stream/host retirement because callers own them.

**Inherited type policy:** This proof package binds loopback HTTP/1 GET routes to one foreign Service implementation. It is not the final NativeWeave.Server API. A host owns one Tokio runtime/OS worker; importing the assembly alone starts none. Operation, live-stream, result-item and retained-byte limits are separate. The current prebuilt asset is Linux x64. All public methods are safe for concurrent calls except concurrent reads of the same response, which are rejected. Dispose requests closure; DisposeAsync joins native worker, callbacks, managed handlers and operation retirement. Owned chunks remain valid.

## `M:NativeWeave.Engines.AsyncProof.HttpProofResponse.Dispose`

```csharp
public void NativeWeave.Engines.AsyncProof.HttpProofResponse.Dispose()
```

**summary:** Closes response admission and requests cancellation while existing native read sources retain their own lifetime.

**remarks:** Nonblocking and idempotent; independently transferred chunks keep their own backing ownership.

**Inherited type policy:** Dispose cancels an outstanding read and releases the response Body owner. A previously returned NativeBuffer remains valid. EOF does not release this fixed response item quota; dispose the response even after EOF. Host close clears live native Bodies but preserves independent chunks.

## `M:NativeWeave.Engines.AsyncProof.HttpProofResponse.ReadChunkAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<NativeWeave.Interop.NativeBuffer?> NativeWeave.Engines.AsyncProof.HttpProofResponse.ReadChunkAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Reads the next bounded native Body frame into an independently owned chunk.

**param cancellationToken:** Requests cancellation of this native read; cancellation closes the remaining Body.

**returns:** An owned chunk to dispose, or null after EOF.

**exception:** Another read of this response is already pending.

**exception:** The response or host admission has closed.

**exception:** The native read confirms cancellation or host/response closure.

**exception:** The finite operation quota is exhausted before native admission.

**exception:** Transport, accepted chunk-item/byte quota, oversized frame or native contract failure occurs.

**remarks:** Uses one A07 operation and one typed take. The received frame is copied into an exact accounted native backing owner; no managed payload copy occurs until requested through NativeBuffer. Failed/canceled reads close this Body. A finite caller wait never proves native retirement.

**Inherited type policy:** Dispose cancels an outstanding read and releases the response Body owner. A previously returned NativeBuffer remains valid. EOF does not release this fixed response item quota; dispose the response even after EOF. Host close clears live native Bodies but preserves independent chunks.

## `M:NativeWeave.Engines.AsyncProof.HttpProofSnapshot.#ctor`

```csharp
public NativeWeave.Engines.AsyncProof.HttpProofSnapshot.HttpProofSnapshot()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.

## `M:NativeWeave.Engines.AsyncProof.HttpProofSnapshot.#ctor(System.UInt64,System.UInt64,System.UInt64,System.UInt64,System.UInt64,System.UInt64,System.UInt64,System.UInt64,System.UInt64,System.Int32,System.Int32,System.Boolean)`

```csharp
public NativeWeave.Engines.AsyncProof.HttpProofSnapshot.HttpProofSnapshot(ulong Operations, ulong Responses, ulong Chunks, ulong RetainedBytes, ulong LiveStreams, ulong Bodies, ulong RuntimeTasks, ulong RuntimeWorkers, ulong PendingNativeHandlers, int ManagedHandlers, int QueuedNotifications, bool Closing)
```

**summary:** Bounded proof diagnostics; native counters are module-wide and scalar reads are not atomic together.

**remarks:** This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.

**param Operations:** Live native operation owners, including completed but unretired operations.

**param Responses:** Held native response owners.

**param Chunks:** Held independent native chunk owners.

**param RetainedBytes:** Conservatively charged backing Bytes capacity.

**param LiveStreams:** Body/producer/claimed-handler stream permits still held.

**param Bodies:** Live native server and client Bodies.

**param RuntimeTasks:** Tracked native tasks not yet destroyed.

**param RuntimeWorkers:** Native runtime workers still active; successful host retirement additionally joins the OS thread.

**param PendingNativeHandlers:** Native notification entries waiting for claim or final managed acknowledgement.

**param ManagedHandlers:** Active managed handlers for this host.

**param QueuedNotifications:** Copied managed notifications waiting for this host's handler admission.

**param Closing:** Whether this managed host has closed ordinary admission.

**Inherited type policy:** This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.

## `M:NativeWeave.Engines.AsyncProof.HttpProofSnapshot.Deconstruct(System.UInt64@,System.UInt64@,System.UInt64@,System.UInt64@,System.UInt64@,System.UInt64@,System.UInt64@,System.UInt64@,System.UInt64@,System.Int32@,System.Int32@,System.Boolean@)`

```csharp
public void NativeWeave.Engines.AsyncProof.HttpProofSnapshot.Deconstruct(out ulong Operations, out ulong Responses, out ulong Chunks, out ulong RetainedBytes, out ulong LiveStreams, out ulong Bodies, out ulong RuntimeTasks, out ulong RuntimeWorkers, out ulong PendingNativeHandlers, out int ManagedHandlers, out int QueuedNotifications, out bool Closing)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.

## `M:NativeWeave.Engines.AsyncProof.HttpProofSnapshot.Equals(NativeWeave.Engines.AsyncProof.HttpProofSnapshot)`

```csharp
public bool NativeWeave.Engines.AsyncProof.HttpProofSnapshot.Equals(NativeWeave.Engines.AsyncProof.HttpProofSnapshot other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.

## `M:NativeWeave.Engines.AsyncProof.HttpProofSnapshot.Equals(System.Object)`

```csharp
public override bool NativeWeave.Engines.AsyncProof.HttpProofSnapshot.Equals(object obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.

## `M:NativeWeave.Engines.AsyncProof.HttpProofSnapshot.GetHashCode`

```csharp
public override int NativeWeave.Engines.AsyncProof.HttpProofSnapshot.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.

## `M:NativeWeave.Engines.AsyncProof.HttpProofSnapshot.ToString`

```csharp
public override string NativeWeave.Engines.AsyncProof.HttpProofSnapshot.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.

## `M:NativeWeave.Engines.AsyncProof.HttpProofSnapshot.op_Equality(NativeWeave.Engines.AsyncProof.HttpProofSnapshot,NativeWeave.Engines.AsyncProof.HttpProofSnapshot)`

```csharp
public static bool NativeWeave.Engines.AsyncProof.HttpProofSnapshot.operator ==(NativeWeave.Engines.AsyncProof.HttpProofSnapshot left, NativeWeave.Engines.AsyncProof.HttpProofSnapshot right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.

## `M:NativeWeave.Engines.AsyncProof.HttpProofSnapshot.op_Inequality(NativeWeave.Engines.AsyncProof.HttpProofSnapshot,NativeWeave.Engines.AsyncProof.HttpProofSnapshot)`

```csharp
public static bool NativeWeave.Engines.AsyncProof.HttpProofSnapshot.operator !=(NativeWeave.Engines.AsyncProof.HttpProofSnapshot left, NativeWeave.Engines.AsyncProof.HttpProofSnapshot right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.

## `P:NativeWeave.Engines.AsyncProof.HttpProofHost.ChunkBytes`

```csharp
public int NativeWeave.Engines.AsyncProof.HttpProofHost.ChunkBytes { get; }
```

**summary:** Gets the maximum handler response and received frame size.

**Inherited type policy:** This proof package binds loopback HTTP/1 GET routes to one foreign Service implementation. It is not the final NativeWeave.Server API. A host owns one Tokio runtime/OS worker; importing the assembly alone starts none. Operation, live-stream, result-item and retained-byte limits are separate. The current prebuilt asset is Linux x64. All public methods are safe for concurrent calls except concurrent reads of the same response, which are rejected. Dispose requests closure; DisposeAsync joins native worker, callbacks, managed handlers and operation retirement. Owned chunks remain valid.

## `P:NativeWeave.Engines.AsyncProof.HttpProofHost.Descriptor`

```csharp
public static NativeWeave.Interop.EngineDescriptor NativeWeave.Engines.AsyncProof.HttpProofHost.Descriptor { get; }
```

**summary:** Gets the explicit immutable package descriptor without loading the native library.

**remarks:** First access reads the embedded asset digest; later access reuses metadata. Inspect performs A02 preflight and modules remain resident.

**Inherited type policy:** This proof package binds loopback HTTP/1 GET routes to one foreign Service implementation. It is not the final NativeWeave.Server API. A host owns one Tokio runtime/OS worker; importing the assembly alone starts none. Operation, live-stream, result-item and retained-byte limits are separate. The current prebuilt asset is Linux x64. All public methods are safe for concurrent calls except concurrent reads of the same response, which are rejected. Dispose requests closure; DisposeAsync joins native worker, callbacks, managed handlers and operation retirement. Owned chunks remain valid.

## `P:NativeWeave.Engines.AsyncProof.HttpProofHost.Port`

```csharp
public int NativeWeave.Engines.AsyncProof.HttpProofHost.Port { get; }
```

**summary:** Gets the ephemeral loopback TCP port without a native call.

**Inherited type policy:** This proof package binds loopback HTTP/1 GET routes to one foreign Service implementation. It is not the final NativeWeave.Server API. A host owns one Tokio runtime/OS worker; importing the assembly alone starts none. Operation, live-stream, result-item and retained-byte limits are separate. The current prebuilt asset is Linux x64. All public methods are safe for concurrent calls except concurrent reads of the same response, which are rejected. Dispose requests closure; DisposeAsync joins native worker, callbacks, managed handlers and operation retirement. Owned chunks remain valid.

## `P:NativeWeave.Engines.AsyncProof.HttpProofResponse.StatusCode`

```csharp
public int NativeWeave.Engines.AsyncProof.HttpProofResponse.StatusCode { get; }
```

**summary:** Gets the actual native HTTP response status without a native call.

**Inherited type policy:** Dispose cancels an outstanding read and releases the response Body owner. A previously returned NativeBuffer remains valid. EOF does not release this fixed response item quota; dispose the response even after EOF. Host close clears live native Bodies but preserves independent chunks.

## `P:NativeWeave.Engines.AsyncProof.HttpProofSnapshot.Bodies`

```csharp
public ulong NativeWeave.Engines.AsyncProof.HttpProofSnapshot.Bodies { get; init; }
```

**summary:** Live native server and client Bodies.

**Inherited type policy:** This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.

## `P:NativeWeave.Engines.AsyncProof.HttpProofSnapshot.Chunks`

```csharp
public ulong NativeWeave.Engines.AsyncProof.HttpProofSnapshot.Chunks { get; init; }
```

**summary:** Held independent native chunk owners.

**Inherited type policy:** This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.

## `P:NativeWeave.Engines.AsyncProof.HttpProofSnapshot.Closing`

```csharp
public bool NativeWeave.Engines.AsyncProof.HttpProofSnapshot.Closing { get; init; }
```

**summary:** Whether this managed host has closed ordinary admission.

**Inherited type policy:** This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.

## `P:NativeWeave.Engines.AsyncProof.HttpProofSnapshot.LiveStreams`

```csharp
public ulong NativeWeave.Engines.AsyncProof.HttpProofSnapshot.LiveStreams { get; init; }
```

**summary:** Body/producer/claimed-handler stream permits still held.

**Inherited type policy:** This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.

## `P:NativeWeave.Engines.AsyncProof.HttpProofSnapshot.ManagedHandlers`

```csharp
public int NativeWeave.Engines.AsyncProof.HttpProofSnapshot.ManagedHandlers { get; init; }
```

**summary:** Active managed handlers for this host.

**Inherited type policy:** This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.

## `P:NativeWeave.Engines.AsyncProof.HttpProofSnapshot.Operations`

```csharp
public ulong NativeWeave.Engines.AsyncProof.HttpProofSnapshot.Operations { get; init; }
```

**summary:** Live native operation owners, including completed but unretired operations.

**Inherited type policy:** This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.

## `P:NativeWeave.Engines.AsyncProof.HttpProofSnapshot.PendingNativeHandlers`

```csharp
public ulong NativeWeave.Engines.AsyncProof.HttpProofSnapshot.PendingNativeHandlers { get; init; }
```

**summary:** Native notification entries waiting for claim or final managed acknowledgement.

**Inherited type policy:** This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.

## `P:NativeWeave.Engines.AsyncProof.HttpProofSnapshot.QueuedNotifications`

```csharp
public int NativeWeave.Engines.AsyncProof.HttpProofSnapshot.QueuedNotifications { get; init; }
```

**summary:** Copied managed notifications waiting for this host's handler admission.

**Inherited type policy:** This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.

## `P:NativeWeave.Engines.AsyncProof.HttpProofSnapshot.Responses`

```csharp
public ulong NativeWeave.Engines.AsyncProof.HttpProofSnapshot.Responses { get; init; }
```

**summary:** Held native response owners.

**Inherited type policy:** This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.

## `P:NativeWeave.Engines.AsyncProof.HttpProofSnapshot.RetainedBytes`

```csharp
public ulong NativeWeave.Engines.AsyncProof.HttpProofSnapshot.RetainedBytes { get; init; }
```

**summary:** Conservatively charged backing Bytes capacity.

**Inherited type policy:** This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.

## `P:NativeWeave.Engines.AsyncProof.HttpProofSnapshot.RuntimeTasks`

```csharp
public ulong NativeWeave.Engines.AsyncProof.HttpProofSnapshot.RuntimeTasks { get; init; }
```

**summary:** Tracked native tasks not yet destroyed.

**Inherited type policy:** This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.

## `P:NativeWeave.Engines.AsyncProof.HttpProofSnapshot.RuntimeWorkers`

```csharp
public ulong NativeWeave.Engines.AsyncProof.HttpProofSnapshot.RuntimeWorkers { get; init; }
```

**summary:** Native runtime workers still active; successful host retirement additionally joins the OS thread.

**Inherited type policy:** This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.

## `T:NativeWeave.Engines.AsyncProof.HttpProofHost`

```csharp
public sealed class NativeWeave.Engines.AsyncProof.HttpProofHost : System.IDisposable, System.IAsyncDisposable
```

**summary:** A bounded verification HTTP service with native Hyper networking and statically compiled C# handlers.

**remarks:** This proof package binds loopback HTTP/1 GET routes to one foreign Service implementation. It is not the final NativeWeave.Server API. A host owns one Tokio runtime/OS worker; importing the assembly alone starts none. Operation, live-stream, result-item and retained-byte limits are separate. The current prebuilt asset is Linux x64. All public methods are safe for concurrent calls except concurrent reads of the same response, which are rejected. Dispose requests closure; DisposeAsync joins native worker, callbacks, managed handlers and operation retirement. Owned chunks remain valid.

## `T:NativeWeave.Engines.AsyncProof.HttpProofResponse`

```csharp
public sealed class NativeWeave.Engines.AsyncProof.HttpProofResponse : System.IDisposable
```

**summary:** Independently owned native HTTP response with one outstanding read at a time.

**remarks:** Dispose cancels an outstanding read and releases the response Body owner. A previously returned NativeBuffer remains valid. EOF does not release this fixed response item quota; dispose the response even after EOF. Host close clears live native Bodies but preserves independent chunks.

## `T:NativeWeave.Engines.AsyncProof.HttpProofSnapshot`

```csharp
public readonly record struct NativeWeave.Engines.AsyncProof.HttpProofSnapshot : System.IEquatable<NativeWeave.Engines.AsyncProof.HttpProofSnapshot>
```

**summary:** Bounded proof diagnostics; native counters are module-wide and scalar reads are not atomic together.

**remarks:** This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.

**param Operations:** Live native operation owners, including completed but unretired operations.

**param Responses:** Held native response owners.

**param Chunks:** Held independent native chunk owners.

**param RetainedBytes:** Conservatively charged backing Bytes capacity.

**param LiveStreams:** Body/producer/claimed-handler stream permits still held.

**param Bodies:** Live native server and client Bodies.

**param RuntimeTasks:** Tracked native tasks not yet destroyed.

**param RuntimeWorkers:** Native runtime workers still active; successful host retirement additionally joins the OS thread.

**param PendingNativeHandlers:** Native notification entries waiting for claim or final managed acknowledgement.

**param ManagedHandlers:** Active managed handlers for this host.

**param QueuedNotifications:** Copied managed notifications waiting for this host's handler admission.

**param Closing:** Whether this managed host has closed ordinary admission.
