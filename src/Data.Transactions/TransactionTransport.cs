namespace Korda.Database;

// Internal B04 engine-operation composition seam. No managed transaction state
// machine, physical pool, runtime, provider or public construction hook lives here.
// The transport owns the native transaction owner and captures its final snapshot
// before releasing it. Every operation retains native and managed roots until the
// real provider future and callback/retirement obligations have completed.
internal readonly record struct TransactionSnapshot(TransactionState State, TransactionOutcome Outcome, bool CommitAttempted, DatabaseCloseResult Cleanup);
internal readonly record struct SavepointId(ulong Generation, ulong Sequence);
internal interface ITransactionTransport
{
    TransactionSnapshot Snapshot { get; }
    Task CommitAsync(CancellationToken admissionToken);
    Task RollbackAsync(CancellationToken admissionToken);
    Task<SavepointId> CreateSavepointAsync(CancellationToken admissionToken);
    Task RollbackSavepointAsync(SavepointId id, CancellationToken admissionToken);
    Task ReleaseSavepointAsync(SavepointId id, CancellationToken admissionToken);
    ValueTask DisposeSavepointAsync(SavepointId id);
    void RequestClose();
    Task<DatabaseCloseResult> WaitCloseAsync(TimeSpan waitBudget, CancellationToken cancellationToken);
    ValueTask JoinAsync();
}
