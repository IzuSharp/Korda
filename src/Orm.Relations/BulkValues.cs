using System.Diagnostics.CodeAnalysis;

namespace Korda.Database;

/// <summary>Selects whole-operation atomicity or explicitly independent committed batches.</summary>
public enum BulkAtomicity
{
    /// <summary>Owns a transaction, or an internal savepoint inside the caller's transaction.</summary>
    All,
    /// <summary>Commits each complete batch independently; rejected inside a caller transaction.</summary>
    PerBatch
}
/// <summary>Immutable finite input batching and explicit transaction ownership policy.</summary>
/// <remarks>This managed configuration owns no native resource. A bulk operation reads its immutable values
/// at admission, then lowers the requested row count to source byte, node and parameter limits.</remarks>
public sealed record BulkOptions
{
    /// <summary>Gets the requested positive maximum rows per batch; byte, node and parameter limits can lower it.</summary>
    public int BatchSize { get; init; } = 256;
    /// <summary>Gets the atomicity policy; whole-operation atomicity is the default.</summary>
    public BulkAtomicity Atomicity { get; init; } = BulkAtomicity.All;
}
/// <summary>The acknowledged result of all admitted batches.</summary>
/// <param name="RowsAffected">The sum of acknowledged provider counts.</param>
/// <param name="Batches">The number of fully acknowledged batches.</param>
/// <param name="CommittedByOperation">True only when this operation confirmed its own commit or commits.</param>
/// <remarks>The owned immutable result contains only acknowledged counts and an ownership observation.
/// Constructing it allocates one managed record; it retains no session, transaction, cursor or native buffer.</remarks>
public sealed record BulkResult(long RowsAffected, int Batches, bool CommittedByOperation);
/// <summary>Immutable progress at failure, distinct from uncertain current-batch effects or later rollback.</summary>
/// <param name="RowsAffectedInCompletedBatches">Affected rows only in fully acknowledged preceding batches.</param>
/// <param name="CompletedBatches">The fully acknowledged batch count.</param>
/// <param name="CompletedBatchesCommitted">True only for confirmed preceding PerBatch commits.</param>
/// <param name="CurrentTransactionOutcome">Actual transaction knowledge, retaining Unknown independently.</param>
/// <remarks>The original admitted bulk exception retains this managed snapshot after cleanup. Counts describe
/// previously completed batches, which All may have rolled back; they do not prove current-batch effects or authorize replay.
/// The record owns no native resource and remains readable after session/source disposal.</remarks>
public sealed record BulkProgress(long RowsAffectedInCompletedBatches, int CompletedBatches, bool CompletedBatchesCommitted, TransactionOutcome CurrentTransactionOutcome);
/// <summary>Reads immutable bulk progress without replacing or wrapping the original exception.</summary>
/// <remarks>The helper reads one exact typed Exception.Data entry without native calls, allocation of a wrapper,
/// recursive error traversal or inference from cancellation. The returned snapshot can outlive all native owners.</remarks>
public static class BulkFailure
{
    internal const string Key = "Korda.Database.BulkProgress.v1";
    /// <summary>Reads only the exact bulk-progress entry on the supplied exception.</summary>
    /// <param name="exception">The original exception to inspect; inner exceptions are not traversed.</param>
    /// <param name="progress">The immutable progress when present, otherwise null.</param>
    /// <returns>True only when this exact exception carries a valid bulk-progress record.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is null.</exception>
    public static bool TryGetProgress(Exception exception, [NotNullWhen(true)] out BulkProgress? progress)
    { ArgumentNullException.ThrowIfNull(exception); progress = exception.Data[Key] as BulkProgress; return progress is not null; }
}
