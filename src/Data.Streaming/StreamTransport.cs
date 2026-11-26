using Korda.Data;

namespace Korda.Database.Internal;

/// <summary>One reusable native batch owner. Only synchronous span leases expose its storage.</summary>
/// <remarks>Releasing a generation closes new reads immediately. Existing read scopes retain the pointer;
/// another generation cannot attach before physical release finishes. The library lifetime stays bound.</remarks>
internal sealed unsafe class ScopedStreamBatch : IDisposable
{
    private readonly object _gate = new();
    private readonly delegate* unmanaged[Cdecl]<nint, void> _release;
    private readonly object _lifetime;
    private nint _owner, _address;
    private int _length, _readers;
    private long _generation;
    private bool _active, _releasing, _closed, _end;

    internal ScopedStreamBatch(delegate* unmanaged[Cdecl]<nint, void> release, object lifetime)
    {
        if (release == null) throw new ArgumentNullException(nameof(release));
        ArgumentNullException.ThrowIfNull(lifetime);
        _release = release; _lifetime = lifetime;
    }
    internal bool IsActive { get { lock (_gate) return _active; } }
    internal bool CanAttach { get { lock (_gate) return !_closed && !_active && !_releasing && _owner == 0 && _readers == 0; } }
    internal long Generation { get { lock (_gate) return _generation; } }
    internal bool End { get { lock (_gate) return _end; } }

    // Ownership transfers only on success. Rejected attachment leaves compensation with the caller.
    internal void Attach(nint owner, nint address, int length, bool end)
    {
        if (owner == 0) throw new ArgumentException("A native batch requires an owner.", nameof(owner));
        if (length < 0 || length != 0 && address == 0) throw new ArgumentOutOfRangeException(nameof(length));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_active || _releasing || _owner != 0 || _readers != 0)
                throw new InvalidOperationException("The previous batch generation has not released its storage.");
            var generation = checked(_generation + 1);
            _owner = owner; _address = address; _length = length; _end = end;
            _generation = generation; _active = true;
        }
    }
    internal ScopedBatchRead AcquireRead() => AcquireRead(null);
    internal ScopedBatchRead AcquireRead(long generation) => AcquireRead((long?)generation);
    private ScopedBatchRead AcquireRead(long? generation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(!_active || generation is { } expected && expected != _generation, this);
            _readers = checked(_readers + 1);
            return new(this, _generation, new ReadOnlySpan<byte>((void*)_address, _length));
        }
    }
    public void Dispose() => ReleaseBatch(false);
    internal void Close() => ReleaseBatch(true);
    private void ReleaseBatch(bool permanent)
    {
        nint release;
        lock (_gate)
        {
            _closed |= permanent; _active = false;
            release = _readers == 0 ? Detach() : 0;
        }
        Release(release);
    }
    private nint Detach()
    {
        var owner = _owner;
        if (owner == 0) return 0;
        _owner = 0; _address = 0; _length = 0; _releasing = true;
        return owner;
    }
    internal void EndRead(long generation)
    {
        nint release;
        lock (_gate)
        {
            if (generation != _generation || _readers == 0)
                throw new InvalidOperationException("A batch read scope must be released exactly once.");
            _readers--;
            release = _readers == 0 && !_active ? Detach() : 0;
        }
        Release(release);
    }
    private void Release(nint owner)
    {
        if (owner != 0)
        {
            try { _release(owner); }
            finally
            {
                GC.KeepAlive(_lifetime);
                lock (_gate) _releasing = false;
            }
        }
        lock (_gate)
            if (_closed && _owner == 0 && _readers == 0 && !_releasing) GC.SuppressFinalize(this);
    }
    ~ScopedStreamBatch() => ReleaseBatch(true);
}

/// <summary>A synchronous span lease; do not copy it or carry it across await.</summary>
internal ref struct ScopedBatchRead
{
    private ScopedStreamBatch? _owner;
    private readonly long _generation;
    private ReadOnlySpan<byte> _bytes;
    internal ScopedBatchRead(ScopedStreamBatch owner, long generation, ReadOnlySpan<byte> bytes)
    { _owner = owner; _generation = generation; _bytes = bytes; }
    internal readonly ReadOnlySpan<byte> Bytes => _owner is not null ? _bytes : throw new ObjectDisposedException(nameof(ScopedBatchRead));
    public void Dispose()
    {
        var owner = _owner; _owner = null; _bytes = default;
        owner?.EndRead(_generation);
    }
}

/// <summary>Provider-confirmed native retirement, distinct from protocol reuse and managed disposal.</summary>
internal readonly record struct StreamRetirement(bool NativeRetired, bool ConnectionReusable, long ProviderRowsPeak);

/// <summary>The private cursor pull seam. Each cursor reuses one controller and one scoped batch owner.</summary>
internal interface IStreamTransport
{
    ValueTask<ScopedStreamBatch> FetchAsync();
    void CheckReadAdmission() { }
    void RequestStop();
    Task<StreamRetirement> JoinAsync();
}

