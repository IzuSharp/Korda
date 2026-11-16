# NativeWeave.Database transaction API reference

Generated from the compiled product assembly and its XML comments by `tests/Data.Transactions/ApiCheck`. B05 transaction scope; native coordinator execution is separate from downstream B04 host composition.

## `F:NativeWeave.Database.CleanupStatus.Failed`

```csharp
NativeWeave.Database.CleanupStatus.Failed = 4
```

**summary:** Cleanup failed; the diagnostic and local retirement observations remain separate.

## `F:NativeWeave.Database.CleanupStatus.NotStarted`

```csharp
NativeWeave.Database.CleanupStatus.NotStarted = 0
```

**summary:** No cleanup has started.

## `F:NativeWeave.Database.CleanupStatus.Pending`

```csharp
NativeWeave.Database.CleanupStatus.Pending = 1
```

**summary:** Native obligations still run or retirement has not been observed.

## `F:NativeWeave.Database.CleanupStatus.RollbackUnconfirmed`

```csharp
NativeWeave.Database.CleanupStatus.RollbackUnconfirmed = 3
```

**summary:** Rollback was not confirmed; this does not imply any COMMIT attempt.

## `F:NativeWeave.Database.CleanupStatus.Succeeded`

```csharp
NativeWeave.Database.CleanupStatus.Succeeded = 2
```

**summary:** The requested cleanup completed safely.

## `F:NativeWeave.Database.ConnectionDisposition.Discarded`

```csharp
NativeWeave.Database.ConnectionDisposition.Discarded = 3
```

**summary:** The physical connection is removed from reuse.

## `F:NativeWeave.Database.ConnectionDisposition.ProviderResetPending`

```csharp
NativeWeave.Database.ConnectionDisposition.ProviderResetPending = 1
```

**summary:** Only known, serialized provider maintenance remains before the next business command.

## `F:NativeWeave.Database.ConnectionDisposition.Quarantined`

```csharp
NativeWeave.Database.ConnectionDisposition.Quarantined = 2
```

**summary:** Protocol or ownership is untrusted; another tenant cannot borrow the connection.

## `F:NativeWeave.Database.ConnectionDisposition.Usable`

```csharp
NativeWeave.Database.ConnectionDisposition.Usable = 0
```

**summary:** The protocol and session are confirmed reusable.

## `F:NativeWeave.Database.TransactionOutcome.Committed`

```csharp
NativeWeave.Database.TransactionOutcome.Committed = 1
```

**summary:** The provider confirmed commit.

## `F:NativeWeave.Database.TransactionOutcome.InProgress`

```csharp
NativeWeave.Database.TransactionOutcome.InProgress = 0
```

**summary:** No trusted terminal effect has been established, including an unconfirmed rollback.

## `F:NativeWeave.Database.TransactionOutcome.RolledBack`

```csharp
NativeWeave.Database.TransactionOutcome.RolledBack = 2
```

**summary:** The provider confirmed rollback.

## `F:NativeWeave.Database.TransactionOutcome.Unknown`

```csharp
NativeWeave.Database.TransactionOutcome.Unknown = 3
```

**summary:** Commit may have occurred; cleanup cannot replace identity-bound recovery evidence.

## `F:NativeWeave.Database.TransactionState.Active`

```csharp
NativeWeave.Database.TransactionState.Active = 0
```

**summary:** Ordinary transaction operations may be admitted.

## `F:NativeWeave.Database.TransactionState.Closed`

```csharp
NativeWeave.Database.TransactionState.Closed = 4
```

**summary:** No additional transaction business operation may start.

## `F:NativeWeave.Database.TransactionState.CommitInFlight`

```csharp
NativeWeave.Database.TransactionState.CommitInFlight = 2
```

**summary:** Commit has crossed the conservative dispatch boundary.

## `F:NativeWeave.Database.TransactionState.Failed`

```csharp
NativeWeave.Database.TransactionState.Failed = 1
```

**summary:** The transaction requires rollback or supported savepoint recovery; commit is prohibited.

## `F:NativeWeave.Database.TransactionState.RollbackInFlight`

