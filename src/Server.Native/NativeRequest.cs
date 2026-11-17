using System.Diagnostics;
using System.Net;
using NativeWeave.Interop.Abi;
using NativeWeave.Interop.Async;
using NativeWeave.Interop.Lifetime;
using NativeWeave.Server.Native;

namespace NativeWeave.Server;

internal sealed partial class NativeRequest : HostRequest
{
    internal readonly NativeHost Engine;
    internal NativeResourceOwner Owner = null!;
    private readonly NativeBody _body;
    private readonly object _gate = new();
    private readonly HashSet<AsyncOperation> _operations = [];
    private readonly CancellationTokenSource _aborted = new();
    private Task _cancellation = Task.CompletedTask;
    private Task? _complete;
    private bool _cancelClosed;
    private int _ended;
    private long _observed;
    private TimeSpan _remaining;
    private byte[] _trailers = [0, 0, 0, 0];
    internal Exception? InitializationFailure;
    internal byte[] GrpcMetadataBytes { get; private set; } = [];
    private IDisposable? _grpcMetadataCredit;
    private NativeRequest(NativeHost host) { Engine = host; _body = new(this); }
    internal static unsafe NativeRequest Take(NativeHost host, ulong ticket, ulong handler, uint kind)
    {
        var request = new NativeRequest(host); nint pointer = 0, metadata = 0; bool lease = false;
        try
        {
            var error = new NwErrorInfoV1 { StructSize = 24 };
            ServerError.Check(host.Native.nw_server_request_take(host.Owner.Acquire(ref lease), ticket, &pointer, &metadata, &error), in error);
            request.Owner = NativeHost.Adopt(pointer, host.Native.nw_server_request_release); pointer = 0;
            var info = new NwServerRequestInfoV1 { struct_size = (uint)sizeof(NwServerRequestInfoV1), version = 1 };
            ServerError.Check(host.Native.nw_server_metadata_read(metadata, &info, &error), in error);
            if (info.request_id != ticket || info.handler_id != handler || info.kind != kind) throw new InvalidOperationException("Native request identity changed across typed take.");
            request._routeId = checked((long)info.handler_id); request._kind = checked((int)info.kind);
            if (kind == 1)
            {
                NwBytesViewV1 decoded = default;
                ServerError.Check(host.Native.nw_server_metadata_grpc_headers(metadata, &decoded, &error), in error);
                if (decoded.Length < 4 || decoded.Length > (ulong)Math.Min(32_768, host.Configuration.Options.MaxHeaderBytes) || decoded.Data == null) throw new InvalidOperationException("Invalid native gRPC metadata bounds.");
                request._grpcMetadataCredit = request._body.AcquireManagedCredit(checked((int)decoded.Length));
                request.GrpcMetadataBytes = new ReadOnlySpan<byte>(decoded.Data, checked((int)decoded.Length)).ToArray();
            }
            request._method = NativeCodec.Text(info.method, 256); request._scheme = NativeCodec.Text(info.scheme, 16); request.HostName = NativeCodec.Text(info.authority, 16_384);
            request._path = NativeCodec.Text(info.path, 16_384); request._queryString = NativeCodec.Text(info.raw_query, 16_385); request._protocol = info.protocol switch { 1 => "HTTP/1.1", 2 => "HTTP/2", _ => throw new InvalidOperationException("Invalid native protocol.") };
            request._headers = NativeCodec.Decode(info.headers, host.Configuration.Options.MaxHeaderBytes); request._routeValues = NativeCodec.Decode(info.route_values, 1_048_576, false); request._query = NativeCodec.Decode(info.query_values, 1_048_576, false);
            request._remoteEndPoint = info.peer_family switch { 0 => null, 4 => new IPEndPoint(new IPAddress(new ReadOnlySpan<byte>(info.peer_address, 4)), checked((int)info.peer_port)), 6 => new IPEndPoint(new IPAddress(new ReadOnlySpan<byte>(info.peer_address, 16)), checked((int)info.peer_port)), _ => throw new InvalidOperationException("Invalid native peer family.") };
            request._traceIdentifier = ticket.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
            request.PopulateDiagnostics(info.trace_context, info.trace_state);
            request._remaining = TimeSpan.FromTicks(checked((long)(info.remaining_timeout_ns / 100))); request._observed = Stopwatch.GetTimestamp();
            if (info.cancellation_status != 0) request.NotifyCancellation(info.cancellation_status);
            return request;
        }
        catch (Exception error)
        {
            if (request.Owner is null) { request._aborted.Dispose(); throw; }
            request.InitializationFailure = error; return request;
        }
        finally
        {
            if (metadata != 0) host.Native.nw_server_metadata_release(metadata);
            if (pointer != 0) { host.Native.nw_server_request_abort(pointer); host.Native.nw_server_dispatch_complete(pointer, 13, null); if (host.Native.nw_server_request_try_retire(pointer) == 0) host.Native.nw_server_request_release(pointer); }
            host.Owner.Release(ref lease);
        }
    }
    private long _routeId = 0;
    internal override long RouteId => _routeId;
    private int _kind = 0;
    internal override int Kind => _kind;
    internal override ServerBodyTransport Body => _body;
    internal Task<ServerHandle> ReserveProtocolWriteAsync(int capacity, uint kind, CancellationToken token) => _body.ReserveAndJoinAsync(capacity, kind, token);
    internal IDisposable AcquireProtocolManagedCredit(int capacity) => _body.AcquireManagedCredit(capacity);
    private string _method = "";
    internal override string Method => _method;
    private string _scheme = "";
    internal override string Scheme => _scheme;
    private string HostName = "";
    internal override string Host => HostName;
    private string _path = "";
    internal override string Path => _path;
    private string _queryString = "";
    internal override string QueryString => _queryString;
    private string _protocol = "";
    internal override string Protocol => _protocol;
    private IPEndPoint? _remoteEndPoint = default;
    internal override IPEndPoint? RemoteEndPoint => _remoteEndPoint;
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _headers = new Dictionary<string, IReadOnlyList<string>>();
    internal override IReadOnlyDictionary<string, IReadOnlyList<string>> Headers => _headers;
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _routeValues = new Dictionary<string, IReadOnlyList<string>>();
    internal override IReadOnlyDictionary<string, IReadOnlyList<string>> RouteValues => _routeValues;
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _query = new Dictionary<string, IReadOnlyList<string>>();
    internal override IReadOnlyDictionary<string, IReadOnlyList<string>> Query => _query;
    private string _traceIdentifier = "";
    internal override string TraceIdentifier => _traceIdentifier;
    internal override CancellationToken RequestAborted => _aborted.Token;
    internal override TimeSpan TimeRemaining => _remaining > Stopwatch.GetElapsedTime(_observed) ? _remaining - Stopwatch.GetElapsedTime(_observed) : TimeSpan.Zero;
    internal override IReadOnlyDictionary<string, IReadOnlyList<string>> GetTrailers() { Alive(); return _body.Trailers; }
    internal void Alive() { ObjectDisposedException.ThrowIf(Volatile.Read(ref _ended) != 0, this); if (InitializationFailure is { } error) throw error; }
    internal void NotifyCancellation(uint status)
    {
        if (status is not (2 or 12)) return;
        RecordCancellationDiagnostics(status);
        lock (_gate) if (!_cancelClosed && !_aborted.IsCancellationRequested) _cancellation = _aborted.CancelAsync();
    }
    internal Task CancellationDrain() { lock (_gate) return _cancellation; }
    internal void Track(AsyncOperation operation) { lock (_gate) { ObjectDisposedException.ThrowIf(_ended != 0, this); _operations.Add(operation); } }
    internal void Untrack(AsyncOperation operation) { lock (_gate) _operations.Remove(operation); }
    internal override Task CommitHeadersAsync(int status, IReadOnlyDictionary<string, IReadOnlyList<string>> headers, CancellationToken cancellationToken)
    {
        Alive(); cancellationToken.ThrowIfCancellationRequested(); ServerValidation.Status(status);
        return Engine.Begin(CreateHeaders(status, headers), Engine.TakeVoid, cancellationToken, this).Task;
    }
    private unsafe nint CreateHeaders(int status, IReadOnlyDictionary<string, IReadOnlyList<string>> headers)
    {
        using var input = new NativeInputs(); var bytes = input.Bytes(NativeCodec.Headers(headers, Engine.Configuration.Options.MaxHeaderBytes));
        return Engine.Create(Owner, (nint owner, nint* output, NwErrorInfoV1* error) => Engine.Native.nw_server_response_begin_create(owner, checked((uint)status), bytes, output, error));
    }
    internal override Task CommitTrailersAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> trailers, CancellationToken cancellationToken)
    { Alive(); cancellationToken.ThrowIfCancellationRequested(); _trailers = NativeCodec.Headers(trailers, Engine.Configuration.Options.MaxHeaderBytes); return Task.CompletedTask; }
    internal override Task WriteServerEventAsync(ServerEvent value, CancellationToken cancellationToken)
    {
        Alive(); ArgumentNullException.ThrowIfNull(value); ArgumentNullException.ThrowIfNull(value.Data); cancellationToken.ThrowIfCancellationRequested();
        if ((Engine.Capabilities & ServerCapabilities.ServerSentEvents) == 0) throw new NotSupportedException("The selected native asset has no SSE formatter capability.");
        if (value.Event?.IndexOfAny(['\r', '\n']) >= 0 || value.Id?.IndexOfAny(['\r', '\n', '\0']) >= 0 || value.Retry is { } retry && (retry < TimeSpan.Zero || retry > TimeSpan.FromDays(1) || retry.Ticks % TimeSpan.TicksPerMillisecond != 0))
            throw new ArgumentException("Invalid SSE metadata.", nameof(value));
        int data = NativeCodec.Utf8.GetByteCount(value.Data), name = NativeCodec.Utf8.GetByteCount(value.Event ?? ""), id = NativeCodec.Utf8.GetByteCount(value.Id ?? "");
        int policy = checked(6 * data + name + id + 128);
        int expanded = checked(data + 6 * (value.Data.Count(c => c is '\r' or '\n') + 1) + name + id + 64);
        if (policy > _body.MaxChunkBytes || expanded > _body.MaxChunkBytes) throw new ServerException("resource_limit", "The SSE event exceeds its bounded native chunk profile.");
        return WriteEventCoreAsync(value, checked(3 * Math.Max(8, Math.Max(policy, expanded))), checked(data + name + id), cancellationToken);
    }
    private async Task WriteEventCoreAsync(ServerEvent value, int nativePeak, int managedBytes, CancellationToken token)
    {
        using ServerHandle reservation = await _body.ReserveAsync(nativePeak, 2, token).ConfigureAwait(false);
        nint operation;
        // The only new managed payload is the three call-only UTF-8 arrays. The
        // caller's strings are already application-owned. No encoding credit or
        // allocation exists while the native queue reservation is pending.
        using (var credit = managedBytes == 0 ? null : _body.AcquireManagedCredit(managedBytes)) operation = CreateEvent(reservation, value);
        await Engine.Begin(operation, Engine.TakeVoid, token, this).Task.ConfigureAwait(false);
    }
    private unsafe nint CreateEvent(ServerHandle reservation, ServerEvent value)
    {
        using var input = new NativeInputs();
        var record = new NwServerSseEventV1
        {
            struct_size = (uint)sizeof(NwServerSseEventV1),
            version = 1,
            present_flags = (value.Event is null ? 0u : 1u) | (value.Id is null ? 0u : 2u) | (value.Retry.HasValue ? 4u : 0u),
            data = input.Text(value.Data),
            event_name = input.Text(value.Event),
            id = input.Text(value.Id),
            retry_milliseconds = checked((ulong)(value.Retry?.Ticks / TimeSpan.TicksPerMillisecond ?? 0))
        };
        bool ownerLease = false, tokenLease = false; nint operation = 0;
        try
        {
            reservation.DangerousAddRef(ref tokenLease); var error = new NwErrorInfoV1 { StructSize = 24 };
            ServerError.Check(Engine.Native.nw_server_sse_write_create(Owner.Acquire(ref ownerLease), reservation.DangerousGetHandle(), &record, &operation, &error), in error);
            reservation.Transfer(); return operation;
        }
        finally { if (tokenLease) reservation.DangerousRelease(); Owner.Release(ref ownerLease); }
    }
    internal override unsafe void Abort()
    { bool lease = false; try { if (!Owner.IsClosing) Engine.Native.nw_server_request_abort(Owner.Acquire(ref lease)); } catch (ObjectDisposedException) { } finally { Owner.Release(ref lease); } }
    internal override Task CompleteDispatchAsync()
    { lock (_gate) return _complete ??= CompleteCoreAsync(); }
    private async Task CompleteCoreAsync()
    {
        await Task.Yield(); Volatile.Write(ref _ended, 1);
        GrpcMetadataBytes = [];
        Interlocked.Exchange(ref _grpcMetadataCredit, null)?.Dispose();
        CompleteNativeDispatch();
        while (true)
        {
            AsyncOperation[] operations; lock (_gate) operations = _operations.ToArray();
            if (operations.Length != 0) await Task.WhenAll(operations.Select(operation => operation.Retirement)).ConfigureAwait(false);
            if (TryRetire() == 0) break;
            await Task.Delay(2).ConfigureAwait(false);
        }
        Task cancellation; lock (_gate) { _cancelClosed = true; cancellation = _cancellation; }
        try { await cancellation.ConfigureAwait(false); }
        finally { _aborted.Dispose(); await Owner.DisposeAsync().ConfigureAwait(false); }
    }
    private unsafe void CompleteNativeDispatch()
    {
        bool lease = false;
        try { var error = new NwErrorInfoV1 { StructSize = 24 }; ServerError.Check(Engine.Native.nw_server_dispatch_complete(Owner.Acquire(ref lease), InitializationFailure is null ? 0u : 13u, &error), in error); }
        finally { Owner.Release(ref lease); }
    }
    private unsafe uint TryRetire()
    { bool lease = false; try { return Engine.Native.nw_server_request_try_retire(Owner.Acquire(ref lease)); } finally { Owner.Release(ref lease); } }

    private sealed class NativeBody(NativeRequest request) : ServerBodyTransport
    {
        private bool _readClosed, _writeClosed, _eof;
        internal IReadOnlyDictionary<string, IReadOnlyList<string>> Trailers { get; private set; } = new Dictionary<string, IReadOnlyList<string>>();
        internal override int MaxChunkBytes => request.Engine.Configuration.Options.Budget.MaxChunkBytes;
        internal override void ThrowIfUnavailable() => request.Alive();
        internal override Task<BodyChunk?> ReadChunkAsync(int maximumBytes, CancellationToken cancellationToken)
        {
            ThrowIfUnavailable(); ObjectDisposedException.ThrowIf(_readClosed, this); cancellationToken.ThrowIfCancellationRequested();
            if (maximumBytes is <= 0 || maximumBytes > MaxChunkBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            if (_eof) return Task.FromResult<BodyChunk?>(null);
            return request.Engine.Begin(CreateRead(maximumBytes), TakeChunk, cancellationToken, request).Task;
        }
        private unsafe nint CreateRead(int maximum) => request.Engine.Create(request.Owner,
            (nint owner, nint* output, NwErrorInfoV1* error) => request.Engine.Native.nw_server_body_read_create(owner, (uint)maximum, output, error));
        private unsafe BodyChunk? TakeChunk(nint operation)
        {
            var native = request.Engine.Native; var handle = new ServerHandle(0, native.nw_server_buffer_release); nint buffer = 0, metadata = 0;
            try
            {
                uint kind = 0; var error = new NwErrorInfoV1 { StructSize = 24 };
                ServerError.Check(native.nw_server_operation_take_chunk(operation, &kind, &buffer, &metadata, &error), in error);
                if (kind == 1)
                {
                    if (buffer != 0) throw new InvalidOperationException("End transferred an unexpected data owner.");
                    if (metadata != 0) { NwBytesViewV1 entries = default; ServerError.Check(native.nw_server_metadata_headers(metadata, &entries, &error), in error); Trailers = NativeCodec.Decode(entries, request.Engine.Configuration.Options.MaxHeaderBytes); }
                    _eof = true; handle.Dispose(); return null;
                }
                if (kind != 0 || buffer == 0 || metadata != 0) throw new InvalidOperationException("Invalid native data result shape.");
                handle.Adopt(buffer); buffer = 0; ulong length = 0;
                ServerError.Check(native.nw_server_buffer_length(handle.DangerousGetHandle(), &length, &error), in error);
                return new BodyChunk(new NativeChunkOwner(handle, checked((int)length), native));
            }
            catch { handle.Dispose(); throw; }
            finally { if (buffer != 0) native.nw_server_buffer_release(buffer); if (metadata != 0) native.nw_server_metadata_release(metadata); }
        }
        internal override Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            ThrowIfUnavailable(); ObjectDisposedException.ThrowIf(_writeClosed, this); cancellationToken.ThrowIfCancellationRequested();
            if (data.IsEmpty || data.Length > MaxChunkBytes) throw new ArgumentOutOfRangeException(nameof(data));
            return WriteCoreAsync(data, cancellationToken);
        }
        private async Task WriteCoreAsync(ReadOnlyMemory<byte> data, CancellationToken token)
        {
            using ServerHandle reservation = await ReserveAsync(data.Length, 0, token).ConfigureAwait(false);
            nint operation = CreateWrite(reservation, data);
            await request.Engine.Begin(operation, request.Engine.TakeVoid, token, request).Task.ConfigureAwait(false);
        }
        internal async Task<ServerHandle> ReserveAndJoinAsync(int capacity, uint kind, CancellationToken token)
        {
            var operation = request.Engine.Begin(CreateReserve(capacity, kind), TakeReservation, token, request);
            try { return await operation.Task.ConfigureAwait(false); }
            finally { await operation.Retirement.ConfigureAwait(false); }
        }
        internal Task<ServerHandle> ReserveAsync(int capacity, uint kind, CancellationToken token) => request.Engine.Begin(CreateReserve(capacity, kind), TakeReservation, token, request).Task;
        private unsafe nint CreateReserve(int capacity, uint kind) => request.Engine.Create(request.Owner,
            (nint owner, nint* output, NwErrorInfoV1* error) => request.Engine.Native.nw_server_write_reserve_create(owner, kind, (ulong)capacity, output, error));
        private unsafe ServerHandle TakeReservation(nint operation)
        {
            var handle = new ServerHandle(0, request.Engine.Native.nw_server_write_reservation_release); nint pointer = 0;
            try { var error = new NwErrorInfoV1 { StructSize = 24 }; ServerError.Check(request.Engine.Native.nw_server_operation_take_write_reservation(operation, &pointer, &error), in error); handle.Adopt(pointer); return handle; }
            catch { handle.Dispose(); throw; }
        }
        private unsafe nint CreateWrite(ServerHandle reservation, ReadOnlyMemory<byte> bytes)
        {
            using var memory = bytes.Pin(); bool ownerLease = false, tokenLease = false; nint operation = 0;
            try
            {
                reservation.DangerousAddRef(ref tokenLease); var error = new NwErrorInfoV1 { StructSize = 24 };
                ServerError.Check(request.Engine.Native.nw_server_response_write_create(request.Owner.Acquire(ref ownerLease), reservation.DangerousGetHandle(), new NwBytesViewV1 { Data = (byte*)memory.Pointer, Length = (ulong)bytes.Length }, &operation, &error), in error);
                reservation.Transfer(); return operation;
            }
            finally { if (tokenLease) reservation.DangerousRelease(); request.Owner.Release(ref ownerLease); }
        }
        internal override Task FlushAsync(CancellationToken cancellationToken)
        { ThrowIfUnavailable(); ObjectDisposedException.ThrowIf(_writeClosed, this); cancellationToken.ThrowIfCancellationRequested(); return request.Engine.Begin(CreateFlush(), request.Engine.TakeVoid, cancellationToken, request).Task; }
        private unsafe nint CreateFlush() => request.Engine.Create(request.Owner,
            (nint owner, nint* output, NwErrorInfoV1* error) => request.Engine.Native.nw_server_response_flush_create(owner, output, error));
        internal override Task FinishAsync(CancellationToken cancellationToken)
        { ThrowIfUnavailable(); cancellationToken.ThrowIfCancellationRequested(); return request.Engine.Begin(CreateFinish(), request.Engine.TakeVoid, cancellationToken, request).Task; }
        private unsafe nint CreateFinish()
        {
            using var input = new NativeInputs(); var trailers = input.Bytes(request._trailers);
            return request.Engine.Create(request.Owner, (nint owner, nint* output, NwErrorInfoV1* error) => request.Engine.Native.nw_server_response_finish_create(owner, trailers, output, error));
        }
        internal override unsafe IDisposable AcquireManagedCredit(int capacity)
        {
            ThrowIfUnavailable(); bool lease = false; nint pointer = 0;
            try { var error = new NwErrorInfoV1 { StructSize = 24 }; ServerError.Check(request.Engine.Native.nw_server_managed_credit_acquire(request.Owner.Acquire(ref lease), checked((ulong)capacity), &pointer, &error), in error); return new ServerHandle(pointer, request.Engine.Native.nw_server_managed_credit_release); }
            finally { request.Owner.Release(ref lease); }
        }
        internal override void CloseRead() => _readClosed = true;
        internal override void CloseWrite() => _writeClosed = true;
    }
}

internal sealed class NativeChunkOwner(ServerHandle handle, int length, ServerNativeCalls native) : BodyChunkOwner(handle, length)
{
    protected override unsafe void CopyLeased(nint pointer, Span<byte> destination)
    {
        fixed (byte* output = destination)
        { ulong written = 0; var error = new NwErrorInfoV1 { StructSize = 24 }; ServerError.Check(native.nw_server_buffer_copy(pointer, output, (ulong)destination.Length, &written, &error), in error); if (written != (ulong)destination.Length) throw new InvalidOperationException("Native body copy length mismatch."); }
    }
    protected override unsafe IDisposable AcquireManagedCreditLeased(nint pointer, int capacity)
    { nint credit = 0; var error = new NwErrorInfoV1 { StructSize = 24 }; ServerError.Check(native.nw_server_buffer_managed_credit_acquire(pointer, (ulong)capacity, &credit, &error), in error); return new ServerHandle(credit, native.nw_server_managed_credit_release); }
}
