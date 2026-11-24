using System.Buffers.Binary;
using Korda.Database.Internal;
using Korda.Query.Internal;

namespace Korda.Database;

internal sealed class HostStreamTransport : IStreamTransport
{
    private readonly DataSource _source;
    private readonly DatabaseOwner _cursor;
    private readonly int _rows;
    private readonly CancellationToken _token;
    private readonly CloseController _close;
    internal HostStreamTransport(DataSource source, DatabaseOwner cursor, int rows, CancellationToken token)
    { _source = source; _cursor = cursor; _rows = rows; _token = token; _close = new(source, cursor, cursor.Dispose); }
    public Task<IStreamBatch> FetchAsync() => _source.Start(() => NativeHost.Fetch(_cursor, _rows, _source.Limits.MaxBatchBytes), operation =>
    {
        var owner = NativeHost.TakeBuffer(_source.Binding, operation);
        try { return (IStreamBatch)new HostStreamBatch(owner, checked((int)_source.Limits.MaxBatchBytes)); }
        catch { owner.Dispose(); throw; }
    }, _token);
    public void RequestStop() => _close.Request();
    public async Task<StreamRetirement> JoinAsync()
    { _close.Request(); await _close.JoinAsync().ConfigureAwait(false); var snapshot = _close.Snapshot; return new(snapshot.LocalResourcesReleased, snapshot.ConnectionDisposition == ConnectionDisposition.Usable, 0); }
}
internal sealed class HostStreamBatch : IStableManagedBatch
{
    private NativeBuffer? _owner;
    private byte[]? _bytes;
    internal HostStreamBatch(NativeBuffer owner, int maximum)
    {
        _bytes = owner.Copy(maximum); _owner = owner;
        if (_bytes.Length < 96) throw new Korda.Data.DatabaseException("Native batch header is incomplete.", Korda.Data.DatabaseErrorKind.ShapeMismatch);
        End = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(56)) == 0;
    }
    public ReadOnlyMemory<byte> Bytes => _bytes ?? throw new ObjectDisposedException(nameof(HostStreamBatch));
    public byte[] OwnedBytes => _bytes ?? throw new ObjectDisposedException(nameof(HostStreamBatch));
    public bool End { get; }
    public void Dispose() { _bytes = null; Interlocked.Exchange(ref _owner, null)?.Dispose(); }
}
