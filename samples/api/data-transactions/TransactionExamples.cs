using Korda.Database;

namespace Korda.Samples.Transactions;

// The owning DataSession (B04) supplies a confirmed transaction. These natural
// consumers use only the exact public B05 surface; the component harness invokes
// them with the real native coordinator in both JIT and Native AOT modes.
public static class TransactionExamples
{
    public static async Task CommitAsync(DataTransaction transaction, CancellationToken requestToken)
    {
        await using (transaction.ConfigureAwait(false))
        {
            await transaction.CommitAsync(requestToken).ConfigureAwait(false);
        }
    }

    public static async Task<string?> CommitWithBoundedCloseAsync(DataTransaction transaction, CancellationToken requestToken)
    {
        try
        {
            await transaction.CommitAsync(requestToken).ConfigureAwait(false);
            return null;
        }
        catch (CommitOutcomeUnknownException error)
        {
            // The caller records this identity in its own recovery protocol;
            // cleanup below never proves that the business write rolled back.
            return error.RecoveryKey;
        }
        finally
        {
            _ = await transaction.CloseAsync(TimeSpan.FromMilliseconds(20)).ConfigureAwait(false);
            // A Pending result intentionally does not force an unbounded join.
        }
    }

    public static async Task RollBackSavepointAsync(DataTransaction transaction)
    {
        await using Savepoint scope = await transaction.CreateSavepointAsync().ConfigureAwait(false);
        await scope.RollbackAsync().ConfigureAwait(false);
        await scope.ReleaseAsync().ConfigureAwait(false);
        // The caller still owns the active outer transaction.
    }
}
