using Korda.Data;
using Korda.Query.Internal;

namespace Korda.Database;

public sealed partial class DataSession
{
    private readonly object _compoundGate = new();
    private BulkSessionLease? _bulkLease;
    partial void CheckCompoundUse()
    { lock (_compoundGate) if (_bulkLease is not null) throw new DatabaseException("A bulk operation owns this session.", DatabaseErrorKind.ConcurrentUse); }
    private BulkSessionLease ReserveBulk()
    {
        lock (_compoundGate)
        {
            CheckBulkOpen();
            if (_bulkLease is not null) throw new DatabaseException("A bulk operation owns this session.", DatabaseErrorKind.ConcurrentUse);
            return _bulkLease = new(this);
        }
    }
    private void CheckBulkOpen() { ObjectDisposedException.ThrowIf(Volatile.Read(ref _closing) != 0, this); Source.CheckOpen(); }
    // The lock covers synchronous native command admission. An ordinary command
    // checked before bulk reserve cannot dispatch later inside its transaction.
    private Task<T> SubmitCommand<T>(NativeCommandKind kind, DatabaseOwner? input, ReadOnlyMemory<byte> payload,
        uint action, uint expectedKind, CommandResultReader<T> read, CancellationToken token, BulkSessionLease? bulk = null)
    {
        lock (_compoundGate)
        {
            CheckBulkOpen();
            if (!ReferenceEquals(_bulkLease, bulk) || bulk is not null && !ReferenceEquals(bulk.Owner, this))
                throw new DatabaseException("Another compound operation owns this session.", DatabaseErrorKind.ConcurrentUse);
            return Source.SubmitCommand(kind, Owner, input, payload, action, expectedKind, read, token);
        }
    }
    internal sealed class BulkSessionLease : IDisposable
    {
        internal DataSession? Owner { get; private set; }
        private DataTransaction.BulkLease? _transaction;
        internal BulkSessionLease(DataSession owner) => Owner = owner;
        internal void AttachTransaction(DataTransaction transaction)
        {
            if (_transaction is not null) throw new InvalidOperationException("A previous transaction lease was not transferred.");
            _transaction = transaction.ReserveBulk();
        }
        internal DataTransaction.BulkLease TakeTransaction() => Interlocked.Exchange(ref _transaction, null) ?? throw new InvalidOperationException("No bulk transaction lease was published.");
        public void Dispose()
        {
            var owner = Owner; if (owner is null) return;
            lock (owner._compoundGate) { if (ReferenceEquals(owner._bulkLease, this)) owner._bulkLease = null; Owner = null; Interlocked.Exchange(ref _transaction, null)?.Dispose(); }
        }
    }
}
