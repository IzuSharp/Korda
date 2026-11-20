namespace NativeWeave.Server.Grpc;

/// <summary>Provides typed protocol state borrowed for one complete gRPC call.</summary>
/// <remarks>The existing request supplies identity, services, correlation, native deadline/cancellation and lifecycle cleanup. Raw HTTP input/output and OnStarting remain forbidden. Initial metadata commits once; trailing metadata freezes at terminal admission. All native message operations retire before common scope cleanup.</remarks>
public sealed class GrpcCallContext
{
    private RequestContext _context = null!;
    private NativeGrpcTransport _transport = null!;
    private string _method = "";
    private GrpcMetadata _requestHeaders = null!, _responseTrailers = null!;
    private readonly object _gate = new();
    private Task? _headers;
    private bool _headersAdmitted, _completed;
    private Func<ValueTask>? _disposeInbound;
    internal bool Invoked;
    internal GrpcCallContext() { }
    internal GrpcCallContext(RequestContext context, string method, int encoded, int decoded, long stream, GrpcWriteScratch? sharedWriteScratch)
    {
        _context = context; _method = method;
        var native = context.Native as NativeRequest ?? throw new InvalidOperationException("gRPC requires the selected Rust Host.");
        _transport = new(native, encoded, decoded, stream, sharedWriteScratch);
        _requestHeaders = GrpcMetadata.Borrow(native.GrpcMetadataBytes, Alive, native.AcquireProtocolManagedCredit);
        _responseTrailers = new(Alive, native.AcquireProtocolManagedCredit);
    }
    internal NativeGrpcTransport Transport => _transport;
    internal void RegisterInbound(Func<ValueTask> dispose)
    { lock (_gate) { Alive(); if (_disposeInbound is not null) throw new InvalidOperationException("A call has only one inbound message stream."); _disposeInbound = dispose; } }
    /// <summary>Gets the same request, service scope, identity and permitted lifecycle hooks.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcCallContext" path="/member/remarks" />
    public RequestContext RequestContext { get { Alive(); return _context; } }
    /// <summary>Gets the canonical method path.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcCallContext" path="/member/remarks" />
    public string Method { get { Alive(); return _method; } }
    /// <summary>Gets cancellation from the native whole-call lifetime.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcCallContext" path="/member/remarks" />
    public CancellationToken CancellationToken { get { Alive(); return _context.RequestAborted; } }
    /// <summary>Gets the remaining monotonic native deadline.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcCallContext" path="/member/remarks" />
    public TimeSpan TimeRemaining { get { Alive(); return _context.TimeRemaining; } }
    /// <summary>Gets the read-only native-decoded metadata; ordinary HTTP headers retain wire text.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcCallContext" path="/member/remarks" />
    public GrpcMetadata RequestHeaders { get { Alive(); return _requestHeaders; } }
    /// <summary>Gets trailing metadata mutable until terminal admission.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcCallContext" path="/member/remarks" />
    public GrpcMetadata ResponseTrailers { get { Alive(); return _responseTrailers; } }
    /// <summary>Copies and commits initial response metadata once.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Grpc.GrpcCallContext" path="/member/remarks" />
    /// <param name="headers">Bounded metadata copied synchronously at admission; later caller edits do not change it.</param>
    /// <param name="cancellationToken">Caller cancellation intent; native ownership remains retained through retirement.</param>
    /// <returns>An operation that joins native header completion and retirement.</returns>
    public Task WriteResponseHeadersAsync(GrpcMetadata headers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(headers); cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Alive(); if (_headersAdmitted) throw new InvalidOperationException("gRPC response headers have already been admitted.");
            _headersAdmitted = true;
            try { return _headers = _transport.HeadersAsync(headers, cancellationToken); }
            catch { _headersAdmitted = false; _headers = null; throw; }
        }
    }
    internal void Alive() { _context.Alive(); if (Volatile.Read(ref _completed)) throw new ObjectDisposedException(nameof(GrpcCallContext)); }
    internal async Task CompleteAsync(Exception? failure)
    {
        Task? headers; Func<ValueTask>? dispose;
        lock (_gate) { if (_completed) throw new InvalidOperationException("gRPC completion is single use."); headers = _headers; dispose = _disposeInbound; }
        Exception? finalFailure = failure;
        try
        {
            if (headers is not null)
            { try { await headers.ConfigureAwait(false); } catch (Exception error) { finalFailure ??= error; } }
            if (_transport.HasPendingDirection)
            {
                // A returned handler cannot leave a borrowed direction running.
                // Abort is intent; the explicit join below still owns retirement.
                _context.Native.Abort(); finalFailure ??= new InvalidOperationException("A handler returned with an unjoined gRPC direction.");
            }
            if (dispose is not null)
            { try { await dispose().ConfigureAwait(false); } catch (Exception error) { finalFailure ??= error; } }
            try { await _transport.JoinDirectionsAsync().ConfigureAwait(false); }
            catch (Exception error) { finalFailure ??= error; }
            _responseTrailers.Freeze();
            var result = Classify(finalFailure);
            GrpcMetadata trailers = result.Error?.Trailers ?? _responseTrailers;
            await _transport.StatusAsync(result.Status, result.Detail, trailers).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _completed = true;
            _transport.Release();
            _requestHeaders.Release(); _responseTrailers.Release();
        }
    }
    private (GrpcStatusCode Status, string Detail, GrpcException? Error) Classify(Exception? failure)
    {
        failure = failure is null ? null : NativeGrpcTransport.Translate(failure);
        if (failure is GrpcException grpc) return (grpc.StatusCode, grpc.Message, grpc);
        if (failure is TimeoutException || failure is OperationCanceledException && _context.TimeRemaining == TimeSpan.Zero)
            return (GrpcStatusCode.DeadlineExceeded, "The call deadline expired.", null);
        if (failure is OperationCanceledException || _context.RequestAborted.IsCancellationRequested)
            return (GrpcStatusCode.Cancelled, "The call was canceled.", null);
        if (failure is ServerException { Code: "authorization_denied" } || failure is null && !Invoked)
            return (_context.User.Identity?.IsAuthenticated == true ? GrpcStatusCode.PermissionDenied : GrpcStatusCode.Unauthenticated, "The protocol request was denied.", null);
        return failure is null ? (GrpcStatusCode.Ok, "", null) : (GrpcStatusCode.Internal, "The application failed to complete the call.", null);
    }
}

