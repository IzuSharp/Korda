using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NativeWeave.Interop;
using NativeWeave.Interop.Async;
using NativeWeave.Interop.Callbacks;
using NativeWeave.Interop.Extensibility;
using NativeWeave.Interop.Lifetime;
using G = NativeWeave.Engines.AsyncProof.Generated;
using Abi = NativeWeave.Interop.Abi;

namespace NativeWeave.Engines.AsyncProof;

/// <summary>A bounded verification HTTP service with native Hyper networking and statically compiled C# handlers.</summary>
/// <remarks>This proof package binds loopback HTTP/1 GET routes to one foreign Service implementation.
/// It is not the final NativeWeave.Server API. A host owns one Tokio runtime/OS worker; importing the
/// assembly alone starts none. Operation, live-stream, result-item and retained-byte limits are separate.
/// The current prebuilt asset is Linux x64. All public methods are safe for concurrent calls except
/// concurrent reads of the same response, which are rejected. Dispose requests closure; DisposeAsync
/// joins native worker, callbacks, managed handlers and operation retirement. Owned chunks remain valid.</remarks>
public sealed class HttpProofHost : IDisposable, IAsyncDisposable
{
    private const string EngineId = "nativeweave.engines.asyncproof";
    private static readonly Lazy<EngineDescriptor> Package = new(CreateDescriptor);
    private readonly object _gate = new();
    private readonly G.Owner_NwHttpHost _owner;
    private readonly CallbackCoordinator _coordinator;
    private readonly CallbackRegistration _callbacks;
    private readonly Func<uint, CancellationToken, Task<byte[]>> _handler;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _maxOperations;
    private int _operationReservations;
    private readonly EngineInfo _engine;
    private readonly uint _delay;
    private TaskCompletionSource? _close;
    private bool _closing;
    // Friend-test seam runs in the actual queued notification, outside the business handler catch.
    internal Action? NotificationProbe { get; set; }
    internal Task? RetirementGate { get; set; }
    internal int OperationReservations { get { lock (_gate) return _operationReservations; } }

