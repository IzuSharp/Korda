namespace NativeWeave.Server;

/// <summary>Asynchronous-only borrowed request-body facade. Reading Body does not select its mode.</summary>
internal sealed class ServerReadStream : Stream
{
    private readonly ServerBodyTransport _transport;
    private readonly ServerBodyReadGate _gate;
    private int _disposed;
    private int _endObserved;

    internal ServerReadStream(ServerBodyTransport transport, ServerBodyReadGate? gate = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _gate = gate ?? new ServerBodyReadGate();
    }
    public override bool CanRead => Volatile.Read(ref _disposed) == 0;
    internal bool EndObserved => Volatile.Read(ref _endObserved) != 0;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException("Synchronous body I/O is not supported.");
    public override Task FlushAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Synchronous body I/O is not supported.");
    public override int Read(Span<byte> buffer) => throw new NotSupportedException("Synchronous body I/O is not supported.");
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        _gate.Enter(1);
        if (cancellationToken.IsCancellationRequested) { _gate.Exit(); return ValueTask.FromCanceled<int>(cancellationToken); }
        if (buffer.IsEmpty) { _gate.Exit(); return ValueTask.FromResult(0); }
        return new ValueTask<int>(ReadCoreAsync(buffer, cancellationToken));
    }

    private async Task<int> ReadCoreAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        try
        {
            using BodyChunk? chunk = await _transport.ReadChunkAsync(Math.Min(destination.Length, _transport.MaxChunkBytes), cancellationToken).ConfigureAwait(false);
            if (chunk is null) { Volatile.Write(ref _endObserved, 1); return 0; }
            int length = chunk.Length;
            if (length > destination.Length || length > _transport.MaxChunkBytes)
                throw new InvalidOperationException("The native body returned a chunk beyond the admitted limit.");
            chunk.CopyTo(destination.Span);
            return length;
        }
        finally { _gate.Exit(); }
    }

    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        if (!destination.CanWrite) throw new ArgumentException("Destination must be writable.", nameof(destination));
        ThrowIfUnavailable();
        _gate.Enter(1);
        if (cancellationToken.IsCancellationRequested) { _gate.Exit(); return Task.FromCanceled(cancellationToken); }
        return CopyCoreAsync(destination, Math.Min(bufferSize, _transport.MaxChunkBytes), cancellationToken);
    }

    private async Task CopyCoreAsync(Stream destination, int capacity, CancellationToken cancellationToken)
    {
        try
        {
            // The staging allocation is independent of caller-provided destinations and is charged.
            using IDisposable credit = _transport.AcquireManagedCredit(capacity);
            byte[] staging = new byte[capacity];
            while (true)
            {
                using BodyChunk? chunk = await _transport.ReadChunkAsync(capacity, cancellationToken).ConfigureAwait(false);
                if (chunk is null) { Volatile.Write(ref _endObserved, 1); return; }
                int length = chunk.Length;
                if (length > capacity) throw new InvalidOperationException("Native chunk exceeds the admitted copy bound.");
                chunk.CopyTo(staging);
                await destination.WriteAsync(staging.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _gate.Exit(); }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _transport.ThrowIfUnavailable();
    }
    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) { _gate.Close(); _transport.CloseRead(); }
        base.Dispose(disposing);
    }
}

/// <summary>Asynchronous-only borrowed response facade; the application supplies its response-start pipeline.</summary>
internal sealed class ServerWriteStream : Stream
{
    private readonly ServerBodyTransport _transport;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> _write;
    private readonly Func<CancellationToken, Task> _flush;
    private int _disposed;
    private int _pending;

    internal ServerWriteStream(ServerBodyTransport transport,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task>? write = null,
        Func<CancellationToken, Task>? flush = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _write = write ?? transport.WriteAsync;
        _flush = flush ?? transport.FlushAsync;
    }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => Volatile.Read(ref _disposed) == 0;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Synchronous body I/O is not supported.");
    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException("Synchronous body I/O is not supported.");
    public override void Flush() => throw new NotSupportedException("Synchronous body I/O is not supported.");

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Enter();
        if (cancellationToken.IsCancellationRequested) { Exit(); return ValueTask.FromCanceled(cancellationToken); }
        return new ValueTask(WriteCoreAsync(buffer, cancellationToken));
    }
    private async Task WriteCoreAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        try { await _write(buffer, cancellationToken).ConfigureAwait(false); }
        finally { Exit(); }
    }
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        Enter();
        if (cancellationToken.IsCancellationRequested) { Exit(); return Task.FromCanceled(cancellationToken); }
        return FlushCoreAsync(cancellationToken);
    }
    private async Task FlushCoreAsync(CancellationToken cancellationToken)
    {
        try { await _flush(cancellationToken).ConfigureAwait(false); }
        finally { Exit(); }
    }
    private void Enter()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _transport.ThrowIfUnavailable();
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0)
            throw new InvalidOperationException("Only one response write or flush may be pending.");
    }
    private void Exit() => Volatile.Write(ref _pending, 0);
    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _transport.CloseWrite();
        base.Dispose(disposing);
    }
}
