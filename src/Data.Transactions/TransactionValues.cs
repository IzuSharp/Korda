using System.Data;
using NativeWeave.Data;

namespace NativeWeave.Database;

/// <summary>Knowledge of database effects, independent from operation completion and local disposal.</summary>
public enum TransactionOutcome
{
    /// <summary>No trusted terminal effect has been established, including an unconfirmed rollback.</summary>
    InProgress,
    /// <summary>The provider confirmed commit.</summary>
    Committed,
    /// <summary>The provider confirmed rollback.</summary>
    RolledBack,
    /// <summary>Commit may have occurred; cleanup cannot replace identity-bound recovery evidence.</summary>
    Unknown
}

/// <summary>Transaction admission state; a closed transaction can still retain native cleanup obligations.</summary>
public enum TransactionState
{
    /// <summary>Ordinary transaction operations may be admitted.</summary>
    Active,
    /// <summary>The transaction requires rollback or supported savepoint recovery; commit is prohibited.</summary>
    Failed,
    /// <summary>Commit has crossed the conservative dispatch boundary.</summary>
    CommitInFlight,
    /// <summary>Rollback has been admitted on the original session.</summary>
    RollbackInFlight,
    /// <summary>No additional transaction business operation may start.</summary>
    Closed
}

/// <summary>Provider evidence about reuse of the original physical session.</summary>
public enum ConnectionDisposition
{
    /// <summary>The protocol and session are confirmed reusable.</summary>
    Usable,
    /// <summary>Only known, serialized provider maintenance remains before the next business command.</summary>
    ProviderResetPending,
    /// <summary>Protocol or ownership is untrusted; another tenant cannot borrow the connection.</summary>
    Quarantined,
    /// <summary>The physical connection is removed from reuse.</summary>
    Discarded
}

/// <summary>The result of cleanup, distinct from transaction outcome.</summary>
public enum CleanupStatus
{
    /// <summary>No cleanup has started.</summary>
    NotStarted,
    /// <summary>Native obligations still run or retirement has not been observed.</summary>
    Pending,
    /// <summary>The requested cleanup completed safely.</summary>
    Succeeded,
    /// <summary>Rollback was not confirmed; this does not imply any COMMIT attempt.</summary>
    RollbackUnconfirmed,
    /// <summary>Cleanup failed; the diagnostic and local retirement observations remain separate.</summary>
    Failed
}

/// <summary>An immutable cleanup observation that remains usable after native resources retire.</summary>
/// <param name="Status">The cleanup result, independent from transaction effects.</param>
/// <param name="CorrelationId">The native diagnostic identity, or zero when absent.</param>
/// <param name="ProviderCode">A bounded provider code without credentials or parameter values.</param>
/// <param name="LocalResourcesReleased">Whether native accessors and retained parent/callback obligations have actually retired.</param>
/// <param name="ConnectionDisposition">Whether protocol and session state permit reuse.</param>
/// <param name="DeadlineExceeded">Whether a cleanup deadline elapsed; expiration never forces resource release.</param>
public sealed record DatabaseCloseResult(CleanupStatus Status, ulong CorrelationId, string? ProviderCode, bool LocalResourcesReleased, ConnectionDisposition ConnectionDisposition, bool DeadlineExceeded);

/// <summary>Exact transaction policy, snapshotted before native BEGIN admission.</summary>
/// <remarks>No engine is initialized by this value. Unsupported isolation or read-only requests fail before BEGIN.
/// Commit and cleanup deadlines are finite and independent from an HTTP/request cancellation token.</remarks>
public sealed record TransactionOptions
{
    /// <summary>Gets the exact requested isolation, or null for the provider default: PostgreSQL ReadCommitted or ordinary SQLite Serializable.</summary>
    public IsolationLevel? Isolation { get; init; }
    /// <summary>Gets an optional application recovery identity; it is retained after disposal and never authorizes automatic retry.</summary>
    public string? RecoveryKey { get; init; }
    /// <summary>Gets the finite, nonnegative commit deadline, initially thirty seconds.</summary>
    public TimeSpan CommitTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Gets the finite, nonnegative cleanup deadline, initially five seconds; native access may outlive its expiration.</summary>
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Gets whether the provider must enforce read-only transaction mode; unsupported profiles reject this request.</summary>
    public bool ReadOnly { get; init; }
}

/// <summary>A possible commit without trustworthy confirmation; cleanup and cancellation do not prove rollback.</summary>
/// <remarks>Recovery uses the original application identity. This exception never suggests a blind transaction replay.</remarks>
public sealed class CommitOutcomeUnknownException : DatabaseException
{
    /// <summary>Creates an immutable uncertain-outcome failure.</summary>
    /// <param name="message">A diagnostic without credentials, SQL or parameter values.</param>
    /// <param name="recoveryKey">The optional application recovery identity.</param>
    /// <param name="providerCode">An optional bounded provider error code.</param>
    /// <param name="correlationId">The native diagnostic identity, or zero when absent.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    public CommitOutcomeUnknownException(string message, string? recoveryKey, string? providerCode = null, ulong correlationId = 0)
        : base(message, DatabaseErrorKind.UnknownOutcome, providerCode, correlationId) => RecoveryKey = recoveryKey;

    /// <summary>Gets Unknown independently from whether local resources have since retired.</summary>
    public TransactionOutcome Outcome => TransactionOutcome.Unknown;
    /// <summary>Gets the optional immutable application recovery identity.</summary>
    public string? RecoveryKey { get; }
}

/// <summary>An explicit rollback/cleanup failure, retaining the cleanup observation separately from business errors.</summary>
public sealed class DatabaseCleanupException : DatabaseException
{
    /// <summary>Creates an explicit cleanup failure from its immutable observation.</summary>
    /// <param name="message">A diagnostic without sensitive connection or command payloads.</param>
    /// <param name="cleanup">The cleanup observation, including local safety and connection disposition.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> or <paramref name="cleanup"/> is null.</exception>
    public DatabaseCleanupException(string message, DatabaseCloseResult cleanup)
        : base(message, DatabaseErrorKind.Provider, cleanup?.ProviderCode, cleanup?.CorrelationId ?? 0)
        => Cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));

    /// <summary>Gets the immutable cleanup result; it never reclassifies an earlier uncertain commit.</summary>
    public DatabaseCloseResult Cleanup { get; }
}
