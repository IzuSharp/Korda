using System.Buffers;
using NativeWeave.Interop;
using NativeWeave.Interop.Abi;
using NativeWeave.Interop.Async;
using NativeWeave.Server.Native;

namespace NativeWeave.Server.Grpc;

internal readonly record struct GrpcReadResult<T>(bool HasValue, T Value);

/// <summary>Complete-message mapping only; Tonic owns every protocol frame and transport.</summary>
internal sealed class NativeGrpcTransport(NativeRequest request, int encodedLimit, int decodedLimit, long streamLimit)
{
    private readonly object _gate = new();
    private Task _readTask = Task.CompletedTask, _writeTask = Task.CompletedTask;
    private int _reading, _writing;
    private long _readBytes, _writtenBytes;
    private bool _ended, _eof, _inputStopped;
    private readonly CancellationTokenSource _stopRead = new();
    private Task? _stopTask;
    internal bool ReachedEnd => _eof;
    internal NativeRequest Request => request;
    internal Task<GrpcReadResult<T>> ReadAsync<T>(GrpcMarshaller<T> marshaller, CancellationToken token)
    {
        request.Alive(); token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_ended || _inputStopped) throw new InvalidOperationException("The gRPC input is ending.");
            if (_reading != 0) throw new InvalidOperationException("Only one gRPC read may be pending.");
            if (_eof) return Task.FromResult(new GrpcReadResult<T>(false, default!));
            _reading = 1;
            Task<GrpcReadResult<T>> task = ReadCoreAsync(marshaller, token); _readTask = task; return task;
        }
    }
    private async Task<GrpcReadResult<T>> ReadCoreAsync<T>(GrpcMarshaller<T> marshaller, CancellationToken token)
    {
        await Task.Yield();
        AsyncOperation<Message?>? operation = null; Message? message = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stopRead.Token);
        try
        {
            operation = request.Engine.Begin(CreateRead(), TakeMessage, linked.Token, request);
            message = await operation.Task.ConfigureAwait(false);
            if (message is null) { _eof = true; return new(false, default!); }
            if (message.Length > decodedLimit) throw new GrpcException(GrpcStatusCode.ResourceExhausted, "The decoded message limit was exceeded.");
            if (message.Length > streamLimit - _readBytes) throw new GrpcException(GrpcStatusCode.ResourceExhausted, "The inbound stream limit was exceeded.");
            _readBytes = checked(_readBytes + message.Length);
            return new(true, message.Decode(marshaller));
        }
        catch (Exception error) { throw Translate(error); }
        finally
        {
            message?.Dispose();
            try { if (operation is not null) await operation.Retirement.ConfigureAwait(false); }
            finally { lock (_gate) _reading = 0; }
        }
    }
    private unsafe nint CreateRead() => request.Engine.Create(request.Owner,
        (nint owner, nint* output, NwErrorInfoV1* error) => request.Engine.Native.nw_server_grpc_read_create(owner, output, error));
    private unsafe Message? TakeMessage(nint pointer)
    {
        var native = request.Engine.Native; var handle = new ServerHandle(0, native.nw_server_buffer_release); nint buffer = 0, metadata = 0;
        try
        {
            uint kind = 0; var error = new NwErrorInfoV1 { StructSize = 24 };
            ServerError.Check(native.nw_server_operation_take_chunk(pointer, &kind, &buffer, &metadata, &error), in error);
            if (kind == 1)
            {
                if (buffer != 0 || metadata != 0) throw new InvalidOperationException("gRPC EOF unexpectedly transferred an owner.");
                handle.Dispose(); return null;
            }
            if (kind != 0 || buffer == 0 || metadata != 0) throw new InvalidOperationException("The gRPC message transfer has an invalid shape.");
            handle.Adopt(buffer); buffer = 0; ulong length = 0;
            ServerError.Check(native.nw_server_buffer_length(handle.DangerousGetHandle(), &length, &error), in error);
            if (length > int.MaxValue) throw new InvalidOperationException("The gRPC message exceeds the managed addressable range.");
            return new(handle, (int)length, native);
        }
        catch { handle.Dispose(); throw; }
        finally { if (buffer != 0) native.nw_server_buffer_release(buffer); if (metadata != 0) native.nw_server_metadata_release(metadata); }
    }
    private sealed class Message(ServerHandle owner, int length, ServerNativeCalls native) : IDisposable
    {
        internal int Length => length;
        internal unsafe T Decode<T>(GrpcMarshaller<T> marshaller)
        {
            bool lease = false; nint credit = 0;
            try
            {
                owner.DangerousAddRef(ref lease); var error = new NwErrorInfoV1 { StructSize = 24 };
                ServerError.Check(native.nw_server_buffer_managed_credit_acquire(owner.DangerousGetHandle(), checked((ulong)Math.Max(length, 1)), &credit, &error), in error);
                byte[] bytes = new byte[length];
                fixed (byte* output = bytes)
                {
                    ulong written = 0;
                    ServerError.Check(native.nw_server_buffer_copy(owner.DangerousGetHandle(), output, (ulong)bytes.Length, &written, &error), in error);
                    if (written != (ulong)length) throw new InvalidOperationException("The native gRPC copy length differs.");
                }
                try { return marshaller.Decode(bytes); }
                catch (GrpcException) { throw; }
                catch (Exception) { throw new GrpcException(GrpcStatusCode.InvalidArgument, "The request message could not be decoded."); }
            }
            finally { if (credit != 0) native.nw_server_managed_credit_release(credit); if (lease) owner.DangerousRelease(); }
        }
        public void Dispose() => owner.Dispose();
    }
    internal Task WriteAsync<T>(T value, GrpcMarshaller<T> marshaller, CancellationToken token)
    {
        request.Alive(); token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_ended) throw new InvalidOperationException("The gRPC call is ending.");
            if (_writing != 0) throw new InvalidOperationException("Only one gRPC write may be pending.");
            _writing = 1; Task task = WriteCoreAsync(value, marshaller, token); _writeTask = task; return task;
        }
    }
    private async Task WriteCoreAsync<T>(T value, GrpcMarshaller<T> marshaller, CancellationToken token)
    {
        await Task.Yield();
        try
        {
            // Only metadata queue admission waits. No managed message allocation
            // precedes the native reservation and nonwaiting overlap grant.
            using ServerHandle reservation = await request.ReserveProtocolWriteAsync(encodedLimit, 1, token).ConfigureAwait(false);
            nint pointer = EncodeAndCreate(reservation, value, marshaller);
            var operation = request.Engine.Begin(pointer, request.Engine.TakeVoid, token, request);
            try { await operation.Task.ConfigureAwait(false); }
            finally { await operation.Retirement.ConfigureAwait(false); }
        }
        catch (Exception error) { throw Translate(error); }
        finally { lock (_gate) _writing = 0; }
    }
    private unsafe nint EncodeAndCreate<T>(ServerHandle reservation, T value, GrpcMarshaller<T> marshaller)
    {
        using var credit = request.AcquireProtocolManagedCredit(encodedLimit);
        using var writer = new MessageWriter(encodedLimit, request);
        marshaller.Encode(value, writer);
        if (writer.Length > streamLimit - _writtenBytes) throw new GrpcException(GrpcStatusCode.ResourceExhausted, "The outbound stream limit was exceeded.");
        bool requestLease = false, tokenLease = false; nint operation = 0;
        try
        {
            reservation.DangerousAddRef(ref tokenLease); var error = new NwErrorInfoV1 { StructSize = 24 };
            fixed (byte* data = writer.Written)
            {
                ServerError.Check(request.Engine.Native.nw_server_grpc_write_create(request.Owner.Acquire(ref requestLease), reservation.DangerousGetHandle(),
                    new NwBytesViewV1 { Data = data, Length = (ulong)writer.Length }, &operation, &error), in error);
            }
            reservation.Transfer(); _writtenBytes = checked(_writtenBytes + writer.Length); return operation;
        }
        finally { if (tokenLease) reservation.DangerousRelease(); request.Owner.Release(ref requestLease); }
    }
    internal Task HeadersAsync(GrpcMetadata headers, CancellationToken token)
    {
        request.Alive(); token.ThrowIfCancellationRequested();
        nint pointer = CreateHeaders(headers);
        return CompleteAsync(request.Engine.Begin(pointer, request.Engine.TakeVoid, token, request));
    }
    private unsafe nint CreateHeaders(GrpcMetadata headers)
    {
        var admitted = headers.AdmitSnapshot(request.AcquireProtocolManagedCredit);
        using var credit = admitted.Credit;
        using var input = new NativeInputs(); var snapshot = input.Bytes(admitted.Bytes);
        return request.Engine.Create(request.Owner, (nint owner, nint* output, NwErrorInfoV1* error) => request.Engine.Native.nw_server_grpc_headers_create(owner, snapshot, output, error));
    }
    internal async Task JoinDirectionsAsync()
    {
        Task read, write;
        lock (_gate) { _ended = true; read = _readTask; write = _writeTask; }
        try { await read.ConfigureAwait(false); }
        finally { await write.ConfigureAwait(false); }
    }
    internal bool HasPendingDirection { get { lock (_gate) return _reading != 0 || _writing != 0; } }
    internal Task StopInputAsync()
    { lock (_gate) { if (_eof) return Task.CompletedTask; _inputStopped = true; return _stopTask ??= StopInputCoreAsync(); } }
    private async Task StopInputCoreAsync()
    {
        await Task.Yield();
        await _stopRead.CancelAsync().ConfigureAwait(false);
        Task pending; lock (_gate) pending = _readTask;
        try { await pending.ConfigureAwait(false); } catch (OperationCanceledException) { }
        // Core explicitly admits cancellation of a Created operation. Record the
        // native read-stop intent before start, then use an uncanceled managed
        // token so Core cannot skip native execution. A ready message cannot race
        // ahead of that intent, and no whole-request/output cancellation occurs.
        var operation = request.Engine.Begin(CreateStoppedRead(), TakeMessage, CancellationToken.None, request);
        try { using var abandoned = await operation.Task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally { await operation.Retirement.ConfigureAwait(false); }
        lock (_gate) _eof = true;
    }
    private unsafe nint CreateStoppedRead()
    {
        nint pointer = CreateRead();
        try
        {
            uint status = request.Engine.Native.nw_server_operation_request_cancel(pointer);
            if (status != 0) throw new InvalidOperationException("Native read-stop intent was rejected.");
            return pointer;
        }
        catch { request.Engine.Native.nw_server_operation_release(pointer); throw; }
    }
    internal void Release() => _stopRead.Dispose();
    internal Task StatusAsync(GrpcStatusCode code, string detail, GrpcMetadata trailers)
    {
        request.Alive();
        return CompleteAsync(request.Engine.Begin(CreateStatus(code, detail, trailers), request.Engine.TakeVoid, CancellationToken.None, request));
    }
    private unsafe nint CreateStatus(GrpcStatusCode code, string detail, GrpcMetadata trailers)
    {
        int detailBytes = NativeCodec.Utf8.GetByteCount(detail);
        var admitted = trailers.AdmitSnapshot(request.AcquireProtocolManagedCredit, true);
        using var metadataCredit = admitted.Credit;
        using var detailCredit = detailBytes == 0 ? null : request.AcquireProtocolManagedCredit(detailBytes);
        using var input = new NativeInputs(); var message = input.Text(detail); var metadata = input.Bytes(admitted.Bytes);
        return request.Engine.Create(request.Owner, (nint owner, nint* output, NwErrorInfoV1* error) => request.Engine.Native.nw_server_grpc_status_create(owner, (uint)code, message, metadata, output, error));
    }
    private static async Task CompleteAsync<T>(AsyncOperation<T> operation)
    {
        try { await operation.Task.ConfigureAwait(false); }
        catch (Exception error) { throw Translate(error); }
        finally { await operation.Retirement.ConfigureAwait(false); }
    }
    internal static Exception Translate(Exception error) => error switch
    {
        GrpcException => error,
        NativeEngineException { ErrorDomain: 0x47525043, NativeCode: >= 1 and <= 16 } native => new GrpcException((GrpcStatusCode)native.NativeCode!.Value, "The native gRPC operation failed."),
        ServerException { Code: "resource_limit" or "body_limit_exceeded" } => new GrpcException(GrpcStatusCode.ResourceExhausted, "The gRPC payload budget was exhausted."),
        _ => error
    };
    private sealed class MessageWriter(int maximum, NativeRequest request) : IBufferWriter<byte>, IDisposable
    {
        private const int InitialCapacity = 16 * 1024;
        private NativeRequest? _request = request;
        private byte[]? _bytes, _growthSource;
        private IDisposable? _overlapCredit;
        private bool _disposed;
        internal int Length { get; private set; }
        internal ReadOnlySpan<byte> Written
        {
            get { ObjectDisposedException.ThrowIf(_disposed, this); return _bytes is { } bytes ? bytes.AsSpan(0, Length) : ReadOnlySpan<byte>.Empty; }
        }
        public void Advance(int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (count < 0 || count > maximum - Length || count > (_bytes?.Length ?? 0) - Length) throw Limit();
            Length += count;
        }
        public Memory<byte> GetMemory(int sizeHint = 0) => Ensure(sizeHint).AsMemory(Length);
        public Span<byte> GetSpan(int sizeHint = 0) => Ensure(sizeHint).AsSpan(Length);
        private byte[] Ensure(int hint)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int required = Math.Max(1, hint);
            if (hint < 0 || required > maximum - Length) throw Limit();
            if (_bytes is null) _bytes = AllocateInitial(required);
            if (required > _bytes.Length - Length)
            {
                // Only the admitted small mode can reach here. At most one growth
                // is possible; old + new capacities already have maximum + 16 KiB
                // credit. No retry, second encoder call or growth admission occurs.
                byte[] grown = new byte[maximum];
                _bytes.AsSpan(0, Length).CopyTo(grown);
                _growthSource = _bytes;
                _bytes = grown;
            }
            return _bytes;
        }
        private byte[] AllocateInitial(int required)
        {
            IDisposable? overlap = null;
            try
            {
                int capacity = maximum;
                if (maximum > InitialCapacity && required <= InitialCapacity)
                {
                    // Full maximum credit was admitted before invoking the encoder.
                    // Select small storage only after granting its future overlap;
                    // a tight budget falls back before any array or bytes exist.
                    try { overlap = _request!.AcquireProtocolManagedCredit(InitialCapacity); capacity = InitialCapacity; }
                    catch (ServerException error) when (error.Code == "resource_limit") { }
                }
                byte[] bytes = new byte[capacity];
                _overlapCredit = overlap; overlap = null; _request = null;
                return bytes;
            }
            finally { overlap?.Dispose(); }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Borrowed old memory can remain on the synchronous encoder's stack.
            // Keep both backings charged through the original native-copy scope.
            GC.KeepAlive(_bytes); GC.KeepAlive(_growthSource);
            _bytes = null; _growthSource = null; _request = null;
            IDisposable? overlap = _overlapCredit; _overlapCredit = null;
            overlap?.Dispose();
        }
        private static GrpcException Limit() => new(GrpcStatusCode.ResourceExhausted, "The encoded response message limit was exceeded.");
    }
}
