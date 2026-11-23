# Korda.Database migration API

Generated from the compiled product assembly and XML by `src/Build.Integration/ApiReference`.

Compiled product API compared to Design commit c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9. B08 owns explicit migration and catalog administration; docs/guides/data-migrations.md describes limits and provider semantics.

## `F:Korda.Database.BulkAtomicity.All`

```csharp
Korda.Database.BulkAtomicity.All = 0
```

**summary:** Owns a transaction, or an internal savepoint inside the caller's transaction.

## `F:Korda.Database.BulkAtomicity.PerBatch`

```csharp
Korda.Database.BulkAtomicity.PerBatch = 1
```

**summary:** Commits each complete batch independently; rejected inside a caller transaction.

## `F:Korda.Database.DatabaseProvider.PostgreSql`

```csharp
Korda.Database.DatabaseProvider.PostgreSql = 0
```

**summary:** The libpq PostgreSQL provider.

## `F:Korda.Database.DatabaseProvider.Sqlite`

```csharp
Korda.Database.DatabaseProvider.Sqlite = 1
```

**summary:** The serialized sqlite3 provider.

## `F:Korda.Database.MigrationChangeKind.AddColumn`

```csharp
Korda.Database.MigrationChangeKind.AddColumn = 1
```

**summary:** Adds one nullable, nongenerated, nonkey column.

## `F:Korda.Database.MigrationChangeKind.CreateTable`

```csharp
Korda.Database.MigrationChangeKind.CreateTable = 0
```

**summary:** Creates a new table with explicit columns and primary key.

## `F:Korda.Database.MigrationChangeKind.DropColumn`

```csharp
Korda.Database.MigrationChangeKind.DropColumn = 2
```

**summary:** Drops a nonkey column and requires individual developer approval.

## `F:Korda.Database.MigrationChangeKind.DropTable`

```csharp
Korda.Database.MigrationChangeKind.DropTable = 3
```

**summary:** Drops a table and requires individual developer approval.

## `F:Korda.Database.MigrationTransactionMode.NonTransactional`

```csharp
Korda.Database.MigrationTransactionMode.NonTransactional = 1
```

**summary:** Requires PostgreSQL and an explicit opt-in; partial effects can remain after failure.

## `F:Korda.Database.MigrationTransactionMode.Transactional`

```csharp
Korda.Database.MigrationTransactionMode.Transactional = 0
```

**summary:** Commits the script and its ledger atomically; SQLite also wraps the entire selected batch.

## `F:Korda.Query.FrameBoundary.CurrentRow`

```csharp
Korda.Query.FrameBoundary.CurrentRow = 1
```

**summary:** Uses the native CurrentRow policy.

**inheritdoc:**

**Inherited type policy:** Only these ROWS endpoints are supported. Reversed or unsupported bounds fail before native state publication; finite numeric PRECEDING/FOLLOWING frames are not declared.

## `F:Korda.Query.FrameBoundary.UnboundedFollowing`

```csharp
Korda.Query.FrameBoundary.UnboundedFollowing = 2
```

**summary:** Uses the native UnboundedFollowing policy.

**inheritdoc:**

**Inherited type policy:** Only these ROWS endpoints are supported. Reversed or unsupported bounds fail before native state publication; finite numeric PRECEDING/FOLLOWING frames are not declared.

## `F:Korda.Query.FrameBoundary.UnboundedPreceding`

```csharp
Korda.Query.FrameBoundary.UnboundedPreceding = 0
```

**summary:** Uses the native UnboundedPreceding policy.

**inheritdoc:**

**Inherited type policy:** Only these ROWS endpoints are supported. Reversed or unsupported bounds fail before native state publication; finite numeric PRECEDING/FOLLOWING frames are not declared.

## `F:Korda.Query.LockWait.NoWait`

```csharp
Korda.Query.LockWait.NoWait = 1
```

**summary:** Uses the native NoWait policy.

**inheritdoc:**

**Inherited type policy:** Controls PostgreSQL row-lock conflicts within the same transaction and total query deadline; it does not enable transparent retries.

## `F:Korda.Query.LockWait.SkipLocked`

```csharp
Korda.Query.LockWait.SkipLocked = 2
```

**summary:** Uses the native SkipLocked policy.

**inheritdoc:**

**Inherited type policy:** Controls PostgreSQL row-lock conflicts within the same transaction and total query deadline; it does not enable transparent retries.

## `F:Korda.Query.LockWait.Wait`

```csharp
Korda.Query.LockWait.Wait = 0
```

**summary:** Uses the native Wait policy.

**inheritdoc:**

**Inherited type policy:** Controls PostgreSQL row-lock conflicts within the same transaction and total query deadline; it does not enable transparent retries.

## `F:Korda.Query.NullOrder.First`

```csharp
Korda.Query.NullOrder.First = 0
```

**summary:** Uses the native First policy.

**inheritdoc:**

**Inherited type policy:** Both providers render explicit SQL NULL placement. Ordering alone supplies no uniqueness or snapshot guarantee.

## `F:Korda.Query.NullOrder.Last`

```csharp
Korda.Query.NullOrder.Last = 1
```

**summary:** Uses the native Last policy.

**inheritdoc:**

**Inherited type policy:** Both providers render explicit SQL NULL placement. Ordering alone supplies no uniqueness or snapshot guarantee.

## `F:Korda.Query.RowLockMode.KeyShare`

```csharp
Korda.Query.RowLockMode.KeyShare = 3
```

**summary:** Uses the native KeyShare policy.

**inheritdoc:**

**Inherited type policy:** Available only on PostgreSQL with an explicit live transaction. SQLite rejects the operation before provider dispatch.

## `F:Korda.Query.RowLockMode.NoKeyUpdate`

```csharp
Korda.Query.RowLockMode.NoKeyUpdate = 2
```

**summary:** Uses the native NoKeyUpdate policy.

**inheritdoc:**

**Inherited type policy:** Available only on PostgreSQL with an explicit live transaction. SQLite rejects the operation before provider dispatch.

## `F:Korda.Query.RowLockMode.Share`

```csharp
Korda.Query.RowLockMode.Share = 1
```

**summary:** Uses the native Share policy.

**inheritdoc:**

**Inherited type policy:** Available only on PostgreSQL with an explicit live transaction. SQLite rejects the operation before provider dispatch.

## `F:Korda.Query.RowLockMode.Update`

```csharp
Korda.Query.RowLockMode.Update = 0
```

**summary:** Uses the native Update policy.

**inheritdoc:**

**Inherited type policy:** Available only on PostgreSQL with an explicit live transaction. SQLite rejects the operation before provider dispatch.

## `F:Korda.Query.SetOperation.Except`

```csharp
Korda.Query.SetOperation.Except = 3
```

**summary:** Uses the native Except policy.

**inheritdoc:**

**Inherited type policy:** Operands must have the same source, field order, aliases, codecs and nullability. The native engine snapshots the right operand; SQL duplicate and NULL semantics apply.

## `F:Korda.Query.SetOperation.Intersect`

```csharp
Korda.Query.SetOperation.Intersect = 2
```

**summary:** Uses the native Intersect policy.

**inheritdoc:**

**Inherited type policy:** Operands must have the same source, field order, aliases, codecs and nullability. The native engine snapshots the right operand; SQL duplicate and NULL semantics apply.

## `F:Korda.Query.SetOperation.Union`

```csharp
Korda.Query.SetOperation.Union = 0
```

**summary:** Uses the native Union policy.

**inheritdoc:**

**Inherited type policy:** Operands must have the same source, field order, aliases, codecs and nullability. The native engine snapshots the right operand; SQL duplicate and NULL semantics apply.

## `F:Korda.Query.SetOperation.UnionAll`

```csharp
Korda.Query.SetOperation.UnionAll = 1
```

**summary:** Uses the native UnionAll policy.

**inheritdoc:**

**Inherited type policy:** Operands must have the same source, field order, aliases, codecs and nullability. The native engine snapshots the right operand; SQL duplicate and NULL semantics apply.

## `F:Korda.Query.SortDirection.Ascending`

```csharp
Korda.Query.SortDirection.Ascending = 0
```

**summary:** Uses the native Ascending policy.

**inheritdoc:**

**Inherited type policy:** The native query validates each enum value before mutation. Direction is explicit; it does not change SQL NULL ordering.

## `F:Korda.Query.SortDirection.Descending`

```csharp
Korda.Query.SortDirection.Descending = 1
```

**summary:** Uses the native Descending policy.

**inheritdoc:**

**Inherited type policy:** The native query validates each enum value before mutation. Direction is explicit; it does not change SQL NULL ordering.

## `M:Korda.Database.BulkAtomicity.#ctor`

```csharp
public Korda.Database.BulkAtomicity.BulkAtomicity()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:Korda.Database.BulkFailure.TryGetProgress(System.Exception,Korda.Database.BulkProgress@)`

```csharp
public static bool Korda.Database.BulkFailure.TryGetProgress(System.Exception exception, [System.Diagnostics.CodeAnalysis.NotNullWhenAttribute(true)] out Korda.Database.BulkProgress? progress)
```

**summary:** Reads only the exact bulk-progress entry on the supplied exception.

**param exception:** The original exception to inspect; inner exceptions are not traversed.

**param progress:** The immutable progress when present, otherwise null.

**returns:** True only when this exact exception carries a valid bulk-progress record.

**exception:** is null.

**Inherited type policy:** The helper reads one exact typed Exception.Data entry without native calls, allocation of a wrapper, recursive error traversal or inference from cancellation. The returned snapshot can outlive all native owners.

## `M:Korda.Database.BulkOptions.#ctor`