    /// <summary>Loads the precompiled engine, binds an ephemeral loopback port and starts its native worker.</summary>
    /// <param name="handler">A statically compiled managed handler for a numeric route. Its bounded result becomes HTTP 200; an ordinary handler exception becomes HTTP 500 with an empty body.</param>
    /// <param name="maxStreams">Maximum live Body/producer/claimed-handler groups, from one through eight.</param>
    /// <param name="maxOperations">Maximum operations including managed retirement bookkeeping, and each independent held response/chunk item quota, from one through 64.</param>
    /// <param name="retainedBytes">Maximum conservatively charged native backing bytes, from chunkBytes through one MiB.</param>
    /// <param name="chunkBytes">Maximum response payload and received frame, from one through 4096 bytes. Each nonempty backing reserves this full amount until its last Bytes owner drops.</param>
    /// <param name="frameDelayMilliseconds">Native producer delay before each of at most two frames, from zero through 1000 milliseconds.</param>
    /// <exception cref="ArgumentNullException">The handler is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A finite profile limit is invalid.</exception>
    /// <exception cref="EngineLoadException">The prebuilt asset fails negotiated preflight.</exception>
    /// <exception cref="NativeEngineException">Native bind/start fails.</exception>
    /// <remarks>Creates a bounded coordinator, a rooted static callback, one native host and one native worker.
    /// Handler execution occurs outside the native callback; ordinary handler/route changes do not compile Rust.
    /// Native frame buffering is capacity one. The limits are payload/item accounting, not a whole-process RSS bound.</remarks>
    public unsafe HttpProofHost(Func<uint, CancellationToken, Task<byte[]>> handler, int maxStreams = 2,
        int maxOperations = 8, int retainedBytes = 16384, int chunkBytes = 4096, int frameDelayMilliseconds = 5)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (maxStreams is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(maxStreams));
        if (maxOperations is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maxOperations));
        if (chunkBytes is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(chunkBytes));
        if (retainedBytes < chunkBytes || retainedBytes > 1048576) throw new ArgumentOutOfRangeException(nameof(retainedBytes));
        if (frameDelayMilliseconds is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(frameDelayMilliseconds));
        _handler = handler; _delay = (uint)frameDelayMilliseconds; ChunkBytes = chunkBytes; _maxOperations = maxOperations;
        _engine = Descriptor.Inspect(); _engine.Require(new EngineCapability("http.loopback.stream.v1"));
        _coordinator = new CallbackCoordinator(maxOperations + 1);
        G.NwHttpLimitsV1 limits = new()
        {
            struct_size = 24,
            streams = (uint)maxStreams,
            operations = (uint)maxOperations,
            retained_bytes = (uint)retainedBytes,
            chunk_bytes = (uint)chunkBytes
        };
        G.Owner_NwHttpHost? owned = null;
        try
        {
            ThrowStatus(G.OwnedCalls.nw_asyncproof_host_create_v1(&limits, out owned), "host_create");
            _owner = owned!;
            uint port = 0;
            ThrowStatus(G.OwnedCalls.nw_asyncproof_host_port_v1(_owner, &port), "host_port");
            Port = checked((int)port);
            _callbacks = new CallbackRegistration(_owner.Resource, new HostCalls(), _coordinator,
                maxStreams, checked(maxStreams * 12), 12, HandlerAsync);
            _callbacks.Start();
        }
        catch
        {
            if (_callbacks is not null) _callbacks.Dispose(); else owned?.Dispose();
            _coordinator.Dispose(); _stop.Dispose(); throw;
        }
    }

    /// <summary>Gets the explicit immutable package descriptor without loading the native library.</summary>
    /// <remarks>First access reads the embedded asset digest; later access reuses metadata. Inspect performs A02 preflight and modules remain resident.</remarks>
    public static EngineDescriptor Descriptor => Package.Value;
    /// <summary>Gets the ephemeral loopback TCP port without a native call.</summary>
    public int Port { get; }
    /// <summary>Gets the maximum handler response and received frame size.</summary>
    public int ChunkBytes { get; }

    /// <summary>Issues a real native Hyper GET request to this host and asynchronously acquires its response Body.</summary>
    /// <param name="route">Numeric route delivered as data to the statically compiled managed handler.</param>
    /// <param name="cancellationToken">Requests cooperative native cancellation. Success may win; cancellation does not acknowledge retirement.</param>
    /// <returns>A multiply awaitable Task whose response must be disposed, including non-200 HTTP status responses.</returns>
    /// <exception cref="ObjectDisposedException">Host ordinary admission is closed.</exception>
    /// <exception cref="OutOfMemoryException">A finite operation/result quota is exhausted before admission.</exception>
    /// <exception cref="OperationCanceledException">Pre-admission or committed native cancellation occurs.</exception>
    /// <exception cref="NativeEngineException">Native transport, accepted-operation quota, contract or panic failure occurs.</exception>
    /// <remarks>Uses native create/register/start and one typed take through A07. There is no managed HTTP core.
    /// Input is a copied scalar route. Headers may complete before Body/producer/handler retirement.
    /// Each operation adds a Task/root and one bounded retirement observer; chunks are acquired separately.</remarks>
    public Task<HttpProofResponse> GetAsync(uint route, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool control = false;
        try
        {
            _ = _callbacks.AcquireControl(ref control);
            ThrowIfClosing();
            uint status = G.OwnedCalls.nw_asyncproof_get_create_v1(_owner, route, out G.Owner_NwHttpOperation? owner);
            ThrowStatus(status, "get_create");
            return Start(owner!, false, TakeResponse, cancellationToken);
        }
        finally { _callbacks.ReleaseControl(ref control); }
    }

    internal Task<NativeBuffer?> ReadAsync(NativeResourceOwner response, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        bool hostLease = false, responseLease = false;
        nint raw = 0;
        try
        {
            _ = _callbacks.AcquireControl(ref hostLease); ThrowIfClosing();
            nint pointer = response.Acquire(ref responseLease);
            unsafe { ThrowStatus(G.NativeMethods.nw_asyncproof_read_create_v1(pointer, &raw), "read_create"); }
            G.Owner_NwHttpOperation owner = G.Owner_NwHttpOperation.Adopt(ref raw);
            return Start(owner, true, TakeChunk, token);
        }
        finally
        {
            if (raw != 0) G.NativeMethods.nw_asyncproof_operation_release_v1(raw);
            response.Release(ref responseLease); _callbacks.ReleaseControl(ref hostLease);
        }
    }

    private Task<T> Start<T>(G.Owner_NwHttpOperation owner, bool read, Func<nint, T> mapper, CancellationToken token)
    {
        AsyncOperation<T>? operation = null;
        bool reserved = false;
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closing, this);
                if (_operationReservations == _maxOperations) throw new OutOfMemoryException("The finite managed operation/cleanup quota is exhausted.");
                _operationReservations++; reserved = true;
            }
            var calls = new OperationCalls(owner, read);
            operation = new AsyncOperation<T>(owner.Resource, calls, _coordinator,
                pointer => read ? G.Operation_Read.MapResult(calls, () => mapper(pointer)) : G.Operation_Get.MapResult(calls, () => mapper(pointer)), token);
            calls.Context = operation;
            if (ExecutionContext.IsFlowSuppressed()) _ = ObserveRetirement(operation);
            else { using (ExecutionContext.SuppressFlow()) _ = ObserveRetirement(operation); }
            return operation.Start();
        }
        catch
        {
            if (operation is null) { owner.Dispose(); if (reserved) ReleaseReservation(); }
            else operation.Dispose(); // its already-created observer retains the reservation through actual retirement
            throw;
        }
    }

    private async Task ObserveRetirement(AsyncOperation operation)
    {
        await operation.Retirement.ConfigureAwait(false);
        if (RetirementGate is { } gate) await gate.ConfigureAwait(false);
        ReleaseReservation();
    }

    private void ReleaseReservation()
    {
        lock (_gate)
        {
            _operationReservations--;
            if (_closing && _operationReservations == 0) _operationsDrained.TrySetResult();
        }
    }

    private unsafe HttpProofResponse TakeResponse(nint operation)
    {
        var handle = new ResponseHandle();
        NativeResourceOwner? owner = null;
        try
        {
            nint raw = 0; uint code = 0;
            ThrowStatus(G.NativeMethods.nw_asyncproof_take_response_v1(operation, &raw, &code), "take_response");
            if (raw == 0) throw new NativeEngineException("Successful HTTP response take returned no owner.");
            handle.Adopt(raw); owner = new NativeResourceOwner(handle);
            return new HttpProofResponse(this, owner, checked((int)code));
        }
        catch { if (owner is null) handle.Dispose(); else owner.Dispose(); throw; }
    }

    private unsafe NativeBuffer? TakeChunk(nint operation)
    {
        var handle = new ChunkHandle();
        try
        {
            nint raw = 0; byte* data = null; ulong length = 0; uint end = 0;
            ThrowStatus(G.NativeMethods.nw_asyncproof_take_chunk_v1(operation, &raw, &data, &length, &end), "take_chunk");
            if (raw == 0) throw new NativeEngineException("Successful HTTP chunk take returned no owner.");
            handle.Adopt(raw);
            if (end == 1) { handle.Dispose(); return null; }
            if (end != 0 || length > (ulong)ChunkBytes) throw new NativeEngineException("HTTP chunk exceeds the negotiated profile.");
            return NativeBufferFactory.Adopt(_engine, handle, (nint)data, checked((int)length));
        }
        catch { handle.Dispose(); throw; }
    }

    private async ValueTask HandlerAsync(uint kind, ReadOnlyMemory<byte> payload)
    {
        if (kind != 1 || payload.Length != 12) throw new NativeEngineException("Malformed fixed HTTP handler notification.");
        NotificationProbe?.Invoke();
        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(payload.Span);
        uint route = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span[8..]);
        bool lease = false;
        nint pointer = _owner.Resource.Acquire(ref lease);
        bool claimed = false;
        try
        {
            uint claim = G.NativeMethods.nw_asyncproof_handler_claim_v1(pointer, id);
            if (claim is 2 or 4) return; // closed/reset before managed delivery claimed this request
            ThrowStatus(claim, "handler_claim"); claimed = true;
            byte[] response;
            uint status = 200;
            try
            {
                response = await _handler(route, _stop.Token).ConfigureAwait(false);
                if (response is null || response.Length > ChunkBytes) throw new ArgumentOutOfRangeException(nameof(response));
            }
            catch (Exception) { response = []; status = 500; }
            uint reply = Reply(pointer, id, status, response);
            if (reply is not (0 or 2 or 4 or 5)) ThrowStatus(reply, "handler_reply");
        }
        finally
        {
            // This physical lease is a private active-handler cleanup capability.
            // A06 counts the handler until this finally ends, even after close.
            if (claimed) G.NativeMethods.nw_asyncproof_handler_ack_v1(pointer, id);
            _owner.Resource.Release(ref lease);
        }
    }

    private unsafe uint Reply(nint host, ulong id, uint status, byte[] bytes)
    {
        fixed (byte* pointer = bytes)
            return G.NativeMethods.nw_asyncproof_handler_reply_v1(host, id, status, pointer, (ulong)bytes.Length, _delay);
    }
    private void ThrowIfClosing() { lock (_gate) ObjectDisposedException.ThrowIf(_closing, this); }

    /// <summary>Returns bounded module-wide native counters and this host's managed handler queue state.</summary>
    /// <returns>A non-atomic diagnostic snapshot; simultaneous operations may advance between scalar reads.</returns>
    /// <remarks>Performs ten fixed scalar FFI reads. Native values cover all proof hosts in the process.
    /// Retained bytes and chunks may remain positive after stream/host retirement because callers own them.</remarks>
    public HttpProofSnapshot GetSnapshot()
    {
        CallbackSnapshot callback = _callbacks.Snapshot;
        bool closing; lock (_gate) closing = _closing;
        return new(Counter(1), Counter(2), Counter(3), Counter(4), Counter(5), Counter(6) + Counter(7), Counter(8), Counter(9), Counter(10),
            callback.ActiveHandlers, callback.Pending, closing);
    }
    internal static unsafe ulong Counter(uint kind)
    {
        ulong value = 0; ThrowStatus(G.NativeMethods.nw_asyncproof_counters_v1(kind, &value), "counters"); return value;
    }

    /// <summary>Closes ordinary admission and requests native/managed cleanup without waiting.</summary>
    /// <remarks>Safe inside a handler. The background owner remains until native and managed retirement;
    /// this method never destroys a live worker, callback or retained result.</remarks>
    public void Dispose() => _ = BeginClose();

    /// <summary>Joins native OS-worker exit, callback/handler drain and all accepted operation retirement.</summary>
    /// <param name="cancellationToken">Cancels only this caller's wait; it does not free the background cleanup owner.</param>
    /// <returns>The shared actual-close completion, optionally wrapped in a cancellable wait.</returns>
    /// <exception cref="OperationCanceledException">The caller stopped waiting before closure completed.</exception>
    /// <exception cref="InvalidOperationException">A handler attempts to await its own host retirement.</exception>
    /// <exception cref="AggregateException">A managed cancellation registration fails; actual cleanup completes before this failure is reported.</exception>
    /// <remarks>Early response disposal and host close request cancellation. Held NativeBuffer chunks remain valid
    /// and keep their independent byte/item quota until disposal or final release. Close never reports success from a timeout.</remarks>
    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        Task close = BeginClose();
        _ = _callbacks.DisposeAsync(); // the shared A06 scope check rejects a self-wait
        return cancellationToken.CanBeCanceled ? close.WaitAsync(cancellationToken) : close;
    }

    /// <summary>Joins actual closure for await using.</summary>
    /// <returns>A ValueTask over the shared retirement promise; each call may be independently awaited.</returns>
    /// <exception cref="InvalidOperationException">A handler attempts to await its own retirement.</exception>
    public ValueTask DisposeAsync() => new(CloseAsync());

    private Task BeginClose()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_close is not null) return _close.Task;
            _closing = true; _close = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_operationReservations == 0) _operationsDrained.TrySetResult();
        }
        _callbacks.Dispose();
        _ = FinishClose(completion);
        return completion.Task;
    }
    private async Task FinishClose(TaskCompletionSource completion)
    {
        try
        {
            Exception? cancellationFailure = null;
            // Foreign cancellation registrations must not run inline on the
            // Dispose/CloseAsync caller. One engine-owned async cancellation task
            // remains part of the real close lifetime, including its failures.
            try { await _stop.CancelAsync().ConfigureAwait(false); } catch (Exception error) { cancellationFailure = error; }
            await _callbacks.Retirement.ConfigureAwait(false);
            await _operationsDrained.Task.ConfigureAwait(false);
            _coordinator.Dispose(); _stop.Dispose();
            await _callbacks.Completion.ConfigureAwait(false);
            if (cancellationFailure is not null) throw cancellationFailure;
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    internal static void ThrowStatus(uint status, string operation)
    {
        if (status == 0) return;
        if (status == 2) throw new OperationCanceledException("The native proof host is closing or the operation was canceled.");
        if (status == 5) throw new OutOfMemoryException("A finite native proof quota or allocation is exhausted.");
        throw new NativeEngineException(EngineId, operation, status, 0x48545450, 1, 0, "The bounded native HTTP operation failed.");
    }
    private static EngineDescriptor CreateDescriptor()
    {
        using Stream input = typeof(HttpProofHost).Assembly.GetManifestResourceStream("NativeWeave.Engines.AsyncProof.native-sha256")
            ?? throw new InvalidOperationException("The proof package is missing its native identity.");
        using var reader = new StreamReader(input);
        string hash = reader.ReadToEnd().Trim().ToLowerInvariant();
        return EnginePackage.CreateDescriptor(new EnginePackageManifest(EngineId, "NativeWeave.Engines.AsyncProof", "0.1.0-proof.1", new AbiVersion(1, 0),
            "nw_asyncproof_get_abi_v1", EngineThreadingModel.EngineLocalAsync,
            [new NativeAssetDescriptor("linux-x64", "runtimes/linux-x64/native/libnativeweave_engine_asyncproof.so", hash, "x86-64")],
            [new EngineCapabilityDefinition(new EngineCapability("http.loopback.stream.v1"), 0)]), typeof(HttpProofHost).Assembly);
    }

    private sealed unsafe class HostCalls : CallbackNativeCalls
    {
        internal override uint Start(nint owner, in Abi.NwCallbacksV1 callbacks)
        {
            fixed (Abi.NwCallbacksV1* pointer = &callbacks)
                return G.NativeMethods.nw_asyncproof_host_start_v1(owner, (G.NwCallbacksV1*)pointer);
        }
        internal override uint RequestStop(nint owner) => G.NativeMethods.nw_asyncproof_host_stop_v1(owner);
        internal override uint TryRetire(nint owner) => G.NativeMethods.nw_asyncproof_host_retire_v1(owner);
    }
    private sealed class ResponseHandle : SafeHandle
    {
        internal ResponseHandle() : base(0, true) { }
        internal void Adopt(nint value) => SetHandle(value);
        public override bool IsInvalid => handle == 0;
        protected override bool ReleaseHandle() { G.NativeMethods.nw_asyncproof_response_release_v1(handle); return true; }
    }
    private sealed class ChunkHandle : SafeHandle
    {
        internal ChunkHandle() : base(0, true) { }
        internal void Adopt(nint value) => SetHandle(value);
        public override bool IsInvalid => handle == 0;
        protected override bool ReleaseHandle() { G.NativeMethods.nw_asyncproof_chunk_release_v1(handle); return true; }
    }
}

/// <summary>Independently owned native HTTP response with one outstanding read at a time.</summary>
/// <remarks>Dispose cancels an outstanding read and releases the response Body owner. A previously
/// returned NativeBuffer remains valid. EOF does not release this fixed response item quota; dispose
/// the response even after EOF. Host close clears live native Bodies but preserves independent chunks.</remarks>
public sealed class HttpProofResponse : IDisposable
{
    private readonly HttpProofHost _host;
    private readonly NativeResourceOwner _owner;
    private int _reading;
    internal HttpProofResponse(HttpProofHost host, NativeResourceOwner owner, int status) { _host = host; _owner = owner; StatusCode = status; }
    /// <summary>Gets the actual native HTTP response status without a native call.</summary>
    public int StatusCode { get; }
    /// <summary>Reads the next bounded native Body frame into an independently owned chunk.</summary>
    /// <param name="cancellationToken">Requests cancellation of this native read; cancellation closes the remaining Body.</param>
    /// <returns>An owned chunk to dispose, or null after EOF.</returns>
    /// <exception cref="InvalidOperationException">Another read of this response is already pending.</exception>
    /// <exception cref="ObjectDisposedException">The response or host admission has closed.</exception>
    /// <exception cref="OperationCanceledException">The native read confirms cancellation or host/response closure.</exception>
    /// <exception cref="OutOfMemoryException">The finite operation quota is exhausted before native admission.</exception>
    /// <exception cref="NativeEngineException">Transport, accepted chunk-item/byte quota, oversized frame or native contract failure occurs.</exception>
    /// <remarks>Uses one A07 operation and one typed take. The received frame is copied into an exact
    /// accounted native backing owner; no managed payload copy occurs until requested through NativeBuffer.
    /// Failed/canceled reads close this Body. A finite caller wait never proves native retirement.</remarks>
    public async Task<NativeBuffer?> ReadChunkAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0) throw new InvalidOperationException("A response permits one active read.");
        try { return await _host.ReadAsync(_owner, cancellationToken).ConfigureAwait(false); }
        finally { Volatile.Write(ref _reading, 0); }
    }
    /// <summary>Closes response admission and requests cancellation while existing native read sources retain their own lifetime.</summary>
    /// <remarks>Nonblocking and idempotent; independently transferred chunks keep their own backing ownership.</remarks>
    public void Dispose() => _owner.Dispose();
}

