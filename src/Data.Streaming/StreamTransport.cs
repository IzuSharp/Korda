using System.Runtime.InteropServices;
using Korda.Data;

namespace Korda.Database.Internal;

/// <summary>Internal host seam for one independently owned, bounded batch.</summary>
/// <remarks>Bytes are immutable and valid until disposal or an explicit owned handoff. The creating transport retains
/// the native owner and its byte permit until Dispose. Native spans are consumed only within a synchronous read scope.</remarks>
internal interface IStreamBatch : IDisposable
{
    ReadOnlyMemory<byte> Bytes { get; }
    bool End { get; }
}

/// <summary>Optional internal handoff for a host batch's exclusive managed copy.</summary>
/// <remarks>TakeBytes transfers exactly once and invalidates Bytes. No writable
/// aliases may remain. End and Dispose stay usable; the native owner and byte
/// permit remain held until Dispose, including when parsing the array fails.
/// Other IStreamBatch implementations keep the defensive-copy path.</remarks>
internal interface IOwnedStreamBatch : IStreamBatch
{
    byte[] TakeBytes();
}

/// <summary>Trusted immutable native storage whose reads require a temporary SafeHandle reference.</summary>
/// <remarks>The batch holds the ordinary SafeHandle owner; it never holds an extra reference across await.
/// Parsing, row sizing and the complete synchronous projection call retain ReadOwner until consumption ends.
/// Returned application values must own their data; borrowed memory never escapes a public row reader.</remarks>
internal interface IBorrowedStreamBatch : IStreamBatch
{
    SafeHandle ReadOwner { get; }
}

/// <summary>A nonallocating native read lifetime; no scope may cross await.</summary>
internal ref struct BatchReadScope
{
    private SafeHandle? _owner;
    internal BatchReadScope(SafeHandle? owner)
    {
        _owner = null;
        if (owner is null) return;
        var retained = false;
        try { owner.DangerousAddRef(ref retained); _owner = owner; }
        catch { if (retained) owner.DangerousRelease(); throw; }
    }
    public void Dispose()
    {
        var owner = _owner; _owner = null;
        owner?.DangerousRelease();
    }
}

/// <summary>Provider-confirmed native retirement, distinct from physical protocol reuse and managed disposal.</summary>
internal readonly record struct StreamRetirement(bool NativeRetired, bool ConnectionReusable, long ProviderRowsPeak);

/// <summary>The internal B04 host composition seam; it owns no managed provider or public native handle.</summary>
/// <remarks>Fetch transfers a batch once. Stop only records a request. Join awaits the real native provider
/// cleanup owner, even if the enumerator or original operation was abandoned. A canceled token cannot locally
/// complete an accepted fetch before its native terminal result. B06 never infers retirement from a dropped task.</remarks>
internal interface IStreamTransport
{
    ValueTask<IStreamBatch> FetchAsync();
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

/// <summary>A synchronous B01 row lease over one validated, retained batch.</summary>
/// <remarks>Complete wire identity and all cell ranges/types are checked before materialization. Disposing
/// invalidates every reader and releases the creating transport's native owner exactly once. A serialized
/// enumerator may attach its next owner only after disposal; no public reader survives its synchronous mapper.</remarks>
internal sealed class RowBatchLease : IRowLease, IDisposable
{
    private IStreamBatch? _owner;
    private DbWire? _wire;
    private readonly ResultField[] _fields;
    private uint _row;
    internal RowBatchLease(IStreamBatch owner, WireExpectation expected, ResultField[] fields, StreamLimits limits)
    {
        _fields = fields;
        Attach(owner, expected, limits);
    }
    internal bool IsActive => _wire.HasValue;
    // Reject an active lease before accepting ownership. After this precondition,
    // all validation failures compensate the incoming owner exactly once.
    internal void Attach(IStreamBatch owner, WireExpectation expected, StreamLimits limits)
    {
        if (IsActive) throw new InvalidOperationException("The previous row batch must be disposed before attachment.");
        try
        {
            using var read = new BatchReadScope((owner as IBorrowedStreamBatch)?.ReadOwner);
            byte[]? ownedBytes = null;
            ReadOnlyMemory<byte> bytes;
            if (owner is IOwnedStreamBatch owned)
            {
                ownedBytes = owned.TakeBytes();
                bytes = ownedBytes;
            }
            else bytes = owner.Bytes;
            if (bytes.Length > limits.MaxBatchBytes) throw new DatabaseException("Native batch exceeds its admitted byte limit.", DatabaseErrorKind.ResourceLimit);
            var wire = ownedBytes is not null ? DbWire.ParseOwned(ownedBytes, expected)
                : owner is IBorrowedStreamBatch ? DbWire.ParseBorrowed(bytes, expected) : DbWire.Parse(bytes.Span, expected);
            if (wire.Rows > limits.MaxBatchRows || wire.Rows == 0 && !owner.End)
                throw new DatabaseException("Native batch row count or end marker is invalid.", DatabaseErrorKind.ShapeMismatch);
            var end = owner.End;
            _owner = owner;
            _wire = wire;
            _row = 0;
            End = end;
        }
        catch (ArgumentException exception)
        {
            owner.Dispose();
            throw new DatabaseException("Native row batch does not match the admitted shape.", DatabaseErrorKind.ShapeMismatch, innerException: exception);
        }
        catch { owner.Dispose(); throw; }
    }
    internal uint Rows => Wire.Rows;
    internal bool End { get; private set; }
    internal long RowBytes(uint row)
    {
        using var read = new BatchReadScope((_owner as IBorrowedStreamBatch)?.ReadOwner);
        var wire = Wire;
        long bytes = checked(_fields.Length * (long)WireSchema.CellBytes);
        for (var i = 0; i < _fields.Length; i++) bytes = checked(bytes + (wire.Cell(row, i)?.Length ?? 0));
        return bytes;
    }
    internal T Materialize<T>(uint row, Projection<T> projection)
    {
        using var read = new BatchReadScope((_owner as IBorrowedStreamBatch)?.ReadOwner);
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