```csharp
public Korda.Database.BulkOptions.BulkOptions()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This managed configuration owns no native resource. A bulk operation reads its immutable values at admission, then lowers the requested row count to source byte, node and parameter limits.

## `M:Korda.Database.BulkOptions.Equals(Korda.Database.BulkOptions)`

```csharp
public bool Korda.Database.BulkOptions.Equals(Korda.Database.BulkOptions? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This managed configuration owns no native resource. A bulk operation reads its immutable values at admission, then lowers the requested row count to source byte, node and parameter limits.

## `M:Korda.Database.BulkOptions.Equals(System.Object)`

```csharp
public override bool Korda.Database.BulkOptions.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This managed configuration owns no native resource. A bulk operation reads its immutable values at admission, then lowers the requested row count to source byte, node and parameter limits.

## `M:Korda.Database.BulkOptions.GetHashCode`

```csharp
public override int Korda.Database.BulkOptions.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This managed configuration owns no native resource. A bulk operation reads its immutable values at admission, then lowers the requested row count to source byte, node and parameter limits.

## `M:Korda.Database.BulkOptions.ToString`

```csharp
public override string Korda.Database.BulkOptions.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This managed configuration owns no native resource. A bulk operation reads its immutable values at admission, then lowers the requested row count to source byte, node and parameter limits.

## `M:Korda.Database.BulkOptions.op_Equality(Korda.Database.BulkOptions,Korda.Database.BulkOptions)`

```csharp
public static bool Korda.Database.BulkOptions.operator ==(Korda.Database.BulkOptions? left, Korda.Database.BulkOptions? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This managed configuration owns no native resource. A bulk operation reads its immutable values at admission, then lowers the requested row count to source byte, node and parameter limits.

## `M:Korda.Database.BulkOptions.op_Inequality(Korda.Database.BulkOptions,Korda.Database.BulkOptions)`

```csharp
public static bool Korda.Database.BulkOptions.operator !=(Korda.Database.BulkOptions? left, Korda.Database.BulkOptions? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This managed configuration owns no native resource. A bulk operation reads its immutable values at admission, then lowers the requested row count to source byte, node and parameter limits.

## `M:Korda.Database.BulkOptions.{Clone}$`

```csharp
public Korda.Database.BulkOptions Korda.Database.BulkOptions.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This managed configuration owns no native resource. A bulk operation reads its immutable values at admission, then lowers the requested row count to source byte, node and parameter limits.

## `M:Korda.Database.BulkProgress.#ctor(System.Int64,System.Int32,System.Boolean,Korda.Database.TransactionOutcome)`

```csharp
public Korda.Database.BulkProgress.BulkProgress(long RowsAffectedInCompletedBatches, int CompletedBatches, bool CompletedBatchesCommitted, Korda.Database.TransactionOutcome CurrentTransactionOutcome)
```

**summary:** Immutable progress at failure, distinct from uncertain current-batch effects or later rollback.

**param RowsAffectedInCompletedBatches:** Affected rows only in fully acknowledged preceding batches.

**param CompletedBatches:** The fully acknowledged batch count.

**param CompletedBatchesCommitted:** True only for confirmed preceding PerBatch commits.

**param CurrentTransactionOutcome:** Actual transaction knowledge, retaining Unknown independently.

**remarks:** The original admitted bulk exception retains this managed snapshot after cleanup. Counts describe previously completed batches, which All may have rolled back; they do not prove current-batch effects or authorize replay. The record owns no native resource and remains readable after session/source disposal.

**Inherited type policy:** The original admitted bulk exception retains this managed snapshot after cleanup. Counts describe previously completed batches, which All may have rolled back; they do not prove current-batch effects or authorize replay. The record owns no native resource and remains readable after session/source disposal.

## `M:Korda.Database.BulkProgress.Deconstruct(System.Int64@,System.Int32@,System.Boolean@,Korda.Database.TransactionOutcome@)`

```csharp
public void Korda.Database.BulkProgress.Deconstruct(out long RowsAffectedInCompletedBatches, out int CompletedBatches, out bool CompletedBatchesCommitted, out Korda.Database.TransactionOutcome CurrentTransactionOutcome)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The original admitted bulk exception retains this managed snapshot after cleanup. Counts describe previously completed batches, which All may have rolled back; they do not prove current-batch effects or authorize replay. The record owns no native resource and remains readable after session/source disposal.

## `M:Korda.Database.BulkProgress.Equals(Korda.Database.BulkProgress)`

```csharp
public bool Korda.Database.BulkProgress.Equals(Korda.Database.BulkProgress? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The original admitted bulk exception retains this managed snapshot after cleanup. Counts describe previously completed batches, which All may have rolled back; they do not prove current-batch effects or authorize replay. The record owns no native resource and remains readable after session/source disposal.

## `M:Korda.Database.BulkProgress.Equals(System.Object)`

```csharp
public override bool Korda.Database.BulkProgress.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The original admitted bulk exception retains this managed snapshot after cleanup. Counts describe previously completed batches, which All may have rolled back; they do not prove current-batch effects or authorize replay. The record owns no native resource and remains readable after session/source disposal.

## `M:Korda.Database.BulkProgress.GetHashCode`

```csharp
public override int Korda.Database.BulkProgress.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The original admitted bulk exception retains this managed snapshot after cleanup. Counts describe previously completed batches, which All may have rolled back; they do not prove current-batch effects or authorize replay. The record owns no native resource and remains readable after session/source disposal.

## `M:Korda.Database.BulkProgress.ToString`

```csharp
public override string Korda.Database.BulkProgress.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The original admitted bulk exception retains this managed snapshot after cleanup. Counts describe previously completed batches, which All may have rolled back; they do not prove current-batch effects or authorize replay. The record owns no native resource and remains readable after session/source disposal.

## `M:Korda.Database.BulkProgress.op_Equality(Korda.Database.BulkProgress,Korda.Database.BulkProgress)`

```csharp
public static bool Korda.Database.BulkProgress.operator ==(Korda.Database.BulkProgress? left, Korda.Database.BulkProgress? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The original admitted bulk exception retains this managed snapshot after cleanup. Counts describe previously completed batches, which All may have rolled back; they do not prove current-batch effects or authorize replay. The record owns no native resource and remains readable after session/source disposal.

## `M:Korda.Database.BulkProgress.op_Inequality(Korda.Database.BulkProgress,Korda.Database.BulkProgress)`

```csharp
public static bool Korda.Database.BulkProgress.operator !=(Korda.Database.BulkProgress? left, Korda.Database.BulkProgress? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The original admitted bulk exception retains this managed snapshot after cleanup. Counts describe previously completed batches, which All may have rolled back; they do not prove current-batch effects or authorize replay. The record owns no native resource and remains readable after session/source disposal.

## `M:Korda.Database.BulkProgress.{Clone}$`

```csharp
public Korda.Database.BulkProgress Korda.Database.BulkProgress.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The original admitted bulk exception retains this managed snapshot after cleanup. Counts describe previously completed batches, which All may have rolled back; they do not prove current-batch effects or authorize replay. The record owns no native resource and remains readable after session/source disposal.

## `M:Korda.Database.BulkResult.#ctor(System.Int64,System.Int32,System.Boolean)`

```csharp
public Korda.Database.BulkResult.BulkResult(long RowsAffected, int Batches, bool CommittedByOperation)
```

**summary:** The acknowledged result of all admitted batches.

**param RowsAffected:** The sum of acknowledged provider counts.

**param Batches:** The number of fully acknowledged batches.

**param CommittedByOperation:** True only when this operation confirmed its own commit or commits.

**remarks:** The owned immutable result contains only acknowledged counts and an ownership observation. Constructing it allocates one managed record; it retains no session, transaction, cursor or native buffer.

**Inherited type policy:** The owned immutable result contains only acknowledged counts and an ownership observation. Constructing it allocates one managed record; it retains no session, transaction, cursor or native buffer.

## `M:Korda.Database.BulkResult.Deconstruct(System.Int64@,System.Int32@,System.Boolean@)`

```csharp
public void Korda.Database.BulkResult.Deconstruct(out long RowsAffected, out int Batches, out bool CommittedByOperation)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The owned immutable result contains only acknowledged counts and an ownership observation. Constructing it allocates one managed record; it retains no session, transaction, cursor or native buffer.

## `M:Korda.Database.BulkResult.Equals(Korda.Database.BulkResult)`

```csharp
public bool Korda.Database.BulkResult.Equals(Korda.Database.BulkResult? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The owned immutable result contains only acknowledged counts and an ownership observation. Constructing it allocates one managed record; it retains no session, transaction, cursor or native buffer.

## `M:Korda.Database.BulkResult.Equals(System.Object)`

```csharp
public override bool Korda.Database.BulkResult.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The owned immutable result contains only acknowledged counts and an ownership observation. Constructing it allocates one managed record; it retains no session, transaction, cursor or native buffer.

## `M:Korda.Database.BulkResult.GetHashCode`

```csharp
public override int Korda.Database.BulkResult.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The owned immutable result contains only acknowledged counts and an ownership observation. Constructing it allocates one managed record; it retains no session, transaction, cursor or native buffer.

## `M:Korda.Database.BulkResult.ToString`

```csharp
public override string Korda.Database.BulkResult.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The owned immutable result contains only acknowledged counts and an ownership observation. Constructing it allocates one managed record; it retains no session, transaction, cursor or native buffer.

## `M:Korda.Database.BulkResult.op_Equality(Korda.Database.BulkResult,Korda.Database.BulkResult)`

```csharp
public static bool Korda.Database.BulkResult.operator ==(Korda.Database.BulkResult? left, Korda.Database.BulkResult? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The owned immutable result contains only acknowledged counts and an ownership observation. Constructing it allocates one managed record; it retains no session, transaction, cursor or native buffer.

## `M:Korda.Database.BulkResult.op_Inequality(Korda.Database.BulkResult,Korda.Database.BulkResult)`

```csharp
public static bool Korda.Database.BulkResult.operator !=(Korda.Database.BulkResult? left, Korda.Database.BulkResult? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The owned immutable result contains only acknowledged counts and an ownership observation. Constructing it allocates one managed record; it retains no session, transaction, cursor or native buffer.

## `M:Korda.Database.BulkResult.{Clone}$`

```csharp
public Korda.Database.BulkResult Korda.Database.BulkResult.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The owned immutable result contains only acknowledged counts and an ownership observation. Constructing it allocates one managed record; it retains no session, transaction, cursor or native buffer.

## `M:Korda.Database.ConflictTarget`1.#ctor(System.String)`

```csharp
public Korda.Database.ConflictTarget<TEntity>.ConflictTarget(string constraintName)
```

**summary:** Creates an explicit named PostgreSQL constraint target.

**param constraintName:** A nonempty exact store constraint name.

**exception:** is null.

**exception:** The name is empty, invalid or exceeds its finite UTF8 capacity.

**Inherited type policy:** Construction performs no schema query. The native database checks that the selected unique constraint exists.

## `M:Korda.Database.ConflictTarget`1.And``1(Korda.Data.Column{`0,``0})`

```csharp
public Korda.Database.ConflictTarget<TEntity> Korda.Database.ConflictTarget<TEntity>.And<T>(Korda.Data.Column<TEntity, T> column)
```

**summary:** Returns a new target with an additional unique column in the stated order.

**typeparam T:** The column's exact value type.

**param column:** A new column of the same built table.

**returns:** A new immutable target; this instance is unchanged.

**exception:** is null.

**exception:** The target is named, or the column is foreign, repeated or unbuilt.

**Inherited type policy:** Construction performs no schema query. The native database checks that the selected unique constraint exists.

## `M:Korda.Database.ConflictTarget`1.Columns``1(Korda.Data.Column{`0,``0})`

```csharp
public static Korda.Database.ConflictTarget<TEntity> Korda.Database.ConflictTarget<TEntity>.Columns<T>(Korda.Data.Column<TEntity, T> column)
```

**summary:** Starts an immutable portable conflict target at one built table column.

**typeparam T:** The column's exact value type.

**param column:** A column from the target table.

**returns:** A new one-column target.

**exception:** is null.

**exception:** The table has not been built.

**Inherited type policy:** Construction performs no schema query. The native database checks that the selected unique constraint exists.

## `M:Korda.Database.DataSession.BeginTransactionAsync(Korda.Database.TransactionOptions,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<Korda.Database.DataTransaction> Korda.Database.DataSession.BeginTransactionAsync(Korda.Database.TransactionOptions options, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Begins an exact native transaction without weakening unsupported isolation or read-only policy.

**param options:** Immutable isolation, effect and independent commit/cleanup deadline settings.

**param cancellationToken:** Controls admission before BEGIN dispatch.

**returns:** The transaction owner, whose outcome is independent from local cleanup.

**exception:** is null.

**exception:** An option is invalid.

**exception:** The provider or admission fails.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:Korda.Database.DataSession.BeginTransactionAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<Korda.Database.DataTransaction> Korda.Database.DataSession.BeginTransactionAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Begins a native transaction using the provider's default isolation.

**param cancellationToken:** Controls admission before BEGIN dispatch.

**returns:** The native transaction owner.

**exception:** Admission is closed.

**exception:** The provider or single-command admission rejects BEGIN.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:Korda.Database.DataSession.CloseAsync(System.TimeSpan,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<Korda.Database.DatabaseCloseResult> Korda.Database.DataSession.CloseAsync(System.TimeSpan waitBudget, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Starts closure, then waits within an independent finite caller budget.

**param waitBudget:** A nonnegative finite wait; zero observes without waiting.

**param cancellationToken:** Cancels only the wait after native cleanup starts.

**returns:** A truthful snapshot; timeout cannot imply native retirement.

**exception:** The budget is invalid.

**exception:** The caller stops waiting.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:Korda.Database.DataSession.DeleteAsync``1(Korda.Data.Table{``0},Korda.Data.EntityKey{``0},System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<long> Korda.Database.DataSession.DeleteAsync<TEntity>(Korda.Data.Table<TEntity> table, Korda.Data.EntityKey<TEntity> key, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where TEntity : class
```

**summary:** Deletes by the entire native primary-key predicate with no read-before-write query.

**typeparam TEntity:** The statically described entity class.

**param table:** The exact table.

**param key:** The complete ordered primary key.

**param cancellationToken:** Requests cancellation under the existing transaction policy.

**returns:** Zero or one affected row; multiple matches are a schema constraint failure.

**exception:** The key is invalid.

**exception:** Provider, constraint or capacity fails.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:Korda.Database.DataSession.DeleteCheckedAsync``1(Korda.Data.Table{``0},Korda.Data.EntityKey{``0},Korda.Data.ConcurrencyCheck{``0},System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<long> Korda.Database.DataSession.DeleteCheckedAsync<TEntity>(Korda.Data.Table<TEntity> table, Korda.Data.EntityKey<TEntity> key, Korda.Data.ConcurrencyCheck<TEntity> concurrency, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where TEntity : class
```

**summary:** Deletes once using the complete key and every explicit expected concurrency token.

**typeparam TEntity:** The statically described entity class.

**param table:** The exact table.

**param key:** The complete ordered primary key.

**param concurrency:** The complete expected-token snapshot.

**param cancellationToken:** Requests cancellation and waits for native retirement.

**returns:** Exactly one acknowledged affected row.

**exception:** No row matched the key and expected tokens.

**exception:** Provider, schema or capacity fails.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:Korda.Database.DataSession.Dispose`

```csharp
public void Korda.Database.DataSession.Dispose()
```

**summary:** Closes admission and transfers cleanup to the bounded native owner without blocking.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:Korda.Database.DataSession.DisposeAsync`

```csharp
public System.Threading.Tasks.ValueTask Korda.Database.DataSession.DisposeAsync()
```

**summary:** Closes admission and joins actual native local safety.

**returns:** The asynchronous safety join.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:Korda.Database.DataSession.ExecuteAsync(Korda.Data.SqlCommand,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<long> Korda.Database.DataSession.ExecuteAsync(Korda.Data.SqlCommand command, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Executes one trusted mutation or explicit session-changing command and observes its affected-row count.

**param command:** An immutable typed command with a nonquery effect classification.

**param cancellationToken:** Requests cancellation; accepted work retains resources until native retirement.

**returns:** The exact affected-row count.

**exception:** The command kind is incompatible.

**exception:** is null.

**exception:** Session or source admission is closed.

**exception:** The provider, capacity or concurrent-use policy rejects execution.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:Korda.Database.DataSession.FindAsync``1(Korda.Data.Table{``0},Korda.Data.EntityKey{``0},System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<TEntity?> Korda.Database.DataSession.FindAsync<TEntity>(Korda.Data.Table<TEntity> table, Korda.Data.EntityKey<TEntity> key, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where TEntity : class
```

**summary:** Executes one native complete-key SELECT and returns the sole row or null.

**typeparam TEntity:** The statically mapped entity class.

**param table:** The exact immutable table descriptor.

**param key:** Every declared key component, including any tenant key.

**param cancellationToken:** Requests cancellation and joins native retirement.

**returns:** The owned row or null; duplicate key-shaped results throw.

**exception:** The key is incomplete or belongs to another table.

**exception:** The declared key matched multiple rows.

**exception:** Shape, codec, provider or capacity fails.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:Korda.Database.DataSession.From``1(Korda.Data.Table{``0})`

```csharp
public Korda.Query.Query<TEntity> Korda.Database.DataSession.From<TEntity>(Korda.Data.Table<TEntity> table) where TEntity : class
```

**summary:** Creates one mutable native query arena bound to this session and exact static table.

**typeparam TEntity:** The statically described entity class.

**param table:** The immutable manual or generated table descriptor.

**returns:** A query to dispose after its terminal or enumeration completes.

**exception:** is null.

**exception:** Session or source admission is closed.

**exception:** The native metadata, capability or capacity rejects construction.

**remarks:** The returned query owns one native arena and borrows this session's admission. Composition copies values immediately; dispose the query after its terminal or stream has retired.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:Korda.Database.DataSession.InTransactionAsync(System.Func{Korda.Database.DataSession,System.Threading.CancellationToken,System.Threading.Tasks.Task},Korda.Database.TransactionOptions,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task Korda.Database.DataSession.InTransactionAsync(System.Func<Korda.Database.DataSession, System.Threading.CancellationToken, System.Threading.Tasks.Task> action, Korda.Database.TransactionOptions? options = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Runs one awaited callback in a new transaction with confirmed commit and independent cleanup.

**param action:** The asynchronous callback, invoked once after BEGIN confirmation.

**param options:** Explicit isolation, recovery identity and independent deadlines; null selects defaults.

**param cancellationToken:** Controls BEGIN, callback operations and pre-dispatch commit admission.

**returns:** A task completing after confirmed commit and local retirement.

**exception:** is null.

**exception:** The session already has an active transaction.

**exception:** Cancellation prevents admission or is observed by the callback.

**exception:** Commit may have happened without trustworthy confirmation.

**exception:** Native admission, execution, commit or cleanup fails.

**remarks:** Ownership, cancellation and original-error preservation are identical to the result-returning overload. The callback awaits all operations and leaves transaction completion to this method.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:Korda.Database.DataSession.InTransactionAsync``1(System.Func{Korda.Database.DataSession,System.Threading.CancellationToken,System.Threading.Tasks.Task{``0}},Korda.Database.TransactionOptions,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<T> Korda.Database.DataSession.InTransactionAsync<T>(System.Func<Korda.Database.DataSession, System.Threading.CancellationToken, System.Threading.Tasks.Task<T>> action, Korda.Database.TransactionOptions? options = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Runs one awaited callback in a new transaction and returns only after a confirmed commit and cleanup.

**typeparam T:** The owned callback result type.

**param action:** The asynchronous callback, invoked once after BEGIN confirmation.

**param options:** Explicit isolation, recovery identity and independent deadlines; null selects defaults.

**param cancellationToken:** Controls BEGIN, callback operations and pre-dispatch commit admission.

**returns:** The callback result after confirmed commit and local retirement.

**exception:** is null.

**exception:** The session already has an active transaction.

**exception:** Cancellation prevents admission or the callback observes cancellation.

**exception:** Commit may have happened without trustworthy confirmation.

**exception:** Native admission, execution, commit or cleanup fails.

**remarks:** The callback must await its operations and must not commit, roll back or dispose this session's transaction. The session retains its single-command concurrency policy. Callback failure requests rollback through independent native cleanup and preserves the original exception, with a TransactionFailure snapshot. Cancellation after commit admission cannot become a false rollback or cancellation receipt. No callback replay, ambient transaction, nested transaction or savepoint is introduced.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:Korda.Database.DataSession.InsertAsync``1(Korda.Data.Table{``0},``0,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<long> Korda.Database.DataSession.InsertAsync<TEntity>(Korda.Data.Table<TEntity> table, TEntity entity, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where TEntity : class
```

**summary:** Snapshots one entity, omits generated columns and executes one native insert.

**typeparam TEntity:** The entity class with its direct writer.

**param table:** The exact immutable table.

**param entity:** The entity to copy before dispatch; it is never mutated.

**param cancellationToken:** Requests cancellation without inferring rollback of a dispatched write.

**returns:** The provider's affected-row count.

**exception:** A required argument is null.

**exception:** The writer or value shape is invalid.

**exception:** A native constraint, provider or finite capacity fails.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:Korda.Database.DataSession.InsertManyAsync``1(Korda.Data.Table{``0},System.Collections.Generic.IAsyncEnumerable{``0},Korda.Database.BulkOptions,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<Korda.Database.BulkResult> Korda.Database.DataSession.InsertManyAsync<TEntity>(Korda.Data.Table<TEntity> table, System.Collections.Generic.IAsyncEnumerable<TEntity> rows, Korda.Database.BulkOptions? options = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where TEntity : class
```

**summary:** Incrementally snapshots and writes finite native batches under explicit transaction ownership.

**typeparam TEntity:** The statically written entity class.

**param table:** The exact immutable table.

**param rows:** A one-pass input, snapshotted before its next MoveNext.

**param options:** Finite batch size and All or PerBatch ownership; null selects defaults.

**param cancellationToken:** Controls input and write admission; cleanup uses an independent token.

**returns:** Acknowledged rows and batches; caller-owned transactions are never reported as committed.

**exception:** A required argument is null.

**exception:** The option is invalid.

**exception:** PerBatch is requested inside a caller transaction.

**exception:** An owned commit crossed dispatch but its outcome is unknown.

**exception:** Cancellation is confirmed after bounded transaction cleanup.

**remarks:** All inside a caller transaction creates a tracked savepoint before input consumption. Every admitted failure preserves its original type and attaches BulkProgress. Prior PerBatch commits are not rolled back.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:Korda.Database.DataSession.InsertReturningAsync``2(Korda.Data.Table{``0},``0,Korda.Data.Projection{``1},System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<T> Korda.Database.DataSession.InsertReturningAsync<TEntity, T>(Korda.Data.Table<TEntity> table, TEntity entity, Korda.Data.Projection<T> returning, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where TEntity : class
```

**summary:** Snapshots one entity and materializes exactly one native INSERT RETURNING row.

**typeparam TEntity:** The statically written entity class.

**typeparam T:** The statically materialized return type.

**param table:** The exact table.

**param entity:** The entity snapshot; generated values are omitted.

**param returning:** Exact existing table columns and codecs in the desired output order.

**param cancellationToken:** Requests cancellation and joins the returned cursor.

**returns:** The single owned returned value.

**exception:** A required argument is null.

**exception:** The returned cardinality differs from one.

**exception:** Capability, output shape, provider or capacity fails.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:Korda.Database.DataSession.LoadRelatedAsync``2(Korda.Data.EntityKey{``0},Korda.Data.Relation{``0,``1},System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<TChild>> Korda.Database.DataSession.LoadRelatedAsync<TParent, TChild>(Korda.Data.EntityKey<TParent> parentKey, Korda.Data.Relation<TParent, TChild> relation, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where TParent : class where TChild : class
```

**summary:** Snapshots one complete parent key and runs one bounded native child foreign-key query.

**typeparam TParent:** The declared parent entity class.

**typeparam TChild:** The statically materialized child class.

**param parentKey:** Every non-null primary-key component in declaration order.

**param relation:** An immutable post-Build relationship description.

**param cancellationToken:** Requests cancellation and waits for actual cursor retirement.

**returns:** An owned bounded child list, without an implicit ordering or parent-existence query.

**exception:** A required argument is null.

**exception:** A key, model, codec or relation shape differs.

**exception:** Capability, capacity, provider or concurrent-use admission fails.

**exception:** Cancellation is confirmed after native cleanup.

**remarks:** NULL foreign-key components match no parent. Additional tenant filtering must be explicit in a Fluent query.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:Korda.Database.DataSession.LoadRelatedAsync``2(System.Collections.Generic.IReadOnlyList{Korda.Data.EntityKey{``0}},Korda.Data.Relation{``0,``1},System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<System.Collections.Generic.IReadOnlyList<TChild>>> Korda.Database.DataSession.LoadRelatedAsync<TParent, TChild>(System.Collections.Generic.IReadOnlyList<Korda.Data.EntityKey<TParent>> parentKeys, Korda.Data.Relation<TParent, TChild> relation, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where TParent : class where TChild : class
```

**summary:** Loads one bounded parent batch in one native SQL execution and returns index-aligned child groups.

**typeparam TParent:** The declared parent entity class.

**typeparam TChild:** The statically materialized child class.

**param parentKeys:** Complete immutable keys in page order; repeated keys retain separate result positions.

**param relation:** The complete immutable primary-key/foreign-key mapping.

**param cancellationToken:** Requests cancellation and waits for actual cursor retirement.

**returns:** One owned child list per input key, including empty lists; no child order is implied.

**exception:** An argument or key is null.

**exception:** A key is incomplete, foreign or incompatible with the relation.

**exception:** The input batch, total result, native query or provider exceeds its admitted limits.

**exception:** Cancellation is observed with cursor cleanup.

**remarks:** The collection is snapshotted before dispatch and may contain at most MaxBatchRows keys. Empty input executes no SQL. Each nonempty input becomes one parameterized native UNION ALL statement; provider preparation and protocol exchanges are separate from SQL execution count. Native predicates preserve composite-key and collation semantics. One result stream bounds all groups together, including duplicate keys; there is no hidden query per parent, parent-existence check, retry or automatic split.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:Korda.Database.DataSession.QueryAsync``1(Korda.Data.SqlCommand,Korda.Data.Projection{``0},System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<T>> Korda.Database.DataSession.QueryAsync<T>(Korda.Data.SqlCommand command, Korda.Data.Projection<T> projection, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Materializes a bounded list using the complete declared row shape.

**typeparam T:** The statically mapped owned result type.

**param command:** An immutable query command.

**param projection:** Exact ordered aliases, codecs and a synchronous materializer.

**param cancellationToken:** The native execution cancellation token.

**returns:** An owned list within both row and encoded-byte budgets.

**exception:** A required argument is null.

**exception:** The command kind is incompatible.

**exception:** Shape, conversion, provider or configured capacity fails.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:Korda.Database.DataSession.ScalarAsync``1(Korda.Data.SqlCommand,Korda.Data.ValueCodec{``0},System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<T> Korda.Database.DataSession.ScalarAsync<T>(Korda.Data.SqlCommand command, Korda.Data.ValueCodec<T> codec, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Reads exactly one row and column through an explicit scalar codec.

**typeparam T:** The owned scalar type, including an explicitly nullable codec.

**param command:** An immutable query command.

**param codec:** The exact scalar codec; no first-column truncation is performed.

**param cancellationToken:** The native execution cancellation token.

**returns:** The sole scalar value.

**exception:** The command or codec is null.

**exception:** The command kind is incompatible.

**exception:** Zero or multiple rows are returned.

**exception:** The complete shape, codec, provider or capacity fails.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:Korda.Database.DataSession.StreamAsync``1(Korda.Data.SqlCommand,Korda.Data.Projection{``0},System.Threading.CancellationToken)`

```csharp
public System.Collections.Generic.IAsyncEnumerable<T> Korda.Database.DataSession.StreamAsync<T>(Korda.Data.SqlCommand command, Korda.Data.Projection<T> projection, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Creates a one-shot bounded stream; the first MoveNext admits native execution.

**typeparam T:** The statically mapped owned result type.

**param command:** An immutable query command.

**param projection:** Exact ordered result shape and synchronous materializer.

**param cancellationToken:** Combined with the enumeration token for native execution.

**returns:** A one-shot stream whose enumerator must be disposed, including on early exit.

**exception:** A required argument is null.

**exception:** The command kind is incompatible.

**exception:** Session or source admission is closed.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:Korda.Database.DataSession.UpdateAsync``1(Korda.Data.Table{``0},Korda.Data.EntityKey{``0},Korda.Data.Changes{``0},System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<long> Korda.Database.DataSession.UpdateAsync<TEntity>(Korda.Data.Table<TEntity> table, Korda.Data.EntityKey<TEntity> key, Korda.Data.Changes<TEntity> changes, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where TEntity : class
```

**summary:** Applies only explicit nonkey assignments under the entire native key predicate.

**typeparam TEntity:** The statically described entity class.

**param table:** The exact table.

**param key:** The complete ordered primary key.

**param changes:** Owned assignment snapshots; Unset is omitted and nullable Set assigns SQL NULL.

**param cancellationToken:** Requests cancellation under the existing write-outcome policy.

**returns:** Zero or one affected row; multiple matches are a schema constraint failure.

**exception:** The key or assignments are invalid.

**exception:** Provider, schema or capacity fails.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:Korda.Database.DataSession.UpdateCheckedAsync``1(Korda.Data.Table{``0},Korda.Data.EntityKey{``0},Korda.Data.Changes{``0},Korda.Data.ConcurrencyCheck{``0},System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<long> Korda.Database.DataSession.UpdateCheckedAsync<TEntity>(Korda.Data.Table<TEntity> table, Korda.Data.EntityKey<TEntity> key, Korda.Data.Changes<TEntity> changes, Korda.Data.ConcurrencyCheck<TEntity> concurrency, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where TEntity : class
```

**summary:** Executes one key-and-version compare-and-swap without an implicit version increment or replay.

**typeparam TEntity:** The statically described entity class.

**param table:** The exact table.

**param key:** Every primary-key component, retaining tenant scope.

**param changes:** Explicit assignments including a caller-supplied new version when desired.

**param concurrency:** Every declared expected concurrency token.

**param cancellationToken:** Requests cancellation without fabricating a write outcome.

**returns:** Exactly one acknowledged affected row.

**exception:** No row matched the exact key and expected tokens.

**exception:** The key, assignments or checks are invalid.

**exception:** The schema, provider or capacity fails.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:Korda.Database.DataSession.UpsertAsync``1(Korda.Data.Table{``0},``0,Korda.Database.ConflictTarget{``0},Korda.Data.Changes{``0},System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<long> Korda.Database.DataSession.UpsertAsync<TEntity>(Korda.Data.Table<TEntity> table, TEntity entity, Korda.Database.ConflictTarget<TEntity> conflict, Korda.Data.Changes<TEntity> update, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) where TEntity : class
```

**summary:** Executes one atomic native upsert with independent insert and conflict-update snapshots.

**typeparam TEntity:** The statically described entity class.

**param table:** The exact table.

**param entity:** The insert row, excluding generated columns.

**param conflict:** An explicit column or PostgreSQL constraint target.

**param update:** A separate nonempty assignment snapshot; values may differ from the inserted row.

**param cancellationToken:** Requests cancellation under the existing write-outcome policy.

**returns:** The provider's affected-row count.

**exception:** A required argument is null.

**exception:** The target is unsupported by this provider.

**exception:** The actual unique constraint, provider or capacity fails.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:Korda.Database.DataSource.CanConnectAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<bool> Korda.Database.DataSource.CanConnectAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Opens and closes a real session, returning false only for provider connection failures.

**param cancellationToken:** The connection test cancellation token.

**returns:** True when a native session was established; false for a provider connection failure.

**exception:** Admission is closed.

**exception:** Cancellation is confirmed.

**remarks:** Configuration, loading, resource, panic and bridge failures retain their exception types.

**Inherited type policy:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

## `M:Korda.Database.DataSource.CloseAsync(System.TimeSpan,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<Korda.Database.DatabaseCloseResult> Korda.Database.DataSource.CloseAsync(System.TimeSpan waitBudget, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Starts closure and waits only within the caller's independent budget.

**param waitBudget:** A finite nonnegative wait; zero returns the current observation.

**param cancellationToken:** Cancels this wait while native cleanup continues.

**returns:** A truthful cleanup observation; expired waits report Pending and unreleased local resources.

**exception:** The budget is invalid.

**exception:** The wait is canceled after closure starts.

**Inherited type policy:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

## `M:Korda.Database.DataSource.Dispose`

```csharp
public void Korda.Database.DataSource.Dispose()
```

**summary:** Closes admission and requests bounded native cleanup without blocking for I/O.

**Inherited type policy:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

## `M:Korda.Database.DataSource.DisposeAsync`

```csharp
public System.Threading.Tasks.ValueTask Korda.Database.DataSource.DisposeAsync()
```

**summary:** Closes admission and joins actual local safety, even beyond an application policy deadline.

**returns:** The native cleanup join.

**Inherited type policy:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

## `M:Korda.Database.DataSource.OpenSessionAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<Korda.Database.DataSession> Korda.Database.DataSource.OpenSessionAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Acquires an independent physical session through bounded native pool admission.

**param cancellationToken:** Cancels admission or requests accepted native work to stop.

**returns:** A session that must be disposed; its ownership is independent from this operation.

**exception:** The source has closed admission.

**exception:** Admission or the provider fails.

**exception:** The native operation confirms cancellation.

**Inherited type policy:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

## `M:Korda.Database.DataSourceBuilder.#ctor`

```csharp
public Korda.Database.DataSourceBuilder.DataSourceBuilder()
```

**summary:** Creates an unconfigured builder without loading a module.

**Inherited type policy:** Configuration snapshots values. Build negotiates the packaged module and creates a lazy native pool; it opens no database connection. Independent builds create independent source generations.

## `M:Korda.Database.DataSourceBuilder.Build`

```csharp
public Korda.Database.DataSource Korda.Database.DataSourceBuilder.Build()
```

**summary:** Negotiates the selected precompiled engine and creates an independently owned lazy pool.

**returns:** A source to dispose asynchronously when its sessions have finished.

**exception:** No provider was configured.

**exception:** The packaged module fails preflight.

**exception:** The native provider rejects its immutable configuration.

**remarks:** This call starts an engine-local runtime but performs no connection or migration I/O.

**Inherited type policy:** Configuration snapshots values. Build negotiates the packaged module and creates a lazy native pool; it opens no database connection. Independent builds create independent source generations.

## `M:Korda.Database.DataSourceBuilder.ConfigureLimits(Korda.Database.DatabaseLimits)`

```csharp
public Korda.Database.DataSourceBuilder Korda.Database.DataSourceBuilder.ConfigureLimits(Korda.Database.DatabaseLimits limits)
```

**summary:** Copies and validates finite resource limits for subsequent builds.

**param limits:** The immutable limit settings.

**returns:** This builder.

**exception:** is null.

**exception:** A value exceeds the native finite profile.

**Inherited type policy:** Configuration snapshots values. Build negotiates the packaged module and creates a lazy native pool; it opens no database connection. Independent builds create independent source generations.

## `M:Korda.Database.DataSourceBuilder.WithName(System.String)`

```csharp
public Korda.Database.DataSourceBuilder Korda.Database.DataSourceBuilder.WithName(string name)
```

**summary:** Sets the diagnostic source identity without including connection credentials.

**param name:** A nonempty strict-Unicode name of at most 256 UTF-8 bytes.

**returns:** This builder.

**exception:** The name is empty, contains NUL, or is invalid Unicode.

**exception:** is null.

**exception:** The name exceeds its byte limit.

**Inherited type policy:** Configuration snapshots values. Build negotiates the packaged module and creates a lazy native pool; it opens no database connection. Independent builds create independent source generations.

## `M:Korda.Database.DatabaseAdministration.DraftMigrationAsync(Korda.Database.DatabaseModel,Korda.Database.DatabaseModel,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<Korda.Database.MigrationDraft> Korda.Database.DatabaseAdministration.DraftMigrationAsync(Korda.Database.DatabaseModel previous, Korda.Database.DatabaseModel current, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Compiles an explicit snapshot difference to a reviewable native SQL draft without opening a connection.

**param previous:** The versioned previously applied model snapshot; use Empty for a new database.

**param current:** The desired explicitly declared model snapshot.

**param cancellationToken:** Requests cancellation while bounded native compilation retires.

**returns:** A detached draft; no SQL is executed and no migration history is read.

**exception:** A model is null.

**exception:** A model or difference is unsupported, ambiguous or exceeds source limits.

**exception:** The loaded native module lacks schema draft support.

**exception:** Cancellation is confirmed after native retirement.

**remarks:** Supports table creation, nullable column addition and explicitly approved drops. Type, key, nullability and other existing-column alterations reject with the offending table/column; use reviewed explicit SQL for backfills, conversions, defaults and provider-specific rebuilds. Source creation never calls this method.

**Inherited type policy:** Migration and catalog operations use one independent administrative connection at a time per source, with bounded waiting and closure before task retirement. Draft compilation opens no connection. None uses a request session or transaction. Result counts, strings and nested columns share source buffering and field limits.

## `M:Korda.Database.DatabaseAdministration.GetMigrationStatusAsync(Korda.Database.MigrationBundle,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<Korda.Database.MigrationStatus>> Korda.Database.DatabaseAdministration.GetMigrationStatusAsync(Korda.Database.MigrationBundle bundle, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Reads an immutable comparison of this bundle and the successful native ledger.

**param bundle:** The exact provider-bound bundle, including previously applied entries.

**param cancellationToken:** The cancellation request retained through connection cleanup.

**returns:** Statuses in strictly ascending version order.

**exception:** The bundle/provider is invalid.

**exception:** History, checksum, provider or result limits fail.

**exception:** Cancellation is confirmed after cleanup.

**Inherited type policy:** Migration and catalog operations use one independent administrative connection at a time per source, with bounded waiting and closure before task retirement. Draft compilation opens no connection. None uses a request session or transaction. Result counts, strings and nested columns share source buffering and field limits.

## `M:Korda.Database.DatabaseAdministration.GetTablesAsync(System.String,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<Korda.Database.SchemaTable>> Korda.Database.DatabaseAdministration.GetTablesAsync(string? schema = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Reads bounded table and column metadata with native parameterized catalog queries.

**param schema:** A user-schema identifier; null selects PostgreSQL current_schema or SQLite main.

**param cancellationToken:** The cancellation request retained through cleanup.

**returns:** Immutable tables with columns in catalog order.

**exception:** The schema scope is invalid or names a system/attached schema.

**exception:** Provider or nested buffering/field limits fail.

**exception:** Cancellation is confirmed after cleanup.

**remarks:** This reads catalog metadata only. It performs no schema difference inference or automatic model creation.

**Inherited type policy:** Migration and catalog operations use one independent administrative connection at a time per source, with bounded waiting and closure before task retirement. Draft compilation opens no connection. None uses a request session or transaction. Result counts, strings and nested columns share source buffering and field limits.

## `M:Korda.Database.DatabaseAdministration.MigrateAsync(Korda.Database.MigrationBundle,Korda.Database.MigrationOptions,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<Korda.Database.MigrationReport> Korda.Database.DatabaseAdministration.MigrateAsync(Korda.Database.MigrationBundle bundle, Korda.Database.MigrationOptions? options = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Applies the selected forward migration range using the native provider and the retained checksum ledger.

**param bundle:** An immutable trusted bundle for this source's provider.

**param options:** Target and explicit nontransactional policy; null uses transactional defaults.

**param cancellationToken:** Requests cancellation while retained native work and connection closure complete.

**returns:** A confirmed completed report; a failed migration throws.

**exception:** The bundle/provider/options are invalid.

**exception:** Provider, checksum, dirty phase or finite result capacity fails.

**exception:** The native provider/profile does not support the requested migration mode.

**exception:** Cancellation is confirmed after native cleanup.

**remarks:** PostgreSQL commits each transactional entry independently. SQLite commits the entire selected batch in one tracked IMMEDIATE transaction. Nontransactional PostgreSQL preserves partial effects and an applying phase; only a new explicit identical call may resume a replay-safe script. Cancellation or connection closure never proves that a previously dispatched business commit did not happen.

**Inherited type policy:** Migration and catalog operations use one independent administrative connection at a time per source, with bounded waiting and closure before task retirement. Draft compilation opens no connection. None uses a request session or transaction. Result counts, strings and nested columns share source buffering and field limits.

## `M:Korda.Database.DatabaseAdministration.ValidateMigrationsAsync(Korda.Database.MigrationBundle,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task Korda.Database.DatabaseAdministration.ValidateMigrationsAsync(Korda.Database.MigrationBundle bundle, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Validates checksum, missing/extra history and dirty execution phases without changing schema.

**param bundle:** The complete trusted manifest expected in the database.

**param cancellationToken:** The cancellation request retained through cleanup.

**returns:** A task completing only after validation and dedicated connection retirement.

**exception:** The manifest/provider is invalid.

**exception:** The ledger is incompatible, dirty, inaccessible or exceeds bounds.

**exception:** Cancellation is confirmed after cleanup.

**Inherited type policy:** Migration and catalog operations use one independent administrative connection at a time per source, with bounded waiting and closure before task retirement. Draft compilation opens no connection. None uses a request session or transaction. Result counts, strings and nested columns share source buffering and field limits.

## `M:Korda.Database.DatabaseCapabilities.Supports(System.String)`

```csharp
public bool Korda.Database.DatabaseCapabilities.Supports(string capability)
```

**summary:** Tests one case-sensitive stable capability name without a native call.

**param capability:** The requested name; an unknown or empty name returns false.

**returns:** True only if the actual module advertised the capability.

**exception:** is null.

**Inherited type policy:** Unknown names return false. A capability name does not authorize a different provider or an arbitrary SQL feature.

## `M:Korda.Database.DatabaseLimits.#ctor`

```csharp
public Korda.Database.DatabaseLimits.DatabaseLimits()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `M:Korda.Database.DatabaseLimits.Equals(Korda.Database.DatabaseLimits)`

```csharp
public bool Korda.Database.DatabaseLimits.Equals(Korda.Database.DatabaseLimits? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `M:Korda.Database.DatabaseLimits.Equals(System.Object)`

```csharp
public override bool Korda.Database.DatabaseLimits.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `M:Korda.Database.DatabaseLimits.GetHashCode`

```csharp
public override int Korda.Database.DatabaseLimits.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `M:Korda.Database.DatabaseLimits.ToString`

```csharp
public override string Korda.Database.DatabaseLimits.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `M:Korda.Database.DatabaseLimits.op_Equality(Korda.Database.DatabaseLimits,Korda.Database.DatabaseLimits)`

```csharp
public static bool Korda.Database.DatabaseLimits.operator ==(Korda.Database.DatabaseLimits? left, Korda.Database.DatabaseLimits? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `M:Korda.Database.DatabaseLimits.op_Inequality(Korda.Database.DatabaseLimits,Korda.Database.DatabaseLimits)`

```csharp
public static bool Korda.Database.DatabaseLimits.operator !=(Korda.Database.DatabaseLimits? left, Korda.Database.DatabaseLimits? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `M:Korda.Database.DatabaseLimits.{Clone}$`

```csharp
public Korda.Database.DatabaseLimits Korda.Database.DatabaseLimits.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `M:Korda.Database.DatabaseModel.Add``1(Korda.Data.Table{``0})`

```csharp
public Korda.Database.DatabaseModel Korda.Database.DatabaseModel.Add<TEntity>(Korda.Data.Table<TEntity> table) where TEntity : class
```

**summary:** Returns a new snapshot containing one additional immutable table.

**typeparam TEntity:** The ordinary application entity type.

**param table:** A generated or manually declared table.

**returns:** A snapshot independent of this instance and future builder changes.

**exception:** is null.

**exception:** The exact store name repeats or the finite snapshot budget is exceeded.

**Inherited type policy:** Construction and persistence perform no native load or database I/O. The snapshot contains store names, codec policies and declared key/generated flags, but no inferred indexes, foreign keys, defaults or backfills. Provider validation occurs when a draft is requested. Keep the previously applied snapshot in source control.

## `M:Korda.Database.DatabaseModel.Parse(System.String)`

```csharp
public static Korda.Database.DatabaseModel Korda.Database.DatabaseModel.Parse(string json)
```

**summary:** Reads a persisted version-one snapshot without provider access or reflection.

**param json:** A complete snapshot previously produced by .

**returns:** An immutable copy of its embedded table metadata.

**exception:** is null.

**exception:** The JSON, version, names or finite byte/table limits are invalid.

**remarks:** Provider-specific type checks and complete native model validation occur before draft execution. Formatting whitespace is normalized; reordering canonical table properties is not supported.

**Inherited type policy:** Construction and persistence perform no native load or database I/O. The snapshot contains store names, codec policies and declared key/generated flags, but no inferred indexes, foreign keys, defaults or backfills. Provider validation occurs when a draft is requested. Keep the previously applied snapshot in source control.

## `M:Korda.Database.DatabaseModel.ToJson`

```csharp
public string Korda.Database.DatabaseModel.ToJson()
```

**summary:** Serializes this snapshot as version-one JSON with canonical embedded table records.

**returns:** Persistable Unicode JSON; this operation does not inspect the database.

**Inherited type policy:** Construction and persistence perform no native load or database I/O. The snapshot contains store names, codec policies and declared key/generated flags, but no inferred indexes, foreign keys, defaults or backfills. Provider validation occurs when a draft is requested. Keep the previously applied snapshot in source control.

## `M:Korda.Database.DatabaseProvider.#ctor`

```csharp
public Korda.Database.DatabaseProvider.DatabaseProvider()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:Korda.Database.Migration.#ctor(System.Int64,System.String,System.String,Korda.Database.MigrationTransactionMode)`

```csharp
public Korda.Database.Migration.Migration(long Version, string Description, string Sql, Korda.Database.MigrationTransactionMode TransactionMode = Korda.Database.MigrationTransactionMode.Transactional)
```

**summary:** Defines one trusted, immutable SQL migration.

**remarks:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

**param Version:** A positive version, strictly ascending within its bundle.

**param Description:** A nonempty diagnostic description without credentials.

**param Sql:** Exact Unicode SQL; UTF-8 bytes are checksummed without newline normalization.

**param TransactionMode:** Transactional by default; explicit transaction-control statements are rejected.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `M:Korda.Database.Migration.Deconstruct(System.Int64@,System.String@,System.String@,Korda.Database.MigrationTransactionMode@)`

```csharp
public void Korda.Database.Migration.Deconstruct(out long Version, out string Description, out string Sql, out Korda.Database.MigrationTransactionMode TransactionMode)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `M:Korda.Database.Migration.Equals(Korda.Database.Migration)`

```csharp
public bool Korda.Database.Migration.Equals(Korda.Database.Migration? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `M:Korda.Database.Migration.Equals(System.Object)`

```csharp
public override bool Korda.Database.Migration.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `M:Korda.Database.Migration.GetHashCode`

```csharp
public override int Korda.Database.Migration.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `M:Korda.Database.Migration.ToString`

```csharp
public override string Korda.Database.Migration.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `M:Korda.Database.Migration.op_Equality(Korda.Database.Migration,Korda.Database.Migration)`

```csharp
public static bool Korda.Database.Migration.operator ==(Korda.Database.Migration? left, Korda.Database.Migration? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `M:Korda.Database.Migration.op_Inequality(Korda.Database.Migration,Korda.Database.Migration)`

```csharp
public static bool Korda.Database.Migration.operator !=(Korda.Database.Migration? left, Korda.Database.Migration? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `M:Korda.Database.Migration.{Clone}$`

```csharp
public Korda.Database.Migration Korda.Database.Migration.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `M:Korda.Database.MigrationBundle.#ctor(Korda.Database.DatabaseProvider,System.Collections.Generic.IReadOnlyList{Korda.Database.Migration})`

```csharp
public Korda.Database.MigrationBundle.MigrationBundle(Korda.Database.DatabaseProvider provider, System.Collections.Generic.IReadOnlyList<Korda.Database.Migration> migrations)
```

**summary:** Creates an immutable snapshot and validates versions, strings, modes and finite encoding limits.

**param provider:** The exact provider that may consume the bundle.

**param migrations:** The trusted scripts in strictly ascending positive version order.

**exception:** The collection, an entry or a string is null.

**exception:** Versions, strings, modes or encoded limits are invalid.

**remarks:** The input collection may be changed after return; this instance retains its own snapshot.

**Inherited type policy:** Construction performs no native load or database I/O. Its SHA-256 identity covers the canonical kind12 provider and entry sequence before the hash field is added. Each entry separately contains SQL SHA-256; the native ledger preserves the existing SQLx-compatible SHA-384 checksum. Scripts must be trusted deployment artifacts.

## `M:Korda.Database.MigrationChange.#ctor(System.String,Korda.Database.MigrationChangeKind,System.String,System.String,System.String,System.String,System.Boolean)`

```csharp
public Korda.Database.MigrationChange.MigrationChange(string Id, Korda.Database.MigrationChangeKind Kind, string? Schema, string Table, string? Column, string Sql, bool IsDestructive)
```

**summary:** One detached, immutable, reviewable native schema change.

**remarks:** This record retains bounded managed strings and no native resource. Its SQL has not executed; the owning draft enforces explicit per-change destructive approval before conversion to a migration.

**param Id:** A stable digest of this exact provider-specific operation.

**param Kind:** The explicitly supported change kind.

**param Schema:** The exact schema, or null for the default schema.

**param Table:** The exact table name.

**param Column:** The exact column, or null for a table operation.

**param Sql:** Provider SQL for review, with identifiers safely quoted by the native dialect.

**param IsDestructive:** Whether converting the draft requires this exact change ID's approval.

**Inherited type policy:** This record retains bounded managed strings and no native resource. Its SQL has not executed; the owning draft enforces explicit per-change destructive approval before conversion to a migration.

## `M:Korda.Database.MigrationChange.Deconstruct(System.String@,Korda.Database.MigrationChangeKind@,System.String@,System.String@,System.String@,System.String@,System.Boolean@)`

```csharp
public void Korda.Database.MigrationChange.Deconstruct(out string Id, out Korda.Database.MigrationChangeKind Kind, out string? Schema, out string Table, out string? Column, out string Sql, out bool IsDestructive)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains bounded managed strings and no native resource. Its SQL has not executed; the owning draft enforces explicit per-change destructive approval before conversion to a migration.

## `M:Korda.Database.MigrationChange.Equals(Korda.Database.MigrationChange)`

```csharp
public bool Korda.Database.MigrationChange.Equals(Korda.Database.MigrationChange? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains bounded managed strings and no native resource. Its SQL has not executed; the owning draft enforces explicit per-change destructive approval before conversion to a migration.

## `M:Korda.Database.MigrationChange.Equals(System.Object)`

```csharp
public override bool Korda.Database.MigrationChange.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains bounded managed strings and no native resource. Its SQL has not executed; the owning draft enforces explicit per-change destructive approval before conversion to a migration.

## `M:Korda.Database.MigrationChange.GetHashCode`

```csharp
public override int Korda.Database.MigrationChange.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains bounded managed strings and no native resource. Its SQL has not executed; the owning draft enforces explicit per-change destructive approval before conversion to a migration.

## `M:Korda.Database.MigrationChange.ToString`

```csharp
public override string Korda.Database.MigrationChange.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains bounded managed strings and no native resource. Its SQL has not executed; the owning draft enforces explicit per-change destructive approval before conversion to a migration.

## `M:Korda.Database.MigrationChange.op_Equality(Korda.Database.MigrationChange,Korda.Database.MigrationChange)`

```csharp
public static bool Korda.Database.MigrationChange.operator ==(Korda.Database.MigrationChange? left, Korda.Database.MigrationChange? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains bounded managed strings and no native resource. Its SQL has not executed; the owning draft enforces explicit per-change destructive approval before conversion to a migration.

## `M:Korda.Database.MigrationChange.op_Inequality(Korda.Database.MigrationChange,Korda.Database.MigrationChange)`

```csharp
public static bool Korda.Database.MigrationChange.operator !=(Korda.Database.MigrationChange? left, Korda.Database.MigrationChange? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains bounded managed strings and no native resource. Its SQL has not executed; the owning draft enforces explicit per-change destructive approval before conversion to a migration.

## `M:Korda.Database.MigrationChange.{Clone}$`

```csharp
public Korda.Database.MigrationChange Korda.Database.MigrationChange.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains bounded managed strings and no native resource. Its SQL has not executed; the owning draft enforces explicit per-change destructive approval before conversion to a migration.

## `M:Korda.Database.MigrationChangeKind.#ctor`

```csharp
public Korda.Database.MigrationChangeKind.MigrationChangeKind()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:Korda.Database.MigrationDraft.ToMigration(System.Int64,System.String,System.Collections.Generic.IReadOnlyCollection{System.String})`

```csharp
public Korda.Database.Migration Korda.Database.MigrationDraft.ToMigration(long version, string description, System.Collections.Generic.IReadOnlyCollection<string>? approvedDestructiveChanges = null)
```

**summary:** Converts reviewed SQL to an explicit transactional migration after per-change approvals.

**param version:** A positive deployment version; bundle ordering is checked by MigrationBundle.

**param description:** A nonempty diagnostic description.

**param approvedDestructiveChanges:** Every destructive change ID chosen by the developer; null approves none.

**returns:** A trusted migration for this draft's provider, ready for explicit bundle construction.

**exception:** An approval is unknown or repeated, or version/description is invalid.

**exception:** The draft is empty or a destructive change lacks explicit approval.

**remarks:** All supported changes are included. To omit one, revise the desired model and generate another draft. Unsupported alteration/default/backfill semantics require an independently reviewed handwritten migration.

**Inherited type policy:** The draft owns read-only managed values and no native lease. Review SQL and approve every destructive change ID before converting it to a trusted migration. Applying the resulting migration is a separate explicit call.

## `M:Korda.Database.MigrationOptions.#ctor`

```csharp
public Korda.Database.MigrationOptions.MigrationOptions()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `M:Korda.Database.MigrationOptions.Equals(Korda.Database.MigrationOptions)`

```csharp
public bool Korda.Database.MigrationOptions.Equals(Korda.Database.MigrationOptions? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `M:Korda.Database.MigrationOptions.Equals(System.Object)`

```csharp
public override bool Korda.Database.MigrationOptions.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `M:Korda.Database.MigrationOptions.GetHashCode`

```csharp
public override int Korda.Database.MigrationOptions.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `M:Korda.Database.MigrationOptions.ToString`

```csharp
public override string Korda.Database.MigrationOptions.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `M:Korda.Database.MigrationOptions.op_Equality(Korda.Database.MigrationOptions,Korda.Database.MigrationOptions)`

```csharp
public static bool Korda.Database.MigrationOptions.operator ==(Korda.Database.MigrationOptions? left, Korda.Database.MigrationOptions? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `M:Korda.Database.MigrationOptions.op_Inequality(Korda.Database.MigrationOptions,Korda.Database.MigrationOptions)`

```csharp
public static bool Korda.Database.MigrationOptions.operator !=(Korda.Database.MigrationOptions? left, Korda.Database.MigrationOptions? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `M:Korda.Database.MigrationOptions.{Clone}$`

```csharp
public Korda.Database.MigrationOptions Korda.Database.MigrationOptions.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `M:Korda.Database.MigrationReport.#ctor(System.Int32,System.Nullable{System.Int64},System.Boolean)`

```csharp
public Korda.Database.MigrationReport.MigrationReport(int AppliedCount, long? CurrentVersion, bool Completed)
```

**summary:** Reports a successfully completed explicit migration call.

**remarks:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

**param AppliedCount:** The number applied by this invocation.

**param CurrentVersion:** The last confirmed applied version, or null for an empty database.

**param Completed:** True on success; failures throw and retain their original error.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `M:Korda.Database.MigrationReport.Deconstruct(System.Int32@,System.Nullable{System.Int64}@,System.Boolean@)`

```csharp
public void Korda.Database.MigrationReport.Deconstruct(out int AppliedCount, out long? CurrentVersion, out bool Completed)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `M:Korda.Database.MigrationReport.Equals(Korda.Database.MigrationReport)`

```csharp
public bool Korda.Database.MigrationReport.Equals(Korda.Database.MigrationReport? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `M:Korda.Database.MigrationReport.Equals(System.Object)`

```csharp
public override bool Korda.Database.MigrationReport.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `M:Korda.Database.MigrationReport.GetHashCode`

```csharp
public override int Korda.Database.MigrationReport.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `M:Korda.Database.MigrationReport.ToString`

```csharp
public override string Korda.Database.MigrationReport.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `M:Korda.Database.MigrationReport.op_Equality(Korda.Database.MigrationReport,Korda.Database.MigrationReport)`

```csharp
public static bool Korda.Database.MigrationReport.operator ==(Korda.Database.MigrationReport? left, Korda.Database.MigrationReport? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `M:Korda.Database.MigrationReport.op_Inequality(Korda.Database.MigrationReport,Korda.Database.MigrationReport)`

```csharp
public static bool Korda.Database.MigrationReport.operator !=(Korda.Database.MigrationReport? left, Korda.Database.MigrationReport? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `M:Korda.Database.MigrationReport.{Clone}$`

```csharp
public Korda.Database.MigrationReport Korda.Database.MigrationReport.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `M:Korda.Database.MigrationStatus.#ctor(System.Int64,System.String,System.String,System.Boolean)`

```csharp
public Korda.Database.MigrationStatus.MigrationStatus(long Version, string Description, string Checksum, bool Applied)
```

**summary:** Describes one bounded immutable bundle/ledger comparison.

**remarks:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

**param Version:** The positive migration version.

**param Description:** The immutable bundle description.

**param Checksum:** The lowercase hexadecimal SQLx-compatible SHA-384 checksum, distinct from the bundle hash.

**param Applied:** True only when the successful retained migration ledger contains this exact migration.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `M:Korda.Database.MigrationStatus.Deconstruct(System.Int64@,System.String@,System.String@,System.Boolean@)`

```csharp
public void Korda.Database.MigrationStatus.Deconstruct(out long Version, out string Description, out string Checksum, out bool Applied)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `M:Korda.Database.MigrationStatus.Equals(Korda.Database.MigrationStatus)`

```csharp
public bool Korda.Database.MigrationStatus.Equals(Korda.Database.MigrationStatus? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `M:Korda.Database.MigrationStatus.Equals(System.Object)`

```csharp
public override bool Korda.Database.MigrationStatus.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `M:Korda.Database.MigrationStatus.GetHashCode`

```csharp
public override int Korda.Database.MigrationStatus.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `M:Korda.Database.MigrationStatus.ToString`

```csharp
public override string Korda.Database.MigrationStatus.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `M:Korda.Database.MigrationStatus.op_Equality(Korda.Database.MigrationStatus,Korda.Database.MigrationStatus)`

```csharp
public static bool Korda.Database.MigrationStatus.operator ==(Korda.Database.MigrationStatus? left, Korda.Database.MigrationStatus? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `M:Korda.Database.MigrationStatus.op_Inequality(Korda.Database.MigrationStatus,Korda.Database.MigrationStatus)`

```csharp
public static bool Korda.Database.MigrationStatus.operator !=(Korda.Database.MigrationStatus? left, Korda.Database.MigrationStatus? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `M:Korda.Database.MigrationStatus.{Clone}$`

```csharp
public Korda.Database.MigrationStatus Korda.Database.MigrationStatus.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `M:Korda.Database.MigrationTransactionMode.#ctor`

```csharp
public Korda.Database.MigrationTransactionMode.MigrationTransactionMode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:Korda.Database.SchemaColumn.#ctor(System.String,System.String,System.Boolean,System.Boolean)`

```csharp
public Korda.Database.SchemaColumn.SchemaColumn(string Name, string StoreType, bool IsNullable, bool IsPrimaryKey)
```

**summary:** Describes a provider catalog column without exposing a provider row object.

**remarks:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

**param Name:** The exact store column name.

**param StoreType:** The provider's declared/formatted type.

**param IsNullable:** Whether the provider permits SQL NULL.

**param IsPrimaryKey:** Whether the column participates in a primary key.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `M:Korda.Database.SchemaColumn.Deconstruct(System.String@,System.String@,System.Boolean@,System.Boolean@)`

```csharp
public void Korda.Database.SchemaColumn.Deconstruct(out string Name, out string StoreType, out bool IsNullable, out bool IsPrimaryKey)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `M:Korda.Database.SchemaColumn.Equals(Korda.Database.SchemaColumn)`

```csharp
public bool Korda.Database.SchemaColumn.Equals(Korda.Database.SchemaColumn? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `M:Korda.Database.SchemaColumn.Equals(System.Object)`

```csharp
public override bool Korda.Database.SchemaColumn.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `M:Korda.Database.SchemaColumn.GetHashCode`

```csharp
public override int Korda.Database.SchemaColumn.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `M:Korda.Database.SchemaColumn.ToString`

```csharp
public override string Korda.Database.SchemaColumn.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `M:Korda.Database.SchemaColumn.op_Equality(Korda.Database.SchemaColumn,Korda.Database.SchemaColumn)`

```csharp
public static bool Korda.Database.SchemaColumn.operator ==(Korda.Database.SchemaColumn? left, Korda.Database.SchemaColumn? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `M:Korda.Database.SchemaColumn.op_Inequality(Korda.Database.SchemaColumn,Korda.Database.SchemaColumn)`

```csharp
public static bool Korda.Database.SchemaColumn.operator !=(Korda.Database.SchemaColumn? left, Korda.Database.SchemaColumn? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `M:Korda.Database.SchemaColumn.{Clone}$`

```csharp
public Korda.Database.SchemaColumn Korda.Database.SchemaColumn.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `M:Korda.Database.SchemaColumnHint.#ctor(System.String,System.String,System.String,System.String,System.String)`

```csharp
public Korda.Database.SchemaColumnHint.SchemaColumnHint(string ColumnName, string ClrType, string? CodecDeclaringType = null, string? CodecMember = null, string? PropertyName = null)
```

**summary:** An explicit CLR/codec interpretation for one catalog column.

**param ColumnName:** The exact catalog column to map.

**param ClrType:** An unqualified built-in or qualified CLR type name, without nullability syntax.

**param CodecDeclaringType:** The optional qualified type exposing the configured nonnullable ValueCodec.

**param CodecMember:** Its accessible static field/property; required together with CodecDeclaringType.

**param PropertyName:** An optional valid C# member name; null retains a valid column name or a deterministic fallback.

**remarks:** Configured decimal, JSON, custom IDs and ambiguous provider types need an explicit codec member. The consuming compiler checks its exact type and accessibility. Nullability comes from the catalog.

**Inherited type policy:** Configured decimal, JSON, custom IDs and ambiguous provider types need an explicit codec member. The consuming compiler checks its exact type and accessibility. Nullability comes from the catalog.

## `M:Korda.Database.SchemaColumnHint.Deconstruct(System.String@,System.String@,System.String@,System.String@,System.String@)`

```csharp
public void Korda.Database.SchemaColumnHint.Deconstruct(out string ColumnName, out string ClrType, out string? CodecDeclaringType, out string? CodecMember, out string? PropertyName)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Configured decimal, JSON, custom IDs and ambiguous provider types need an explicit codec member. The consuming compiler checks its exact type and accessibility. Nullability comes from the catalog.

## `M:Korda.Database.SchemaColumnHint.Equals(Korda.Database.SchemaColumnHint)`

```csharp
public bool Korda.Database.SchemaColumnHint.Equals(Korda.Database.SchemaColumnHint? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Configured decimal, JSON, custom IDs and ambiguous provider types need an explicit codec member. The consuming compiler checks its exact type and accessibility. Nullability comes from the catalog.

## `M:Korda.Database.SchemaColumnHint.Equals(System.Object)`

```csharp
public override bool Korda.Database.SchemaColumnHint.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Configured decimal, JSON, custom IDs and ambiguous provider types need an explicit codec member. The consuming compiler checks its exact type and accessibility. Nullability comes from the catalog.

## `M:Korda.Database.SchemaColumnHint.GetHashCode`

```csharp
public override int Korda.Database.SchemaColumnHint.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Configured decimal, JSON, custom IDs and ambiguous provider types need an explicit codec member. The consuming compiler checks its exact type and accessibility. Nullability comes from the catalog.

## `M:Korda.Database.SchemaColumnHint.ToString`

```csharp
public override string Korda.Database.SchemaColumnHint.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Configured decimal, JSON, custom IDs and ambiguous provider types need an explicit codec member. The consuming compiler checks its exact type and accessibility. Nullability comes from the catalog.

## `M:Korda.Database.SchemaColumnHint.op_Equality(Korda.Database.SchemaColumnHint,Korda.Database.SchemaColumnHint)`

```csharp
public static bool Korda.Database.SchemaColumnHint.operator ==(Korda.Database.SchemaColumnHint? left, Korda.Database.SchemaColumnHint? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Configured decimal, JSON, custom IDs and ambiguous provider types need an explicit codec member. The consuming compiler checks its exact type and accessibility. Nullability comes from the catalog.

## `M:Korda.Database.SchemaColumnHint.op_Inequality(Korda.Database.SchemaColumnHint,Korda.Database.SchemaColumnHint)`

```csharp
public static bool Korda.Database.SchemaColumnHint.operator !=(Korda.Database.SchemaColumnHint? left, Korda.Database.SchemaColumnHint? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Configured decimal, JSON, custom IDs and ambiguous provider types need an explicit codec member. The consuming compiler checks its exact type and accessibility. Nullability comes from the catalog.

## `M:Korda.Database.SchemaColumnHint.{Clone}$`

```csharp
public Korda.Database.SchemaColumnHint Korda.Database.SchemaColumnHint.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Configured decimal, JSON, custom IDs and ambiguous provider types need an explicit codec member. The consuming compiler checks its exact type and accessibility. Nullability comes from the catalog.

## `M:Korda.Database.SchemaScaffoldOptions.#ctor(System.String,System.String)`

```csharp
public Korda.Database.SchemaScaffoldOptions.SchemaScaffoldOptions(string Namespace, string TypeName)
```

**summary:** Explicit decisions for generating one ordinary C# entity from catalog v1.

**param Namespace:** The namespace for emitted source, using ordinary identifier segments.

**param TypeName:** The entity class name.

**remarks:** Catalog v1 omits ordered composite keys, generated flags and defaults. Supply the exact generated column list, including an explicit empty list when none are generated, and a complete composite-key order.

**Inherited type policy:** Catalog v1 omits ordered composite keys, generated flags and defaults. Supply the exact generated column list, including an explicit empty list when none are generated, and a complete composite-key order.

## `M:Korda.Database.SchemaScaffoldOptions.Deconstruct(System.String@,System.String@)`

```csharp
public void Korda.Database.SchemaScaffoldOptions.Deconstruct(out string Namespace, out string TypeName)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Catalog v1 omits ordered composite keys, generated flags and defaults. Supply the exact generated column list, including an explicit empty list when none are generated, and a complete composite-key order.

## `M:Korda.Database.SchemaScaffoldOptions.Equals(Korda.Database.SchemaScaffoldOptions)`

```csharp
public bool Korda.Database.SchemaScaffoldOptions.Equals(Korda.Database.SchemaScaffoldOptions? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Catalog v1 omits ordered composite keys, generated flags and defaults. Supply the exact generated column list, including an explicit empty list when none are generated, and a complete composite-key order.

## `M:Korda.Database.SchemaScaffoldOptions.Equals(System.Object)`

```csharp
public override bool Korda.Database.SchemaScaffoldOptions.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Catalog v1 omits ordered composite keys, generated flags and defaults. Supply the exact generated column list, including an explicit empty list when none are generated, and a complete composite-key order.

## `M:Korda.Database.SchemaScaffoldOptions.GetHashCode`

```csharp
public override int Korda.Database.SchemaScaffoldOptions.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Catalog v1 omits ordered composite keys, generated flags and defaults. Supply the exact generated column list, including an explicit empty list when none are generated, and a complete composite-key order.

## `M:Korda.Database.SchemaScaffoldOptions.ToString`

```csharp
public override string Korda.Database.SchemaScaffoldOptions.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Catalog v1 omits ordered composite keys, generated flags and defaults. Supply the exact generated column list, including an explicit empty list when none are generated, and a complete composite-key order.

## `M:Korda.Database.SchemaScaffoldOptions.op_Equality(Korda.Database.SchemaScaffoldOptions,Korda.Database.SchemaScaffoldOptions)`

```csharp
public static bool Korda.Database.SchemaScaffoldOptions.operator ==(Korda.Database.SchemaScaffoldOptions? left, Korda.Database.SchemaScaffoldOptions? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Catalog v1 omits ordered composite keys, generated flags and defaults. Supply the exact generated column list, including an explicit empty list when none are generated, and a complete composite-key order.

## `M:Korda.Database.SchemaScaffoldOptions.op_Inequality(Korda.Database.SchemaScaffoldOptions,Korda.Database.SchemaScaffoldOptions)`

```csharp
public static bool Korda.Database.SchemaScaffoldOptions.operator !=(Korda.Database.SchemaScaffoldOptions? left, Korda.Database.SchemaScaffoldOptions? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Catalog v1 omits ordered composite keys, generated flags and defaults. Supply the exact generated column list, including an explicit empty list when none are generated, and a complete composite-key order.

## `M:Korda.Database.SchemaScaffoldOptions.{Clone}$`

```csharp
public Korda.Database.SchemaScaffoldOptions Korda.Database.SchemaScaffoldOptions.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Catalog v1 omits ordered composite keys, generated flags and defaults. Supply the exact generated column list, including an explicit empty list when none are generated, and a complete composite-key order.

## `M:Korda.Database.SchemaScaffolder.Generate(Korda.Database.DatabaseProvider,Korda.Database.SchemaTable,Korda.Database.SchemaScaffoldOptions)`

```csharp
public static Korda.Database.SchemaScaffoldResult Korda.Database.SchemaScaffolder.Generate(Korda.Database.DatabaseProvider provider, Korda.Database.SchemaTable table, Korda.Database.SchemaScaffoldOptions options)
```

**summary:** Generates one ordinary class using the public static-mapping attributes.

**param provider:** The provider that produced the catalog record.

**param table:** The detached catalog table, including its exact columns.

**param options:** Explicit names, generated flags, key order and type/codec hints.

**returns:** Compilable source after supplied custom symbols are resolved, plus explicit catalog limitations.

**exception:** A required argument or collection is null.

**exception:** Names, keys, hints, missing generated decisions or finite budgets are invalid.

**remarks:** Nullable primary keys reject because the static model contract cannot represent them. SQLite storage classes map to their literal CLR storage types by default; JSON, dates, custom IDs and money require hints.

**Inherited type policy:** This synchronous pure tool performs no database I/O, compilation, native loading or schema alteration. It does not reconstruct migrations or infer absent catalog metadata. Generated source must be reviewed and compiled.

## `M:Korda.Database.SchemaTable.#ctor(System.String,System.String,System.Collections.Generic.IReadOnlyList{Korda.Database.SchemaColumn})`

```csharp
public Korda.Database.SchemaTable.SchemaTable(string? Schema, string Name, System.Collections.Generic.IReadOnlyList<Korda.Database.SchemaColumn> Columns)
```

**summary:** Describes a table and an immutable, ordinally ordered column snapshot.

**remarks:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

**param Schema:** The admitted provider schema; SQLite reports main.

**param Name:** The exact table name.

**param Columns:** The bounded columns in native catalog ordinal order.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

## `M:Korda.Database.SchemaTable.Deconstruct(System.String@,System.String@,System.Collections.Generic.IReadOnlyList{Korda.Database.SchemaColumn}@)`

```csharp
public void Korda.Database.SchemaTable.Deconstruct(out string? Schema, out string Name, out System.Collections.Generic.IReadOnlyList<Korda.Database.SchemaColumn> Columns)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

## `M:Korda.Database.SchemaTable.Equals(Korda.Database.SchemaTable)`

```csharp
public bool Korda.Database.SchemaTable.Equals(Korda.Database.SchemaTable? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

## `M:Korda.Database.SchemaTable.Equals(System.Object)`

```csharp
public override bool Korda.Database.SchemaTable.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

## `M:Korda.Database.SchemaTable.GetHashCode`

```csharp
public override int Korda.Database.SchemaTable.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

## `M:Korda.Database.SchemaTable.ToString`

```csharp
public override string Korda.Database.SchemaTable.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

## `M:Korda.Database.SchemaTable.op_Equality(Korda.Database.SchemaTable,Korda.Database.SchemaTable)`

```csharp
public static bool Korda.Database.SchemaTable.operator ==(Korda.Database.SchemaTable? left, Korda.Database.SchemaTable? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

## `M:Korda.Database.SchemaTable.op_Inequality(Korda.Database.SchemaTable,Korda.Database.SchemaTable)`

```csharp
public static bool Korda.Database.SchemaTable.operator !=(Korda.Database.SchemaTable? left, Korda.Database.SchemaTable? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

## `M:Korda.Database.SchemaTable.{Clone}$`

```csharp
public Korda.Database.SchemaTable Korda.Database.SchemaTable.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

## `M:Korda.Database.TransactionFailure.#ctor(Korda.Database.TransactionOutcome,System.Boolean,Korda.Database.DatabaseCloseResult,System.String)`

```csharp
public Korda.Database.TransactionFailure.TransactionFailure(Korda.Database.TransactionOutcome Outcome, bool CommitAttempted, Korda.Database.DatabaseCloseResult Cleanup, string? RecoveryKey)
```

**summary:** An immutable transaction callback failure observation, retained after native cleanup.

**param Outcome:** Observed durable-effect knowledge; Unknown never implies rollback.

**param CommitAttempted:** Whether commit crossed the conservative native dispatch boundary.

**param Cleanup:** The native cleanup observation, independent from transaction effects.

**param RecoveryKey:** The optional application recovery identity.

**remarks:** The original exception retains this snapshot without replacement or automatic retry. It owns no native resource and remains usable after the source and session have been disposed. Snapshot retrieval also works when a custom exception's Data dictionary rejects optional enrichment.

**Inherited type policy:** The original exception retains this snapshot without replacement or automatic retry. It owns no native resource and remains usable after the source and session have been disposed. Snapshot retrieval also works when a custom exception's Data dictionary rejects optional enrichment.

## `M:Korda.Database.TransactionFailure.Deconstruct(Korda.Database.TransactionOutcome@,System.Boolean@,Korda.Database.DatabaseCloseResult@,System.String@)`

```csharp
public void Korda.Database.TransactionFailure.Deconstruct(out Korda.Database.TransactionOutcome Outcome, out bool CommitAttempted, out Korda.Database.DatabaseCloseResult Cleanup, out string? RecoveryKey)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The original exception retains this snapshot without replacement or automatic retry. It owns no native resource and remains usable after the source and session have been disposed. Snapshot retrieval also works when a custom exception's Data dictionary rejects optional enrichment.

## `M:Korda.Database.TransactionFailure.Equals(Korda.Database.TransactionFailure)`

```csharp
public bool Korda.Database.TransactionFailure.Equals(Korda.Database.TransactionFailure? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The original exception retains this snapshot without replacement or automatic retry. It owns no native resource and remains usable after the source and session have been disposed. Snapshot retrieval also works when a custom exception's Data dictionary rejects optional enrichment.

## `M:Korda.Database.TransactionFailure.Equals(System.Object)`

```csharp
public override bool Korda.Database.TransactionFailure.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The original exception retains this snapshot without replacement or automatic retry. It owns no native resource and remains usable after the source and session have been disposed. Snapshot retrieval also works when a custom exception's Data dictionary rejects optional enrichment.

## `M:Korda.Database.TransactionFailure.GetHashCode`

```csharp
public override int Korda.Database.TransactionFailure.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The original exception retains this snapshot without replacement or automatic retry. It owns no native resource and remains usable after the source and session have been disposed. Snapshot retrieval also works when a custom exception's Data dictionary rejects optional enrichment.

## `M:Korda.Database.TransactionFailure.ToString`

```csharp
public override string Korda.Database.TransactionFailure.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The original exception retains this snapshot without replacement or automatic retry. It owns no native resource and remains usable after the source and session have been disposed. Snapshot retrieval also works when a custom exception's Data dictionary rejects optional enrichment.

## `M:Korda.Database.TransactionFailure.TryGet(System.Exception,Korda.Database.TransactionFailure@)`

```csharp
public static bool Korda.Database.TransactionFailure.TryGet(System.Exception exception, [System.Diagnostics.CodeAnalysis.NotNullWhenAttribute(true)] out Korda.Database.TransactionFailure? failure)
```

**summary:** Reads only the exact callback-failure observation on the supplied exception.

**param exception:** The original exception; inner exceptions are not traversed.

**param failure:** The retained snapshot when present, otherwise null.

**returns:** True when this exception carries a transaction callback snapshot.

**exception:** is null.

**Inherited type policy:** The original exception retains this snapshot without replacement or automatic retry. It owns no native resource and remains usable after the source and session have been disposed. Snapshot retrieval also works when a custom exception's Data dictionary rejects optional enrichment.

## `M:Korda.Database.TransactionFailure.op_Equality(Korda.Database.TransactionFailure,Korda.Database.TransactionFailure)`

```csharp
public static bool Korda.Database.TransactionFailure.operator ==(Korda.Database.TransactionFailure? left, Korda.Database.TransactionFailure? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The original exception retains this snapshot without replacement or automatic retry. It owns no native resource and remains usable after the source and session have been disposed. Snapshot retrieval also works when a custom exception's Data dictionary rejects optional enrichment.

## `M:Korda.Database.TransactionFailure.op_Inequality(Korda.Database.TransactionFailure,Korda.Database.TransactionFailure)`

```csharp
public static bool Korda.Database.TransactionFailure.operator !=(Korda.Database.TransactionFailure? left, Korda.Database.TransactionFailure? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The original exception retains this snapshot without replacement or automatic retry. It owns no native resource and remains usable after the source and session have been disposed. Snapshot retrieval also works when a custom exception's Data dictionary rejects optional enrichment.

## `M:Korda.Database.TransactionFailure.{Clone}$`

```csharp
public Korda.Database.TransactionFailure Korda.Database.TransactionFailure.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The original exception retains this snapshot without replacement or automatic retry. It owns no native resource and remains usable after the source and session have been disposed. Snapshot retrieval also works when a custom exception's Data dictionary rejects optional enrichment.

## `M:Korda.Query.CommonTable`1.Create(Korda.Data.Table{`0},Korda.Query.QuerySelection{`0},System.Boolean)`

```csharp
public static Korda.Query.CommonTable<T> Korda.Query.CommonTable<T>.Create(Korda.Data.Table<T> declaredShape, Korda.Query.QuerySelection<T> body, bool recursive = false)
```

**summary:** Clones a native selected body into the declared shape; recursive mode requires a validated anchor/member UNION.

**inheritdoc:**

**param declaredShape:** The static CTE row descriptor with an explicit logical table name and compatible fields.

**param body:** The selected native CTE body to freeze; later body mutations do not affect this snapshot.

**param recursive:** Whether the validated body is a recursive anchor/member UNION with one declared self-reference.

**returns:** The typed owner or proxy described above, retaining its original source/query association.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** The snapshot is bound to one source and a declared static row shape. With imports a native retained reference. Disposal prevents new imports and does not mutate previously imported snapshots. Recursive creation requires one validated anchor/member self-reference.

## `M:Korda.Query.CommonTable`1.Dispose`

```csharp
public void Korda.Query.CommonTable<T>.Dispose()
```

**summary:** Prevents new CTE imports and releases this native snapshot reference.

**inheritdoc:**

**Inherited type policy:** The snapshot is bound to one source and a declared static row shape. With imports a native retained reference. Disposal prevents new imports and does not mutate previously imported snapshots. Recursive creation requires one validated anchor/member self-reference.

## `M:Korda.Query.FrameBoundary.#ctor`

```csharp
public Korda.Query.FrameBoundary.FrameBoundary()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Only these ROWS endpoints are supported. Reversed or unsupported bounds fail before native state publication; finite numeric PRECEDING/FOLLOWING frames are not declared.

## `M:Korda.Query.JoinPredicate`2.#ctor`

```csharp
public Korda.Query.JoinPredicate<TLeft, TRight>.JoinPredicate()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Use only inside the synchronous JOIN callback. Provisional right-side expressions are invalidated on commit or abort. Values and nullability stay exact; explicitly lift a non-null parent key to compare a nullable foreign key.

## `M:Korda.Query.JoinPredicate`2.Left``1(Korda.Data.Column{`0,``0})`

```csharp
public Korda.Query.SqlExpression<T> Korda.Query.JoinPredicate<TLeft, TRight>.Left<T>(Korda.Data.Column<TLeft, T> column)
```

**summary:** Reads a root-table column during the synchronous ON callback.

**inheritdoc:**

**param column:** The strongly typed column from the declared table; foreign table identities reject.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Use only inside the synchronous JOIN callback. Provisional right-side expressions are invalidated on commit or abort. Values and nullability stay exact; explicitly lift a non-null parent key to compare a nullable foreign key.

## `M:Korda.Query.JoinPredicate`2.Right``1(Korda.Data.Column{`1,``0})`

```csharp
public Korda.Query.SqlExpression<T> Korda.Query.JoinPredicate<TLeft, TRight>.Right<T>(Korda.Data.Column<TRight, T> column)
```

**summary:** Reads a provisional joined column; the resulting ON token expires on commit or abort.

**inheritdoc:**

**param column:** The strongly typed column from the declared table; foreign table identities reject.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Use only inside the synchronous JOIN callback. Provisional right-side expressions are invalidated on commit or abort. Values and nullability stay exact; explicitly lift a non-null parent key to compare a nullable foreign key.

## `M:Korda.Query.JoinedTable`1.#ctor`

```csharp
public Korda.Query.JoinedTable<TEntity>.JoinedTable()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The alias is a borrowed token, not an independently disposable query. Default aliases and aliases from another query reject, even if the CLR entity type matches.

## `M:Korda.Query.LeftJoinedTable`1.#ctor`

```csharp
public Korda.Query.LeftJoinedTable<TEntity>.LeftJoinedTable()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Use NullableValue or NullableReference to project its columns and HasMatch to distinguish absence. No default scalar sentinel or implicit object graph is introduced. Its query owns the alias lifetime.

## `M:Korda.Query.LockWait.#ctor`

```csharp
public Korda.Query.LockWait.LockWait()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Controls PostgreSQL row-lock conflicts within the same transaction and total query deadline; it does not enable transparent retries.

## `M:Korda.Query.NullOrder.#ctor`

```csharp
public Korda.Query.NullOrder.NullOrder()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Both providers render explicit SQL NULL placement. Ordering alone supplies no uniqueness or snapshot guarantee.

## `M:Korda.Query.PreparedQuery`1.AsAsyncEnumerable(Korda.Database.DataSession,Korda.Query.QueryParameters,System.Threading.CancellationToken)`

```csharp
public System.Collections.Generic.IAsyncEnumerable<T> Korda.Query.PreparedQuery<T>.AsAsyncEnumerable(Korda.Database.DataSession session, Korda.Query.QueryParameters? parameters = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Snapshots exact parameter values now and creates one independent lazy native stream.

**inheritdoc:**

**param session:** An open execution session belonging to this plan's exact source.

**param parameters:** The complete declared parameter set; null denotes an empty set and is valid only for a parameterless plan.

**param cancellationToken:** Cancellation requests native stop; completion still waits for cursor and operation retirement.

**returns:** A one-shot lazy stream of owned results. Dispose its enumerator to request stop and await retirement on early exit.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**exception:** Cancellation requests termination and cleanup.

**exception:** The finite total query deadline expires; cleanup still completes.

**Inherited type policy:** Concurrent execution uses separate sessions from the same source. Disposing prevents new dispatch; accepted native operations retain their plan independently and complete safely.

## `M:Korda.Query.PreparedQuery`1.Dispose`

```csharp
public void Korda.Query.PreparedQuery<T>.Dispose()
```

**summary:** Prevents new execution and releases this plan reference without cancelling accepted operations.

**inheritdoc:**

**Inherited type policy:** Concurrent execution uses separate sessions from the same source. Disposing prevents new dispatch; accepted native operations retain their plan independently and complete safely.

## `M:Korda.Query.PreparedQuery`1.ToListAsync(Korda.Database.DataSession,Korda.Query.QueryParameters,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<T>> Korda.Query.PreparedQuery<T>.ToListAsync(Korda.Database.DataSession session, Korda.Query.QueryParameters? parameters = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Snapshots the exact declared parameter set before await and executes a bounded owned list.

**inheritdoc:**

**param session:** An open execution session belonging to this plan's exact source.

**param parameters:** The complete declared parameter set; null denotes an empty set and is valid only for a parameterless plan.

**param cancellationToken:** Cancellation requests native stop; completion still waits for cursor and operation retirement.

**returns:** The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**exception:** Cancellation requests termination and cleanup.

**exception:** The finite total query deadline expires; cleanup still completes.

**Inherited type policy:** Concurrent execution uses separate sessions from the same source. Disposing prevents new dispatch; accepted native operations retain their plan independently and complete safely.

## `M:Korda.Query.ProjectionBuilder`1.Add``1(Korda.Query.SqlExpression{``0},System.String)`

```csharp
public Korda.Data.ResultColumn<TValue> Korda.Query.ProjectionBuilder<T>.Add<TValue>(Korda.Query.SqlExpression<TValue> expression, string alias)
```

**summary:** Adds an exact same-query scalar and unique result alias, returning its projection token.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param alias:** The bounded exact SQL/result alias; duplicate or invalid names reject.

**typeparam TValue:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** The typed result token associated with this exact projection builder.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Fields must come from its exact live query. Build freezes field order, unique names, codec/nullability and the direct materializer; later Add or Build rejects. RowReader validates the full shape before the callback, and escaping DTO values own their data.

## `M:Korda.Query.ProjectionBuilder`1.Add``1(Korda.Query.SqlExpression{``0},System.String,Korda.Data.ValueCodec{``0})`

```csharp
public Korda.Data.ResultColumn<TValue> Korda.Query.ProjectionBuilder<T>.Add<TValue>(Korda.Query.SqlExpression<TValue> expression, string alias, Korda.Data.ValueCodec<TValue> codec)
```

**summary:** Adds a same-query scalar after checking its exact declared DTO codec policy.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param alias:** The unique exact SQL/result alias.

**param codec:** The expected logical codec, configuration and SQL nullability used by the DTO reader.

**typeparam TValue:** The statically known property type.

**returns:** The result token associated with this projection builder and explicit reader policy.

**exception:** The expected codec is null.

**exception:** The expression belongs elsewhere or differs in codec, configuration or nullability.

**exception:** The owner or builder is in an invalid state.

**exception:** The native query owner has closed admission.

**exception:** Native capability or finite resource limits reject the operation.

**Inherited type policy:** Fields must come from its exact live query. Build freezes field order, unique names, codec/nullability and the direct materializer; later Add or Build rejects. RowReader validates the full shape before the callback, and escaping DTO values own their data.

## `M:Korda.Query.ProjectionBuilder`1.Build(Korda.Data.RowMaterializer{`0})`

```csharp
public Korda.Data.Projection<T> Korda.Query.ProjectionBuilder<T>.Build(Korda.Data.RowMaterializer<T> materializer)
```

**summary:** Consumes the builder and freezes a direct AOT-visible materializer; it does not execute SQL.

**inheritdoc:**

**param materializer:** The statically compiled direct constructor or field reader; runtime reflection is not used.

**returns:** An immutable projection with typed field tokens and the static direct materializer.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Fields must come from its exact live query. Build freezes field order, unique names, codec/nullability and the direct materializer; later Add or Build rejects. RowReader validates the full shape before the callback, and escaping DTO values own their data.

## `M:Korda.Query.QueryCursor`1.Create``1(Korda.Data.Column{`0,``0},``0)`

```csharp
public static Korda.Query.QueryCursor<TEntity> Korda.Query.QueryCursor<TEntity>.Create<T>(Korda.Data.Column<TEntity, T> column, T value)
```

**summary:** Copies the first non-null ordering value from a built table.

**typeparam T:** The exact column value type.

**param column:** The first root-table ordering column.

**param value:** The non-null value to snapshot.

**returns:** A new immutable cursor prefix.

**exception:** The column or a required value is null.

**exception:** The column is unbuilt, nullable or the value is invalid.

**Inherited type policy:** Values are copied when added. The cursor owns no native resource and can be reused with the same table metadata. Applying it requires an exact non-null column ordering containing the complete primary key; the query supplies each column's ascending or descending direction.

## `M:Korda.Query.QueryCursor`1.Then``1(Korda.Data.Column{`0,``0},``0)`

```csharp
public Korda.Query.QueryCursor<TEntity> Korda.Query.QueryCursor<TEntity>.Then<T>(Korda.Data.Column<TEntity, T> column, T value)
```

**summary:** Copies the next ordering value and returns a new cursor without changing this instance.

**typeparam T:** The exact next column value type.

**param column:** The next distinct non-null column from the same table.

**param value:** The non-null value to snapshot.

**returns:** A new immutable cursor with the next component appended.

**exception:** The column or a required value is null.

**exception:** The column is foreign, repeated, nullable or the value is invalid.

**Inherited type policy:** Values are copied when added. The cursor owns no native resource and can be reused with the same table metadata. Applying it requires an exact non-null column ordering containing the complete primary key; the query supplies each column's ascending or descending direction.

## `M:Korda.Query.QueryParameter`1.#ctor`

```csharp
public Korda.Query.QueryParameter<T>.QueryParameter()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Parameter IDs are monotonically allocated within a source generation, independently of query expression tokens. A copied parameter remains an identity; it owns no native arena or value, and only declared/imported plans may execute it.

## `M:Korda.Query.QueryParameters.#ctor`

```csharp
public Korda.Query.QueryParameters.QueryParameters()
```

**summary:** Creates an empty explicit parameter set.

**inheritdoc:**

**Inherited type policy:** Set snapshots each encoded value before returning. A terminal copies the complete finite set before its first await; later Set calls do not change admitted execution. Missing, extra, foreign or codec-incompatible identities reject before native dispatch. No global parameter dictionary exists.

## `M:Korda.Query.QueryParameters.Set``1(Korda.Query.QueryParameter{``0},``0)`

```csharp
public Korda.Query.QueryParameters Korda.Query.QueryParameters.Set<T>(Korda.Query.QueryParameter<T> parameter, T value)
```

**summary:** Copies or replaces one typed parameter value; default or conflicting source identities reject.

**inheritdoc:**

**param parameter:** The declared source-generation parameter identity with its exact typed codec.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** The typed owner or proxy described above, retaining its original source/query association.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Set snapshots each encoded value before returning. A terminal copies the complete finite set before its first await; later Set calls do not change admitted execution. Missing, extra, foreign or codec-incompatible identities reject before native dispatch. No global parameter dictionary exists.

## `M:Korda.Query.QueryRow`1.#ctor`

```csharp
public Korda.Query.QueryRow<TEntity>.QueryRow()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This borrowed proxy retains its query association. Default, foreign, closed or sealed owners reject. Each operation forwards immediately to a finite native arena; no managed SQL tree or captured callback is retained.

## `M:Korda.Query.QueryRow`1.Equal``1(Korda.Data.Column{`0,``0},``0)`

```csharp
public Korda.Query.SqlPredicate Korda.Query.QueryRow<TEntity>.Equal<T>(Korda.Data.Column<TEntity, T> column, T value)
```

**summary:** Snapshots a typed value and creates native SQL equality.

**inheritdoc:**

**param column:** The strongly typed column from the declared table; foreign table identities reject.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** This borrowed proxy retains its query association. Default, foreign, closed or sealed owners reject. Each operation forwards immediately to a finite native arena; no managed SQL tree or captured callback is retained.

## `M:Korda.Query.QueryRow`1.Get``1(Korda.Data.Column{`0,``0})`

```csharp
public Korda.Query.SqlExpression<T> Korda.Query.QueryRow<TEntity>.Get<T>(Korda.Data.Column<TEntity, T> column)
```

**summary:** Creates an immediate native expression for an exact root-table column.

**inheritdoc:**

**param column:** The strongly typed column from the declared table; foreign table identities reject.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** This borrowed proxy retains its query association. Default, foreign, closed or sealed owners reject. Each operation forwards immediately to a finite native arena; no managed SQL tree or captured callback is retained.

## `M:Korda.Query.QueryRow`1.GreaterThanOrEqual``1(Korda.Data.Column{`0,``0},``0)`

```csharp
public Korda.Query.SqlPredicate Korda.Query.QueryRow<TEntity>.GreaterThanOrEqual<T>(Korda.Data.Column<TEntity, T> column, T value)
```

**summary:** Snapshots a typed lower bound and creates a native comparison.

**inheritdoc:**

**param column:** The strongly typed column from the declared table; foreign table identities reject.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** This borrowed proxy retains its query association. Default, foreign, closed or sealed owners reject. Each operation forwards immediately to a finite native arena; no managed SQL tree or captured callback is retained.

## `M:Korda.Query.QueryRow`1.IsNotNull``1(Korda.Data.Column{`0,``0})`

```csharp
public Korda.Query.SqlPredicate Korda.Query.QueryRow<TEntity>.IsNotNull<T>(Korda.Data.Column<TEntity, T> column)
```

**summary:** Tests the exact root column for SQL non-NULL.

**inheritdoc:**

**param column:** The strongly typed column from the declared table; foreign table identities reject.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** This borrowed proxy retains its query association. Default, foreign, closed or sealed owners reject. Each operation forwards immediately to a finite native arena; no managed SQL tree or captured callback is retained.

## `M:Korda.Query.QueryRow`1.IsNull``1(Korda.Data.Column{`0,``0})`

```csharp
public Korda.Query.SqlPredicate Korda.Query.QueryRow<TEntity>.IsNull<T>(Korda.Data.Column<TEntity, T> column)
```

**summary:** Tests the exact root column for SQL NULL.

**inheritdoc:**

**param column:** The strongly typed column from the declared table; foreign table identities reject.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** This borrowed proxy retains its query association. Default, foreign, closed or sealed owners reject. Each operation forwards immediately to a finite native arena; no managed SQL tree or captured callback is retained.

## `M:Korda.Query.QuerySelection`1.#ctor`

```csharp
public Korda.Query.QuerySelection<T>.QuerySelection()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.

## `M:Korda.Query.QuerySelection`1.AsAsyncEnumerable(System.Threading.CancellationToken)`

```csharp
public System.Collections.Generic.IAsyncEnumerable<T> Korda.Query.QuerySelection<T>.AsAsyncEnumerable(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Seals synchronously and creates a one-shot bounded stream whose first MoveNext dispatches.

**inheritdoc:**

**param cancellationToken:** Cancellation requests native stop; completion still waits for cursor and operation retirement.

**returns:** A one-shot lazy stream of owned results. Dispose its enumerator to request stop and await retirement on early exit.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**exception:** Cancellation requests termination and cleanup.

**exception:** The finite total query deadline expires; cleanup still completes.

**Inherited type policy:** This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.

## `M:Korda.Query.QuerySelection`1.Combine(Korda.Query.QuerySelection{`0},Korda.Query.SetOperation)`

```csharp
public Korda.Query.QuerySelection<T> Korda.Query.QuerySelection<T>.Combine(Korda.Query.QuerySelection<T> other, Korda.Query.SetOperation operation = Korda.Query.SetOperation.UnionAll)
```

**summary:** Appends a native set operation using a frozen same-source selection; the other query remains mutable.

**inheritdoc:**

**param other:** The compatible same-source selection or same-query expression required by this operation.

**param operation:** The native SQL set operation with exact compatible row shapes.

**returns:** The borrowed current-selection view; its owning query retains the native arena.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.

## `M:Korda.Query.QuerySelection`1.FirstAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<T> Korda.Query.QuerySelection<T>.FirstAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Reads at most one business row and retires the cursor; absence throws.

**inheritdoc:**

**param cancellationToken:** Cancellation requests native stop; completion still waits for cursor and operation retirement.

**returns:** The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**exception:** Cancellation requests termination and cleanup.

**exception:** The finite total query deadline expires; cleanup still completes.

**Inherited type policy:** This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.

## `M:Korda.Query.QuerySelection`1.FirstOrDefaultAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<T?> Korda.Query.QuerySelection<T>.FirstOrDefaultAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Reads at most one business row, returning default(T) on absence.

**inheritdoc:**

**param cancellationToken:** Cancellation requests native stop; completion still waits for cursor and operation retirement.

**returns:** The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**exception:** Cancellation requests termination and cleanup.

**exception:** The finite total query deadline expires; cleanup still completes.

**Inherited type policy:** This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.

## `M:Korda.Query.QuerySelection`1.Prepare`

```csharp
public Korda.Query.PreparedQuery<T> Korda.Query.QuerySelection<T>.Prepare()
```

**summary:** Seals and transfers an immutable source-bound native plan for independent parameter snapshots.

**inheritdoc:**

**returns:** An independently disposable immutable native plan bound to this source.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.

## `M:Korda.Query.QuerySelection`1.SingleAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<T> Korda.Query.QuerySelection<T>.SingleAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Reads at most two business rows and requires exactly one, with cleanup on either cardinality failure.

**inheritdoc:**

**param cancellationToken:** Cancellation requests native stop; completion still waits for cursor and operation retirement.

**returns:** The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**exception:** Cancellation requests termination and cleanup.

**exception:** The finite total query deadline expires; cleanup still completes.

**Inherited type policy:** This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.

## `M:Korda.Query.QuerySelection`1.SingleOrDefaultAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<T?> Korda.Query.QuerySelection<T>.SingleOrDefaultAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Requires at most one business row and returns default(T) when absent.

**inheritdoc:**

**param cancellationToken:** Cancellation requests native stop; completion still waits for cursor and operation retirement.

**returns:** The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**exception:** Cancellation requests termination and cleanup.

**exception:** The finite total query deadline expires; cleanup still completes.

**Inherited type policy:** This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.

## `M:Korda.Query.QuerySelection`1.ToArrayAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<T[]> Korda.Query.QuerySelection<T>.ToArrayAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Seals once and returns a bounded owned array after native cursor retirement.

**inheritdoc:**

**param cancellationToken:** Cancellation requests native stop; completion still waits for cursor and operation retirement.

**returns:** The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**exception:** Cancellation requests termination and cleanup.

**exception:** The finite total query deadline expires; cleanup still completes.

**Inherited type policy:** This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.

## `M:Korda.Query.QuerySelection`1.ToListAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<T>> Korda.Query.QuerySelection<T>.ToListAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Seals once and returns a bounded owned list after native cursor retirement.

**inheritdoc:**

**param cancellationToken:** Cancellation requests native stop; completion still waits for cursor and operation retirement.

**returns:** The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**exception:** Cancellation requests termination and cleanup.

**exception:** The finite total query deadline expires; cleanup still completes.

**Inherited type policy:** This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.

## `M:Korda.Query.QuerySelection`1.ToSql`

```csharp
public string Korda.Query.QuerySelection<T>.ToSql()
```

**summary:** Renders a bounded native clone with placeholders and no parameter values; does not execute or seal.

**inheritdoc:**

**returns:** A bounded native SQL rendering with placeholders and no bound values.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.

## `M:Korda.Query.Query`1.AnyAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<bool> Korda.Query.Query<TEntity>.AnyAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Performs native EXISTS over the fully composed query.

**inheritdoc:**

**param cancellationToken:** Cancellation requests native stop; completion still waits for cursor and operation retirement.

**returns:** The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**exception:** Cancellation requests termination and cleanup.

**exception:** The finite total query deadline expires; cleanup still completes.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.Clone`

```csharp
public Korda.Query.Query<TEntity> Korda.Query.Query<TEntity>.Clone()
```

**summary:** Creates an explicit deep native mutable clone; existing expression/alias proxies remain bound to this query.

**inheritdoc:**

**returns:** A new independently disposable native mutable clone; old proxies still belong to the old query.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.Column``1(Korda.Data.Column{`0,``0})`

```csharp
public Korda.Query.SqlExpression<T> Korda.Query.Query<TEntity>.Column<T>(Korda.Data.Column<TEntity, T> column)
```

**summary:** Creates an immediate root-column native scalar token.

**inheritdoc:**

**param column:** The strongly typed column from the declared table; foreign table identities reject.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.Column``2(Korda.Query.JoinedTable{``0},Korda.Data.Column{``0,``1})`

```csharp
public Korda.Query.SqlExpression<T> Korda.Query.Query<TEntity>.Column<TJoin, T>(Korda.Query.JoinedTable<TJoin> table, Korda.Data.Column<TJoin, T> column) where TJoin : class
```

**summary:** Creates a scalar from an exact same-query inner-join alias and table.

**inheritdoc:**

**param table:** The exact static table descriptor or same-query joined alias required by this overload.

**param column:** The strongly typed column from the declared table; foreign table identities reject.

**typeparam TJoin:** The statically known entity class with explicit table metadata.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.CountAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<long> Korda.Query.Query<TEntity>.CountAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Counts the fully composed native result without fetching DTOs into managed memory.

**inheritdoc:**

**param cancellationToken:** Cancellation requests native stop; completion still waits for cursor and operation retirement.

**returns:** The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**exception:** Cancellation requests termination and cleanup.

**exception:** The finite total query deadline expires; cleanup still completes.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.CountRows`

```csharp
public Korda.Query.SqlExpression<long> Korda.Query.Query<TEntity>.CountRows()
```

**summary:** Creates a native COUNT(*) aggregate with exact Int64 result metadata.

**inheritdoc:**

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.DeleteAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<long> Korda.Query.Query<TEntity>.DeleteAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Executes a filtered native DELETE; missing filters and unsupported joined/grouped writes reject.

**inheritdoc:**

**param cancellationToken:** Cancellation requests native stop; completion still waits for cursor and operation retirement.

**returns:** The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**exception:** Cancellation requests termination and cleanup.

**exception:** The finite total query deadline expires; cleanup still completes.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.Dispose`

```csharp
public void Korda.Query.Query<TEntity>.Dispose()
```

**summary:** Closes query admission, requests any active cursor stop and releases the native arena reference.

**inheritdoc:**

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.Distinct`

```csharp
public Korda.Query.Query<TEntity> Korda.Query.Query<TEntity>.Distinct()
```

**summary:** Applies native SQL DISTINCT to the selected shape.

**inheritdoc:**

**returns:** This mutable query for further synchronous composition before its first terminal.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.Exists``1(Korda.Query.QuerySelection{``0})`

```csharp
public Korda.Query.SqlPredicate Korda.Query.Query<TEntity>.Exists<T>(Korda.Query.QuerySelection<T> subquery)
```

**summary:** Snapshots a same-source selection into a native EXISTS condition.

**inheritdoc:**

**param subquery:** A current same-source selection whose native plan is cloned under admission.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.ForUpdate(Korda.Query.RowLockMode,Korda.Query.LockWait)`

```csharp
public Korda.Query.Query<TEntity> Korda.Query.Query<TEntity>.ForUpdate(Korda.Query.RowLockMode mode = Korda.Query.RowLockMode.Update, Korda.Query.LockWait wait = Korda.Query.LockWait.Wait)
```

**summary:** Applies an admitted PostgreSQL row lock; execution requires an explicit active transaction.

**inheritdoc:**

**param mode:** The PostgreSQL row-lock strength; an explicit transaction is required.

**param wait:** The PostgreSQL lock-conflict policy.

**returns:** This mutable query for further synchronous composition before its first terminal.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.GroupBy``1(Korda.Query.SqlExpression{``0})`

```csharp
public Korda.Query.Query<TEntity> Korda.Query.Query<TEntity>.GroupBy<T>(Korda.Query.SqlExpression<T> expression)
```

**summary:** Appends a native grouping key.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** This mutable query for further synchronous composition before its first terminal.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.HasMatch``1(Korda.Query.LeftJoinedTable{``0})`

```csharp
public Korda.Query.SqlPredicate Korda.Query.Query<TEntity>.HasMatch<TJoin>(Korda.Query.LeftJoinedTable<TJoin> table) where TJoin : class
```

**summary:** Tests the native explicit LEFT JOIN match marker, independently from nullable child data.

**inheritdoc:**

**param table:** The exact static table descriptor or same-query joined alias required by this overload.

**typeparam TJoin:** The statically known entity class with explicit table metadata.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.Having(Korda.Query.SqlPredicate)`

```csharp
public Korda.Query.Query<TEntity> Korda.Query.Query<TEntity>.Having(Korda.Query.SqlPredicate predicate)
```

**summary:** Appends a same-query native group predicate.

**inheritdoc:**

**param predicate:** The compatible native condition or synchronous typed callback; callbacks are invoked at this call.

**returns:** This mutable query for further synchronous composition before its first terminal.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.InnerJoin``1(Korda.Data.Table{``0},System.String,System.Func{Korda.Query.JoinPredicate{`0,``0},Korda.Query.SqlPredicate})`

```csharp
public Korda.Query.JoinedTable<TJoin> Korda.Query.Query<TEntity>.InnerJoin<TJoin>(Korda.Data.Table<TJoin> table, string alias, System.Func<Korda.Query.JoinPredicate<TEntity, TJoin>, Korda.Query.SqlPredicate> on) where TJoin : class
```

**summary:** Reserves, builds and atomically commits an explicit native INNER JOIN ON clause.

**inheritdoc:**

**param table:** The exact static table descriptor or same-query joined alias required by this overload.

**param alias:** The bounded exact SQL/result alias; duplicate or invalid names reject.

**param on:** The synchronous typed JOIN ON callback; it is neither stored nor invoked during row enumeration.

**typeparam TJoin:** The statically known entity class with explicit table metadata.

**returns:** The typed owner or proxy described above, retaining its original source/query association.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.LeftJoin``1(Korda.Data.Table{``0},System.String,System.Func{Korda.Query.JoinPredicate{`0,``0},Korda.Query.SqlPredicate})`

```csharp
public Korda.Query.LeftJoinedTable<TJoin> Korda.Query.Query<TEntity>.LeftJoin<TJoin>(Korda.Data.Table<TJoin> table, string alias, System.Func<Korda.Query.JoinPredicate<TEntity, TJoin>, Korda.Query.SqlPredicate> on) where TJoin : class
```

**summary:** Commits a native LEFT JOIN and returns a nullable-only projected alias.

**inheritdoc:**

**param table:** The exact static table descriptor or same-query joined alias required by this overload.

**param alias:** The bounded exact SQL/result alias; duplicate or invalid names reject.

**param on:** The synchronous typed JOIN ON callback; it is neither stored nor invoked during row enumeration.

**typeparam TJoin:** The statically known entity class with explicit table metadata.

**returns:** The typed owner or proxy described above, retaining its original source/query association.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.NullableReference``2(Korda.Query.LeftJoinedTable{``0},Korda.Data.Column{``0,``1})`

```csharp
public Korda.Query.SqlExpression<T?> Korda.Query.Query<TEntity>.NullableReference<TJoin, T>(Korda.Query.LeftJoinedTable<TJoin> table, Korda.Data.Column<TJoin, T> column) where TJoin : class where T : class?
```

**summary:** Reads a reference column through a LEFT JOIN with explicit nullable result metadata.

**inheritdoc:**

**param table:** The exact static table descriptor or same-query joined alias required by this overload.

**param column:** The strongly typed column from the declared table; foreign table identities reject.

**typeparam TJoin:** The statically known entity class with explicit table metadata.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.NullableValue``2(Korda.Query.LeftJoinedTable{``0},Korda.Data.Column{``0,System.Nullable{``1}})`

```csharp
public Korda.Query.SqlExpression<T?> Korda.Query.Query<TEntity>.NullableValue<TJoin, T>(Korda.Query.LeftJoinedTable<TJoin> table, Korda.Data.Column<TJoin, T?> column) where TJoin : class where T : struct
```

**summary:** Preserves an already nullable value column through a LEFT JOIN.

**inheritdoc:**

**param table:** The exact static table descriptor or same-query joined alias required by this overload.

**param column:** The strongly typed column from the declared table; foreign table identities reject.

**typeparam TJoin:** The statically known entity class with explicit table metadata.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.NullableValue``2(Korda.Query.LeftJoinedTable{``0},Korda.Data.Column{``0,``1})`

```csharp
public Korda.Query.SqlExpression<T?> Korda.Query.Query<TEntity>.NullableValue<TJoin, T>(Korda.Query.LeftJoinedTable<TJoin> table, Korda.Data.Column<TJoin, T> column) where TJoin : class where T : struct
```

**summary:** Reads a non-null value column through a LEFT JOIN as an explicitly nullable scalar.

**inheritdoc:**

**param table:** The exact static table descriptor or same-query joined alias required by this overload.

**param column:** The strongly typed column from the declared table; foreign table identities reject.

**typeparam TJoin:** The statically known entity class with explicit table metadata.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.OrderBy``1(Korda.Data.Column{`0,``0},Korda.Query.SortDirection,Korda.Query.NullOrder)`

```csharp
public Korda.Query.Query<TEntity> Korda.Query.Query<TEntity>.OrderBy<T>(Korda.Data.Column<TEntity, T> column, Korda.Query.SortDirection direction = Korda.Query.SortDirection.Ascending, Korda.Query.NullOrder nullOrder = Korda.Query.NullOrder.Last)
```

**summary:** Replaces native ordering with the exact root column and explicit NULL policy.

**inheritdoc:**

**param column:** The strongly typed column from the declared table; foreign table identities reject.

**param direction:** Explicit ascending or descending SQL order.

**param nullOrder:** Explicit placement of SQL NULL values in this ordering key.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** This mutable query for further synchronous composition before its first terminal.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.OrderBy``1(Korda.Query.SqlExpression{``0},Korda.Query.SortDirection,Korda.Query.NullOrder)`

```csharp
public Korda.Query.Query<TEntity> Korda.Query.Query<TEntity>.OrderBy<T>(Korda.Query.SqlExpression<T> expression, Korda.Query.SortDirection direction = Korda.Query.SortDirection.Ascending, Korda.Query.NullOrder nullOrder = Korda.Query.NullOrder.Last)
```

**summary:** Replaces native ordering with the same-query expression and explicit NULL policy.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param direction:** Explicit ascending or descending SQL order.

**param nullOrder:** Explicit placement of SQL NULL values in this ordering key.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** This mutable query for further synchronous composition before its first terminal.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.ParameterValue``1(Korda.Query.QueryParameter{``0})`

```csharp
public Korda.Query.SqlExpression<T> Korda.Query.Query<TEntity>.ParameterValue<T>(Korda.Query.QueryParameter<T> parameter)
```

**summary:** Creates a native reference to an identity already declared or imported into this query.

**inheritdoc:**

**param parameter:** The declared source-generation parameter identity with its exact typed codec.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.Parameter``1(System.String,Korda.Data.ValueCodec{``0})`

```csharp
public Korda.Query.QueryParameter<T> Korda.Query.Query<TEntity>.Parameter<T>(string name, Korda.Data.ValueCodec<T> codec)
```

**summary:** Allocates one nonrecycled source-generation parameter identity and declares it in this query.

**inheritdoc:**

**param name:** The bounded exact parameter name; conflicting identities in composed plans reject.

**param codec:** The statically known codec and exact nullability/configuration for the value.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** The typed owner or proxy described above, retaining its original source/query association.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.Projection``1`

```csharp
public Korda.Query.ProjectionBuilder<T> Korda.Query.Query<TEntity>.Projection<T>()
```

**summary:** Starts an explicit typed DTO projection in this query.

**inheritdoc:**

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** The typed owner or proxy described above, retaining its original source/query association.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.SeekAfter(Korda.Query.QueryCursor{`0})`

```csharp
public Korda.Query.Query<TEntity> Korda.Query.Query<TEntity>.SeekAfter(Korda.Query.QueryCursor<TEntity> cursor)
```

**summary:** Appends a strict lexicographic cursor predicate using the exact current root-column ordering.

**param cursor:** Every non-null ordering column and its snapshotted value, including the complete primary key.

**returns:** This mutable query with a native cursor predicate and unchanged row limit.

**exception:** is null.

**exception:** The cursor differs from the exact ordering, omits key columns or conflicts with an offset.

**exception:** A composite cursor was already applied or the query is sealed.

**exception:** The query or session has closed.

**exception:** The native query capability or finite capacity rejects composition.

**remarks:** Mixed ascending and descending columns are supported. SQL comparisons, parameters and dialect rendering remain native. Later ordering changes and offsets reject. A unique root key does not remove duplicate rows introduced by a one-to-many join or provide a cross-request database snapshot.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.SeekAfter``1(Korda.Data.Column{`0,``0},``0)`

```csharp
public Korda.Query.Query<TEntity> Korda.Query.Query<TEntity>.SeekAfter<T>(Korda.Data.Column<TEntity, T> uniqueOrderKey, T value)
```

**summary:** Appends strict keyset comparison for one non-null unique ascending root key.

**inheritdoc:**

**param uniqueOrderKey:** The sole non-null primary key already ordered ascending, with no conflicting offset.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** This mutable query for further synchronous composition before its first terminal.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.SelectAll`

```csharp
public Korda.Query.QuerySelection<TEntity> Korda.Query.Query<TEntity>.SelectAll()
```

**summary:** Selects only root-table columns in the static entity constructor order.

**inheritdoc:**

**returns:** The borrowed current-selection view; its owning query retains the native arena.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.Select``1(Korda.Data.Projection{``0})`

```csharp
public Korda.Query.QuerySelection<T> Korda.Query.Query<TEntity>.Select<T>(Korda.Data.Projection<T> projection)
```

**summary:** Selects a projection built by this exact query, or the root table's static row descriptor.

**inheritdoc:**

**param projection:** The exact query-built projection or root table row descriptor to select.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** The borrowed current-selection view; its owning query retains the native arena.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.Select``1(Korda.Query.SqlExpression{``0})`

```csharp
public Korda.Query.QuerySelection<T> Korda.Query.Query<TEntity>.Select<T>(Korda.Query.SqlExpression<T> expression)
```

**summary:** Selects one same-query native expression through its exact static codec.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** The borrowed current-selection view; its owning query retains the native arena.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.Select``3(Korda.Query.SqlExpression{``0},Korda.Query.SqlExpression{``1},System.Func{``0,``1,``2})`

```csharp
public Korda.Query.QuerySelection<TOut> Korda.Query.Query<TEntity>.Select<T1, T2, TOut>(Korda.Query.SqlExpression<T1> first, Korda.Query.SqlExpression<T2> second, System.Func<T1, T2, TOut> materializer)
```

**summary:** Selects two native expressions into an AOT-visible direct DTO factory.

**inheritdoc:**

**param first:** The first same-query native expression in result order.

**param second:** The second same-query native expression in result order.

**param materializer:** The statically compiled direct constructor or field reader; runtime reflection is not used.

**typeparam T1:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**typeparam T2:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**typeparam TOut:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** The borrowed current-selection view; its owning query retains the native arena.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.Skip(System.Int32)`

```csharp
public Korda.Query.Query<TEntity> Korda.Query.Query<TEntity>.Skip(int count)
```

**summary:** Sets a finite nonnegative offset only with known unique ordering.

**inheritdoc:**

**param count:** The nonnegative bounded row count.

**returns:** This mutable query for further synchronous composition before its first terminal.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.Take(System.Int32)`

```csharp
public Korda.Query.Query<TEntity> Korda.Query.Query<TEntity>.Take(int count)
```

**summary:** Sets a finite nonnegative native row limit; without order the selected rows are nondeterministic.

**inheritdoc:**

**param count:** The nonnegative bounded row count.

**returns:** This mutable query for further synchronous composition before its first terminal.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.ThenBy``1(Korda.Data.Column{`0,``0},Korda.Query.SortDirection,Korda.Query.NullOrder)`

```csharp
public Korda.Query.Query<TEntity> Korda.Query.Query<TEntity>.ThenBy<T>(Korda.Data.Column<TEntity, T> column, Korda.Query.SortDirection direction = Korda.Query.SortDirection.Ascending, Korda.Query.NullOrder nullOrder = Korda.Query.NullOrder.Last)
```

**summary:** Appends a root-column order after an existing order.

**inheritdoc:**

**param column:** The strongly typed column from the declared table; foreign table identities reject.

**param direction:** Explicit ascending or descending SQL order.

**param nullOrder:** Explicit placement of SQL NULL values in this ordering key.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** This mutable query for further synchronous composition before its first terminal.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.ThenBy``1(Korda.Query.SqlExpression{``0},Korda.Query.SortDirection,Korda.Query.NullOrder)`

```csharp
public Korda.Query.Query<TEntity> Korda.Query.Query<TEntity>.ThenBy<T>(Korda.Query.SqlExpression<T> expression, Korda.Query.SortDirection direction = Korda.Query.SortDirection.Ascending, Korda.Query.NullOrder nullOrder = Korda.Query.NullOrder.Last)
```

**summary:** Appends a same-query expression after an existing order.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param direction:** Explicit ascending or descending SQL order.

**param nullOrder:** Explicit placement of SQL NULL values in this ordering key.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** This mutable query for further synchronous composition before its first terminal.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.UpdateAsync(Korda.Data.Changes{`0},System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<long> Korda.Query.Query<TEntity>.UpdateAsync(Korda.Data.Changes<TEntity> changes, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Snapshots explicit typed changes and performs a filtered native set-based UPDATE.

**inheritdoc:**

**param changes:** The explicit immutable snapshot of permitted typed root-column assignments.

**param cancellationToken:** Cancellation requests native stop; completion still waits for cursor and operation retirement.

**returns:** The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**exception:** Cancellation requests termination and cleanup.

**exception:** The finite total query deadline expires; cleanup still completes.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.UpdateAsync(System.Action{Korda.Query.UpdateSetters{`0}},System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<long> Korda.Query.Query<TEntity>.UpdateAsync(System.Action<Korda.Query.UpdateSetters<TEntity>> setters, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Runs a synchronous typed setter callback, then applies complete native assignments and a filtered UPDATE.

**inheritdoc:**

**param setters:** The synchronous callback that stages permitted typed assignments before submission.

**param cancellationToken:** Cancellation requests native stop; completion still waits for cursor and operation retirement.

**returns:** The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**exception:** Cancellation requests termination and cleanup.

**exception:** The finite total query deadline expires; cleanup still completes.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.Value``1(``0,Korda.Data.ValueCodec{``0})`

```csharp
public Korda.Query.SqlExpression<T> Korda.Query.Query<TEntity>.Value<T>(T value, Korda.Data.ValueCodec<T> codec)
```

**summary:** Immediately snapshots a typed literal into the native arena.

**inheritdoc:**

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**param codec:** The statically known codec and exact nullability/configuration for the value.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.Where(Korda.Query.SqlPredicate)`

```csharp
public Korda.Query.Query<TEntity> Korda.Query.Query<TEntity>.Where(Korda.Query.SqlPredicate predicate)
```

**summary:** Appends a same-query native predicate with AND.

**inheritdoc:**

**param predicate:** The compatible native condition or synchronous typed callback; callbacks are invoked at this call.

**returns:** This mutable query for further synchronous composition before its first terminal.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.Where(System.Func{Korda.Query.QueryRow{`0},Korda.Query.SqlPredicate})`

```csharp
public Korda.Query.Query<TEntity> Korda.Query.Query<TEntity>.Where(System.Func<Korda.Query.QueryRow<TEntity>, Korda.Query.SqlPredicate> predicate)
```

**summary:** Invokes the typed callback synchronously once, then attaches its complete native predicate.

**inheritdoc:**

**param predicate:** The compatible native condition or synchronous typed callback; callbacks are invoked at this call.

**returns:** This mutable query for further synchronous composition before its first terminal.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.WhereEqual``1(Korda.Data.Column{`0,``0},``0)`

```csharp
public Korda.Query.Query<TEntity> Korda.Query.Query<TEntity>.WhereEqual<T>(Korda.Data.Column<TEntity, T> column, T value)
```

**summary:** Snapshots the value and appends root-column equality without a predicate delegate.

**inheritdoc:**

**param column:** The strongly typed column from the declared table; foreign table identities reject.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** This mutable query for further synchronous composition before its first terminal.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.WhereGreaterThanOrEqual``1(Korda.Data.Column{`0,``0},``0)`

```csharp
public Korda.Query.Query<TEntity> Korda.Query.Query<TEntity>.WhereGreaterThanOrEqual<T>(Korda.Data.Column<TEntity, T> column, T value)
```

**summary:** Snapshots the value and appends an inclusive native lower bound.

**inheritdoc:**

**param column:** The strongly typed column from the declared table; foreign table identities reject.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** This mutable query for further synchronous composition before its first terminal.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.WhereIf(System.Boolean,System.Func{Korda.Query.QueryRow{`0},Korda.Query.SqlPredicate})`

```csharp
public Korda.Query.Query<TEntity> Korda.Query.Query<TEntity>.WhereIf(bool condition, System.Func<Korda.Query.QueryRow<TEntity>, Korda.Query.SqlPredicate> predicate)
```

**summary:** Invokes and attaches the callback only when the condition is true; false binds no captured values.

**inheritdoc:**

**param condition:** The CLR branch condition, or same-query native CASE predicate required by this overload.

**param predicate:** The compatible native condition or synchronous typed callback; callbacks are invoked at this call.

**returns:** This mutable query for further synchronous composition before its first terminal.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.WhereIsNotNull``1(Korda.Data.Column{`0,``0})`

```csharp
public Korda.Query.Query<TEntity> Korda.Query.Query<TEntity>.WhereIsNotNull<T>(Korda.Data.Column<TEntity, T> column)
```

**summary:** Appends an explicit root-column IS NOT NULL predicate.

**inheritdoc:**

**param column:** The strongly typed column from the declared table; foreign table identities reject.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** This mutable query for further synchronous composition before its first terminal.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.WhereIsNull``1(Korda.Data.Column{`0,``0})`

```csharp
public Korda.Query.Query<TEntity> Korda.Query.Query<TEntity>.WhereIsNull<T>(Korda.Data.Column<TEntity, T> column)
```

**summary:** Appends an explicit root-column IS NULL predicate.

**inheritdoc:**

**param column:** The strongly typed column from the declared table; foreign table identities reject.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** This mutable query for further synchronous composition before its first terminal.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.Window`

```csharp
public Korda.Query.WindowDefinition Korda.Query.Query<TEntity>.Window()
```

**summary:** Creates finite query-owned native window state.

**inheritdoc:**

**returns:** The typed owner or proxy described above, retaining its original source/query association.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.WithTimeout(System.TimeSpan)`

```csharp
public Korda.Query.Query<TEntity> Korda.Query.Query<TEntity>.WithTimeout(System.TimeSpan timeout)
```

**summary:** Snapshots a finite total execution/cursor timeout; zero expires before dispatch.

**inheritdoc:**

**param timeout:** The finite nonnegative total execution and cursor deadline, snapshotted before dispatch.

**returns:** This mutable query for further synchronous composition before its first terminal.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.Query`1.With``1(Korda.Query.CommonTable{``0})`

```csharp
public Korda.Query.Query<TEntity> Korda.Query.Query<TEntity>.With<TCte>(Korda.Query.CommonTable<TCte> commonTable) where TCte : class
```

**summary:** Attaches a frozen same-source native CTE and its exact immutable parameter identities.

**inheritdoc:**

**param commonTable:** The immutable CTE from this exact source to retain in the query.

**typeparam TCte:** The statically known entity class with explicit table metadata.

**returns:** This mutable query for further synchronous composition before its first terminal.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `M:Korda.Query.RowLockMode.#ctor`

```csharp
public Korda.Query.RowLockMode.RowLockMode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Available only on PostgreSQL with an explicit live transaction. SQLite rejects the operation before provider dispatch.

## `M:Korda.Query.SetOperation.#ctor`

```csharp
public Korda.Query.SetOperation.SetOperation()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Operands must have the same source, field order, aliases, codecs and nullability. The native engine snapshots the right operand; SQL duplicate and NULL semantics apply.

## `M:Korda.Query.SortDirection.#ctor`

```csharp
public Korda.Query.SortDirection.SortDirection()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** The native query validates each enum value before mutation. Direction is explicit; it does not change SQL NULL ordering.

## `M:Korda.Query.SqlArithmeticExtensions.Add``1(Korda.Query.SqlExpression{System.Nullable{``0}},Korda.Query.SqlExpression{System.Nullable{``0}})`

```csharp
public static Korda.Query.SqlExpression<T?> Korda.Query.SqlArithmeticExtensions.Add<T>(this Korda.Query.SqlExpression<T?> expression, Korda.Query.SqlExpression<T?> value) where T : struct, System.Numerics.INumber<T>
```

**summary:** Performs native numeric add with explicit SQL NULL propagation.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. The native engine owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

## `M:Korda.Query.SqlArithmeticExtensions.Add``1(Korda.Query.SqlExpression{System.Nullable{``0}},``0)`

```csharp
public static Korda.Query.SqlExpression<T?> Korda.Query.SqlArithmeticExtensions.Add<T>(this Korda.Query.SqlExpression<T?> expression, T value) where T : struct, System.Numerics.INumber<T>
```

**summary:** Performs native numeric add with explicit SQL NULL propagation.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. The native engine owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

## `M:Korda.Query.SqlArithmeticExtensions.Add``1(Korda.Query.SqlExpression{``0},Korda.Query.SqlExpression{``0})`

```csharp
public static Korda.Query.SqlExpression<T> Korda.Query.SqlArithmeticExtensions.Add<T>(this Korda.Query.SqlExpression<T> expression, Korda.Query.SqlExpression<T> value) where T : System.Numerics.INumber<T>
```

**summary:** Performs native numeric add with explicit SQL NULL propagation.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. The native engine owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

## `M:Korda.Query.SqlArithmeticExtensions.Add``1(Korda.Query.SqlExpression{``0},``0)`

```csharp
public static Korda.Query.SqlExpression<T> Korda.Query.SqlArithmeticExtensions.Add<T>(this Korda.Query.SqlExpression<T> expression, T value) where T : System.Numerics.INumber<T>
```

**summary:** Performs native numeric add with explicit SQL NULL propagation.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. The native engine owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

## `M:Korda.Query.SqlArithmeticExtensions.Divide``1(Korda.Query.SqlExpression{System.Nullable{``0}},Korda.Query.SqlExpression{System.Nullable{``0}})`

```csharp
public static Korda.Query.SqlExpression<T?> Korda.Query.SqlArithmeticExtensions.Divide<T>(this Korda.Query.SqlExpression<T?> expression, Korda.Query.SqlExpression<T?> value) where T : struct, System.Numerics.INumber<T>
```

**summary:** Performs native numeric divide with explicit SQL NULL propagation.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. The native engine owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

## `M:Korda.Query.SqlArithmeticExtensions.Divide``1(Korda.Query.SqlExpression{System.Nullable{``0}},``0)`

```csharp
public static Korda.Query.SqlExpression<T?> Korda.Query.SqlArithmeticExtensions.Divide<T>(this Korda.Query.SqlExpression<T?> expression, T value) where T : struct, System.Numerics.INumber<T>
```

**summary:** Performs native numeric divide with explicit SQL NULL propagation.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. The native engine owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

## `M:Korda.Query.SqlArithmeticExtensions.Divide``1(Korda.Query.SqlExpression{``0},Korda.Query.SqlExpression{``0})`

```csharp
public static Korda.Query.SqlExpression<T> Korda.Query.SqlArithmeticExtensions.Divide<T>(this Korda.Query.SqlExpression<T> expression, Korda.Query.SqlExpression<T> value) where T : System.Numerics.INumber<T>
```

**summary:** Performs native numeric divide with explicit SQL NULL propagation.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. The native engine owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

## `M:Korda.Query.SqlArithmeticExtensions.Divide``1(Korda.Query.SqlExpression{``0},``0)`

```csharp
public static Korda.Query.SqlExpression<T> Korda.Query.SqlArithmeticExtensions.Divide<T>(this Korda.Query.SqlExpression<T> expression, T value) where T : System.Numerics.INumber<T>
```

**summary:** Performs native numeric divide with explicit SQL NULL propagation.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. The native engine owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

## `M:Korda.Query.SqlArithmeticExtensions.Multiply``1(Korda.Query.SqlExpression{System.Nullable{``0}},Korda.Query.SqlExpression{System.Nullable{``0}})`

```csharp
public static Korda.Query.SqlExpression<T?> Korda.Query.SqlArithmeticExtensions.Multiply<T>(this Korda.Query.SqlExpression<T?> expression, Korda.Query.SqlExpression<T?> value) where T : struct, System.Numerics.INumber<T>
```

**summary:** Performs native numeric multiply with explicit SQL NULL propagation.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. The native engine owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

## `M:Korda.Query.SqlArithmeticExtensions.Multiply``1(Korda.Query.SqlExpression{System.Nullable{``0}},``0)`

```csharp
public static Korda.Query.SqlExpression<T?> Korda.Query.SqlArithmeticExtensions.Multiply<T>(this Korda.Query.SqlExpression<T?> expression, T value) where T : struct, System.Numerics.INumber<T>
```

**summary:** Performs native numeric multiply with explicit SQL NULL propagation.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. The native engine owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

## `M:Korda.Query.SqlArithmeticExtensions.Multiply``1(Korda.Query.SqlExpression{``0},Korda.Query.SqlExpression{``0})`

```csharp
public static Korda.Query.SqlExpression<T> Korda.Query.SqlArithmeticExtensions.Multiply<T>(this Korda.Query.SqlExpression<T> expression, Korda.Query.SqlExpression<T> value) where T : System.Numerics.INumber<T>
```

**summary:** Performs native numeric multiply with explicit SQL NULL propagation.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. The native engine owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

## `M:Korda.Query.SqlArithmeticExtensions.Multiply``1(Korda.Query.SqlExpression{``0},``0)`

```csharp
public static Korda.Query.SqlExpression<T> Korda.Query.SqlArithmeticExtensions.Multiply<T>(this Korda.Query.SqlExpression<T> expression, T value) where T : System.Numerics.INumber<T>
```

**summary:** Performs native numeric multiply with explicit SQL NULL propagation.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. The native engine owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

## `M:Korda.Query.SqlArithmeticExtensions.Subtract``1(Korda.Query.SqlExpression{System.Nullable{``0}},Korda.Query.SqlExpression{System.Nullable{``0}})`

```csharp
public static Korda.Query.SqlExpression<T?> Korda.Query.SqlArithmeticExtensions.Subtract<T>(this Korda.Query.SqlExpression<T?> expression, Korda.Query.SqlExpression<T?> value) where T : struct, System.Numerics.INumber<T>
```

**summary:** Performs native numeric subtract with explicit SQL NULL propagation.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. The native engine owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

## `M:Korda.Query.SqlArithmeticExtensions.Subtract``1(Korda.Query.SqlExpression{System.Nullable{``0}},``0)`

```csharp
public static Korda.Query.SqlExpression<T?> Korda.Query.SqlArithmeticExtensions.Subtract<T>(this Korda.Query.SqlExpression<T?> expression, T value) where T : struct, System.Numerics.INumber<T>
```

**summary:** Performs native numeric subtract with explicit SQL NULL propagation.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. The native engine owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

## `M:Korda.Query.SqlArithmeticExtensions.Subtract``1(Korda.Query.SqlExpression{``0},Korda.Query.SqlExpression{``0})`

```csharp
public static Korda.Query.SqlExpression<T> Korda.Query.SqlArithmeticExtensions.Subtract<T>(this Korda.Query.SqlExpression<T> expression, Korda.Query.SqlExpression<T> value) where T : System.Numerics.INumber<T>
```

**summary:** Performs native numeric subtract with explicit SQL NULL propagation.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. The native engine owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

## `M:Korda.Query.SqlArithmeticExtensions.Subtract``1(Korda.Query.SqlExpression{``0},``0)`

```csharp
public static Korda.Query.SqlExpression<T> Korda.Query.SqlArithmeticExtensions.Subtract<T>(this Korda.Query.SqlExpression<T> expression, T value) where T : System.Numerics.INumber<T>
```

**summary:** Performs native numeric subtract with explicit SQL NULL propagation.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. The native engine owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

## `M:Korda.Query.SqlExpression`1.#ctor`

```csharp
public Korda.Query.SqlExpression<T>.SqlExpression()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

## `M:Korda.Query.SqlExpression`1.Between(`0,`0)`

```csharp
public Korda.Query.SqlPredicate Korda.Query.SqlExpression<T>.Between(T lower, T upper)
```

**summary:** Snapshots two bounds and creates native inclusive BETWEEN.

**inheritdoc:**

**param lower:** The inclusive lower endpoint, copied before the method returns.

**param upper:** The inclusive upper endpoint, copied before the method returns.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

## `M:Korda.Query.SqlExpression`1.Equal(Korda.Query.SqlExpression{`0})`

```csharp
public Korda.Query.SqlPredicate Korda.Query.SqlExpression<T>.Equal(Korda.Query.SqlExpression<T> other)
```

**summary:** Applies native Equal with exact codec/source checks and SQL NULL semantics.

**inheritdoc:**

**param other:** The compatible same-source selection or same-query expression required by this operation.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

## `M:Korda.Query.SqlExpression`1.Equal(`0)`

```csharp
public Korda.Query.SqlPredicate Korda.Query.SqlExpression<T>.Equal(T value)
```

**summary:** Applies native Equal with exact codec/source checks and SQL NULL semantics.

**inheritdoc:**

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

## `M:Korda.Query.SqlExpression`1.GreaterThan(Korda.Query.SqlExpression{`0})`

```csharp
public Korda.Query.SqlPredicate Korda.Query.SqlExpression<T>.GreaterThan(Korda.Query.SqlExpression<T> other)
```

**summary:** Applies native GreaterThan with exact codec/source checks and SQL NULL semantics.

**inheritdoc:**

**param other:** The compatible same-source selection or same-query expression required by this operation.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

## `M:Korda.Query.SqlExpression`1.GreaterThan(`0)`

```csharp
public Korda.Query.SqlPredicate Korda.Query.SqlExpression<T>.GreaterThan(T value)
```

**summary:** Applies native GreaterThan with exact codec/source checks and SQL NULL semantics.

**inheritdoc:**

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

## `M:Korda.Query.SqlExpression`1.GreaterThanOrEqual(Korda.Query.SqlExpression{`0})`

```csharp
public Korda.Query.SqlPredicate Korda.Query.SqlExpression<T>.GreaterThanOrEqual(Korda.Query.SqlExpression<T> other)
```

**summary:** Applies native GreaterThanOrEqual with exact codec/source checks and SQL NULL semantics.

**inheritdoc:**

**param other:** The compatible same-source selection or same-query expression required by this operation.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

## `M:Korda.Query.SqlExpression`1.GreaterThanOrEqual(`0)`

```csharp
public Korda.Query.SqlPredicate Korda.Query.SqlExpression<T>.GreaterThanOrEqual(T value)
```

**summary:** Applies native GreaterThanOrEqual with exact codec/source checks and SQL NULL semantics.

**inheritdoc:**

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

## `M:Korda.Query.SqlExpression`1.In(Korda.Query.QuerySelection{`0})`

```csharp
public Korda.Query.SqlPredicate Korda.Query.SqlExpression<T>.In(Korda.Query.QuerySelection<T> subquery)
```

**summary:** Snapshots a compatible same-source single-column subquery into native IN.

**inheritdoc:**

**param subquery:** A current same-source selection whose native plan is cloned under admission.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

## `M:Korda.Query.SqlExpression`1.In(System.ReadOnlySpan{`0})`

```csharp
public Korda.Query.SqlPredicate Korda.Query.SqlExpression<T>.In(System.ReadOnlySpan<T> values)
```

**summary:** Snapshots a finite value span; an empty span yields native FALSE.

**inheritdoc:**

**param values:** The finite typed input values, copied before returning; the span is never retained.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

## `M:Korda.Query.SqlExpression`1.IsNotNull`

```csharp
public Korda.Query.SqlPredicate Korda.Query.SqlExpression<T>.IsNotNull()
```

**summary:** Tests SQL non-NULL explicitly.

**inheritdoc:**

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

## `M:Korda.Query.SqlExpression`1.IsNull`

```csharp
public Korda.Query.SqlPredicate Korda.Query.SqlExpression<T>.IsNull()
```

**summary:** Tests SQL NULL explicitly without substituting a sentinel.

**inheritdoc:**

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

## `M:Korda.Query.SqlExpression`1.LessThan(Korda.Query.SqlExpression{`0})`

```csharp
public Korda.Query.SqlPredicate Korda.Query.SqlExpression<T>.LessThan(Korda.Query.SqlExpression<T> other)
```

**summary:** Applies native LessThan with exact codec/source checks and SQL NULL semantics.

**inheritdoc:**

**param other:** The compatible same-source selection or same-query expression required by this operation.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

## `M:Korda.Query.SqlExpression`1.LessThan(`0)`

```csharp
public Korda.Query.SqlPredicate Korda.Query.SqlExpression<T>.LessThan(T value)
```

**summary:** Applies native LessThan with exact codec/source checks and SQL NULL semantics.

**inheritdoc:**

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

## `M:Korda.Query.SqlExpression`1.LessThanOrEqual(Korda.Query.SqlExpression{`0})`

```csharp
public Korda.Query.SqlPredicate Korda.Query.SqlExpression<T>.LessThanOrEqual(Korda.Query.SqlExpression<T> other)
```

**summary:** Applies native LessThanOrEqual with exact codec/source checks and SQL NULL semantics.

**inheritdoc:**

**param other:** The compatible same-source selection or same-query expression required by this operation.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

## `M:Korda.Query.SqlExpression`1.LessThanOrEqual(`0)`

```csharp
public Korda.Query.SqlPredicate Korda.Query.SqlExpression<T>.LessThanOrEqual(T value)
```

**summary:** Applies native LessThanOrEqual with exact codec/source checks and SQL NULL semantics.

**inheritdoc:**

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

## `M:Korda.Query.SqlExpression`1.NotEqual(Korda.Query.SqlExpression{`0})`

```csharp
public Korda.Query.SqlPredicate Korda.Query.SqlExpression<T>.NotEqual(Korda.Query.SqlExpression<T> other)
```

**summary:** Applies native NotEqual with exact codec/source checks and SQL NULL semantics.

**inheritdoc:**

**param other:** The compatible same-source selection or same-query expression required by this operation.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

## `M:Korda.Query.SqlExpression`1.NotEqual(`0)`

```csharp
public Korda.Query.SqlPredicate Korda.Query.SqlExpression<T>.NotEqual(T value)
```

**summary:** Applies native NotEqual with exact codec/source checks and SQL NULL semantics.

**inheritdoc:**

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

## `M:Korda.Query.SqlExpression`1.NotIn(System.ReadOnlySpan{`0})`

```csharp
public Korda.Query.SqlPredicate Korda.Query.SqlExpression<T>.NotIn(System.ReadOnlySpan<T> values)
```

**summary:** Snapshots a finite value span; an empty span yields native TRUE.

**inheritdoc:**

**param values:** The finite typed input values, copied before returning; the span is never retained.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

## `M:Korda.Query.SqlExpression`1.NullSafeEqual(Korda.Query.SqlExpression{`0})`

```csharp
public Korda.Query.SqlPredicate Korda.Query.SqlExpression<T>.NullSafeEqual(Korda.Query.SqlExpression<T> other)
```

**summary:** Applies native NullSafeEqual with exact codec/source checks and SQL NULL semantics.

**inheritdoc:**

**param other:** The compatible same-source selection or same-query expression required by this operation.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

## `M:Korda.Query.SqlExpression`1.NullSafeEqual(`0)`

```csharp
public Korda.Query.SqlPredicate Korda.Query.SqlExpression<T>.NullSafeEqual(T value)
```

**summary:** Applies native NullSafeEqual with exact codec/source checks and SQL NULL semantics.

**inheritdoc:**

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

## `M:Korda.Query.SqlExpression`1.Over(Korda.Query.WindowDefinition)`

```csharp
public Korda.Query.SqlExpression<T> Korda.Query.SqlExpression<T>.Over(Korda.Query.WindowDefinition window)
```

**summary:** Snapshots same-query native window state onto an eligible aggregate or rank expression.

**inheritdoc:**

**param window:** The same-query window definition to snapshot at this call.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

## `M:Korda.Query.SqlFunctions.Average(Korda.Query.SqlExpression{System.Decimal})`

```csharp
public static Korda.Query.SqlExpression<decimal?> Korda.Query.SqlFunctions.Average(Korda.Query.SqlExpression<decimal> expression)
```

**summary:** Aggregates native decimal inputs to nullable decimal, preserving empty-input NULL and explicit conversion failures.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Average(Korda.Query.SqlExpression{System.Double})`

```csharp
public static Korda.Query.SqlExpression<double?> Korda.Query.SqlFunctions.Average(Korda.Query.SqlExpression<double> expression)
```

**summary:** Aggregates native double inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Average(Korda.Query.SqlExpression{System.Int32})`

```csharp
public static Korda.Query.SqlExpression<double?> Korda.Query.SqlFunctions.Average(Korda.Query.SqlExpression<int> expression)
```

**summary:** Aggregates native int inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Average(Korda.Query.SqlExpression{System.Int64})`

```csharp
public static Korda.Query.SqlExpression<double?> Korda.Query.SqlFunctions.Average(Korda.Query.SqlExpression<long> expression)
```

**summary:** Aggregates native long inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Average(Korda.Query.SqlExpression{System.Nullable{System.Decimal}})`

```csharp
public static Korda.Query.SqlExpression<decimal?> Korda.Query.SqlFunctions.Average(Korda.Query.SqlExpression<decimal?> expression)
```

**summary:** Aggregates native decimal? inputs to nullable decimal, preserving empty-input NULL and explicit conversion failures.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Average(Korda.Query.SqlExpression{System.Nullable{System.Double}})`

```csharp
public static Korda.Query.SqlExpression<double?> Korda.Query.SqlFunctions.Average(Korda.Query.SqlExpression<double?> expression)
```

**summary:** Aggregates native double? inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Average(Korda.Query.SqlExpression{System.Nullable{System.Int32}})`

```csharp
public static Korda.Query.SqlExpression<double?> Korda.Query.SqlFunctions.Average(Korda.Query.SqlExpression<int?> expression)
```

**summary:** Aggregates native int? inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Average(Korda.Query.SqlExpression{System.Nullable{System.Int64}})`

```csharp
public static Korda.Query.SqlExpression<double?> Korda.Query.SqlFunctions.Average(Korda.Query.SqlExpression<long?> expression)
```

**summary:** Aggregates native long? inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Average(Korda.Query.SqlExpression{System.Nullable{System.Single}})`

```csharp
public static Korda.Query.SqlExpression<double?> Korda.Query.SqlFunctions.Average(Korda.Query.SqlExpression<float?> expression)
```

**summary:** Aggregates native float? inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Average(Korda.Query.SqlExpression{System.Single})`

```csharp
public static Korda.Query.SqlExpression<double?> Korda.Query.SqlFunctions.Average(Korda.Query.SqlExpression<float> expression)
```

**summary:** Aggregates native float inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Case``1(Korda.Query.SqlPredicate,Korda.Query.SqlExpression{``0},Korda.Query.SqlExpression{``0})`

```csharp
public static Korda.Query.SqlExpression<T> Korda.Query.SqlFunctions.Case<T>(Korda.Query.SqlPredicate condition, Korda.Query.SqlExpression<T> whenTrue, Korda.Query.SqlExpression<T> whenFalse)
```

**summary:** Creates a native CASE with same-query compatible scalar alternatives.

**inheritdoc:**

**param condition:** The CLR branch condition, or same-query native CASE predicate required by this overload.

**param whenTrue:** The compatible same-query expression for a true SQL condition.

**param whenFalse:** The compatible same-query expression for false or unknown SQL condition.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.CoalesceReference``1(Korda.Query.SqlExpression{``0},``0)`

```csharp
public static Korda.Query.SqlExpression<T> Korda.Query.SqlFunctions.CoalesceReference<T>(Korda.Query.SqlExpression<T?> expression, T fallback) where T : class
```

**summary:** Produces a non-null reference scalar with native SQL COALESCE.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param fallback:** The non-null typed fallback, snapshotted now and used by native COALESCE.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Coalesce``1(Korda.Query.SqlExpression{System.Nullable{``0}},``0)`

```csharp
public static Korda.Query.SqlExpression<T> Korda.Query.SqlFunctions.Coalesce<T>(Korda.Query.SqlExpression<T?> expression, T fallback) where T : struct
```

**summary:** Produces a non-null scalar with native SQL COALESCE and an explicitly copied fallback.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param fallback:** The non-null typed fallback, snapshotted now and used by native COALESCE.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Count``1(Korda.Query.SqlExpression{``0},System.Boolean)`

```csharp
public static Korda.Query.SqlExpression<long> Korda.Query.SqlFunctions.Count<T>(Korda.Query.SqlExpression<T> expression, bool distinct = false)
```

**summary:** Counts non-null native values, optionally using DISTINCT, with exact Int64 result metadata.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param distinct:** Whether native aggregate inputs apply DISTINCT with SQL equality semantics.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.MaxNullableText(Korda.Query.SqlExpression{System.String})`

```csharp
public static Korda.Query.SqlExpression<string?> Korda.Query.SqlFunctions.MaxNullableText(Korda.Query.SqlExpression<string?> expression)
```

**summary:** Returns native text maximum under SQL collation with empty-input NULL.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.MaxText(Korda.Query.SqlExpression{System.String})`

```csharp
public static Korda.Query.SqlExpression<string?> Korda.Query.SqlFunctions.MaxText(Korda.Query.SqlExpression<string> expression)
```

**summary:** Returns native text maximum under SQL collation with empty-input NULL.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Max``1(Korda.Query.SqlExpression{System.Nullable{``0}})`

```csharp
public static Korda.Query.SqlExpression<T?> Korda.Query.SqlFunctions.Max<T>(Korda.Query.SqlExpression<T?> expression) where T : struct
```

**summary:** Returns the native maximum with the exact scalar codec and empty-input SQL NULL.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Max``1(Korda.Query.SqlExpression{``0})`

```csharp
public static Korda.Query.SqlExpression<T?> Korda.Query.SqlFunctions.Max<T>(Korda.Query.SqlExpression<T> expression) where T : struct
```

**summary:** Returns the native maximum with the exact scalar codec and empty-input SQL NULL.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.MinNullableText(Korda.Query.SqlExpression{System.String})`

```csharp
public static Korda.Query.SqlExpression<string?> Korda.Query.SqlFunctions.MinNullableText(Korda.Query.SqlExpression<string?> expression)
```

**summary:** Returns native text minimum under SQL collation with empty-input NULL.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.MinText(Korda.Query.SqlExpression{System.String})`

```csharp
public static Korda.Query.SqlExpression<string?> Korda.Query.SqlFunctions.MinText(Korda.Query.SqlExpression<string> expression)
```

**summary:** Returns native text minimum under SQL collation with empty-input NULL.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Min``1(Korda.Query.SqlExpression{System.Nullable{``0}})`

```csharp
public static Korda.Query.SqlExpression<T?> Korda.Query.SqlFunctions.Min<T>(Korda.Query.SqlExpression<T?> expression) where T : struct
```

**summary:** Returns the native minimum with the exact scalar codec and empty-input SQL NULL.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Min``1(Korda.Query.SqlExpression{``0})`

```csharp
public static Korda.Query.SqlExpression<T?> Korda.Query.SqlFunctions.Min<T>(Korda.Query.SqlExpression<T> expression) where T : struct
```

**summary:** Returns the native minimum with the exact scalar codec and empty-input SQL NULL.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Rank(Korda.Query.WindowDefinition)`

```csharp
public static Korda.Query.SqlExpression<long> Korda.Query.SqlFunctions.Rank(Korda.Query.WindowDefinition window)
```

**summary:** Snapshots a native window into an exact Int64 RANK expression.

**inheritdoc:**

**param window:** The same-query window definition to snapshot at this call.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.RowNumber(Korda.Query.WindowDefinition)`

```csharp
public static Korda.Query.SqlExpression<long> Korda.Query.SqlFunctions.RowNumber(Korda.Query.WindowDefinition window)
```

**summary:** Snapshots a native window into an exact Int64 ROW_NUMBER expression.

**inheritdoc:**

**param window:** The same-query window definition to snapshot at this call.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Sum(Korda.Query.SqlExpression{System.Decimal})`

```csharp
public static Korda.Query.SqlExpression<decimal?> Korda.Query.SqlFunctions.Sum(Korda.Query.SqlExpression<decimal> expression)
```

**summary:** Aggregates native decimal inputs to nullable decimal, preserving empty-input NULL and explicit conversion failures.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Sum(Korda.Query.SqlExpression{System.Double})`

```csharp
public static Korda.Query.SqlExpression<double?> Korda.Query.SqlFunctions.Sum(Korda.Query.SqlExpression<double> expression)
```

**summary:** Aggregates native double inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Sum(Korda.Query.SqlExpression{System.Int32})`

```csharp
public static Korda.Query.SqlExpression<long?> Korda.Query.SqlFunctions.Sum(Korda.Query.SqlExpression<int> expression)
```

**summary:** Aggregates native int inputs to nullable long, preserving empty-input NULL and explicit conversion failures.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Sum(Korda.Query.SqlExpression{System.Int64})`

```csharp
public static Korda.Query.SqlExpression<long?> Korda.Query.SqlFunctions.Sum(Korda.Query.SqlExpression<long> expression)
```

**summary:** Aggregates native long inputs to nullable long, preserving empty-input NULL and explicit conversion failures.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Sum(Korda.Query.SqlExpression{System.Nullable{System.Decimal}})`

```csharp
public static Korda.Query.SqlExpression<decimal?> Korda.Query.SqlFunctions.Sum(Korda.Query.SqlExpression<decimal?> expression)
```

**summary:** Aggregates native decimal? inputs to nullable decimal, preserving empty-input NULL and explicit conversion failures.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Sum(Korda.Query.SqlExpression{System.Nullable{System.Double}})`

```csharp
public static Korda.Query.SqlExpression<double?> Korda.Query.SqlFunctions.Sum(Korda.Query.SqlExpression<double?> expression)
```

**summary:** Aggregates native double? inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Sum(Korda.Query.SqlExpression{System.Nullable{System.Int32}})`

```csharp
public static Korda.Query.SqlExpression<long?> Korda.Query.SqlFunctions.Sum(Korda.Query.SqlExpression<int?> expression)
```

**summary:** Aggregates native int? inputs to nullable long, preserving empty-input NULL and explicit conversion failures.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Sum(Korda.Query.SqlExpression{System.Nullable{System.Int64}})`

```csharp
public static Korda.Query.SqlExpression<long?> Korda.Query.SqlFunctions.Sum(Korda.Query.SqlExpression<long?> expression)
```

**summary:** Aggregates native long? inputs to nullable long, preserving empty-input NULL and explicit conversion failures.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Sum(Korda.Query.SqlExpression{System.Nullable{System.Single}})`

```csharp
public static Korda.Query.SqlExpression<double?> Korda.Query.SqlFunctions.Sum(Korda.Query.SqlExpression<float?> expression)
```

**summary:** Aggregates native float? inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlFunctions.Sum(Korda.Query.SqlExpression{System.Single})`

```csharp
public static Korda.Query.SqlExpression<double?> Korda.Query.SqlFunctions.Sum(Korda.Query.SqlExpression<float> expression)
```

**summary:** Aggregates native float inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `M:Korda.Query.SqlNullabilityExtensions.AsNullableReference``1(Korda.Query.SqlExpression{``0})`

```csharp
public static Korda.Query.SqlExpression<T?> Korda.Query.SqlNullabilityExtensions.AsNullableReference<T>(this Korda.Query.SqlExpression<T> expression) where T : class
```

**summary:** Returns a fresh same-query nullable reference token; already-nullable metadata rejects.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** These forward-only native metadata lifts preserve the SQL value and codec configuration. They do not emit a sentinel, COALESCE or reverse conversion. Default/foreign owners reject, and repeating a reference lift rejects its already nullable descriptor.

## `M:Korda.Query.SqlNullabilityExtensions.AsNullable``1(Korda.Query.SqlExpression{``0})`

```csharp
public static Korda.Query.SqlExpression<T?> Korda.Query.SqlNullabilityExtensions.AsNullable<T>(this Korda.Query.SqlExpression<T> expression) where T : struct
```

**summary:** Returns a fresh same-query nullable scalar token, preserving the exact value and codec.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** These forward-only native metadata lifts preserve the SQL value and codec configuration. They do not emit a sentinel, COALESCE or reverse conversion. Default/foreign owners reject, and repeating a reference lift rejects its already nullable descriptor.

## `M:Korda.Query.SqlPredicate.#ctor`

```csharp
public Korda.Query.SqlPredicate.SqlPredicate()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Boolean composition uses SQL three-valued logic and performs one direct native operation.

## `M:Korda.Query.SqlPredicate.And(Korda.Query.SqlPredicate)`

```csharp
public Korda.Query.SqlPredicate Korda.Query.SqlPredicate.And(Korda.Query.SqlPredicate other)
```

**summary:** Combines same-query conditions with native AND.

**inheritdoc:**

**param other:** The compatible same-source selection or same-query expression required by this operation.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Boolean composition uses SQL three-valued logic and performs one direct native operation.

## `M:Korda.Query.SqlPredicate.Not`

```csharp
public Korda.Query.SqlPredicate Korda.Query.SqlPredicate.Not()
```

**summary:** Negates this condition using native SQL NOT.

**inheritdoc:**

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Boolean composition uses SQL three-valued logic and performs one direct native operation.

## `M:Korda.Query.SqlPredicate.Or(Korda.Query.SqlPredicate)`

```csharp
public Korda.Query.SqlPredicate Korda.Query.SqlPredicate.Or(Korda.Query.SqlPredicate other)
```

**summary:** Combines same-query conditions with native OR.

**inheritdoc:**

**param other:** The compatible same-source selection or same-query expression required by this operation.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Boolean composition uses SQL three-valued logic and performs one direct native operation.

## `M:Korda.Query.SqlStringExtensions.Collate(Korda.Query.SqlExpression{System.String},System.String)`

```csharp
public static Korda.Query.SqlExpression<string> Korda.Query.SqlStringExtensions.Collate(this Korda.Query.SqlExpression<string> expression, string collation)
```

**summary:** Applies only a collation admitted by the provider's fixed allowlist.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param collation:** The exact collation name in the current provider's allowlist.

**returns:** A same-query borrowed native scalar token with exact typed codec and SQL nullability.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Literal-pattern methods snapshot and escape the supplied text before native SQL rendering; LikePattern deliberately accepts SQL wildcards. Collate resolves an exact provider allowlist, never arbitrary SQL. NULL behavior follows SQL three-valued logic.

## `M:Korda.Query.SqlStringExtensions.ContainsLiteral(Korda.Query.SqlExpression{System.String},System.String)`

```csharp
public static Korda.Query.SqlPredicate Korda.Query.SqlStringExtensions.ContainsLiteral(this Korda.Query.SqlExpression<string> expression, string value)
```

**summary:** Escapes wildcard characters and binds a literal contains pattern.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Literal-pattern methods snapshot and escape the supplied text before native SQL rendering; LikePattern deliberately accepts SQL wildcards. Collate resolves an exact provider allowlist, never arbitrary SQL. NULL behavior follows SQL three-valued logic.

## `M:Korda.Query.SqlStringExtensions.ContainsNullableLiteral(Korda.Query.SqlExpression{System.String},System.String)`

```csharp
public static Korda.Query.SqlPredicate Korda.Query.SqlStringExtensions.ContainsNullableLiteral(this Korda.Query.SqlExpression<string?> expression, string value)
```

**summary:** Escapes literal wildcard characters and searches nullable SQL text.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Literal-pattern methods snapshot and escape the supplied text before native SQL rendering; LikePattern deliberately accepts SQL wildcards. Collate resolves an exact provider allowlist, never arbitrary SQL. NULL behavior follows SQL three-valued logic.

## `M:Korda.Query.SqlStringExtensions.EndsWithLiteral(Korda.Query.SqlExpression{System.String},System.String)`

```csharp
public static Korda.Query.SqlPredicate Korda.Query.SqlStringExtensions.EndsWithLiteral(this Korda.Query.SqlExpression<string> expression, string value)
```

**summary:** Escapes wildcard characters and binds a literal suffix pattern.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Literal-pattern methods snapshot and escape the supplied text before native SQL rendering; LikePattern deliberately accepts SQL wildcards. Collate resolves an exact provider allowlist, never arbitrary SQL. NULL behavior follows SQL three-valued logic.

## `M:Korda.Query.SqlStringExtensions.Like(Korda.Query.SqlExpression{System.String},System.String,System.Char)`

```csharp
public static Korda.Query.SqlPredicate Korda.Query.SqlStringExtensions.Like(this Korda.Query.SqlExpression<string> expression, string pattern, char escape = '\\')
```

**summary:** Binds the supplied raw SQL LIKE pattern with an explicit escape character.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param pattern:** An explicit SQL LIKE pattern; percent and underscore retain wildcard meaning.

**param escape:** The explicit single escape character for the SQL LIKE pattern.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Literal-pattern methods snapshot and escape the supplied text before native SQL rendering; LikePattern deliberately accepts SQL wildcards. Collate resolves an exact provider allowlist, never arbitrary SQL. NULL behavior follows SQL three-valued logic.

## `M:Korda.Query.SqlStringExtensions.LikeNullable(Korda.Query.SqlExpression{System.String},System.String,System.Char)`

```csharp
public static Korda.Query.SqlPredicate Korda.Query.SqlStringExtensions.LikeNullable(this Korda.Query.SqlExpression<string?> expression, string pattern, char escape = '\\')
```

**summary:** Uses an explicit raw LIKE pattern against nullable SQL text.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param pattern:** An explicit SQL LIKE pattern; percent and underscore retain wildcard meaning.

**param escape:** The explicit single escape character for the SQL LIKE pattern.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Literal-pattern methods snapshot and escape the supplied text before native SQL rendering; LikePattern deliberately accepts SQL wildcards. Collate resolves an exact provider allowlist, never arbitrary SQL. NULL behavior follows SQL three-valued logic.

## `M:Korda.Query.SqlStringExtensions.StartsWithLiteral(Korda.Query.SqlExpression{System.String},System.String)`

```csharp
public static Korda.Query.SqlPredicate Korda.Query.SqlStringExtensions.StartsWithLiteral(this Korda.Query.SqlExpression<string> expression, string value)
```

**summary:** Escapes wildcard characters and binds a literal prefix pattern.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**returns:** A same-query borrowed native predicate token with SQL three-valued semantics.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** Literal-pattern methods snapshot and escape the supplied text before native SQL rendering; LikePattern deliberately accepts SQL wildcards. Collate resolves an exact provider allowlist, never arbitrary SQL. NULL behavior follows SQL three-valued logic.

## `M:Korda.Query.UpdateSetters`1.Set``1(Korda.Data.Column{`0,``0},Korda.Query.SqlExpression{``0})`

```csharp
public Korda.Query.UpdateSetters<TEntity> Korda.Query.UpdateSetters<TEntity>.Set<T>(Korda.Data.Column<TEntity, T> column, Korda.Query.SqlExpression<T> value)
```

**summary:** Stages a compatible same-query expression; assignments attach only after the callback succeeds.

**inheritdoc:**

**param column:** The strongly typed column from the declared table; foreign table identities reject.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** The typed owner or proxy described above, retaining its original source/query association.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** The owner is valid only during the synchronous update callback. Each scalar is snapshotted immediately; duplicate, generated and primary-key assignments reject. Assignment clauses attach after callback success; no tracked entity or managed SQL expression tree is created.

## `M:Korda.Query.UpdateSetters`1.Set``1(Korda.Data.Column{`0,``0},``0)`

```csharp
public Korda.Query.UpdateSetters<TEntity> Korda.Query.UpdateSetters<TEntity>.Set<T>(Korda.Data.Column<TEntity, T> column, T value)
```

**summary:** Copies a typed value for a nonkey, nongenerated root-column assignment.

**inheritdoc:**

**param column:** The strongly typed column from the declared table; foreign table identities reject.

**param value:** The typed value to snapshot now, or the compatible same-query native expression.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** The typed owner or proxy described above, retaining its original source/query association.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** The owner is valid only during the synchronous update callback. Each scalar is snapshotted immediately; duplicate, generated and primary-key assignments reject. Assignment clauses attach after callback success; no tracked entity or managed SQL expression tree is created.

## `M:Korda.Query.WindowDefinition.OrderBy``1(Korda.Query.SqlExpression{``0},Korda.Query.SortDirection,Korda.Query.NullOrder)`

```csharp
public Korda.Query.WindowDefinition Korda.Query.WindowDefinition.OrderBy<T>(Korda.Query.SqlExpression<T> expression, Korda.Query.SortDirection direction = Korda.Query.SortDirection.Ascending, Korda.Query.NullOrder nullOrder = Korda.Query.NullOrder.Last)
```

**summary:** Appends native ordering with explicit direction and SQL NULL placement.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**param direction:** Explicit ascending or descending SQL order.

**param nullOrder:** Explicit placement of SQL NULL values in this ordering key.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** The typed owner or proxy described above, retaining its original source/query association.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** The owning query bounds lifetime and concurrent access. Rank, RowNumber and Over snapshot native window state immediately; subsequent mutation cannot change existing expressions. Defaults and limits follow the provider capability contract.

## `M:Korda.Query.WindowDefinition.PartitionBy``1(Korda.Query.SqlExpression{``0})`

```csharp
public Korda.Query.WindowDefinition Korda.Query.WindowDefinition.PartitionBy<T>(Korda.Query.SqlExpression<T> expression)
```

**summary:** Appends a same-query native partition expression.

**inheritdoc:**

**param expression:** The compatible native scalar owned by the same live query.

**typeparam T:** The statically known CLR value or owned result type with the exact declared codec/nullability.

**returns:** The typed owner or proxy described above, retaining its original source/query association.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** The owning query bounds lifetime and concurrent access. Rank, RowNumber and Over snapshot native window state immediately; subsequent mutation cannot change existing expressions. Defaults and limits follow the provider capability contract.

## `M:Korda.Query.WindowDefinition.RowsBetween(Korda.Query.FrameBoundary,Korda.Query.FrameBoundary)`

```csharp
public Korda.Query.WindowDefinition Korda.Query.WindowDefinition.RowsBetween(Korda.Query.FrameBoundary start, Korda.Query.FrameBoundary end)
```

**summary:** Sets one supported ROWS frame; invalid endpoints reject before publication.

**inheritdoc:**

**param start:** The supported starting ROWS frame endpoint.

**param end:** The supported ending ROWS frame endpoint.

**returns:** The typed owner or proxy described above, retaining its original source/query association.

**exception:** An argument, owner association or codec/nullability is incompatible.

**exception:** The owner, projection or one-shot terminal is in an invalid state.

**exception:** The required native owner or its parent has closed admission.

**exception:** Native capability, finite resource limits or provider execution rejects the operation.

**Inherited type policy:** The owning query bounds lifetime and concurrent access. Rank, RowNumber and Over snapshot native window state immediately; subsequent mutation cannot change existing expressions. Defaults and limits follow the provider capability contract.

## `P:Korda.Database.BulkOptions.Atomicity`

```csharp
public Korda.Database.BulkAtomicity Korda.Database.BulkOptions.Atomicity { get; init; }
```

**summary:** Gets the atomicity policy; whole-operation atomicity is the default.

**Inherited type policy:** This managed configuration owns no native resource. A bulk operation reads its immutable values at admission, then lowers the requested row count to source byte, node and parameter limits.

## `P:Korda.Database.BulkOptions.BatchSize`

```csharp
public int Korda.Database.BulkOptions.BatchSize { get; init; }
```

**summary:** Gets the requested positive maximum rows per batch; byte, node and parameter limits can lower it.

**Inherited type policy:** This managed configuration owns no native resource. A bulk operation reads its immutable values at admission, then lowers the requested row count to source byte, node and parameter limits.

## `P:Korda.Database.BulkProgress.CompletedBatches`

```csharp
public int Korda.Database.BulkProgress.CompletedBatches { get; init; }
```

**summary:** The fully acknowledged batch count.

**Inherited type policy:** The original admitted bulk exception retains this managed snapshot after cleanup. Counts describe previously completed batches, which All may have rolled back; they do not prove current-batch effects or authorize replay. The record owns no native resource and remains readable after session/source disposal.

## `P:Korda.Database.BulkProgress.CompletedBatchesCommitted`

```csharp
public bool Korda.Database.BulkProgress.CompletedBatchesCommitted { get; init; }
```

**summary:** True only for confirmed preceding PerBatch commits.

**Inherited type policy:** The original admitted bulk exception retains this managed snapshot after cleanup. Counts describe previously completed batches, which All may have rolled back; they do not prove current-batch effects or authorize replay. The record owns no native resource and remains readable after session/source disposal.

## `P:Korda.Database.BulkProgress.CurrentTransactionOutcome`

```csharp
public Korda.Database.TransactionOutcome Korda.Database.BulkProgress.CurrentTransactionOutcome { get; init; }
```

**summary:** Actual transaction knowledge, retaining Unknown independently.

**Inherited type policy:** The original admitted bulk exception retains this managed snapshot after cleanup. Counts describe previously completed batches, which All may have rolled back; they do not prove current-batch effects or authorize replay. The record owns no native resource and remains readable after session/source disposal.

## `P:Korda.Database.BulkProgress.RowsAffectedInCompletedBatches`

```csharp
public long Korda.Database.BulkProgress.RowsAffectedInCompletedBatches { get; init; }
```

**summary:** Affected rows only in fully acknowledged preceding batches.

**Inherited type policy:** The original admitted bulk exception retains this managed snapshot after cleanup. Counts describe previously completed batches, which All may have rolled back; they do not prove current-batch effects or authorize replay. The record owns no native resource and remains readable after session/source disposal.

## `P:Korda.Database.BulkResult.Batches`

```csharp
public int Korda.Database.BulkResult.Batches { get; init; }
```

**summary:** The number of fully acknowledged batches.

**Inherited type policy:** The owned immutable result contains only acknowledged counts and an ownership observation. Constructing it allocates one managed record; it retains no session, transaction, cursor or native buffer.

## `P:Korda.Database.BulkResult.CommittedByOperation`

```csharp
public bool Korda.Database.BulkResult.CommittedByOperation { get; init; }
```

**summary:** True only when this operation confirmed its own commit or commits.

**Inherited type policy:** The owned immutable result contains only acknowledged counts and an ownership observation. Constructing it allocates one managed record; it retains no session, transaction, cursor or native buffer.

## `P:Korda.Database.BulkResult.RowsAffected`

```csharp
public long Korda.Database.BulkResult.RowsAffected { get; init; }
```

**summary:** The sum of acknowledged provider counts.

**Inherited type policy:** The owned immutable result contains only acknowledged counts and an ownership observation. Constructing it allocates one managed record; it retains no session, transaction, cursor or native buffer.

## `P:Korda.Database.DataSession.Cleanup`

```csharp
public Korda.Database.DatabaseCloseResult Korda.Database.DataSession.Cleanup { get; }
```

**summary:** Gets the current cleanup observation, retained after disposal.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `P:Korda.Database.DataSession.Source`

```csharp
public Korda.Database.DataSource Korda.Database.DataSession.Source { get; }
```

**summary:** Gets the creating data source, retained while this physical session remains owned.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `P:Korda.Database.DataSession.Transaction`

```csharp
public Korda.Database.DataTransaction? Korda.Database.DataSession.Transaction { get; }
```

**summary:** Gets the last active transaction, or null after its native state has closed.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `P:Korda.Database.DataSource.Administration`

```csharp
public Korda.Database.DatabaseAdministration Korda.Database.DataSource.Administration { get; }
```

**summary:** Gets the explicit administration facade without opening a database connection.

**remarks:** Migration and catalog operations own a separate, bounded administrative connection; drafting uses static metadata without opening one. Source creation and DI activation never apply migrations. Private in-memory SQLite cannot expose a separate administrative connection.

**exception:** Source admission is closed.

**Inherited type policy:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

## `P:Korda.Database.DataSource.Capabilities`

```csharp
public Korda.Database.DatabaseCapabilities Korda.Database.DataSource.Capabilities { get; }
```

**summary:** Gets the actual negotiated provider capability snapshot.

**Inherited type policy:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

## `P:Korda.Database.DataSource.Cleanup`

```csharp
public Korda.Database.DatabaseCloseResult Korda.Database.DataSource.Cleanup { get; }
```

**summary:** Gets the current cleanup observation, retained after disposal.

**Inherited type policy:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

## `P:Korda.Database.DataSource.Limits`

```csharp
public Korda.Database.DatabaseLimits Korda.Database.DataSource.Limits { get; }
```

**summary:** Gets the immutable source limits.

**Inherited type policy:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

## `P:Korda.Database.DataSource.Name`

```csharp
public string Korda.Database.DataSource.Name { get; }
```

**summary:** Gets the immutable noncredential diagnostic name.

**Inherited type policy:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

## `P:Korda.Database.DataSource.Provider`

```csharp
public Korda.Database.DatabaseProvider Korda.Database.DataSource.Provider { get; }
```

**summary:** Gets the selected native provider.

**Inherited type policy:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

## `P:Korda.Database.DatabaseCapabilities.Names`

```csharp
public System.Collections.Generic.IReadOnlyList<string> Korda.Database.DatabaseCapabilities.Names { get; }
```

**summary:** Gets the immutable ordinal capability-name snapshot.

**Inherited type policy:** Unknown names return false. A capability name does not authorize a different provider or an arbitrary SQL feature.

## `P:Korda.Database.DatabaseCapabilities.Profile`

```csharp
public string Korda.Database.DatabaseCapabilities.Profile { get; }
```

**summary:** Gets the stable selected semantic profile.

**Inherited type policy:** Unknown names return false. A capability name does not authorize a different provider or an arbitrary SQL feature.

## `P:Korda.Database.DatabaseCapabilities.Provider`

```csharp
public Korda.Database.DatabaseProvider Korda.Database.DatabaseCapabilities.Provider { get; }
```

**summary:** Gets the selected provider.

**Inherited type policy:** Unknown names return false. A capability name does not authorize a different provider or an arbitrary SQL feature.

## `P:Korda.Database.DatabaseLimits.AdmissionTimeout`

```csharp
public System.TimeSpan Korda.Database.DatabaseLimits.AdmissionTimeout { get; init; }
```

**summary:** Gets the positive finite pool admission timeout; defaults to fifteen seconds.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:Korda.Database.DatabaseLimits.CommandTimeout`

```csharp
public System.TimeSpan Korda.Database.DatabaseLimits.CommandTimeout { get; init; }
```

**summary:** Gets the positive default total command timeout; defaults to thirty seconds.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:Korda.Database.DatabaseLimits.MaxBatchBytes`

```csharp
public long Korda.Database.DatabaseLimits.MaxBatchBytes { get; init; }
```

**summary:** Gets the entire encoded batch budget including descriptors; defaults to 1 MiB.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:Korda.Database.DatabaseLimits.MaxBatchRows`

```csharp
public int Korda.Database.DatabaseLimits.MaxBatchRows { get; init; }
```

**summary:** Gets the maximum rows in one transferred batch; defaults to 256.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:Korda.Database.DatabaseLimits.MaxBufferedBytes`

```csharp
public long Korda.Database.DatabaseLimits.MaxBufferedBytes { get; init; }
```

**summary:** Gets the buffered encoded-byte budget; defaults to 16 MiB. Zero prohibits buffered demand.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:Korda.Database.DatabaseLimits.MaxBufferedRows`

```csharp
public int Korda.Database.DatabaseLimits.MaxBufferedRows { get; init; }
```

**summary:** Gets the maximum buffered result rows; defaults to 10,000. Zero prohibits buffered demand.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:Korda.Database.DatabaseLimits.MaxConnections`

```csharp
public int Korda.Database.DatabaseLimits.MaxConnections { get; init; }
```

**summary:** Gets the maximum simultaneous physical sessions; defaults to 32.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:Korda.Database.DatabaseLimits.MaxFieldBytes`

```csharp
public long Korda.Database.DatabaseLimits.MaxFieldBytes { get; init; }
```

**summary:** Gets the general field byte limit; defaults to 8 MiB. An inline field must also fit its batch.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:Korda.Database.DatabaseLimits.MaxParameterBytes`

```csharp
public long Korda.Database.DatabaseLimits.MaxParameterBytes { get; init; }
```

**summary:** Gets the total encoded parameter budget; defaults to 4 MiB.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:Korda.Database.DatabaseLimits.MaxParameters`

```csharp
public int Korda.Database.DatabaseLimits.MaxParameters { get; init; }
```

**summary:** Gets the maximum typed parameters per operation; defaults to 999.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:Korda.Database.DatabaseLimits.MaxPreparedPlanBytes`

```csharp
public long Korda.Database.DatabaseLimits.MaxPreparedPlanBytes { get; init; }
```

**summary:** Gets the source-owned prepared shape byte limit; defaults to 8 MiB.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:Korda.Database.DatabaseLimits.MaxPreparedPlans`

```csharp
public int Korda.Database.DatabaseLimits.MaxPreparedPlans { get; init; }
```

**summary:** Gets the source-owned prepared shape entry limit; defaults to 128.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:Korda.Database.DatabaseLimits.MaxQueryDepth`

```csharp
public int Korda.Database.DatabaseLimits.MaxQueryDepth { get; init; }
```

**summary:** Gets the maximum native query depth; defaults to 64.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:Korda.Database.DatabaseLimits.MaxQueryNodes`

```csharp
public int Korda.Database.DatabaseLimits.MaxQueryNodes { get; init; }
```

**summary:** Gets the maximum query and registration nodes; defaults to 4,096.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:Korda.Database.DatabaseLimits.MaxWaiters`

```csharp
public int Korda.Database.DatabaseLimits.MaxWaiters { get; init; }
```

**summary:** Gets the maximum bounded session waiters; defaults to 128.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:Korda.Database.DatabaseModel.Empty`

```csharp
public static Korda.Database.DatabaseModel Korda.Database.DatabaseModel.Empty { get; }
```

**summary:** Gets the empty schema snapshot.

**Inherited type policy:** Construction and persistence perform no native load or database I/O. The snapshot contains store names, codec policies and declared key/generated flags, but no inferred indexes, foreign keys, defaults or backfills. Provider validation occurs when a draft is requested. Keep the previously applied snapshot in source control.

## `P:Korda.Database.Migration.Description`

```csharp
public string Korda.Database.Migration.Description { get; init; }
```

**summary:** A nonempty diagnostic description without credentials.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `P:Korda.Database.Migration.Sql`

```csharp
public string Korda.Database.Migration.Sql { get; init; }
```

**summary:** Exact Unicode SQL; UTF-8 bytes are checksummed without newline normalization.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `P:Korda.Database.Migration.TransactionMode`

```csharp
public Korda.Database.MigrationTransactionMode Korda.Database.Migration.TransactionMode { get; init; }
```

**summary:** Transactional by default; explicit transaction-control statements are rejected.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `P:Korda.Database.Migration.Version`

```csharp
public long Korda.Database.Migration.Version { get; init; }
```

**summary:** A positive version, strictly ascending within its bundle.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `P:Korda.Database.MigrationBundle.ContentHash`

```csharp
public string Korda.Database.MigrationBundle.ContentHash { get; }
```

**summary:** Gets the lowercase SHA-256 of the canonical provider and immutable entry sequence.

**Inherited type policy:** Construction performs no native load or database I/O. Its SHA-256 identity covers the canonical kind12 provider and entry sequence before the hash field is added. Each entry separately contains SQL SHA-256; the native ledger preserves the existing SQLx-compatible SHA-384 checksum. Scripts must be trusted deployment artifacts.

## `P:Korda.Database.MigrationChange.Column`

```csharp
public string? Korda.Database.MigrationChange.Column { get; init; }
```

**summary:** The exact column, or null for a table operation.

**Inherited type policy:** This record retains bounded managed strings and no native resource. Its SQL has not executed; the owning draft enforces explicit per-change destructive approval before conversion to a migration.

## `P:Korda.Database.MigrationChange.Id`

```csharp
public string Korda.Database.MigrationChange.Id { get; init; }
```

**summary:** A stable digest of this exact provider-specific operation.

**Inherited type policy:** This record retains bounded managed strings and no native resource. Its SQL has not executed; the owning draft enforces explicit per-change destructive approval before conversion to a migration.

## `P:Korda.Database.MigrationChange.IsDestructive`

```csharp
public bool Korda.Database.MigrationChange.IsDestructive { get; init; }
```

**summary:** Whether converting the draft requires this exact change ID's approval.

**Inherited type policy:** This record retains bounded managed strings and no native resource. Its SQL has not executed; the owning draft enforces explicit per-change destructive approval before conversion to a migration.

## `P:Korda.Database.MigrationChange.Kind`

```csharp
public Korda.Database.MigrationChangeKind Korda.Database.MigrationChange.Kind { get; init; }
```

**summary:** The explicitly supported change kind.

**Inherited type policy:** This record retains bounded managed strings and no native resource. Its SQL has not executed; the owning draft enforces explicit per-change destructive approval before conversion to a migration.

## `P:Korda.Database.MigrationChange.Schema`

```csharp
public string? Korda.Database.MigrationChange.Schema { get; init; }
```

**summary:** The exact schema, or null for the default schema.

**Inherited type policy:** This record retains bounded managed strings and no native resource. Its SQL has not executed; the owning draft enforces explicit per-change destructive approval before conversion to a migration.

## `P:Korda.Database.MigrationChange.Sql`

```csharp
public string Korda.Database.MigrationChange.Sql { get; init; }
```

**summary:** Provider SQL for review, with identifiers safely quoted by the native dialect.

**Inherited type policy:** This record retains bounded managed strings and no native resource. Its SQL has not executed; the owning draft enforces explicit per-change destructive approval before conversion to a migration.

## `P:Korda.Database.MigrationChange.Table`

```csharp
public string Korda.Database.MigrationChange.Table { get; init; }
```

**summary:** The exact table name.

**Inherited type policy:** This record retains bounded managed strings and no native resource. Its SQL has not executed; the owning draft enforces explicit per-change destructive approval before conversion to a migration.

## `P:Korda.Database.MigrationDraft.Changes`

```csharp
public System.Collections.Generic.IReadOnlyList<Korda.Database.MigrationChange> Korda.Database.MigrationDraft.Changes { get; }
```

**summary:** Gets the immutable ordered list of changes; an empty list means no supported difference.

**Inherited type policy:** The draft owns read-only managed values and no native lease. Review SQL and approve every destructive change ID before converting it to a trusted migration. Applying the resulting migration is a separate explicit call.

## `P:Korda.Database.MigrationDraft.Provider`

```csharp
public Korda.Database.DatabaseProvider Korda.Database.MigrationDraft.Provider { get; }
```

**summary:** Gets the provider that compiled this draft.

**Inherited type policy:** The draft owns read-only managed values and no native lease. Review SQL and approve every destructive change ID before converting it to a trusted migration. Applying the resulting migration is a separate explicit call.

## `P:Korda.Database.MigrationOptions.AllowNonTransactional`

```csharp
public bool Korda.Database.MigrationOptions.AllowNonTransactional { get; init; }
```

**summary:** Gets whether explicitly nontransactional PostgreSQL entries may execute or be resumed.

**remarks:** A new call is explicit replay of the identical script after an unfinished phase. The script must handle any previously completed effects. Korda never retries a write within the call.

**Inherited type policy:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `P:Korda.Database.MigrationOptions.LockTimeout`

```csharp
public System.TimeSpan Korda.Database.MigrationOptions.LockTimeout { get; init; }
```

**summary:** Gets the positive finite administrative writer/advisory-lock wait, defaulting to thirty seconds.

**Inherited type policy:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `P:Korda.Database.MigrationOptions.TargetVersion`

```csharp
public long? Korda.Database.MigrationOptions.TargetVersion { get; init; }
```

**summary:** Gets the last version eligible for forward application; null selects the complete bundle.

**remarks:** A value below the applied current version is rejected; no downgrade is performed.

**Inherited type policy:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `P:Korda.Database.MigrationReport.AppliedCount`

```csharp
public int Korda.Database.MigrationReport.AppliedCount { get; init; }
```

**summary:** The number applied by this invocation.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `P:Korda.Database.MigrationReport.Completed`

```csharp
public bool Korda.Database.MigrationReport.Completed { get; init; }
```

**summary:** True on success; failures throw and retain their original error.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `P:Korda.Database.MigrationReport.CurrentVersion`

```csharp
public long? Korda.Database.MigrationReport.CurrentVersion { get; init; }
```

**summary:** The last confirmed applied version, or null for an empty database.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `P:Korda.Database.MigrationStatus.Applied`

```csharp
public bool Korda.Database.MigrationStatus.Applied { get; init; }
```

**summary:** True only when the successful retained migration ledger contains this exact migration.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `P:Korda.Database.MigrationStatus.Checksum`

```csharp
public string Korda.Database.MigrationStatus.Checksum { get; init; }
```

**summary:** The lowercase hexadecimal SQLx-compatible SHA-384 checksum, distinct from the bundle hash.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `P:Korda.Database.MigrationStatus.Description`

```csharp
public string Korda.Database.MigrationStatus.Description { get; init; }
```

**summary:** The immutable bundle description.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `P:Korda.Database.MigrationStatus.Version`

```csharp
public long Korda.Database.MigrationStatus.Version { get; init; }
```

**summary:** The positive migration version.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `P:Korda.Database.SchemaColumn.IsNullable`

```csharp
public bool Korda.Database.SchemaColumn.IsNullable { get; init; }
```

**summary:** Whether the provider permits SQL NULL.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `P:Korda.Database.SchemaColumn.IsPrimaryKey`

```csharp
public bool Korda.Database.SchemaColumn.IsPrimaryKey { get; init; }
```

**summary:** Whether the column participates in a primary key.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `P:Korda.Database.SchemaColumn.Name`

```csharp
public string Korda.Database.SchemaColumn.Name { get; init; }
```

**summary:** The exact store column name.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `P:Korda.Database.SchemaColumn.StoreType`

```csharp
public string Korda.Database.SchemaColumn.StoreType { get; init; }
```

**summary:** The provider's declared/formatted type.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `P:Korda.Database.SchemaColumnHint.ClrType`

```csharp
public string Korda.Database.SchemaColumnHint.ClrType { get; init; }
```

**summary:** An unqualified built-in or qualified CLR type name, without nullability syntax.

**Inherited type policy:** Configured decimal, JSON, custom IDs and ambiguous provider types need an explicit codec member. The consuming compiler checks its exact type and accessibility. Nullability comes from the catalog.

## `P:Korda.Database.SchemaColumnHint.CodecDeclaringType`

```csharp
public string? Korda.Database.SchemaColumnHint.CodecDeclaringType { get; init; }
```

**summary:** The optional qualified type exposing the configured nonnullable ValueCodec.

**Inherited type policy:** Configured decimal, JSON, custom IDs and ambiguous provider types need an explicit codec member. The consuming compiler checks its exact type and accessibility. Nullability comes from the catalog.

## `P:Korda.Database.SchemaColumnHint.CodecMember`

```csharp
public string? Korda.Database.SchemaColumnHint.CodecMember { get; init; }
```

**summary:** Its accessible static field/property; required together with CodecDeclaringType.

**Inherited type policy:** Configured decimal, JSON, custom IDs and ambiguous provider types need an explicit codec member. The consuming compiler checks its exact type and accessibility. Nullability comes from the catalog.

## `P:Korda.Database.SchemaColumnHint.ColumnName`

```csharp
public string Korda.Database.SchemaColumnHint.ColumnName { get; init; }
```

**summary:** The exact catalog column to map.

**Inherited type policy:** Configured decimal, JSON, custom IDs and ambiguous provider types need an explicit codec member. The consuming compiler checks its exact type and accessibility. Nullability comes from the catalog.

## `P:Korda.Database.SchemaColumnHint.PropertyName`

```csharp
public string? Korda.Database.SchemaColumnHint.PropertyName { get; init; }
```

**summary:** An optional valid C# member name; null retains a valid column name or a deterministic fallback.

**Inherited type policy:** Configured decimal, JSON, custom IDs and ambiguous provider types need an explicit codec member. The consuming compiler checks its exact type and accessibility. Nullability comes from the catalog.

## `P:Korda.Database.SchemaScaffoldOptions.ColumnHints`

```csharp
public System.Collections.Generic.IReadOnlyList<Korda.Database.SchemaColumnHint> Korda.Database.SchemaScaffoldOptions.ColumnHints { get; init; }
```

**summary:** Gets explicit overrides for ambiguous CLR/codec interpretations; defaults to an empty list.

**Inherited type policy:** Catalog v1 omits ordered composite keys, generated flags and defaults. Supply the exact generated column list, including an explicit empty list when none are generated, and a complete composite-key order.

## `P:Korda.Database.SchemaScaffoldOptions.GeneratedColumns`

```csharp
public System.Collections.Generic.IReadOnlyList<string>? Korda.Database.SchemaScaffoldOptions.GeneratedColumns { get; init; }
```

**summary:** Gets the explicitly reviewed generated column names; null is rejected because catalog v1 cannot infer them.

**Inherited type policy:** Catalog v1 omits ordered composite keys, generated flags and defaults. Supply the exact generated column list, including an explicit empty list when none are generated, and a complete composite-key order.

## `P:Korda.Database.SchemaScaffoldOptions.Namespace`

```csharp
public string Korda.Database.SchemaScaffoldOptions.Namespace { get; init; }
```

**summary:** The namespace for emitted source, using ordinary identifier segments.

**Inherited type policy:** Catalog v1 omits ordered composite keys, generated flags and defaults. Supply the exact generated column list, including an explicit empty list when none are generated, and a complete composite-key order.

## `P:Korda.Database.SchemaScaffoldOptions.PrimaryKeyOrder`

```csharp
public System.Collections.Generic.IReadOnlyList<string>? Korda.Database.SchemaScaffoldOptions.PrimaryKeyOrder { get; init; }
```

**summary:** Gets the complete ordered primary-key names; required for a composite key.

**Inherited type policy:** Catalog v1 omits ordered composite keys, generated flags and defaults. Supply the exact generated column list, including an explicit empty list when none are generated, and a complete composite-key order.

## `P:Korda.Database.SchemaScaffoldOptions.TypeName`

```csharp
public string Korda.Database.SchemaScaffoldOptions.TypeName { get; init; }
```

**summary:** The entity class name.

**Inherited type policy:** Catalog v1 omits ordered composite keys, generated flags and defaults. Supply the exact generated column list, including an explicit empty list when none are generated, and a complete composite-key order.

## `P:Korda.Database.SchemaScaffoldResult.Notes`

```csharp
public System.Collections.Generic.IReadOnlyList<string> Korda.Database.SchemaScaffoldResult.Notes { get; }
```

**summary:** Gets immutable limitations about defaults, relationships, indexes and catalog ambiguity.

**Inherited type policy:** Source is ready to include in a project with the provider's bundled generator. A successful consuming compilation remains the authority for user-supplied custom type/codec members.

## `P:Korda.Database.SchemaScaffoldResult.Source`

```csharp
public string Korda.Database.SchemaScaffoldResult.Source { get; }
```

**summary:** Gets the ordinary attributed entity source with explicit store names.

**Inherited type policy:** Source is ready to include in a project with the provider's bundled generator. A successful consuming compilation remains the authority for user-supplied custom type/codec members.

## `P:Korda.Database.SchemaTable.Columns`

```csharp
public System.Collections.Generic.IReadOnlyList<Korda.Database.SchemaColumn> Korda.Database.SchemaTable.Columns { get; init; }
```

**summary:** The bounded columns in native catalog ordinal order.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

## `P:Korda.Database.SchemaTable.Name`

```csharp
public string Korda.Database.SchemaTable.Name { get; init; }
```

**summary:** The exact table name.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

## `P:Korda.Database.SchemaTable.Schema`

```csharp
public string? Korda.Database.SchemaTable.Schema { get; init; }
```

**summary:** The admitted provider schema; SQLite reports main.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

## `P:Korda.Database.TransactionFailure.Cleanup`

```csharp
public Korda.Database.DatabaseCloseResult Korda.Database.TransactionFailure.Cleanup { get; init; }
```

**summary:** The native cleanup observation, independent from transaction effects.

**Inherited type policy:** The original exception retains this snapshot without replacement or automatic retry. It owns no native resource and remains usable after the source and session have been disposed. Snapshot retrieval also works when a custom exception's Data dictionary rejects optional enrichment.

## `P:Korda.Database.TransactionFailure.CommitAttempted`

```csharp
public bool Korda.Database.TransactionFailure.CommitAttempted { get; init; }
```

**summary:** Whether commit crossed the conservative native dispatch boundary.

**Inherited type policy:** The original exception retains this snapshot without replacement or automatic retry. It owns no native resource and remains usable after the source and session have been disposed. Snapshot retrieval also works when a custom exception's Data dictionary rejects optional enrichment.

## `P:Korda.Database.TransactionFailure.Outcome`

```csharp
public Korda.Database.TransactionOutcome Korda.Database.TransactionFailure.Outcome { get; init; }
```

**summary:** Observed durable-effect knowledge; Unknown never implies rollback.

**Inherited type policy:** The original exception retains this snapshot without replacement or automatic retry. It owns no native resource and remains usable after the source and session have been disposed. Snapshot retrieval also works when a custom exception's Data dictionary rejects optional enrichment.

## `P:Korda.Database.TransactionFailure.RecoveryKey`

```csharp
public string? Korda.Database.TransactionFailure.RecoveryKey { get; init; }
```

**summary:** The optional application recovery identity.

**Inherited type policy:** The original exception retains this snapshot without replacement or automatic retry. It owns no native resource and remains usable after the source and session have been disposed. Snapshot retrieval also works when a custom exception's Data dictionary rejects optional enrichment.

## `P:Korda.Query.Query`1.Row`

```csharp
public Korda.Query.QueryRow<TEntity> Korda.Query.Query<TEntity>.Row { get; }
```

**summary:** Gets the live typed root-column proxy.

**inheritdoc:**

**Inherited type policy:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `T:Korda.Database.BulkAtomicity`

```csharp
public enum Korda.Database.BulkAtomicity : int
```

**summary:** Selects whole-operation atomicity or explicitly independent committed batches.

## `T:Korda.Database.BulkFailure`

```csharp
public static class Korda.Database.BulkFailure
```

**summary:** Reads immutable bulk progress without replacing or wrapping the original exception.

**remarks:** The helper reads one exact typed Exception.Data entry without native calls, allocation of a wrapper, recursive error traversal or inference from cancellation. The returned snapshot can outlive all native owners.

## `T:Korda.Database.BulkOptions`

```csharp
public sealed record class Korda.Database.BulkOptions : System.IEquatable<Korda.Database.BulkOptions>
```

**summary:** Immutable finite input batching and explicit transaction ownership policy.

**remarks:** This managed configuration owns no native resource. A bulk operation reads its immutable values at admission, then lowers the requested row count to source byte, node and parameter limits.

## `T:Korda.Database.BulkProgress`

```csharp
public sealed record class Korda.Database.BulkProgress : System.IEquatable<Korda.Database.BulkProgress>
```

**summary:** Immutable progress at failure, distinct from uncertain current-batch effects or later rollback.

**param RowsAffectedInCompletedBatches:** Affected rows only in fully acknowledged preceding batches.

**param CompletedBatches:** The fully acknowledged batch count.

**param CompletedBatchesCommitted:** True only for confirmed preceding PerBatch commits.

**param CurrentTransactionOutcome:** Actual transaction knowledge, retaining Unknown independently.

**remarks:** The original admitted bulk exception retains this managed snapshot after cleanup. Counts describe previously completed batches, which All may have rolled back; they do not prove current-batch effects or authorize replay. The record owns no native resource and remains readable after session/source disposal.

## `T:Korda.Database.BulkResult`

```csharp
public sealed record class Korda.Database.BulkResult : System.IEquatable<Korda.Database.BulkResult>
```

**summary:** The acknowledged result of all admitted batches.

**param RowsAffected:** The sum of acknowledged provider counts.

**param Batches:** The number of fully acknowledged batches.

**param CommittedByOperation:** True only when this operation confirmed its own commit or commits.

**remarks:** The owned immutable result contains only acknowledged counts and an ownership observation. Constructing it allocates one managed record; it retains no session, transaction, cursor or native buffer.

## `T:Korda.Database.ConflictTarget`1`

```csharp
public sealed class Korda.Database.ConflictTarget<TEntity> where TEntity : class
```

**summary:** An immutable explicit unique-column or PostgreSQL constraint target for one atomic upsert.

**typeparam TEntity:** The statically described entity class.

**remarks:** Construction performs no schema query. The native database checks that the selected unique constraint exists.

## `T:Korda.Database.DataSession`

```csharp
public sealed class Korda.Database.DataSession : System.IDisposable, System.IAsyncDisposable
```

**summary:** Owns one physical native session with fail-fast single-command admission.

**remarks:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `T:Korda.Database.DataSource`

```csharp
public sealed class Korda.Database.DataSource : System.IDisposable, System.IAsyncDisposable
```

**summary:** Owns one lazy C++ connection pool, bounded runtime and source-generation metadata registry.

**remarks:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

## `T:Korda.Database.DataSourceBuilder`

```csharp
public sealed class Korda.Database.DataSourceBuilder
```

**summary:** A single-writer builder for one explicitly selected native database engine.

**remarks:** Configuration snapshots values. Build negotiates the packaged module and creates a lazy native pool; it opens no database connection. Independent builds create independent source generations.

## `T:Korda.Database.DatabaseAdministration`

```csharp
public sealed class Korda.Database.DatabaseAdministration
```

**summary:** Runs explicit trusted migration and catalog operations through the native provider.

**remarks:** Migration and catalog operations use one independent administrative connection at a time per source, with bounded waiting and closure before task retirement. Draft compilation opens no connection. None uses a request session or transaction. Result counts, strings and nested columns share source buffering and field limits.

## `T:Korda.Database.DatabaseCapabilities`

```csharp
public sealed class Korda.Database.DatabaseCapabilities
```

**summary:** An immutable snapshot of the capabilities actually negotiated for this provider module.

**remarks:** Unknown names return false. A capability name does not authorize a different provider or an arbitrary SQL feature.

## `T:Korda.Database.DatabaseLimits`

```csharp
public sealed record class Korda.Database.DatabaseLimits : System.IEquatable<Korda.Database.DatabaseLimits>
```

**summary:** Immutable finite limits snapshotted by a data source before native creation.

**remarks:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `T:Korda.Database.DatabaseModel`

```csharp
public sealed class Korda.Database.DatabaseModel
```

**summary:** An immutable versioned snapshot of explicit static table metadata for migration review.

**remarks:** Construction and persistence perform no native load or database I/O. The snapshot contains store names, codec policies and declared key/generated flags, but no inferred indexes, foreign keys, defaults or backfills. Provider validation occurs when a draft is requested. Keep the previously applied snapshot in source control.

## `T:Korda.Database.DatabaseProvider`

```csharp
public enum Korda.Database.DatabaseProvider : int
```

**summary:** The selected independently packaged C++ database provider.

## `T:Korda.Database.Migration`

```csharp
public sealed record class Korda.Database.Migration : System.IEquatable<Korda.Database.Migration>
```

**summary:** Defines one trusted, immutable SQL migration.

**remarks:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

**param Version:** A positive version, strictly ascending within its bundle.

**param Description:** A nonempty diagnostic description without credentials.

**param Sql:** Exact Unicode SQL; UTF-8 bytes are checksummed without newline normalization.

**param TransactionMode:** Transactional by default; explicit transaction-control statements are rejected.

## `T:Korda.Database.MigrationBundle`

```csharp
public sealed class Korda.Database.MigrationBundle
```

**summary:** Snapshots a strictly ordered, provider-bound set of explicit SQL scripts.

**remarks:** Construction performs no native load or database I/O. Its SHA-256 identity covers the canonical kind12 provider and entry sequence before the hash field is added. Each entry separately contains SQL SHA-256; the native ledger preserves the existing SQLx-compatible SHA-384 checksum. Scripts must be trusted deployment artifacts.

## `T:Korda.Database.MigrationChange`

```csharp
public sealed record class Korda.Database.MigrationChange : System.IEquatable<Korda.Database.MigrationChange>
```

**summary:** One detached, immutable, reviewable native schema change.

**remarks:** This record retains bounded managed strings and no native resource. Its SQL has not executed; the owning draft enforces explicit per-change destructive approval before conversion to a migration.

**param Id:** A stable digest of this exact provider-specific operation.

**param Kind:** The explicitly supported change kind.

**param Schema:** The exact schema, or null for the default schema.

**param Table:** The exact table name.

**param Column:** The exact column, or null for a table operation.

**param Sql:** Provider SQL for review, with identifiers safely quoted by the native dialect.

**param IsDestructive:** Whether converting the draft requires this exact change ID's approval.

## `T:Korda.Database.MigrationChangeKind`

```csharp
public enum Korda.Database.MigrationChangeKind : int
```

**summary:** The supported explicit changes in a native migration draft.

## `T:Korda.Database.MigrationDraft`

```csharp
public sealed class Korda.Database.MigrationDraft
```

**summary:** A bounded provider-specific migration draft that has not changed the database.

**remarks:** The draft owns read-only managed values and no native lease. Review SQL and approve every destructive change ID before converting it to a trusted migration. Applying the resulting migration is a separate explicit call.

## `T:Korda.Database.MigrationOptions`

```csharp
public sealed record class Korda.Database.MigrationOptions : System.IEquatable<Korda.Database.MigrationOptions>
```

**summary:** Controls one explicit forward migration call.

**remarks:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `T:Korda.Database.MigrationReport`

```csharp
public sealed record class Korda.Database.MigrationReport : System.IEquatable<Korda.Database.MigrationReport>
```

**summary:** Reports a successfully completed explicit migration call.

**remarks:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

**param AppliedCount:** The number applied by this invocation.

**param CurrentVersion:** The last confirmed applied version, or null for an empty database.

**param Completed:** True on success; failures throw and retain their original error.

## `T:Korda.Database.MigrationStatus`

```csharp
public sealed record class Korda.Database.MigrationStatus : System.IEquatable<Korda.Database.MigrationStatus>
```

**summary:** Describes one bounded immutable bundle/ledger comparison.

**remarks:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

**param Version:** The positive migration version.

**param Description:** The immutable bundle description.

**param Checksum:** The lowercase hexadecimal SQLx-compatible SHA-384 checksum, distinct from the bundle hash.

**param Applied:** True only when the successful retained migration ledger contains this exact migration.

## `T:Korda.Database.MigrationTransactionMode`

```csharp
public enum Korda.Database.MigrationTransactionMode : int
```

**summary:** Selects native transactional execution or explicitly resumable PostgreSQL execution.

## `T:Korda.Database.SchemaColumn`

```csharp
public sealed record class Korda.Database.SchemaColumn : System.IEquatable<Korda.Database.SchemaColumn>
```

**summary:** Describes a provider catalog column without exposing a provider row object.

**remarks:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

**param Name:** The exact store column name.

**param StoreType:** The provider's declared/formatted type.

**param IsNullable:** Whether the provider permits SQL NULL.

**param IsPrimaryKey:** Whether the column participates in a primary key.

## `T:Korda.Database.SchemaColumnHint`

```csharp
public sealed record class Korda.Database.SchemaColumnHint : System.IEquatable<Korda.Database.SchemaColumnHint>
```

**summary:** An explicit CLR/codec interpretation for one catalog column.

**param ColumnName:** The exact catalog column to map.

**param ClrType:** An unqualified built-in or qualified CLR type name, without nullability syntax.

**param CodecDeclaringType:** The optional qualified type exposing the configured nonnullable ValueCodec.

**param CodecMember:** Its accessible static field/property; required together with CodecDeclaringType.

**param PropertyName:** An optional valid C# member name; null retains a valid column name or a deterministic fallback.

**remarks:** Configured decimal, JSON, custom IDs and ambiguous provider types need an explicit codec member. The consuming compiler checks its exact type and accessibility. Nullability comes from the catalog.

## `T:Korda.Database.SchemaScaffoldOptions`

```csharp
public sealed record class Korda.Database.SchemaScaffoldOptions : System.IEquatable<Korda.Database.SchemaScaffoldOptions>
```

**summary:** Explicit decisions for generating one ordinary C# entity from catalog v1.

**param Namespace:** The namespace for emitted source, using ordinary identifier segments.

**param TypeName:** The entity class name.

**remarks:** Catalog v1 omits ordered composite keys, generated flags and defaults. Supply the exact generated column list, including an explicit empty list when none are generated, and a complete composite-key order.

## `T:Korda.Database.SchemaScaffoldResult`

```csharp
public sealed class Korda.Database.SchemaScaffoldResult
```

**summary:** Detached generated C# source and the catalog information it intentionally cannot recover.

**remarks:** Source is ready to include in a project with the provider's bundled generator. A successful consuming compilation remains the authority for user-supplied custom type/codec members.

## `T:Korda.Database.SchemaScaffolder`

```csharp
public static class Korda.Database.SchemaScaffolder
```

**summary:** Emits AOT-visible entity source from explicit provider catalog data and developer decisions.

**remarks:** This synchronous pure tool performs no database I/O, compilation, native loading or schema alteration. It does not reconstruct migrations or infer absent catalog metadata. Generated source must be reviewed and compiled.

## `T:Korda.Database.SchemaTable`

```csharp
public sealed record class Korda.Database.SchemaTable : System.IEquatable<Korda.Database.SchemaTable>
```

**summary:** Describes a table and an immutable, ordinally ordered column snapshot.

**remarks:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

**param Schema:** The admitted provider schema; SQLite reports main.

**param Name:** The exact table name.

**param Columns:** The bounded columns in native catalog ordinal order.

## `T:Korda.Database.TransactionFailure`

```csharp
public sealed record class Korda.Database.TransactionFailure : System.IEquatable<Korda.Database.TransactionFailure>
```

**summary:** An immutable transaction callback failure observation, retained after native cleanup.

**param Outcome:** Observed durable-effect knowledge; Unknown never implies rollback.

**param CommitAttempted:** Whether commit crossed the conservative native dispatch boundary.

**param Cleanup:** The native cleanup observation, independent from transaction effects.

**param RecoveryKey:** The optional application recovery identity.

**remarks:** The original exception retains this snapshot without replacement or automatic retry. It owns no native resource and remains usable after the source and session have been disposed. Snapshot retrieval also works when a custom exception's Data dictionary rejects optional enrichment.

## `T:Korda.Query.CommonTable`1`

```csharp
public sealed class Korda.Query.CommonTable<T> : System.IDisposable where T : class
```

**summary:** A frozen native CTE with an explicit declared row shape and source identity.

**typeparam T:** The declared CTE row class.

**remarks:** The snapshot is bound to one source and a declared static row shape. With imports a native retained reference. Disposal prevents new imports and does not mutate previously imported snapshots. Recursive creation requires one validated anchor/member self-reference.

## `T:Korda.Query.FrameBoundary`

```csharp
public enum Korda.Query.FrameBoundary : int
```

**summary:** Supported finite window-frame endpoint choices.

**remarks:** Only these ROWS endpoints are supported. Reversed or unsupported bounds fail before native state publication; finite numeric PRECEDING/FOLLOWING frames are not declared.

## `T:Korda.Query.JoinPredicate`2`

```csharp
public readonly struct Korda.Query.JoinPredicate<TLeft, TRight> where TLeft : class where TRight : class
```

**summary:** A synchronous native JOIN ON proxy; both sides retain their declared nullability.

**typeparam TLeft:** The root entity class.

**typeparam TRight:** The explicitly joined entity class.

**remarks:** Use only inside the synchronous JOIN callback. Provisional right-side expressions are invalidated on commit or abort. Values and nullability stay exact; explicitly lift a non-null parent key to compare a nullable foreign key.

## `T:Korda.Query.JoinedTable`1`

```csharp
public readonly struct Korda.Query.JoinedTable<TEntity> where TEntity : class
```

**summary:** An opaque inner-join alias belonging to exactly one native query and table.

**typeparam TEntity:** The joined entity class.

**remarks:** The alias is a borrowed token, not an independently disposable query. Default aliases and aliases from another query reject, even if the CLR entity type matches.

## `T:Korda.Query.LeftJoinedTable`1`

```csharp
public readonly struct Korda.Query.LeftJoinedTable<TEntity> where TEntity : class
```

**summary:** An opaque left-join alias whose projected columns require explicit nullable reads.

**typeparam TEntity:** The joined entity class.

**remarks:** Use NullableValue or NullableReference to project its columns and HasMatch to distinguish absence. No default scalar sentinel or implicit object graph is introduced. Its query owns the alias lifetime.

## `T:Korda.Query.LockWait`

```csharp
public enum Korda.Query.LockWait : int
```

**summary:** PostgreSQL row-lock conflict behavior.

**remarks:** Controls PostgreSQL row-lock conflicts within the same transaction and total query deadline; it does not enable transparent retries.

## `T:Korda.Query.NullOrder`

```csharp
public enum Korda.Query.NullOrder : int
```

**summary:** Explicit SQL NULL placement.

**remarks:** Both providers render explicit SQL NULL placement. Ordering alone supplies no uniqueness or snapshot guarantee.

## `T:Korda.Query.PreparedQuery`1`

```csharp
public sealed class Korda.Query.PreparedQuery<T> : System.IDisposable
```

**summary:** An immutable source-bound native query plan with independent execution values and cursor owners.

**typeparam T:** The owned static result type.

**remarks:** Concurrent execution uses separate sessions from the same source. Disposing prevents new dispatch; accepted native operations retain their plan independently and complete safely.

## `T:Korda.Query.ProjectionBuilder`1`

```csharp
public sealed class Korda.Query.ProjectionBuilder<T>
```

**summary:** A query-scoped static projection builder that freezes typed result tokens and a direct constructor.

**typeparam T:** The statically known owned result type.

**remarks:** Fields must come from its exact live query. Build freezes field order, unique names, codec/nullability and the direct materializer; later Add or Build rejects. RowReader validates the full shape before the callback, and escaping DTO values own their data.

## `T:Korda.Query.QueryCursor`1`

```csharp
public sealed class Korda.Query.QueryCursor<TEntity> where TEntity : class
```

**summary:** An immutable typed snapshot of all values in one root-table cursor ordering.

**typeparam TEntity:** The statically mapped root entity class.

**remarks:** Values are copied when added. The cursor owns no native resource and can be reused with the same table metadata. Applying it requires an exact non-null column ordering containing the complete primary key; the query supplies each column's ascending or descending direction.

## `T:Korda.Query.QueryParameter`1`

```csharp
public readonly struct Korda.Query.QueryParameter<T>
```

**summary:** An immutable source-generation parameter identity, distinct from a query-arena expression.

**typeparam T:** The exact statically encoded parameter type.

**remarks:** Parameter IDs are monotonically allocated within a source generation, independently of query expression tokens. A copied parameter remains an identity; it owns no native arena or value, and only declared/imported plans may execute it.

## `T:Korda.Query.QueryParameters`

```csharp
public sealed class Korda.Query.QueryParameters
```

**summary:** A finite explicit parameter set; each Set copies its value and each execution takes a separate snapshot.

**remarks:** Set snapshots each encoded value before returning. A terminal copies the complete finite set before its first await; later Set calls do not change admitted execution. Missing, extra, foreign or codec-incompatible identities reject before native dispatch. No global parameter dictionary exists.

## `T:Korda.Query.QueryRow`1`

```csharp
public readonly struct Korda.Query.QueryRow<TEntity> where TEntity : class
```

**summary:** A stack-independent typed root-table proxy that belongs to one live native query.

**typeparam TEntity:** The declared root entity class.

**remarks:** This borrowed proxy retains its query association. Default, foreign, closed or sealed owners reject. Each operation forwards immediately to a finite native arena; no managed SQL tree or captured callback is retained.

## `T:Korda.Query.QuerySelection`1`

```csharp
public readonly struct Korda.Query.QuerySelection<T>
```

**summary:** A borrowed typed view of a native query's current selection; it owns no second arena.

**typeparam T:** The statically materialized owned result type.

**remarks:** This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.

## `T:Korda.Query.Query`1`

```csharp
public sealed class Korda.Query.Query<TEntity> : System.IDisposable where TEntity : class
```

**summary:** One mutable, source-bound native query arena with a strongly typed application facade.

**typeparam TEntity:** The explicitly described root entity class.

**remarks:** Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

## `T:Korda.Query.RowLockMode`

```csharp
public enum Korda.Query.RowLockMode : int
```

**summary:** PostgreSQL row lock strength requiring an explicit transaction.

**remarks:** Available only on PostgreSQL with an explicit live transaction. SQLite rejects the operation before provider dispatch.

## `T:Korda.Query.SetOperation`

```csharp
public enum Korda.Query.SetOperation : int
```

**summary:** Native SQL set operation with exact compatible projection types.

**remarks:** Operands must have the same source, field order, aliases, codecs and nullability. The native engine snapshots the right operand; SQL duplicate and NULL semantics apply.

## `T:Korda.Query.SortDirection`

```csharp
public enum Korda.Query.SortDirection : int
```

**summary:** Native SQL ordering direction.

**remarks:** The native query validates each enum value before mutation. Direction is explicit; it does not change SQL NULL ordering.

## `T:Korda.Query.SqlArithmeticExtensions`

```csharp
public static class Korda.Query.SqlArithmeticExtensions
```

**summary:** Statically numeric native SQL arithmetic; overflow, precision and division remain provider semantics.

**remarks:** Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. The native engine owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

## `T:Korda.Query.SqlExpression`1`

```csharp
public readonly struct Korda.Query.SqlExpression<T>
```

**summary:** An opaque query-owned scalar token with its exact static codec and SQL nullability.

**typeparam T:** The statically known value type.

**remarks:** No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

## `T:Korda.Query.SqlFunctions`

```csharp
public static class Korda.Query.SqlFunctions
```

**summary:** Static typed native aggregates, ranking and conditional scalar composition.

**remarks:** SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

## `T:Korda.Query.SqlNullabilityExtensions`

```csharp
public static class Korda.Query.SqlNullabilityExtensions
```

**summary:** Explicit one-way SQL nullability lifts; no SQL cast, coalesce or new parameter is emitted.

**remarks:** These forward-only native metadata lifts preserve the SQL value and codec configuration. They do not emit a sentinel, COALESCE or reverse conversion. Default/foreign owners reject, and repeating a reference lift rejects its already nullable descriptor.

## `T:Korda.Query.SqlPredicate`

```csharp
public readonly struct Korda.Query.SqlPredicate
```

**summary:** An opaque same-query native SQL condition; default predicates reject.

**remarks:** Boolean composition uses SQL three-valued logic and performs one direct native operation.

## `T:Korda.Query.SqlStringExtensions`

```csharp
public static class Korda.Query.SqlStringExtensions
```

**summary:** Explicit SQL string pattern and allowlisted collation operations; no CLR culture comparison.

**remarks:** Literal-pattern methods snapshot and escape the supplied text before native SQL rendering; LikePattern deliberately accepts SQL wildcards. Collate resolves an exact provider allowlist, never arbitrary SQL. NULL behavior follows SQL three-valued logic.

## `T:Korda.Query.UpdateSetters`1`

```csharp
public sealed class Korda.Query.UpdateSetters<TEntity> where TEntity : class
```

**summary:** A callback-scoped typed assignment collector; scalar expressions are built natively immediately.

**typeparam TEntity:** The root entity class.

**remarks:** The owner is valid only during the synchronous update callback. Each scalar is snapshotted immediately; duplicate, generated and primary-key assignments reject. Assignment clauses attach after callback success; no tracked entity or managed SQL expression tree is created.

## `T:Korda.Query.WindowDefinition`

```csharp
public sealed class Korda.Query.WindowDefinition
```

**summary:** Finite mutable native window state; each Over/rank call snapshots its current definition.

**remarks:** The owning query bounds lifetime and concurrent access. Rank, RowNumber and Over snapshot native window state immediately; subsequent mutation cannot change existing expressions. Defaults and limits follow the provider capability contract.