/// <summary>Bounded proof diagnostics; native counters are module-wide and scalar reads are not atomic together.</summary>
/// <remarks>This copied value owns no native resource. Reading its properties performs no FFI; obtaining it from a host performs ten scalar native reads.</remarks>
/// <param name="Operations">Live native operation owners, including completed but unretired operations.</param>
/// <param name="Responses">Held native response owners.</param>
/// <param name="Chunks">Held independent native chunk owners.</param>
/// <param name="RetainedBytes">Conservatively charged backing Bytes capacity.</param>
/// <param name="LiveStreams">Body/producer/claimed-handler stream permits still held.</param>
/// <param name="Bodies">Live native server and client Bodies.</param>
/// <param name="RuntimeTasks">Tracked native tasks not yet destroyed.</param>
/// <param name="RuntimeWorkers">Native runtime workers still active; successful host retirement additionally joins the OS thread.</param>
/// <param name="PendingNativeHandlers">Native notification entries waiting for claim or final managed acknowledgement.</param>
/// <param name="ManagedHandlers">Active managed handlers for this host.</param>
/// <param name="QueuedNotifications">Copied managed notifications waiting for this host's handler admission.</param>
/// <param name="Closing">Whether this managed host has closed ordinary admission.</param>
public readonly record struct HttpProofSnapshot(ulong Operations, ulong Responses, ulong Chunks, ulong RetainedBytes,
    ulong LiveStreams, ulong Bodies, ulong RuntimeTasks, ulong RuntimeWorkers, ulong PendingNativeHandlers,
    int ManagedHandlers, int QueuedNotifications, bool Closing);