```csharp
NativeWeave.Database.TransactionState.RollbackInFlight = 3
```

**summary:** Rollback has been admitted on the original session.

## `M:NativeWeave.Database.CleanupStatus.#ctor`

```csharp
public NativeWeave.Database.CleanupStatus.CleanupStatus()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Database.CommitOutcomeUnknownException.#ctor(System.String,System.String,System.String,System.UInt64)`

```csharp
public NativeWeave.Database.CommitOutcomeUnknownException.CommitOutcomeUnknownException(string message, string? recoveryKey, string? providerCode = null, ulong correlationId = 0)
```

**summary:** Creates an immutable uncertain-outcome failure.

**param message:** A diagnostic without credentials, SQL or parameter values.

**param recoveryKey:** The optional application recovery identity.

**param providerCode:** An optional bounded provider error code.

**param correlationId:** The native diagnostic identity, or zero when absent.

**exception:** is null.

## `M:NativeWeave.Database.ConnectionDisposition.#ctor`

```csharp
public NativeWeave.Database.ConnectionDisposition.ConnectionDisposition()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Database.DataTransaction.CloseAsync(System.TimeSpan,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<NativeWeave.Database.DatabaseCloseResult> NativeWeave.Database.DataTransaction.CloseAsync(System.TimeSpan waitBudget, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Closes admission and waits for at most the supplied budget while the engine retains cleanup ownership.

**param waitBudget:** A finite nonnegative wait; zero requests closure and returns a current snapshot.

**param cancellationToken:** Cancels only this wait, after closure has been requested.

**returns:** An immutable cleanup snapshot; Pending has LocalResourcesReleased=false.

**exception:** is negative.

**exception:** The caller stopped waiting; native cleanup continues.

**remarks:** A finite HTTP response path can use this result without immediately joining indefinitely through await using.

## `M:NativeWeave.Database.DataTransaction.CommitAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task NativeWeave.Database.DataTransaction.CommitAsync(System.Threading.CancellationToken admissionToken = default(System.Threading.CancellationToken))
```

**summary:** Commits once; successful completion means a trustworthy native Committed outcome.

**param admissionToken:** Cancellation observed only before native commit admission.

**returns:** A task completing after commit confirmation, confirmed failure, or an Unknown classification.

**exception:** The token canceled before native dispatch admission.

**exception:** Commit may have occurred without trustworthy confirmation.

**exception:** The scope is failed, busy or closed, or the provider confirmed a failed transaction.

**remarks:** After admission, the independent commit deadline applies. The engine retains an in-flight call after deadline expiration; request cancellation never converts a possible commit into cancellation.

## `M:NativeWeave.Database.DataTransaction.CreateSavepointAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<NativeWeave.Database.Savepoint> NativeWeave.Database.DataTransaction.CreateSavepointAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Creates a tracked native savepoint on this transaction's physical session.

**param cancellationToken:** Cancellation observed before savepoint admission.

**returns:** The top generation-bound savepoint, with its generated native identifier.

**exception:** Cancellation prevented native admission.

**exception:** The scope is busy, failed, closed or the native operation failed.

**remarks:** Savepoints are LIFO. Creation allocates one managed wrapper and one bounded native stack entry; it never starts or commits a separate outer transaction.

## `M:NativeWeave.Database.DataTransaction.Dispose`

```csharp
public void NativeWeave.Database.DataTransaction.Dispose()
```

**summary:** Closes admission and transfers remaining work to the retained engine cleanup owner without blocking.

**remarks:** This is a request, not proof of local safety or rollback. Repeated disposal is idempotent.

## `M:NativeWeave.Database.DataTransaction.DisposeAsync`

```csharp
public System.Threading.Tasks.ValueTask NativeWeave.Database.DataTransaction.DisposeAsync()
```

**summary:** Closes admission and joins actual local retirement without replacing an earlier business error.

**returns:** A safety join that completes only when LocalResourcesReleased is true.

**remarks:** This join can outlive the configured cleanup deadline. Cleanup errors stay in Cleanup; explicit RollbackAsync reports its own error. Unknown remains separately recoverable.