/// <summary>Finite capacities charge encoded bytes, not CLR heap or RSS.</summary>
internal readonly record struct StreamLimits(int MaxBatchRows, int MaxBatchBytes, int MaxBufferedRows, long MaxBufferedBytes)
{
    internal static StreamLimits Default => new(256, 1024 * 1024, 10_000, 16 * 1024 * 1024);
    internal void Validate()
    {
        if (MaxBatchRows is <= 0 or > 4096 || MaxBatchBytes is <= 0 or > DbWire.MaxEncodedBytes
            || MaxBufferedRows <= 0 || MaxBufferedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(StreamLimits), "Streaming limits must be positive and finite.");
    }
}

/// <summary>One validated layout and row lifetime over a scoped native generation.</summary>
internal sealed class RowBatchLease : IRowLifetime, IDisposable
{
    private readonly object _gate = new();
    private ScopedStreamBatch? _owner;
    private WireLayout? _layout;
    private readonly ResultField[] _fields;
    private object? _validatedProjection;
    private WireExpectation? _validatedExpectation;
    private long _generation;
    private int _active, _readers;

    internal RowBatchLease(ScopedStreamBatch owner, WireExpectation expected, ResultField[] fields, StreamLimits limits)
    { _fields = fields; Attach(owner, expected, limits); }
    internal bool IsActive => Volatile.Read(ref _active) != 0;
    internal uint Rows { get { lock (_gate) return Layout.Rows; } }
    internal bool End { get; private set; }
    internal void Attach(ScopedStreamBatch owner, WireExpectation expected, StreamLimits limits)
    {
        lock (_gate)
        {
            if (IsActive || _readers != 0)
                throw new InvalidOperationException("The previous row batch must finish every read before attachment.");
            try
            {
                var generation = owner.Generation;
                using var read = owner.AcquireRead(generation);
                if (read.Bytes.Length > limits.MaxBatchBytes)
                    throw new DatabaseException("Native batch exceeds its admitted byte limit.", DatabaseErrorKind.ResourceLimit);
                var layout = DbWire.Validate(read.Bytes, expected);
                if (layout.Rows > limits.MaxBatchRows || layout.Rows == 0 && !owner.End)
                    throw new DatabaseException("Native batch row count or end marker is invalid.", DatabaseErrorKind.ShapeMismatch);
                _owner = owner; _generation = generation; _layout = layout; End = owner.End;
                Volatile.Write(ref _active, 1);
            }
            catch (ArgumentException error)
            {
                owner.Dispose();
                throw new DatabaseException("Native row batch does not match the admitted shape.", DatabaseErrorKind.ShapeMismatch, innerException: error);
            }
            catch { owner.Dispose(); throw; }
        }
    }
    internal T MaterializeBuffered<T>(ref uint row, Projection<T> projection, in StreamLimits limits,
        ref int bufferedRows, ref long bufferedBytes)
    {
        var read = BeginRead(out var layout);
        try
        {
            var bytes = RowBytes(row, layout, read.Bytes);
            if (bufferedRows >= limits.MaxBufferedRows || bytes > limits.MaxBufferedBytes - bufferedBytes)
                throw new DatabaseException("Buffered result exceeds its row or charged-byte limit.", DatabaseErrorKind.ResourceLimit);
            // Charge before invoking the mapper, preserving the original row
            // admission order while keeping this exact native generation held.
            bufferedRows++; bufferedBytes += bytes;
            return Materialize(row++, projection, layout, read.Bytes);
        }
        finally { read.Dispose(); EndRead(); }
    }
    internal T Materialize<T>(uint row, Projection<T> projection)
    {
        var read = BeginRead(out var layout);
        try { return Materialize(row, projection, layout, read.Bytes); }
        finally { read.Dispose(); EndRead(); }
    }
    private long RowBytes(uint row, WireLayout layout, ReadOnlySpan<byte> data)
    {
        long bytes = checked(_fields.Length * (long)WireSchema.CellBytes);
        for (var i = 0; i < _fields.Length; i++) bytes = checked(bytes + layout.Cell(data, row, i, out _).Length);
        return bytes;
    }
    private T Materialize<T>(uint row, Projection<T> projection, WireLayout layout, ReadOnlySpan<byte> data)
    {
        if (row >= layout.Rows) throw new ArgumentOutOfRangeException(nameof(row));
        var validated = ReferenceEquals(_validatedProjection, projection) && ReferenceEquals(_validatedExpectation, layout.Expected);
        if (!validated && projection.MatchesValidatedBatch(_fields, layout.Expected))
        { _validatedProjection = projection; _validatedExpectation = layout.Expected; validated = true; }
        return projection.ReadBatch(this, _fields, layout, data, row, validated);
    }
    private ScopedBatchRead BeginRead(out WireLayout layout)
    {
        lock (_gate)
        {
            layout = Layout;
            var read = _owner!.AcquireRead(_generation);
            _readers++;
            return read;
        }
    }
    private void EndRead() { lock (_gate) _readers--; }
    private WireLayout Layout => _layout ?? throw new ObjectDisposedException(nameof(RowBatchLease));
    public void AssertAlive() => ObjectDisposedException.ThrowIf(!IsActive, this);
    public void Dispose()
    {
        ScopedStreamBatch? owner;
        lock (_gate)
        {
            Volatile.Write(ref _active, 0); _layout = null;
            owner = _owner; _owner = null;
        }
        owner?.Dispose();
    }
}
