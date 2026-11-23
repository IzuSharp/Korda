using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Korda.Data;

namespace Korda.Database;

/// <summary>An immutable transaction callback failure observation, retained after native cleanup.</summary>
/// <param name="Outcome">Observed durable-effect knowledge; Unknown never implies rollback.</param>
/// <param name="CommitAttempted">Whether commit crossed the conservative native dispatch boundary.</param>
/// <param name="Cleanup">The native cleanup observation, independent from transaction effects.</param>
/// <param name="RecoveryKey">The optional application recovery identity.</param>
/// <remarks>The original exception retains this snapshot without replacement or automatic retry. It owns
/// no native resource and remains usable after the source and session have been disposed. Snapshot retrieval
/// also works when a custom exception's Data dictionary rejects optional enrichment.</remarks>
public sealed record TransactionFailure(TransactionOutcome Outcome, bool CommitAttempted, DatabaseCloseResult Cleanup, string? RecoveryKey)
{
    internal const string Key = "Korda.Database.TransactionFailure.v1";
    internal const string CleanupKey = "Korda.Database.TransactionCleanup.v1";
    private static readonly ConditionalWeakTable<Exception, TransactionFailure> Observations = new();

    /// <summary>Reads only the exact callback-failure observation on the supplied exception.</summary>
    /// <param name="exception">The original exception; inner exceptions are not traversed.</param>
    /// <param name="failure">The retained snapshot when present, otherwise null.</param>
    /// <returns>True when this exception carries a transaction callback snapshot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is null.</exception>
    public static bool TryGet(Exception exception, [NotNullWhen(true)] out TransactionFailure? failure)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (Observations.TryGetValue(exception, out failure)) return true;
        try { failure = exception.Data[Key] as TransactionFailure; }
        catch (Exception) { failure = null; }
        return failure is not null;
    }

    internal static void Attach(Exception exception, DataTransaction transaction)
    {
        var failure = new TransactionFailure(transaction.Outcome, transaction.CommitAttempted, transaction.Cleanup, transaction.RecoveryKey);
        Observations.AddOrUpdate(exception, failure);
        AttachData(exception, Key, failure);
    }
    internal static void AttachCleanup(Exception exception, Exception cleanup) => AttachData(exception, CleanupKey, cleanup);
    private static void AttachData(Exception exception, string key, object value)
    {
        // Exception.Data is application-overridable. Optional enrichment cannot
        // replace the business error; the weak identity table retains its snapshot.
        try { exception.Data[key] = value; }
        catch (Exception) { }
    }
}

public sealed partial class DataSession
{
    /// <summary>Runs one awaited callback in a new transaction and returns only after a confirmed commit and cleanup.</summary>
    /// <typeparam name="T">The owned callback result type.</typeparam>
    /// <param name="action">The asynchronous callback, invoked once after BEGIN confirmation.</param>
    /// <param name="options">Explicit isolation, recovery identity and independent deadlines; null selects defaults.</param>
    /// <param name="cancellationToken">Controls BEGIN, callback operations and pre-dispatch commit admission.</param>
    /// <returns>The callback result after confirmed commit and local retirement.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The session already has an active transaction.</exception>
    /// <exception cref="OperationCanceledException">Cancellation prevents admission or the callback observes cancellation.</exception>
    /// <exception cref="CommitOutcomeUnknownException">Commit may have happened without trustworthy confirmation.</exception>
    /// <exception cref="DatabaseException">Native admission, execution, commit or cleanup fails.</exception>
    /// <remarks>The callback must await its operations and must not commit, roll back or dispose this session's
    /// transaction. The session retains its single-command concurrency policy. Callback failure requests rollback
    /// through independent native cleanup and preserves the original exception, with a TransactionFailure snapshot.
    /// Cancellation after commit admission cannot become a false rollback or cancellation receipt. No callback
    /// replay, ambient transaction, nested transaction or savepoint is introduced.</remarks>
    public Task<T> InTransactionAsync<T>(Func<DataSession, CancellationToken, Task<T>> action, TransactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action); CheckOpen();
        if (Transaction is not null) throw new InvalidOperationException("The transaction callback requires a session without an active transaction.");
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<T>(cancellationToken);
        return RunTransaction(action, options ?? new TransactionOptions(), cancellationToken);
    }

    /// <summary>Runs one awaited callback in a new transaction with confirmed commit and independent cleanup.</summary>
    /// <param name="action">The asynchronous callback, invoked once after BEGIN confirmation.</param>
    /// <param name="options">Explicit isolation, recovery identity and independent deadlines; null selects defaults.</param>
    /// <param name="cancellationToken">Controls BEGIN, callback operations and pre-dispatch commit admission.</param>
    /// <returns>A task completing after confirmed commit and local retirement.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The session already has an active transaction.</exception>
    /// <exception cref="OperationCanceledException">Cancellation prevents admission or is observed by the callback.</exception>
    /// <exception cref="CommitOutcomeUnknownException">Commit may have happened without trustworthy confirmation.</exception>
    /// <exception cref="DatabaseException">Native admission, execution, commit or cleanup fails.</exception>
    /// <remarks>Ownership, cancellation and original-error preservation are identical to the result-returning
    /// overload. The callback awaits all operations and leaves transaction completion to this method.</remarks>
    public Task InTransactionAsync(Func<DataSession, CancellationToken, Task> action, TransactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InTransactionAsync(async (session, token) => { await action(session, token).ConfigureAwait(false); return true; }, options, cancellationToken);
    }

    private async Task<T> RunTransaction<T>(Func<DataSession, CancellationToken, Task<T>> action, TransactionOptions options, CancellationToken token)
    {
        var transaction = await BeginTransactionAsync(options, token).ConfigureAwait(false);
        T result;
        try
        {
            token.ThrowIfCancellationRequested();
            result = await action(this, token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            try { await transaction.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { TransactionFailure.AttachCleanup(failure, cleanup); }
            TransactionFailure.Attach(failure, transaction);
            throw;
        }
        try { await transaction.DisposeAsync().ConfigureAwait(false); }
        catch (Exception failure) { TransactionFailure.Attach(failure, transaction); throw; }
        return result;
    }
}