## `M:NativeWeave.Database.DataTransaction.RollbackAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task NativeWeave.Database.DataTransaction.RollbackAsync(System.Threading.CancellationToken admissionToken = default(System.Threading.CancellationToken))
```

**summary:** Requests rollback on the original native session using independent cleanup policy.

**param admissionToken:** Cancellation observed only before rollback admission.

**returns:** A task for the explicit rollback result; local retirement remains separately observable.

**exception:** The token canceled before native rollback admission.

**exception:** Rollback was unconfirmed or its explicit cleanup failed.

**exception:** The scope is busy, closed or invalid for rollback.

## `M:NativeWeave.Database.DatabaseCleanupException.#ctor(System.String,NativeWeave.Database.DatabaseCloseResult)`

```csharp
public NativeWeave.Database.DatabaseCleanupException.DatabaseCleanupException(string message, NativeWeave.Database.DatabaseCloseResult cleanup)
```

**summary:** Creates an explicit cleanup failure from its immutable observation.

**param message:** A diagnostic without sensitive connection or command payloads.

**param cleanup:** The cleanup observation, including local safety and connection disposition.

**exception:** or is null.

## `M:NativeWeave.Database.DatabaseCloseResult.#ctor(NativeWeave.Database.CleanupStatus,System.UInt64,System.String,System.Boolean,NativeWeave.Database.ConnectionDisposition,System.Boolean)`

```csharp
public NativeWeave.Database.DatabaseCloseResult.DatabaseCloseResult(NativeWeave.Database.CleanupStatus Status, ulong CorrelationId, string? ProviderCode, bool LocalResourcesReleased, NativeWeave.Database.ConnectionDisposition ConnectionDisposition, bool DeadlineExceeded)
```

**summary:** An immutable cleanup observation that remains usable after native resources retire.

**param Status:** The cleanup result, independent from transaction effects.

**param CorrelationId:** The native diagnostic identity, or zero when absent.

**param ProviderCode:** A bounded provider code without credentials or parameter values.

**param LocalResourcesReleased:** Whether native accessors and retained parent/callback obligations have actually retired.

**param ConnectionDisposition:** Whether protocol and session state permit reuse.

**param DeadlineExceeded:** Whether a cleanup deadline elapsed; expiration never forces resource release.

## `M:NativeWeave.Database.DatabaseCloseResult.Deconstruct(NativeWeave.Database.CleanupStatus@,System.UInt64@,System.String@,System.Boolean@,NativeWeave.Database.ConnectionDisposition@,System.Boolean@)`

```csharp
public void NativeWeave.Database.DatabaseCloseResult.Deconstruct(out NativeWeave.Database.CleanupStatus Status, out ulong CorrelationId, out string? ProviderCode, out bool LocalResourcesReleased, out NativeWeave.Database.ConnectionDisposition ConnectionDisposition, out bool DeadlineExceeded)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Database.DatabaseCloseResult.Equals(NativeWeave.Database.DatabaseCloseResult)`

```csharp
public bool NativeWeave.Database.DatabaseCloseResult.Equals(NativeWeave.Database.DatabaseCloseResult? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Database.DatabaseCloseResult.Equals(System.Object)`

```csharp
public override bool NativeWeave.Database.DatabaseCloseResult.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Database.DatabaseCloseResult.GetHashCode`

```csharp
public override int NativeWeave.Database.DatabaseCloseResult.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Database.DatabaseCloseResult.ToString`

```csharp
public override string NativeWeave.Database.DatabaseCloseResult.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Database.DatabaseCloseResult.op_Equality(NativeWeave.Database.DatabaseCloseResult,NativeWeave.Database.DatabaseCloseResult)`

```csharp
public static bool NativeWeave.Database.DatabaseCloseResult.operator ==(NativeWeave.Database.DatabaseCloseResult? left, NativeWeave.Database.DatabaseCloseResult? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Database.DatabaseCloseResult.op_Inequality(NativeWeave.Database.DatabaseCloseResult,NativeWeave.Database.DatabaseCloseResult)`

```csharp
public static bool NativeWeave.Database.DatabaseCloseResult.operator !=(NativeWeave.Database.DatabaseCloseResult? left, NativeWeave.Database.DatabaseCloseResult? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Database.DatabaseCloseResult.{Clone}$`

