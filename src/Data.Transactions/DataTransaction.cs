using NativeWeave.Data;

namespace NativeWeave.Database;

/// <summary>An exclusive native transaction scope with separately observed outcome and cleanup.</summary>
/// <remarks>The owning DataSession creates this scope after native BEGIN confirms. Ordinary calls share one
/// session command gate; concurrent transaction operations reject rather than queue. No reflection, managed SQL,
/// physical pool or business retry is introduced. Snapshots and recovery identity survive disposal. The engine
/// retains cleanup work until safety is established, even when a bounded CloseAsync waiter returns Pending.</remarks>
public sealed class DataTransaction : IDisposable, IAsyncDisposable
{
    private readonly ITransactionTransport _transport;

    internal DataTransaction(ITransactionTransport transport, string? recoveryKey)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        RecoveryKey = recoveryKey;
    }

    /// <summary>Gets the native knowledge of durable effects; Unknown is not cleared by disposal.</summary>
    public TransactionOutcome Outcome => _transport.Snapshot.Outcome;
    /// <summary>Gets transaction admission state independently from native local retirement.</summary>
    public TransactionState State => _transport.Snapshot.State;
    /// <summary>Gets whether commit crossed the conservative native dispatch boundary.</summary>
    public bool CommitAttempted => _transport.Snapshot.CommitAttempted;
    /// <summary>Gets the optional application recovery identity retained after disposal.</summary>
    public string? RecoveryKey { get; }
    /// <summary>Gets an immutable cleanup snapshot; Pending never claims that native access has stopped.</summary>
    public DatabaseCloseResult Cleanup => _transport.Snapshot.Cleanup;

    /// <summary>Commits once; successful completion means a trustworthy native Committed outcome.</summary>
    /// <param name="admissionToken">Cancellation observed only before native commit admission.</param>
    /// <returns>A task completing after commit confirmation, confirmed failure, or an Unknown classification.</returns>
    /// <exception cref="OperationCanceledException">The token canceled before native dispatch admission.</exception>
    /// <exception cref="CommitOutcomeUnknownException">Commit may have occurred without trustworthy confirmation.</exception>
    /// <exception cref="DatabaseException">The scope is failed, busy or closed, or the provider confirmed a failed transaction.</exception>
    /// <remarks>After admission, the independent commit deadline applies. The engine retains an in-flight call
    /// after deadline expiration; request cancellation never converts a possible commit into cancellation.</remarks>
    public Task CommitAsync(CancellationToken admissionToken = default) => _transport.CommitAsync(admissionToken);

    /// <summary>Requests rollback on the original native session using independent cleanup policy.</summary>
    /// <param name="admissionToken">Cancellation observed only before rollback admission.</param>
    /// <returns>A task for the explicit rollback result; local retirement remains separately observable.</returns>
    /// <exception cref="OperationCanceledException">The token canceled before native rollback admission.</exception>
    /// <exception cref="DatabaseCleanupException">Rollback was unconfirmed or its explicit cleanup failed.</exception>
    /// <exception cref="DatabaseException">The scope is busy, closed or invalid for rollback.</exception>
    public Task RollbackAsync(CancellationToken admissionToken = default) => _transport.RollbackAsync(admissionToken);

    /// <summary>Creates a tracked native savepoint on this transaction's physical session.</summary>
    /// <param name="cancellationToken">Cancellation observed before savepoint admission.</param>
    /// <returns>The top generation-bound savepoint, with its generated native identifier.</returns>
    /// <exception cref="OperationCanceledException">Cancellation prevented native admission.</exception>
    /// <exception cref="DatabaseException">The scope is busy, failed, closed or the native operation failed.</exception>
    /// <remarks>Savepoints are LIFO. Creation allocates one managed wrapper and one bounded native stack entry;
    /// it never starts or commits a separate outer transaction.</remarks>
    public async Task<Savepoint> CreateSavepointAsync(CancellationToken cancellationToken = default)
        => new(this, await _transport.CreateSavepointAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Closes admission and waits for at most the supplied budget while the engine retains cleanup ownership.</summary>
    /// <param name="waitBudget">A finite nonnegative wait; zero requests closure and returns a current snapshot.</param>
    /// <param name="cancellationToken">Cancels only this wait, after closure has been requested.</param>
    /// <returns>An immutable cleanup snapshot; Pending has LocalResourcesReleased=false.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="waitBudget"/> is negative.</exception>
    /// <exception cref="OperationCanceledException">The caller stopped waiting; native cleanup continues.</exception>
    /// <remarks>A finite HTTP response path can use this result without immediately joining indefinitely through await using.</remarks>
    public Task<DatabaseCloseResult> CloseAsync(TimeSpan waitBudget, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(waitBudget, TimeSpan.Zero);
        _transport.RequestClose();
        return _transport.WaitCloseAsync(waitBudget, cancellationToken);
    }

    /// <summary>Closes admission and transfers remaining work to the retained engine cleanup owner without blocking.</summary>
    /// <remarks>This is a request, not proof of local safety or rollback. Repeated disposal is idempotent.</remarks>
    public void Dispose() => _transport.RequestClose();

    /// <summary>Closes admission and joins actual local retirement without replacing an earlier business error.</summary>
    /// <returns>A safety join that completes only when LocalResourcesReleased is true.</returns>
    /// <remarks>This join can outlive the configured cleanup deadline. Cleanup errors stay in Cleanup;
    /// explicit RollbackAsync reports its own error. Unknown remains separately recoverable.</remarks>
    public ValueTask DisposeAsync()
    {
        _transport.RequestClose();
        return _transport.JoinAsync();
    }

    internal Task RollbackSavepointAsync(SavepointId id, CancellationToken token) => _transport.RollbackSavepointAsync(id, token);
    internal Task ReleaseSavepointAsync(SavepointId id, CancellationToken token) => _transport.ReleaseSavepointAsync(id, token);
    internal ValueTask DisposeSavepointAsync(SavepointId id) => _transport.DisposeSavepointAsync(id);
}

