namespace NativeWeave.Server;

/// <summary>Typed engine seam, implemented by the negotiated B17 native transport.</summary>
/// <remarks>Operations use Core create/start/take/retire. Credit acquisition never waits.
/// A taken BodyChunk retains its own native request credit anchor independently of this seam.</remarks>
internal abstract class ServerBodyTransport
{
    internal abstract int MaxChunkBytes { get; }
    internal abstract void ThrowIfUnavailable();
    internal abstract Task<BodyChunk?> ReadChunkAsync(int maximumBytes, CancellationToken cancellationToken);
    internal abstract Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
    internal abstract Task FlushAsync(CancellationToken cancellationToken);
    internal abstract Task FinishAsync(CancellationToken cancellationToken);
    internal abstract IDisposable AcquireManagedCredit(int capacity);
    internal abstract void CloseRead();
    internal abstract void CloseWrite();
}

/// <summary>One request's body-mode selection and concurrent-read admission.</summary>
internal sealed class ServerBodyReadGate
{
    private int _mode;
    private int _pending;
    private int _closed;
    internal void Select(int mode)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        if (mode is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(mode));
        int previous = Interlocked.CompareExchange(ref _mode, mode, 0);
        if (previous != 0 && previous != mode) throw new InvalidOperationException("A request must use exactly one body consumption mode.");
    }
    internal void Enter(int mode)
    {
        Select(mode);
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0)
            throw new InvalidOperationException("Only one body read may be pending.");
    }
    internal void Exit() => Volatile.Write(ref _pending, 0);
    internal void Close() => Interlocked.Exchange(ref _closed, 1);
}