/// <summary>One pull-based inbound stream; no queue or read-ahead collection exists here.</summary>
internal sealed class GrpcMessageStream<T>(GrpcCallContext context, GrpcMarshaller<T> marshaller) : IAsyncEnumerable<T>, IAsyncEnumerator<T>
{
    private readonly object _gate = new();
    private bool _taken, _disposed, _hasCurrent, _eof;
    private Task<bool>? _moving;
    private Task? _disposeTask;
    private CancellationTokenSource? _linked;
    private CancellationToken _token;
    private T _current = default!;
    internal Action? TestBeforePublish { get; set; }
    public T Current { get { lock (_gate) { if (!_hasCurrent || _disposed) throw new InvalidOperationException("There is no current gRPC message."); return _current; } } }
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            context.Alive(); if (_taken || _disposed) throw new InvalidOperationException("A gRPC inbound enumerable is single use.");
            _taken = true; _token = context.CancellationToken;
            if (cancellationToken.CanBeCanceled && cancellationToken != _token)
            { _linked = CancellationTokenSource.CreateLinkedTokenSource(_token, cancellationToken); _token = _linked.Token; }
            return this;
        }
    }
    public ValueTask<bool> MoveNextAsync()
    {
        lock (_gate)
        {
            context.Alive(); ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_taken) throw new InvalidOperationException("Acquire the inbound enumerator before reading.");
            if (_moving is { IsCompleted: false }) throw new InvalidOperationException("Only one MoveNext may be pending.");
            if (_eof) return ValueTask.FromResult(false);
            _hasCurrent = false; _moving = MoveCoreAsync(); return new(_moving);
        }
    }
    private async Task<bool> MoveCoreAsync()
    {
        await Task.Yield();
        var next = await context.Transport.ReadAsync(marshaller, _token).ConfigureAwait(false);
        TestBeforePublish?.Invoke();
        lock (_gate) { if (_disposed) return false; _eof = !next.HasValue; _current = next.Value; _hasCurrent = next.HasValue; return next.HasValue; }
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true; _hasCurrent = false; _current = default!;
            return new(_disposeTask = DisposeCoreAsync(_moving, !_eof));
        }
    }
    private async Task DisposeCoreAsync(Task<bool>? moving, bool stop)
    {
        await Task.Yield();
        Exception? failure = null;
        try
        {
            try { if (stop) await context.Transport.StopInputAsync().ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            try { if (moving is not null) await moving.ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
        }
        finally { lock (_gate) { _hasCurrent = false; _current = default!; } _linked?.Dispose(); }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