/// <summary>A transaction-generation-bound native savepoint; only the current top may execute an action.</summary>
/// <remarks>The provider uses its tracked same-session nesting. There is no user SQL identifier or connection
/// escape hatch. Releasing the scope never commits the outer transaction.</remarks>
public sealed class Savepoint : IAsyncDisposable
{
    private readonly DataTransaction _owner;
    private readonly SavepointId _id;

    internal Savepoint(DataTransaction owner, SavepointId id) { _owner = owner; _id = id; }

    /// <summary>Rolls back to the current top savepoint and retains it for a subsequent release.</summary>
    /// <param name="cancellationToken">Cancellation observed before native admission.</param>
    /// <returns>A task completing after the provider confirms savepoint recovery.</returns>
    /// <exception cref="OperationCanceledException">Cancellation prevented native admission.</exception>
    /// <exception cref="DatabaseException">The scope is stale, non-top, busy, closed or the provider rejected recovery.</exception>
    public Task RollbackAsync(CancellationToken cancellationToken = default) => _owner.RollbackSavepointAsync(_id, cancellationToken);

    /// <summary>Releases only this top savepoint without committing the caller-owned outer transaction.</summary>
    /// <param name="cancellationToken">Cancellation observed before native admission.</param>
    /// <returns>A task completing after tracked native release.</returns>
    /// <exception cref="OperationCanceledException">Cancellation prevented native admission.</exception>
    /// <exception cref="DatabaseException">The scope is stale, non-top, failed, busy or closed.</exception>
    public Task ReleaseAsync(CancellationToken cancellationToken = default) => _owner.ReleaseSavepointAsync(_id, cancellationToken);

    /// <summary>Rolls back to and releases an unreleased top savepoint, then joins its native safety.</summary>
    /// <returns>A cleanup join; an already released scope or closed outer transaction is idempotent.</returns>
    /// <remarks>Non-top disposal records invalid-state cleanup and closes outer admission without reordering SQL.
    /// It never touches a subsequently reused connection or implicitly commits post-savepoint changes.</remarks>
    public ValueTask DisposeAsync() => _owner.DisposeSavepointAsync(_id);
}