```csharp
public NativeWeave.Database.DatabaseCloseResult NativeWeave.Database.DatabaseCloseResult.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Database.Savepoint.DisposeAsync`

```csharp
public System.Threading.Tasks.ValueTask NativeWeave.Database.Savepoint.DisposeAsync()
```

**summary:** Rolls back to and releases an unreleased top savepoint, then joins its native safety.

**returns:** A cleanup join; an already released scope or closed outer transaction is idempotent.

**remarks:** Non-top disposal records invalid-state cleanup and closes outer admission without reordering SQL. It never touches a subsequently reused connection or implicitly commits post-savepoint changes.

## `M:NativeWeave.Database.Savepoint.ReleaseAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task NativeWeave.Database.Savepoint.ReleaseAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Releases only this top savepoint without committing the caller-owned outer transaction.

**param cancellationToken:** Cancellation observed before native admission.

**returns:** A task completing after tracked native release.

**exception:** Cancellation prevented native admission.

**exception:** The scope is stale, non-top, failed, busy or closed.

## `M:NativeWeave.Database.Savepoint.RollbackAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task NativeWeave.Database.Savepoint.RollbackAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Rolls back to the current top savepoint and retains it for a subsequent release.

**param cancellationToken:** Cancellation observed before native admission.

**returns:** A task completing after the provider confirms savepoint recovery.

**exception:** Cancellation prevented native admission.

**exception:** The scope is stale, non-top, busy, closed or the provider rejected recovery.

## `M:NativeWeave.Database.TransactionOptions.#ctor`

```csharp
public NativeWeave.Database.TransactionOptions.TransactionOptions()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Database.TransactionOptions.Equals(NativeWeave.Database.TransactionOptions)`

```csharp
public bool NativeWeave.Database.TransactionOptions.Equals(NativeWeave.Database.TransactionOptions? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Database.TransactionOptions.Equals(System.Object)`

```csharp
public override bool NativeWeave.Database.TransactionOptions.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Database.TransactionOptions.GetHashCode`

```csharp
public override int NativeWeave.Database.TransactionOptions.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Database.TransactionOptions.ToString`

```csharp
public override string NativeWeave.Database.TransactionOptions.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Database.TransactionOptions.op_Equality(NativeWeave.Database.TransactionOptions,NativeWeave.Database.TransactionOptions)`

```csharp
public static bool NativeWeave.Database.TransactionOptions.operator ==(NativeWeave.Database.TransactionOptions? left, NativeWeave.Database.TransactionOptions? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Database.TransactionOptions.op_Inequality(NativeWeave.Database.TransactionOptions,NativeWeave.Database.TransactionOptions)`

```csharp
public static bool NativeWeave.Database.TransactionOptions.operator !=(NativeWeave.Database.TransactionOptions? left, NativeWeave.Database.TransactionOptions? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Database.TransactionOptions.{Clone}$`

```csharp
public NativeWeave.Database.TransactionOptions NativeWeave.Database.TransactionOptions.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Database.TransactionOutcome.#ctor`

```csharp
public NativeWeave.Database.TransactionOutcome.TransactionOutcome()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Database.TransactionState.#ctor`

