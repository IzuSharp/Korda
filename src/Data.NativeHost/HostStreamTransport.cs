using System.Buffers;
using System.Runtime.InteropServices;
using Korda.Database.Internal;
using Korda.Interop.Abi;
using Korda.Query.Internal;

namespace Korda.Database;

internal sealed class HostStreamTransport : IStreamTransport
{
    private readonly DataSource _source;
    private readonly DatabaseOwner _cursor;
    private readonly int _rows;
    private readonly CancellationToken _token;
    private readonly CloseController _close;
    private readonly Func<nint> _createFetch;
    private readonly Func<nint, IStreamBatch> _takeBatch;
    internal HostStreamTransport(DataSource source, DatabaseOwner cursor, int rows, CancellationToken token)
    {
        _source = source; _cursor = cursor; _rows = rows; _token = token; _close = new(source, cursor, cursor.Dispose);
        _createFetch = CreateFetch; _takeBatch = TakeBatch;
    }
    public ValueTask<IStreamBatch> FetchAsync() => _source.StartRetiredValueTask(_createFetch, _takeBatch, _token);
    private nint CreateFetch() => NativeHost.Fetch(_cursor, _rows, _source.Limits.MaxBatchBytes);
    private IStreamBatch TakeBatch(nint operation) => NativeHost.TakeBatch(_source.Binding, operation, checked((int)_source.Limits.MaxBatchBytes));
    public void CheckReadAdmission()
    {
        _source.CheckOpen(); _token.ThrowIfCancellationRequested();
        NativeHost.CheckCursorReadAdmission(_cursor);
    }
    public void RequestStop() => _close.Request();
    public async Task<StreamRetirement> JoinAsync()
    { _close.Request(); await _close.JoinAsync().ConfigureAwait(false); var snapshot = _close.Snapshot; return new(snapshot.LocalResourcesReleased, snapshot.ConnectionDisposition == ConnectionDisposition.Usable, 0); }
}
internal sealed class HostStreamBatch : MemoryManager<byte>, IBorrowedStreamBatch
{
    private NativeBuffer? _owner;
    private readonly nint _address;
    private readonly int _length;
    internal unsafe HostStreamBatch(NativeBuffer owner, NwBytesViewV1 bytes, uint knownEnd, uint reserved, int maximum)
    {
        using var read = new BatchReadScope(owner);
        if (knownEnd > 1 || reserved != 0)
            throw new Korda.Data.DatabaseException("Native batch result flags are invalid.", Korda.Data.DatabaseErrorKind.ShapeMismatch);
        NativeBuffer.CheckView(bytes, maximum, out _address, out _length);
        if (_length < 96) throw new Korda.Data.DatabaseException("Native batch header is incomplete.", Korda.Data.DatabaseErrorKind.ShapeMismatch);
        End = knownEnd != 0;
        _owner = owner;
    }
    public SafeHandle ReadOwner => Volatile.Read(ref _owner) ?? throw new ObjectDisposedException(nameof(HostStreamBatch));
    public ReadOnlyMemory<byte> Bytes { get { _ = ReadOwner; return CreateMemory(_length); } }
    // Every caller of this internal memory manager holds a BatchReadScope over
    // the complete synchronous consumption, not just over this GetSpan call.
    public override unsafe Span<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(ReadOwner.IsClosed, this);
        return new Span<byte>((void*)_address, _length);
    }
    public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException("Native batch memory cannot escape its synchronous read scope.");
    public override void Unpin() { }
    public bool End { get; }
    public void Dispose() => Dispose(true);
    protected override void Dispose(bool disposing) => Interlocked.Exchange(ref _owner, null)?.Dispose();
}
