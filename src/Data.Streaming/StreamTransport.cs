using NativeWeave.Data;

namespace NativeWeave.Database.Internal;

/// <summary>Internal host seam for one independently owned native batch and its bounded managed copy.</summary>
/// <remarks>Bytes are managed-owned, immutable, and valid until disposal. The creating transport retains
/// the native owner and its byte permit until Dispose; a raw pointer or native borrow never crosses await.</remarks>
internal interface IStreamBatch : IDisposable
{
    ReadOnlyMemory<byte> Bytes { get; }
    bool End { get; }
}

/// <summary>Provider-confirmed native retirement, distinct from physical protocol reuse and managed disposal.</summary>
internal readonly record struct StreamRetirement(bool NativeRetired, bool ConnectionReusable, long ProviderRowsPeak);

/// <summary>The internal B04 host composition seam; it owns no managed provider or public native handle.</summary>
/// <remarks>Fetch transfers a batch once. Stop only records a request. Join awaits the real native provider
/// cleanup owner, even if the enumerator or original operation was abandoned. A canceled token cannot locally
/// complete an accepted fetch before its native terminal result. B06 never infers retirement from a dropped task.</remarks>
internal interface IStreamTransport
{
    Task<IStreamBatch> FetchAsync();
    void RequestStop();
    Task<StreamRetirement> JoinAsync();
}

/// <summary>Finite managed batch and buffered-result capacities; these are charged encoded bytes, not CLR heap or RSS.</summary>
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

/// <summary>A synchronous B01 row lease over one validated, copied batch.</summary>
/// <remarks>Complete wire identity and all cell ranges/types are checked before materialization. Disposing
/// invalidates every reader and releases the creating transport's native owner exactly once.</remarks>
internal sealed class RowBatchLease : IRowLease, IDisposable
{
    private IStreamBatch? _owner;
    private DbWire? _wire;
    private readonly ResultField[] _fields;
    private uint _row;
    internal RowBatchLease(IStreamBatch owner, WireExpectation expected, ResultField[] fields, StreamLimits limits)
    {
        _fields = fields;
        try
        {
            if (owner.Bytes.Length > limits.MaxBatchBytes) throw new DatabaseException("Native batch exceeds its admitted byte limit.", DatabaseErrorKind.ResourceLimit);
            _wire = DbWire.Parse(owner.Bytes.Span, expected);
            if (_wire.Rows > limits.MaxBatchRows || _wire.Rows == 0 && !owner.End)
                throw new DatabaseException("Native batch row count or end marker is invalid.", DatabaseErrorKind.ShapeMismatch);
            _owner = owner;
            End = owner.End;
        }
        catch (ArgumentException exception)
        {
            owner.Dispose();
            throw new DatabaseException("Native row batch does not match the admitted shape.", DatabaseErrorKind.ShapeMismatch, innerException: exception);
        }
        catch { owner.Dispose(); throw; }
    }
    internal uint Rows => Wire.Rows;
    internal bool End { get; }
    internal long RowBytes(uint row)
    {
        var wire = Wire;
        long bytes = checked(_fields.Length * (long)WireSchema.CellBytes);
        for (var i = 0; i < _fields.Length; i++) bytes = checked(bytes + (wire.Cell(row, i)?.Length ?? 0));
        return bytes;
    }
    internal T Materialize<T>(uint row, Projection<T> projection)
    {
        if (row >= Rows) throw new ArgumentOutOfRangeException(nameof(row));
        _row = row;
        return projection.Read(this);
    }
    private DbWire Wire => _wire ?? throw new ObjectDisposedException(nameof(RowBatchLease));
    public int Count { get { AssertAlive(); return _fields.Length; } }
    public string Name(int ordinal) { AssertAlive(); return _fields[ordinal].Name; }
    public CodecDescriptor Codec(int ordinal) { AssertAlive(); return _fields[ordinal].Descriptor; }
    public ReadOnlyMemory<byte>? Value(int ordinal) => Wire.Cell(_row, ordinal);
    public void AssertAlive() => ObjectDisposedException.ThrowIf(_wire is null, this);
    public void Dispose()
    {
        _wire = null;
        Interlocked.Exchange(ref _owner, null)?.Dispose();
    }
}