```csharp
public NativeWeave.Database.TransactionState.TransactionState()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `P:NativeWeave.Database.CommitOutcomeUnknownException.Outcome`

```csharp
public NativeWeave.Database.TransactionOutcome NativeWeave.Database.CommitOutcomeUnknownException.Outcome { get; }
```

**summary:** Gets Unknown independently from whether local resources have since retired.

## `P:NativeWeave.Database.CommitOutcomeUnknownException.RecoveryKey`

```csharp
public string? NativeWeave.Database.CommitOutcomeUnknownException.RecoveryKey { get; }
```

**summary:** Gets the optional immutable application recovery identity.

## `P:NativeWeave.Database.DataTransaction.Cleanup`

```csharp
public NativeWeave.Database.DatabaseCloseResult NativeWeave.Database.DataTransaction.Cleanup { get; }
```

**summary:** Gets an immutable cleanup snapshot; Pending never claims that native access has stopped.

## `P:NativeWeave.Database.DataTransaction.CommitAttempted`

```csharp
public bool NativeWeave.Database.DataTransaction.CommitAttempted { get; }
```

**summary:** Gets whether commit crossed the conservative native dispatch boundary.

## `P:NativeWeave.Database.DataTransaction.Outcome`

```csharp
public NativeWeave.Database.TransactionOutcome NativeWeave.Database.DataTransaction.Outcome { get; }
```

**summary:** Gets the native knowledge of durable effects; Unknown is not cleared by disposal.

## `P:NativeWeave.Database.DataTransaction.RecoveryKey`

```csharp
public string? NativeWeave.Database.DataTransaction.RecoveryKey { get; }
```

**summary:** Gets the optional application recovery identity retained after disposal.

## `P:NativeWeave.Database.DataTransaction.State`

```csharp
public NativeWeave.Database.TransactionState NativeWeave.Database.DataTransaction.State { get; }
```

**summary:** Gets transaction admission state independently from native local retirement.

## `P:NativeWeave.Database.DatabaseCleanupException.Cleanup`

```csharp
public NativeWeave.Database.DatabaseCloseResult NativeWeave.Database.DatabaseCleanupException.Cleanup { get; }
```

**summary:** Gets the immutable cleanup result; it never reclassifies an earlier uncertain commit.

## `P:NativeWeave.Database.DatabaseCloseResult.ConnectionDisposition`

```csharp
public NativeWeave.Database.ConnectionDisposition NativeWeave.Database.DatabaseCloseResult.ConnectionDisposition { get; init; }
```

**summary:** Whether protocol and session state permit reuse.

## `P:NativeWeave.Database.DatabaseCloseResult.CorrelationId`

```csharp
public ulong NativeWeave.Database.DatabaseCloseResult.CorrelationId { get; init; }
```

**summary:** The native diagnostic identity, or zero when absent.

## `P:NativeWeave.Database.DatabaseCloseResult.DeadlineExceeded`

```csharp
public bool NativeWeave.Database.DatabaseCloseResult.DeadlineExceeded { get; init; }
```

**summary:** Whether a cleanup deadline elapsed; expiration never forces resource release.

## `P:NativeWeave.Database.DatabaseCloseResult.LocalResourcesReleased`

```csharp
public bool NativeWeave.Database.DatabaseCloseResult.LocalResourcesReleased { get; init; }
```

**summary:** Whether native accessors and retained parent/callback obligations have actually retired.

## `P:NativeWeave.Database.DatabaseCloseResult.ProviderCode`

```csharp
public string? NativeWeave.Database.DatabaseCloseResult.ProviderCode { get; init; }
```

**summary:** A bounded provider code without credentials or parameter values.

## `P:NativeWeave.Database.DatabaseCloseResult.Status`

```csharp
public NativeWeave.Database.CleanupStatus NativeWeave.Database.DatabaseCloseResult.Status { get; init; }
```

**summary:** The cleanup result, independent from transaction effects.

## `P:NativeWeave.Database.TransactionOptions.CleanupTimeout`

```csharp
public System.TimeSpan NativeWeave.Database.TransactionOptions.CleanupTimeout { get; init; }
```

**summary:** Gets the finite, nonnegative cleanup deadline, initially five seconds; native access may outlive its expiration.

## `P:NativeWeave.Database.TransactionOptions.CommitTimeout`

```csharp
public System.TimeSpan NativeWeave.Database.TransactionOptions.CommitTimeout { get; init; }
```

**summary:** Gets the finite, nonnegative commit deadline, initially thirty seconds.

## `P:NativeWeave.Database.TransactionOptions.Isolation`

```csharp
public System.Data.IsolationLevel? NativeWeave.Database.TransactionOptions.Isolation { get; init; }
```

**summary:** Gets the exact requested isolation, or null for the provider default: PostgreSQL ReadCommitted or ordinary SQLite Serializable.

## `P:NativeWeave.Database.TransactionOptions.ReadOnly`

```csharp
public bool NativeWeave.Database.TransactionOptions.ReadOnly { get; init; }
```

**summary:** Gets whether the provider must enforce read-only transaction mode; unsupported profiles reject this request.

## `P:NativeWeave.Database.TransactionOptions.RecoveryKey`

```csharp
public string? NativeWeave.Database.TransactionOptions.RecoveryKey { get; init; }
```

**summary:** Gets an optional application recovery identity; it is retained after disposal and never authorizes automatic retry.

## `T:NativeWeave.Database.CleanupStatus`

```csharp
public enum NativeWeave.Database.CleanupStatus : int
```

**summary:** The result of cleanup, distinct from transaction outcome.

## `T:NativeWeave.Database.CommitOutcomeUnknownException`

```csharp
public sealed class NativeWeave.Database.CommitOutcomeUnknownException : NativeWeave.Data.DatabaseException
```

**summary:** A possible commit without trustworthy confirmation; cleanup and cancellation do not prove rollback.

**remarks:** Recovery uses the original application identity. This exception never suggests a blind transaction replay.

## `T:NativeWeave.Database.ConnectionDisposition`

```csharp
public enum NativeWeave.Database.ConnectionDisposition : int
```

**summary:** Provider evidence about reuse of the original physical session.

## `T:NativeWeave.Database.DataTransaction`

```csharp
public sealed class NativeWeave.Database.DataTransaction : System.IDisposable, System.IAsyncDisposable
```

**summary:** An exclusive native transaction scope with separately observed outcome and cleanup.

**remarks:** The owning DataSession creates this scope after native BEGIN confirms. Ordinary calls share one session command gate; concurrent transaction operations reject rather than queue. No reflection, managed SQL, physical pool or business retry is introduced. Snapshots and recovery identity survive disposal. The engine retains cleanup work until safety is established, even when a bounded CloseAsync waiter returns Pending.

## `T:NativeWeave.Database.DatabaseCleanupException`

```csharp
public sealed class NativeWeave.Database.DatabaseCleanupException : NativeWeave.Data.DatabaseException
```

**summary:** An explicit rollback/cleanup failure, retaining the cleanup observation separately from business errors.

## `T:NativeWeave.Database.DatabaseCloseResult`

```csharp
public sealed record class NativeWeave.Database.DatabaseCloseResult : System.IEquatable<NativeWeave.Database.DatabaseCloseResult>
```

**summary:** An immutable cleanup observation that remains usable after native resources retire.

**param Status:** The cleanup result, independent from transaction effects.

**param CorrelationId:** The native diagnostic identity, or zero when absent.

**param ProviderCode:** A bounded provider code without credentials or parameter values.

**param LocalResourcesReleased:** Whether native accessors and retained parent/callback obligations have actually retired.

**param ConnectionDisposition:** Whether protocol and session state permit reuse.

**param DeadlineExceeded:** Whether a cleanup deadline elapsed; expiration never forces resource release.

## `T:NativeWeave.Database.Savepoint`

```csharp
public sealed class NativeWeave.Database.Savepoint : System.IAsyncDisposable
```

**summary:** A transaction-generation-bound native savepoint; only the current top may execute an action.

**remarks:** The provider uses its tracked same-session nesting. There is no user SQL identifier or connection escape hatch. Releasing the scope never commits the outer transaction.

## `T:NativeWeave.Database.TransactionOptions`

```csharp
public sealed record class NativeWeave.Database.TransactionOptions : System.IEquatable<NativeWeave.Database.TransactionOptions>
```

**summary:** Exact transaction policy, snapshotted before native BEGIN admission.

**remarks:** No engine is initialized by this value. Unsupported isolation or read-only requests fail before BEGIN. Commit and cleanup deadlines are finite and independent from an HTTP/request cancellation token.

## `T:NativeWeave.Database.TransactionOutcome`

```csharp
public enum NativeWeave.Database.TransactionOutcome : int
```

**summary:** Knowledge of database effects, independent from operation completion and local disposal.

## `T:NativeWeave.Database.TransactionState`

```csharp
public enum NativeWeave.Database.TransactionState : int
```

**summary:** Transaction admission state; a closed transaction can still retain native cleanup obligations.

