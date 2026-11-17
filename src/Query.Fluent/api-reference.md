# Fluent query API reference

Generated from the actual compiled public API and XML; compared against the exact pinned design declarations. The public namespace is composed into NativeWeave.Database. All expression and SQL operations use B09/B11 native owners; B01/B12 own static row contracts.

## `F:NativeWeave.Query.FrameBoundary.CurrentRow`

```csharp
NativeWeave.Query.FrameBoundary.CurrentRow = 1
```

Uses the native CurrentRow policy.

Only these ROWS endpoints are supported. Reversed or unsupported bounds fail before native state publication; finite numeric PRECEDING/FOLLOWING frames are not declared.


## `F:NativeWeave.Query.FrameBoundary.UnboundedFollowing`

```csharp
NativeWeave.Query.FrameBoundary.UnboundedFollowing = 2
```

Uses the native UnboundedFollowing policy.

Only these ROWS endpoints are supported. Reversed or unsupported bounds fail before native state publication; finite numeric PRECEDING/FOLLOWING frames are not declared.


## `F:NativeWeave.Query.FrameBoundary.UnboundedPreceding`

```csharp
NativeWeave.Query.FrameBoundary.UnboundedPreceding = 0
```

Uses the native UnboundedPreceding policy.

Only these ROWS endpoints are supported. Reversed or unsupported bounds fail before native state publication; finite numeric PRECEDING/FOLLOWING frames are not declared.


## `F:NativeWeave.Query.LockWait.NoWait`

```csharp
NativeWeave.Query.LockWait.NoWait = 1
```

Uses the native NoWait policy.

Controls PostgreSQL row-lock conflicts within the same transaction and total query deadline; it does not enable transparent retries.


## `F:NativeWeave.Query.LockWait.SkipLocked`

```csharp
NativeWeave.Query.LockWait.SkipLocked = 2
```

Uses the native SkipLocked policy.

Controls PostgreSQL row-lock conflicts within the same transaction and total query deadline; it does not enable transparent retries.


## `F:NativeWeave.Query.LockWait.Wait`

```csharp
NativeWeave.Query.LockWait.Wait = 0
```

Uses the native Wait policy.

Controls PostgreSQL row-lock conflicts within the same transaction and total query deadline; it does not enable transparent retries.


## `F:NativeWeave.Query.NullOrder.First`

```csharp
NativeWeave.Query.NullOrder.First = 0
```

Uses the native First policy.

Both providers render explicit SQL NULL placement. Ordering alone supplies no uniqueness or snapshot guarantee.


## `F:NativeWeave.Query.NullOrder.Last`

```csharp
NativeWeave.Query.NullOrder.Last = 1
```

Uses the native Last policy.

Both providers render explicit SQL NULL placement. Ordering alone supplies no uniqueness or snapshot guarantee.


## `F:NativeWeave.Query.RowLockMode.KeyShare`

```csharp
NativeWeave.Query.RowLockMode.KeyShare = 3
```

Uses the native KeyShare policy.

Available only on PostgreSQL with an explicit live transaction. SQLite rejects the operation before provider dispatch.


## `F:NativeWeave.Query.RowLockMode.NoKeyUpdate`

```csharp
NativeWeave.Query.RowLockMode.NoKeyUpdate = 2
```

Uses the native NoKeyUpdate policy.

Available only on PostgreSQL with an explicit live transaction. SQLite rejects the operation before provider dispatch.


## `F:NativeWeave.Query.RowLockMode.Share`

```csharp
NativeWeave.Query.RowLockMode.Share = 1
```

Uses the native Share policy.

Available only on PostgreSQL with an explicit live transaction. SQLite rejects the operation before provider dispatch.


## `F:NativeWeave.Query.RowLockMode.Update`

```csharp
NativeWeave.Query.RowLockMode.Update = 0
```

Uses the native Update policy.

Available only on PostgreSQL with an explicit live transaction. SQLite rejects the operation before provider dispatch.


## `F:NativeWeave.Query.SetOperation.Except`

```csharp
NativeWeave.Query.SetOperation.Except = 3
```

Uses the native Except policy.

Operands must have the same source, field order, aliases, codecs and nullability. Rust snapshots the right operand; SQL duplicate and NULL semantics apply.


## `F:NativeWeave.Query.SetOperation.Intersect`

```csharp
NativeWeave.Query.SetOperation.Intersect = 2
```

Uses the native Intersect policy.

Operands must have the same source, field order, aliases, codecs and nullability. Rust snapshots the right operand; SQL duplicate and NULL semantics apply.


## `F:NativeWeave.Query.SetOperation.Union`

```csharp
NativeWeave.Query.SetOperation.Union = 0
```

Uses the native Union policy.

Operands must have the same source, field order, aliases, codecs and nullability. Rust snapshots the right operand; SQL duplicate and NULL semantics apply.


## `F:NativeWeave.Query.SetOperation.UnionAll`

```csharp
NativeWeave.Query.SetOperation.UnionAll = 1
```

Uses the native UnionAll policy.

Operands must have the same source, field order, aliases, codecs and nullability. Rust snapshots the right operand; SQL duplicate and NULL semantics apply.


## `F:NativeWeave.Query.SortDirection.Ascending`

```csharp
NativeWeave.Query.SortDirection.Ascending = 0
```

Uses the native Ascending policy.

The native query validates each enum value before mutation. Direction is explicit; it does not change SQL NULL ordering.


## `F:NativeWeave.Query.SortDirection.Descending`

```csharp
NativeWeave.Query.SortDirection.Descending = 1
```

Uses the native Descending policy.

The native query validates each enum value before mutation. Direction is explicit; it does not change SQL NULL ordering.


## `M:NativeWeave.Database.DataSession.From``1(NativeWeave.Data.Table{``0})`

```csharp
public NativeWeave.Query.Query<TEntity> NativeWeave.Database.DataSession.From<TEntity>(NativeWeave.Data.Table<TEntity> table) where TEntity : class
```

Creates one mutable native SeaQuery arena bound to this session and exact static table.

The returned query owns one native arena and borrows this session's admission. Composition copies values immediately; dispose the query after its terminal or stream has retired.

- **TEntity**: The statically described entity class.
- **table**: The immutable manual or generated table descriptor.
- **returns**: A query to dispose after its terminal or enumeration completes.
- **T:System.ArgumentNullException**: is null.
- **T:System.ObjectDisposedException**: Session or source admission is closed.
- **T:NativeWeave.Data.DatabaseException**: The native metadata, capability or capacity rejects construction.

## `M:NativeWeave.Query.CommonTable`1.Create(NativeWeave.Data.Table{`0},NativeWeave.Query.QuerySelection{`0},System.Boolean)`

```csharp
public static NativeWeave.Query.CommonTable<T> NativeWeave.Query.CommonTable<T>.Create(NativeWeave.Data.Table<T> declaredShape, NativeWeave.Query.QuerySelection<T> body, [System.Boolean recursive = false])
```

Clones a native selected body into the declared shape; recursive mode requires a validated anchor/member UNION.

The snapshot is bound to one source and a declared static row shape. With imports a native retained reference. Disposal prevents new imports and does not mutate previously imported snapshots. Recursive creation requires one validated anchor/member self-reference.

- **declaredShape**: The static CTE row descriptor with an explicit logical table name and compatible fields.
- **body**: The selected native CTE body to freeze; later body mutations do not affect this snapshot.
- **recursive**: Whether the validated body is a recursive anchor/member UNION with one declared self-reference.
- **returns**: The typed owner or proxy described above, retaining its original source/query association.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.CommonTable`1.Dispose`

```csharp
public void NativeWeave.Query.CommonTable<T>.Dispose()
```

Prevents new CTE imports and releases this native snapshot reference.

The snapshot is bound to one source and a declared static row shape. With imports a native retained reference. Disposal prevents new imports and does not mutate previously imported snapshots. Recursive creation requires one validated anchor/member self-reference.


## `M:NativeWeave.Query.JoinPredicate`2.Left``1(NativeWeave.Data.Column{`0,``0})`

```csharp
public NativeWeave.Query.SqlExpression<T> NativeWeave.Query.JoinPredicate<TLeft, TRight>.Left<T>(NativeWeave.Data.Column<TLeft, T> column)
```

Reads a root-table column during the synchronous ON callback.

Use only inside the synchronous JOIN callback. Provisional right-side expressions are invalidated on commit or abort. Values and nullability stay exact; explicitly lift a non-null parent key to compare a nullable foreign key.

- **column**: The strongly typed column from the declared table; foreign table identities reject.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.JoinPredicate`2.Right``1(NativeWeave.Data.Column{`1,``0})`

```csharp
public NativeWeave.Query.SqlExpression<T> NativeWeave.Query.JoinPredicate<TLeft, TRight>.Right<T>(NativeWeave.Data.Column<TRight, T> column)
```

Reads a provisional joined column; the resulting ON token expires on commit or abort.

Use only inside the synchronous JOIN callback. Provisional right-side expressions are invalidated on commit or abort. Values and nullability stay exact; explicitly lift a non-null parent key to compare a nullable foreign key.

- **column**: The strongly typed column from the declared table; foreign table identities reject.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.PreparedQuery`1.AsAsyncEnumerable(NativeWeave.Database.DataSession,NativeWeave.Query.QueryParameters,System.Threading.CancellationToken)`

```csharp
public System.Collections.Generic.IAsyncEnumerable<T> NativeWeave.Query.PreparedQuery<T>.AsAsyncEnumerable(NativeWeave.Database.DataSession session, [NativeWeave.Query.QueryParameters? parameters = null], [System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Snapshots exact parameter values now and creates one independent lazy native stream.

Concurrent execution uses separate sessions from the same source. Disposing prevents new dispatch; accepted native operations retain their plan independently and complete safely.

- **session**: An open execution session belonging to this plan's exact source.
- **parameters**: The complete declared parameter set; null denotes an empty set and is valid only for a parameterless plan.
- **cancellationToken**: Cancellation requests native stop; completion still waits for cursor and operation retirement.
- **returns**: A one-shot lazy stream of owned results. Dispose its enumerator to request stop and await retirement on early exit.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.
- **T:System.OperationCanceledException**: Cancellation requests termination and cleanup.
- **T:System.TimeoutException**: The finite total query deadline expires; cleanup still completes.

## `M:NativeWeave.Query.PreparedQuery`1.Dispose`

```csharp
public void NativeWeave.Query.PreparedQuery<T>.Dispose()
```

Prevents new execution and releases this plan reference without cancelling accepted operations.

Concurrent execution uses separate sessions from the same source. Disposing prevents new dispatch; accepted native operations retain their plan independently and complete safely.


## `M:NativeWeave.Query.PreparedQuery`1.ToListAsync(NativeWeave.Database.DataSession,NativeWeave.Query.QueryParameters,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<T>> NativeWeave.Query.PreparedQuery<T>.ToListAsync(NativeWeave.Database.DataSession session, [NativeWeave.Query.QueryParameters? parameters = null], [System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Snapshots the exact declared parameter set before await and executes a bounded owned list.

Concurrent execution uses separate sessions from the same source. Disposing prevents new dispatch; accepted native operations retain their plan independently and complete safely.

- **session**: An open execution session belonging to this plan's exact source.
- **parameters**: The complete declared parameter set; null denotes an empty set and is valid only for a parameterless plan.
- **cancellationToken**: Cancellation requests native stop; completion still waits for cursor and operation retirement.
- **returns**: The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.
- **T:System.OperationCanceledException**: Cancellation requests termination and cleanup.
- **T:System.TimeoutException**: The finite total query deadline expires; cleanup still completes.

## `M:NativeWeave.Query.ProjectionBuilder`1.Add``1(NativeWeave.Query.SqlExpression{``0},System.String)`

```csharp
public NativeWeave.Data.ResultColumn<TValue> NativeWeave.Query.ProjectionBuilder<T>.Add<TValue>(NativeWeave.Query.SqlExpression<TValue> expression, System.String alias)
```

Adds an exact same-query scalar and unique result alias, returning its projection token.

Fields must come from its exact live query. Build freezes field order, unique names, codec/nullability and the direct materializer; later Add or Build rejects. RowReader validates the full shape before the callback, and escaping DTO values own their data.

- **expression**: The compatible native scalar owned by the same live query.
- **alias**: The bounded exact SQL/result alias; duplicate or invalid names reject.
- **TValue**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: The typed result token associated with this exact projection builder.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.ProjectionBuilder`1.Build(NativeWeave.Data.RowMaterializer{`0})`

```csharp
public NativeWeave.Data.Projection<T> NativeWeave.Query.ProjectionBuilder<T>.Build(NativeWeave.Data.RowMaterializer<T> materializer)
```

Consumes the builder and freezes a direct AOT-visible materializer; it does not execute SQL.

Fields must come from its exact live query. Build freezes field order, unique names, codec/nullability and the direct materializer; later Add or Build rejects. RowReader validates the full shape before the callback, and escaping DTO values own their data.

- **materializer**: The statically compiled direct constructor or field reader; runtime reflection is not used.
- **returns**: An immutable projection with typed field tokens and the static direct materializer.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.QueryParameters.#ctor`

```csharp
public NativeWeave.Query.QueryParameters.QueryParameters()
```

Creates an empty explicit parameter set.

Set snapshots each encoded value before returning. A terminal copies the complete finite set before its first await; later Set calls do not change admitted execution. Missing, extra, foreign or codec-incompatible identities reject before native dispatch. No global parameter dictionary exists.


## `M:NativeWeave.Query.QueryParameters.Set``1(NativeWeave.Query.QueryParameter{``0},``0)`

```csharp
public NativeWeave.Query.QueryParameters NativeWeave.Query.QueryParameters.Set<T>(NativeWeave.Query.QueryParameter<T> parameter, T value)
```

Copies or replaces one typed parameter value; default or conflicting source identities reject.

Set snapshots each encoded value before returning. A terminal copies the complete finite set before its first await; later Set calls do not change admitted execution. Missing, extra, foreign or codec-incompatible identities reject before native dispatch. No global parameter dictionary exists.

- **parameter**: The declared source-generation parameter identity with its exact typed codec.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: The typed owner or proxy described above, retaining its original source/query association.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.QueryRow`1.Equal``1(NativeWeave.Data.Column{`0,``0},``0)`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.QueryRow<TEntity>.Equal<T>(NativeWeave.Data.Column<TEntity, T> column, T value)
```

Snapshots a typed value and creates native SQL equality.

This borrowed proxy retains its query association. Default, foreign, closed or sealed owners reject. Each operation forwards immediately to a finite native arena; no managed SQL tree or captured callback is retained.

- **column**: The strongly typed column from the declared table; foreign table identities reject.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.QueryRow`1.Get``1(NativeWeave.Data.Column{`0,``0})`

```csharp
public NativeWeave.Query.SqlExpression<T> NativeWeave.Query.QueryRow<TEntity>.Get<T>(NativeWeave.Data.Column<TEntity, T> column)
```

Creates an immediate native expression for an exact root-table column.

This borrowed proxy retains its query association. Default, foreign, closed or sealed owners reject. Each operation forwards immediately to a finite native arena; no managed SQL tree or captured callback is retained.

- **column**: The strongly typed column from the declared table; foreign table identities reject.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.QueryRow`1.GreaterThanOrEqual``1(NativeWeave.Data.Column{`0,``0},``0)`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.QueryRow<TEntity>.GreaterThanOrEqual<T>(NativeWeave.Data.Column<TEntity, T> column, T value)
```

Snapshots a typed lower bound and creates a native comparison.

This borrowed proxy retains its query association. Default, foreign, closed or sealed owners reject. Each operation forwards immediately to a finite native arena; no managed SQL tree or captured callback is retained.

- **column**: The strongly typed column from the declared table; foreign table identities reject.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.QueryRow`1.IsNotNull``1(NativeWeave.Data.Column{`0,``0})`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.QueryRow<TEntity>.IsNotNull<T>(NativeWeave.Data.Column<TEntity, T> column)
```

Tests the exact root column for SQL non-NULL.

This borrowed proxy retains its query association. Default, foreign, closed or sealed owners reject. Each operation forwards immediately to a finite native arena; no managed SQL tree or captured callback is retained.

- **column**: The strongly typed column from the declared table; foreign table identities reject.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.QueryRow`1.IsNull``1(NativeWeave.Data.Column{`0,``0})`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.QueryRow<TEntity>.IsNull<T>(NativeWeave.Data.Column<TEntity, T> column)
```

Tests the exact root column for SQL NULL.

This borrowed proxy retains its query association. Default, foreign, closed or sealed owners reject. Each operation forwards immediately to a finite native arena; no managed SQL tree or captured callback is retained.

- **column**: The strongly typed column from the declared table; foreign table identities reject.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.QuerySelection`1.AsAsyncEnumerable(System.Threading.CancellationToken)`

```csharp
public System.Collections.Generic.IAsyncEnumerable<T> NativeWeave.Query.QuerySelection<T>.AsAsyncEnumerable([System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Seals synchronously and creates a one-shot bounded stream whose first MoveNext dispatches.

This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.

- **cancellationToken**: Cancellation requests native stop; completion still waits for cursor and operation retirement.
- **returns**: A one-shot lazy stream of owned results. Dispose its enumerator to request stop and await retirement on early exit.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.
- **T:System.OperationCanceledException**: Cancellation requests termination and cleanup.
- **T:System.TimeoutException**: The finite total query deadline expires; cleanup still completes.

## `M:NativeWeave.Query.QuerySelection`1.Combine(NativeWeave.Query.QuerySelection{`0},NativeWeave.Query.SetOperation)`

```csharp
public NativeWeave.Query.QuerySelection<T> NativeWeave.Query.QuerySelection<T>.Combine(NativeWeave.Query.QuerySelection<T> other, [NativeWeave.Query.SetOperation operation = NativeWeave.Query.SetOperation.UnionAll])
```

Appends a native set operation using a frozen same-source selection; the other query remains mutable.

This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.

- **other**: The compatible same-source selection or same-query expression required by this operation.
- **operation**: The native SQL set operation with exact compatible row shapes.
- **returns**: The borrowed current-selection view; its owning query retains the native arena.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.QuerySelection`1.FirstAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<T> NativeWeave.Query.QuerySelection<T>.FirstAsync([System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Reads at most one business row and retires the cursor; absence throws.

This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.

- **cancellationToken**: Cancellation requests native stop; completion still waits for cursor and operation retirement.
- **returns**: The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.
- **T:System.OperationCanceledException**: Cancellation requests termination and cleanup.
- **T:System.TimeoutException**: The finite total query deadline expires; cleanup still completes.

## `M:NativeWeave.Query.QuerySelection`1.FirstOrDefaultAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<T?> NativeWeave.Query.QuerySelection<T>.FirstOrDefaultAsync([System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Reads at most one business row, returning default(T) on absence.

This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.

- **cancellationToken**: Cancellation requests native stop; completion still waits for cursor and operation retirement.
- **returns**: The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.
- **T:System.OperationCanceledException**: Cancellation requests termination and cleanup.
- **T:System.TimeoutException**: The finite total query deadline expires; cleanup still completes.

## `M:NativeWeave.Query.QuerySelection`1.Prepare`

```csharp
public NativeWeave.Query.PreparedQuery<T> NativeWeave.Query.QuerySelection<T>.Prepare()
```

Seals and transfers an immutable source-bound native plan for independent parameter snapshots.

This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.

- **returns**: An independently disposable immutable native plan bound to this source.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.QuerySelection`1.SingleAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<T> NativeWeave.Query.QuerySelection<T>.SingleAsync([System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Reads at most two business rows and requires exactly one, with cleanup on either cardinality failure.

This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.

- **cancellationToken**: Cancellation requests native stop; completion still waits for cursor and operation retirement.
- **returns**: The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.
- **T:System.OperationCanceledException**: Cancellation requests termination and cleanup.
- **T:System.TimeoutException**: The finite total query deadline expires; cleanup still completes.

## `M:NativeWeave.Query.QuerySelection`1.SingleOrDefaultAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<T?> NativeWeave.Query.QuerySelection<T>.SingleOrDefaultAsync([System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Requires at most one business row and returns default(T) when absent.

This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.

- **cancellationToken**: Cancellation requests native stop; completion still waits for cursor and operation retirement.
- **returns**: The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.
- **T:System.OperationCanceledException**: Cancellation requests termination and cleanup.
- **T:System.TimeoutException**: The finite total query deadline expires; cleanup still completes.

## `M:NativeWeave.Query.QuerySelection`1.ToArrayAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<T[]> NativeWeave.Query.QuerySelection<T>.ToArrayAsync([System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Seals once and returns a bounded owned array after native cursor retirement.

This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.

- **cancellationToken**: Cancellation requests native stop; completion still waits for cursor and operation retirement.
- **returns**: The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.
- **T:System.OperationCanceledException**: Cancellation requests termination and cleanup.
- **T:System.TimeoutException**: The finite total query deadline expires; cleanup still completes.

## `M:NativeWeave.Query.QuerySelection`1.ToListAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<T>> NativeWeave.Query.QuerySelection<T>.ToListAsync([System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Seals once and returns a bounded owned list after native cursor retirement.

This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.

- **cancellationToken**: Cancellation requests native stop; completion still waits for cursor and operation retirement.
- **returns**: The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.
- **T:System.OperationCanceledException**: Cancellation requests termination and cleanup.
- **T:System.TimeoutException**: The finite total query deadline expires; cleanup still completes.

## `M:NativeWeave.Query.QuerySelection`1.ToSql`

```csharp
public System.String NativeWeave.Query.QuerySelection<T>.ToSql()
```

Renders a bounded native clone with placeholders and no parameter values; does not execute or seal.

This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.

- **returns**: A bounded native SQL rendering with placeholders and no bound values.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.AnyAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<System.Boolean> NativeWeave.Query.Query<TEntity>.AnyAsync([System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Performs native EXISTS over the fully composed query.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **cancellationToken**: Cancellation requests native stop; completion still waits for cursor and operation retirement.
- **returns**: The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.
- **T:System.OperationCanceledException**: Cancellation requests termination and cleanup.
- **T:System.TimeoutException**: The finite total query deadline expires; cleanup still completes.

## `M:NativeWeave.Query.Query`1.Clone`

```csharp
public NativeWeave.Query.Query<TEntity> NativeWeave.Query.Query<TEntity>.Clone()
```

Creates an explicit deep native mutable clone; existing expression/alias proxies remain bound to this query.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **returns**: A new independently disposable native mutable clone; old proxies still belong to the old query.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.Column``1(NativeWeave.Data.Column{`0,``0})`

```csharp
public NativeWeave.Query.SqlExpression<T> NativeWeave.Query.Query<TEntity>.Column<T>(NativeWeave.Data.Column<TEntity, T> column)
```

Creates an immediate root-column native scalar token.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **column**: The strongly typed column from the declared table; foreign table identities reject.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.Column``2(NativeWeave.Query.JoinedTable{``0},NativeWeave.Data.Column{``0,``1})`

```csharp
public NativeWeave.Query.SqlExpression<T> NativeWeave.Query.Query<TEntity>.Column<TJoin, T>(NativeWeave.Query.JoinedTable<TJoin> table, NativeWeave.Data.Column<TJoin, T> column) where TJoin : class
```

Creates a scalar from an exact same-query inner-join alias and table.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **table**: The exact static table descriptor or same-query joined alias required by this overload.
- **column**: The strongly typed column from the declared table; foreign table identities reject.
- **TJoin**: The statically known entity class with explicit table metadata.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.CountAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<System.Int64> NativeWeave.Query.Query<TEntity>.CountAsync([System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Counts the fully composed native result without fetching DTOs into managed memory.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **cancellationToken**: Cancellation requests native stop; completion still waits for cursor and operation retirement.
- **returns**: The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.
- **T:System.OperationCanceledException**: Cancellation requests termination and cleanup.
- **T:System.TimeoutException**: The finite total query deadline expires; cleanup still completes.

## `M:NativeWeave.Query.Query`1.CountRows`

```csharp
public NativeWeave.Query.SqlExpression<System.Int64> NativeWeave.Query.Query<TEntity>.CountRows()
```

Creates a native COUNT(*) aggregate with exact Int64 result metadata.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.DeleteAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<System.Int64> NativeWeave.Query.Query<TEntity>.DeleteAsync([System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Executes a filtered native DELETE; missing filters and unsupported joined/grouped writes reject.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **cancellationToken**: Cancellation requests native stop; completion still waits for cursor and operation retirement.
- **returns**: The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.
- **T:System.OperationCanceledException**: Cancellation requests termination and cleanup.
- **T:System.TimeoutException**: The finite total query deadline expires; cleanup still completes.

## `M:NativeWeave.Query.Query`1.Dispose`

```csharp
public void NativeWeave.Query.Query<TEntity>.Dispose()
```

Closes query admission, requests any active cursor stop and releases the native arena reference.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.


## `M:NativeWeave.Query.Query`1.Distinct`

```csharp
public NativeWeave.Query.Query<TEntity> NativeWeave.Query.Query<TEntity>.Distinct()
```

Applies native SQL DISTINCT to the selected shape.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **returns**: This mutable query for further synchronous composition before its first terminal.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.Exists``1(NativeWeave.Query.QuerySelection{``0})`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.Query<TEntity>.Exists<T>(NativeWeave.Query.QuerySelection<T> subquery)
```

Snapshots a same-source selection into a native EXISTS condition.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **subquery**: A current same-source selection whose native plan is cloned under admission.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.ForUpdate(NativeWeave.Query.RowLockMode,NativeWeave.Query.LockWait)`

```csharp
public NativeWeave.Query.Query<TEntity> NativeWeave.Query.Query<TEntity>.ForUpdate([NativeWeave.Query.RowLockMode mode = NativeWeave.Query.RowLockMode.Update], [NativeWeave.Query.LockWait wait = NativeWeave.Query.LockWait.Wait])
```

Applies an admitted PostgreSQL row lock; execution requires an explicit active transaction.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **mode**: The PostgreSQL row-lock strength; an explicit transaction is required.
- **wait**: The PostgreSQL lock-conflict policy.
- **returns**: This mutable query for further synchronous composition before its first terminal.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.GroupBy``1(NativeWeave.Query.SqlExpression{``0})`

```csharp
public NativeWeave.Query.Query<TEntity> NativeWeave.Query.Query<TEntity>.GroupBy<T>(NativeWeave.Query.SqlExpression<T> expression)
```

Appends a native grouping key.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **expression**: The compatible native scalar owned by the same live query.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: This mutable query for further synchronous composition before its first terminal.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.HasMatch``1(NativeWeave.Query.LeftJoinedTable{``0})`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.Query<TEntity>.HasMatch<TJoin>(NativeWeave.Query.LeftJoinedTable<TJoin> table) where TJoin : class
```

Tests the native explicit LEFT JOIN match marker, independently from nullable child data.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **table**: The exact static table descriptor or same-query joined alias required by this overload.
- **TJoin**: The statically known entity class with explicit table metadata.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.Having(NativeWeave.Query.SqlPredicate)`

```csharp
public NativeWeave.Query.Query<TEntity> NativeWeave.Query.Query<TEntity>.Having(NativeWeave.Query.SqlPredicate predicate)
```

Appends a same-query native group predicate.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **predicate**: The compatible native condition or synchronous typed callback; callbacks are invoked at this call.
- **returns**: This mutable query for further synchronous composition before its first terminal.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.InnerJoin``1(NativeWeave.Data.Table{``0},System.String,System.Func{NativeWeave.Query.JoinPredicate{`0,``0},NativeWeave.Query.SqlPredicate})`

```csharp
public NativeWeave.Query.JoinedTable<TJoin> NativeWeave.Query.Query<TEntity>.InnerJoin<TJoin>(NativeWeave.Data.Table<TJoin> table, System.String alias, System.Func<NativeWeave.Query.JoinPredicate<TEntity, TJoin>, NativeWeave.Query.SqlPredicate> on) where TJoin : class
```

Reserves, builds and atomically commits an explicit native INNER JOIN ON clause.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **table**: The exact static table descriptor or same-query joined alias required by this overload.
- **alias**: The bounded exact SQL/result alias; duplicate or invalid names reject.
- **on**: The synchronous typed JOIN ON callback; it is neither stored nor invoked during row enumeration.
- **TJoin**: The statically known entity class with explicit table metadata.
- **returns**: The typed owner or proxy described above, retaining its original source/query association.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.LeftJoin``1(NativeWeave.Data.Table{``0},System.String,System.Func{NativeWeave.Query.JoinPredicate{`0,``0},NativeWeave.Query.SqlPredicate})`

```csharp
public NativeWeave.Query.LeftJoinedTable<TJoin> NativeWeave.Query.Query<TEntity>.LeftJoin<TJoin>(NativeWeave.Data.Table<TJoin> table, System.String alias, System.Func<NativeWeave.Query.JoinPredicate<TEntity, TJoin>, NativeWeave.Query.SqlPredicate> on) where TJoin : class
```

Commits a native LEFT JOIN and returns a nullable-only projected alias.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **table**: The exact static table descriptor or same-query joined alias required by this overload.
- **alias**: The bounded exact SQL/result alias; duplicate or invalid names reject.
- **on**: The synchronous typed JOIN ON callback; it is neither stored nor invoked during row enumeration.
- **TJoin**: The statically known entity class with explicit table metadata.
- **returns**: The typed owner or proxy described above, retaining its original source/query association.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.NullableReference``2(NativeWeave.Query.LeftJoinedTable{``0},NativeWeave.Data.Column{``0,``1})`

```csharp
public NativeWeave.Query.SqlExpression<T?> NativeWeave.Query.Query<TEntity>.NullableReference<TJoin, T>(NativeWeave.Query.LeftJoinedTable<TJoin> table, NativeWeave.Data.Column<TJoin, T> column) where TJoin : class where T : class?
```

Reads a reference column through a LEFT JOIN with explicit nullable result metadata.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **table**: The exact static table descriptor or same-query joined alias required by this overload.
- **column**: The strongly typed column from the declared table; foreign table identities reject.
- **TJoin**: The statically known entity class with explicit table metadata.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.NullableValue``2(NativeWeave.Query.LeftJoinedTable{``0},NativeWeave.Data.Column{``0,System.Nullable{``1}})`

```csharp
public NativeWeave.Query.SqlExpression<T?> NativeWeave.Query.Query<TEntity>.NullableValue<TJoin, T>(NativeWeave.Query.LeftJoinedTable<TJoin> table, NativeWeave.Data.Column<TJoin, T?> column) where TJoin : class where T : struct
```

Preserves an already nullable value column through a LEFT JOIN.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **table**: The exact static table descriptor or same-query joined alias required by this overload.
- **column**: The strongly typed column from the declared table; foreign table identities reject.
- **TJoin**: The statically known entity class with explicit table metadata.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.NullableValue``2(NativeWeave.Query.LeftJoinedTable{``0},NativeWeave.Data.Column{``0,``1})`

```csharp
public NativeWeave.Query.SqlExpression<T?> NativeWeave.Query.Query<TEntity>.NullableValue<TJoin, T>(NativeWeave.Query.LeftJoinedTable<TJoin> table, NativeWeave.Data.Column<TJoin, T> column) where TJoin : class where T : struct
```

Reads a non-null value column through a LEFT JOIN as an explicitly nullable scalar.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **table**: The exact static table descriptor or same-query joined alias required by this overload.
- **column**: The strongly typed column from the declared table; foreign table identities reject.
- **TJoin**: The statically known entity class with explicit table metadata.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.OrderBy``1(NativeWeave.Data.Column{`0,``0},NativeWeave.Query.SortDirection,NativeWeave.Query.NullOrder)`

```csharp
public NativeWeave.Query.Query<TEntity> NativeWeave.Query.Query<TEntity>.OrderBy<T>(NativeWeave.Data.Column<TEntity, T> column, [NativeWeave.Query.SortDirection direction = NativeWeave.Query.SortDirection.Ascending], [NativeWeave.Query.NullOrder nullOrder = NativeWeave.Query.NullOrder.Last])
```

Replaces native ordering with the exact root column and explicit NULL policy.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **column**: The strongly typed column from the declared table; foreign table identities reject.
- **direction**: Explicit ascending or descending SQL order.
- **nullOrder**: Explicit placement of SQL NULL values in this ordering key.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: This mutable query for further synchronous composition before its first terminal.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.OrderBy``1(NativeWeave.Query.SqlExpression{``0},NativeWeave.Query.SortDirection,NativeWeave.Query.NullOrder)`

```csharp
public NativeWeave.Query.Query<TEntity> NativeWeave.Query.Query<TEntity>.OrderBy<T>(NativeWeave.Query.SqlExpression<T> expression, [NativeWeave.Query.SortDirection direction = NativeWeave.Query.SortDirection.Ascending], [NativeWeave.Query.NullOrder nullOrder = NativeWeave.Query.NullOrder.Last])
```

Replaces native ordering with the same-query expression and explicit NULL policy.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **expression**: The compatible native scalar owned by the same live query.
- **direction**: Explicit ascending or descending SQL order.
- **nullOrder**: Explicit placement of SQL NULL values in this ordering key.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: This mutable query for further synchronous composition before its first terminal.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.ParameterValue``1(NativeWeave.Query.QueryParameter{``0})`

```csharp
public NativeWeave.Query.SqlExpression<T> NativeWeave.Query.Query<TEntity>.ParameterValue<T>(NativeWeave.Query.QueryParameter<T> parameter)
```

Creates a native reference to an identity already declared or imported into this query.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **parameter**: The declared source-generation parameter identity with its exact typed codec.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.Parameter``1(System.String,NativeWeave.Data.ValueCodec{``0})`

```csharp
public NativeWeave.Query.QueryParameter<T> NativeWeave.Query.Query<TEntity>.Parameter<T>(System.String name, NativeWeave.Data.ValueCodec<T> codec)
```

Allocates one nonrecycled source-generation parameter identity and declares it in this query.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **name**: The bounded exact parameter name; conflicting identities in composed plans reject.
- **codec**: The statically known codec and exact nullability/configuration for the value.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: The typed owner or proxy described above, retaining its original source/query association.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.Projection``1`

```csharp
public NativeWeave.Query.ProjectionBuilder<T> NativeWeave.Query.Query<TEntity>.Projection<T>()
```

Starts an explicit typed DTO projection in this query.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: The typed owner or proxy described above, retaining its original source/query association.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.SeekAfter``1(NativeWeave.Data.Column{`0,``0},``0)`

```csharp
public NativeWeave.Query.Query<TEntity> NativeWeave.Query.Query<TEntity>.SeekAfter<T>(NativeWeave.Data.Column<TEntity, T> uniqueOrderKey, T value)
```

Appends strict keyset comparison for one non-null unique ascending root key.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **uniqueOrderKey**: The sole non-null primary key already ordered ascending, with no conflicting offset.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: This mutable query for further synchronous composition before its first terminal.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.SelectAll`

```csharp
public NativeWeave.Query.QuerySelection<TEntity> NativeWeave.Query.Query<TEntity>.SelectAll()
```

Selects only root-table columns in the static entity constructor order.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **returns**: The borrowed current-selection view; its owning query retains the native arena.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.Select``1(NativeWeave.Data.Projection{``0})`

```csharp
public NativeWeave.Query.QuerySelection<T> NativeWeave.Query.Query<TEntity>.Select<T>(NativeWeave.Data.Projection<T> projection)
```

Selects a projection built by this exact query, or the root table's static row descriptor.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **projection**: The exact query-built projection or root table row descriptor to select.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: The borrowed current-selection view; its owning query retains the native arena.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.Select``1(NativeWeave.Query.SqlExpression{``0})`

```csharp
public NativeWeave.Query.QuerySelection<T> NativeWeave.Query.Query<TEntity>.Select<T>(NativeWeave.Query.SqlExpression<T> expression)
```

Selects one same-query native expression through its exact static codec.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **expression**: The compatible native scalar owned by the same live query.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: The borrowed current-selection view; its owning query retains the native arena.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.Select``3(NativeWeave.Query.SqlExpression{``0},NativeWeave.Query.SqlExpression{``1},System.Func{``0,``1,``2})`

```csharp
public NativeWeave.Query.QuerySelection<TOut> NativeWeave.Query.Query<TEntity>.Select<T1, T2, TOut>(NativeWeave.Query.SqlExpression<T1> first, NativeWeave.Query.SqlExpression<T2> second, System.Func<T1, T2, TOut> materializer)
```

Selects two native expressions into an AOT-visible direct DTO factory.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **first**: The first same-query native expression in result order.
- **second**: The second same-query native expression in result order.
- **materializer**: The statically compiled direct constructor or field reader; runtime reflection is not used.
- **T1**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **T2**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **TOut**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: The borrowed current-selection view; its owning query retains the native arena.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.Skip(System.Int32)`

```csharp
public NativeWeave.Query.Query<TEntity> NativeWeave.Query.Query<TEntity>.Skip(System.Int32 count)
```

Sets a finite nonnegative offset only with known unique ordering.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **count**: The nonnegative bounded row count.
- **returns**: This mutable query for further synchronous composition before its first terminal.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.Take(System.Int32)`

```csharp
public NativeWeave.Query.Query<TEntity> NativeWeave.Query.Query<TEntity>.Take(System.Int32 count)
```

Sets a finite nonnegative native row limit; without order the selected rows are nondeterministic.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **count**: The nonnegative bounded row count.
- **returns**: This mutable query for further synchronous composition before its first terminal.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.ThenBy``1(NativeWeave.Data.Column{`0,``0},NativeWeave.Query.SortDirection,NativeWeave.Query.NullOrder)`

```csharp
public NativeWeave.Query.Query<TEntity> NativeWeave.Query.Query<TEntity>.ThenBy<T>(NativeWeave.Data.Column<TEntity, T> column, [NativeWeave.Query.SortDirection direction = NativeWeave.Query.SortDirection.Ascending], [NativeWeave.Query.NullOrder nullOrder = NativeWeave.Query.NullOrder.Last])
```

Appends a root-column order after an existing order.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **column**: The strongly typed column from the declared table; foreign table identities reject.
- **direction**: Explicit ascending or descending SQL order.
- **nullOrder**: Explicit placement of SQL NULL values in this ordering key.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: This mutable query for further synchronous composition before its first terminal.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.ThenBy``1(NativeWeave.Query.SqlExpression{``0},NativeWeave.Query.SortDirection,NativeWeave.Query.NullOrder)`

```csharp
public NativeWeave.Query.Query<TEntity> NativeWeave.Query.Query<TEntity>.ThenBy<T>(NativeWeave.Query.SqlExpression<T> expression, [NativeWeave.Query.SortDirection direction = NativeWeave.Query.SortDirection.Ascending], [NativeWeave.Query.NullOrder nullOrder = NativeWeave.Query.NullOrder.Last])
```

Appends a same-query expression after an existing order.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **expression**: The compatible native scalar owned by the same live query.
- **direction**: Explicit ascending or descending SQL order.
- **nullOrder**: Explicit placement of SQL NULL values in this ordering key.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: This mutable query for further synchronous composition before its first terminal.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.UpdateAsync(NativeWeave.Data.Changes{`0},System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<System.Int64> NativeWeave.Query.Query<TEntity>.UpdateAsync(NativeWeave.Data.Changes<TEntity> changes, [System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Snapshots explicit typed changes and performs a filtered native set-based UPDATE.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **changes**: The explicit immutable snapshot of permitted typed root-column assignments.
- **cancellationToken**: Cancellation requests native stop; completion still waits for cursor and operation retirement.
- **returns**: The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.
- **T:System.OperationCanceledException**: Cancellation requests termination and cleanup.
- **T:System.TimeoutException**: The finite total query deadline expires; cleanup still completes.

## `M:NativeWeave.Query.Query`1.UpdateAsync(System.Action{NativeWeave.Query.UpdateSetters{`0}},System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<System.Int64> NativeWeave.Query.Query<TEntity>.UpdateAsync(System.Action<NativeWeave.Query.UpdateSetters<TEntity>> setters, [System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)])
```

Runs a synchronous typed setter callback, then applies complete native assignments and a filtered UPDATE.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **setters**: The synchronous callback that stages permitted typed assignments before submission.
- **cancellationToken**: Cancellation requests native stop; completion still waits for cursor and operation retirement.
- **returns**: The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.
- **T:System.OperationCanceledException**: Cancellation requests termination and cleanup.
- **T:System.TimeoutException**: The finite total query deadline expires; cleanup still completes.

## `M:NativeWeave.Query.Query`1.Value``1(``0,NativeWeave.Data.ValueCodec{``0})`

```csharp
public NativeWeave.Query.SqlExpression<T> NativeWeave.Query.Query<TEntity>.Value<T>(T value, NativeWeave.Data.ValueCodec<T> codec)
```

Immediately snapshots a typed literal into the native arena.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **codec**: The statically known codec and exact nullability/configuration for the value.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.Where(NativeWeave.Query.SqlPredicate)`

```csharp
public NativeWeave.Query.Query<TEntity> NativeWeave.Query.Query<TEntity>.Where(NativeWeave.Query.SqlPredicate predicate)
```

Appends a same-query native predicate with AND.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **predicate**: The compatible native condition or synchronous typed callback; callbacks are invoked at this call.
- **returns**: This mutable query for further synchronous composition before its first terminal.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.Where(System.Func{NativeWeave.Query.QueryRow{`0},NativeWeave.Query.SqlPredicate})`

```csharp
public NativeWeave.Query.Query<TEntity> NativeWeave.Query.Query<TEntity>.Where(System.Func<NativeWeave.Query.QueryRow<TEntity>, NativeWeave.Query.SqlPredicate> predicate)
```

Invokes the typed callback synchronously once, then attaches its complete native predicate.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **predicate**: The compatible native condition or synchronous typed callback; callbacks are invoked at this call.
- **returns**: This mutable query for further synchronous composition before its first terminal.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.WhereEqual``1(NativeWeave.Data.Column{`0,``0},``0)`

```csharp
public NativeWeave.Query.Query<TEntity> NativeWeave.Query.Query<TEntity>.WhereEqual<T>(NativeWeave.Data.Column<TEntity, T> column, T value)
```

Snapshots the value and appends root-column equality without a predicate delegate.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **column**: The strongly typed column from the declared table; foreign table identities reject.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: This mutable query for further synchronous composition before its first terminal.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.WhereGreaterThanOrEqual``1(NativeWeave.Data.Column{`0,``0},``0)`

```csharp
public NativeWeave.Query.Query<TEntity> NativeWeave.Query.Query<TEntity>.WhereGreaterThanOrEqual<T>(NativeWeave.Data.Column<TEntity, T> column, T value)
```

Snapshots the value and appends an inclusive native lower bound.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **column**: The strongly typed column from the declared table; foreign table identities reject.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: This mutable query for further synchronous composition before its first terminal.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.WhereIf(System.Boolean,System.Func{NativeWeave.Query.QueryRow{`0},NativeWeave.Query.SqlPredicate})`

```csharp
public NativeWeave.Query.Query<TEntity> NativeWeave.Query.Query<TEntity>.WhereIf(System.Boolean condition, System.Func<NativeWeave.Query.QueryRow<TEntity>, NativeWeave.Query.SqlPredicate> predicate)
```

Invokes and attaches the callback only when the condition is true; false binds no captured values.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **condition**: The CLR branch condition, or same-query native CASE predicate required by this overload.
- **predicate**: The compatible native condition or synchronous typed callback; callbacks are invoked at this call.
- **returns**: This mutable query for further synchronous composition before its first terminal.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.WhereIsNotNull``1(NativeWeave.Data.Column{`0,``0})`

```csharp
public NativeWeave.Query.Query<TEntity> NativeWeave.Query.Query<TEntity>.WhereIsNotNull<T>(NativeWeave.Data.Column<TEntity, T> column)
```

Appends an explicit root-column IS NOT NULL predicate.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **column**: The strongly typed column from the declared table; foreign table identities reject.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: This mutable query for further synchronous composition before its first terminal.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.WhereIsNull``1(NativeWeave.Data.Column{`0,``0})`

```csharp
public NativeWeave.Query.Query<TEntity> NativeWeave.Query.Query<TEntity>.WhereIsNull<T>(NativeWeave.Data.Column<TEntity, T> column)
```

Appends an explicit root-column IS NULL predicate.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **column**: The strongly typed column from the declared table; foreign table identities reject.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: This mutable query for further synchronous composition before its first terminal.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.Window`

```csharp
public NativeWeave.Query.WindowDefinition NativeWeave.Query.Query<TEntity>.Window()
```

Creates finite query-owned native window state.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **returns**: The typed owner or proxy described above, retaining its original source/query association.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.WithTimeout(System.TimeSpan)`

```csharp
public NativeWeave.Query.Query<TEntity> NativeWeave.Query.Query<TEntity>.WithTimeout(System.TimeSpan timeout)
```

Snapshots a finite total execution/cursor timeout; zero expires before dispatch.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **timeout**: The finite nonnegative total execution and cursor deadline, snapshotted before dispatch.
- **returns**: This mutable query for further synchronous composition before its first terminal.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.Query`1.With``1(NativeWeave.Query.CommonTable{``0})`

```csharp
public NativeWeave.Query.Query<TEntity> NativeWeave.Query.Query<TEntity>.With<TCte>(NativeWeave.Query.CommonTable<TCte> commonTable) where TCte : class
```

Attaches a frozen same-source native CTE and its exact immutable parameter identities.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **commonTable**: The immutable CTE from this exact source to retain in the query.
- **TCte**: The statically known entity class with explicit table metadata.
- **returns**: This mutable query for further synchronous composition before its first terminal.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlArithmeticExtensions.Add``1(NativeWeave.Query.SqlExpression{System.Nullable{``0}},NativeWeave.Query.SqlExpression{System.Nullable{``0}})`

```csharp
public static NativeWeave.Query.SqlExpression<T?> NativeWeave.Query.SqlArithmeticExtensions.Add<T>(NativeWeave.Query.SqlExpression<T?> expression, NativeWeave.Query.SqlExpression<T?> value) where T : struct, System.Numerics.INumber<T>
```

Performs native numeric add with explicit SQL NULL propagation.

Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. Rust owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

- **expression**: The compatible native scalar owned by the same live query.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlArithmeticExtensions.Add``1(NativeWeave.Query.SqlExpression{System.Nullable{``0}},``0)`

```csharp
public static NativeWeave.Query.SqlExpression<T?> NativeWeave.Query.SqlArithmeticExtensions.Add<T>(NativeWeave.Query.SqlExpression<T?> expression, T value) where T : struct, System.Numerics.INumber<T>
```

Performs native numeric add with explicit SQL NULL propagation.

Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. Rust owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

- **expression**: The compatible native scalar owned by the same live query.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlArithmeticExtensions.Add``1(NativeWeave.Query.SqlExpression{``0},NativeWeave.Query.SqlExpression{``0})`

```csharp
public static NativeWeave.Query.SqlExpression<T> NativeWeave.Query.SqlArithmeticExtensions.Add<T>(NativeWeave.Query.SqlExpression<T> expression, NativeWeave.Query.SqlExpression<T> value) where T : System.Numerics.INumber<T>
```

Performs native numeric add with explicit SQL NULL propagation.

Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. Rust owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

- **expression**: The compatible native scalar owned by the same live query.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlArithmeticExtensions.Add``1(NativeWeave.Query.SqlExpression{``0},``0)`

```csharp
public static NativeWeave.Query.SqlExpression<T> NativeWeave.Query.SqlArithmeticExtensions.Add<T>(NativeWeave.Query.SqlExpression<T> expression, T value) where T : System.Numerics.INumber<T>
```

Performs native numeric add with explicit SQL NULL propagation.

Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. Rust owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

- **expression**: The compatible native scalar owned by the same live query.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlArithmeticExtensions.Divide``1(NativeWeave.Query.SqlExpression{System.Nullable{``0}},NativeWeave.Query.SqlExpression{System.Nullable{``0}})`

```csharp
public static NativeWeave.Query.SqlExpression<T?> NativeWeave.Query.SqlArithmeticExtensions.Divide<T>(NativeWeave.Query.SqlExpression<T?> expression, NativeWeave.Query.SqlExpression<T?> value) where T : struct, System.Numerics.INumber<T>
```

Performs native numeric divide with explicit SQL NULL propagation.

Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. Rust owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

- **expression**: The compatible native scalar owned by the same live query.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlArithmeticExtensions.Divide``1(NativeWeave.Query.SqlExpression{System.Nullable{``0}},``0)`

```csharp
public static NativeWeave.Query.SqlExpression<T?> NativeWeave.Query.SqlArithmeticExtensions.Divide<T>(NativeWeave.Query.SqlExpression<T?> expression, T value) where T : struct, System.Numerics.INumber<T>
```

Performs native numeric divide with explicit SQL NULL propagation.

Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. Rust owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

- **expression**: The compatible native scalar owned by the same live query.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlArithmeticExtensions.Divide``1(NativeWeave.Query.SqlExpression{``0},NativeWeave.Query.SqlExpression{``0})`

```csharp
public static NativeWeave.Query.SqlExpression<T> NativeWeave.Query.SqlArithmeticExtensions.Divide<T>(NativeWeave.Query.SqlExpression<T> expression, NativeWeave.Query.SqlExpression<T> value) where T : System.Numerics.INumber<T>
```

Performs native numeric divide with explicit SQL NULL propagation.

Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. Rust owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

- **expression**: The compatible native scalar owned by the same live query.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlArithmeticExtensions.Divide``1(NativeWeave.Query.SqlExpression{``0},``0)`

```csharp
public static NativeWeave.Query.SqlExpression<T> NativeWeave.Query.SqlArithmeticExtensions.Divide<T>(NativeWeave.Query.SqlExpression<T> expression, T value) where T : System.Numerics.INumber<T>
```

Performs native numeric divide with explicit SQL NULL propagation.

Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. Rust owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

- **expression**: The compatible native scalar owned by the same live query.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlArithmeticExtensions.Multiply``1(NativeWeave.Query.SqlExpression{System.Nullable{``0}},NativeWeave.Query.SqlExpression{System.Nullable{``0}})`

```csharp
public static NativeWeave.Query.SqlExpression<T?> NativeWeave.Query.SqlArithmeticExtensions.Multiply<T>(NativeWeave.Query.SqlExpression<T?> expression, NativeWeave.Query.SqlExpression<T?> value) where T : struct, System.Numerics.INumber<T>
```

Performs native numeric multiply with explicit SQL NULL propagation.

Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. Rust owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

- **expression**: The compatible native scalar owned by the same live query.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlArithmeticExtensions.Multiply``1(NativeWeave.Query.SqlExpression{System.Nullable{``0}},``0)`

```csharp
public static NativeWeave.Query.SqlExpression<T?> NativeWeave.Query.SqlArithmeticExtensions.Multiply<T>(NativeWeave.Query.SqlExpression<T?> expression, T value) where T : struct, System.Numerics.INumber<T>
```

Performs native numeric multiply with explicit SQL NULL propagation.

Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. Rust owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

- **expression**: The compatible native scalar owned by the same live query.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlArithmeticExtensions.Multiply``1(NativeWeave.Query.SqlExpression{``0},NativeWeave.Query.SqlExpression{``0})`

```csharp
public static NativeWeave.Query.SqlExpression<T> NativeWeave.Query.SqlArithmeticExtensions.Multiply<T>(NativeWeave.Query.SqlExpression<T> expression, NativeWeave.Query.SqlExpression<T> value) where T : System.Numerics.INumber<T>
```

Performs native numeric multiply with explicit SQL NULL propagation.

Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. Rust owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

- **expression**: The compatible native scalar owned by the same live query.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlArithmeticExtensions.Multiply``1(NativeWeave.Query.SqlExpression{``0},``0)`

```csharp
public static NativeWeave.Query.SqlExpression<T> NativeWeave.Query.SqlArithmeticExtensions.Multiply<T>(NativeWeave.Query.SqlExpression<T> expression, T value) where T : System.Numerics.INumber<T>
```

Performs native numeric multiply with explicit SQL NULL propagation.

Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. Rust owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

- **expression**: The compatible native scalar owned by the same live query.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlArithmeticExtensions.Subtract``1(NativeWeave.Query.SqlExpression{System.Nullable{``0}},NativeWeave.Query.SqlExpression{System.Nullable{``0}})`

```csharp
public static NativeWeave.Query.SqlExpression<T?> NativeWeave.Query.SqlArithmeticExtensions.Subtract<T>(NativeWeave.Query.SqlExpression<T?> expression, NativeWeave.Query.SqlExpression<T?> value) where T : struct, System.Numerics.INumber<T>
```

Performs native numeric subtract with explicit SQL NULL propagation.

Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. Rust owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

- **expression**: The compatible native scalar owned by the same live query.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlArithmeticExtensions.Subtract``1(NativeWeave.Query.SqlExpression{System.Nullable{``0}},``0)`

```csharp
public static NativeWeave.Query.SqlExpression<T?> NativeWeave.Query.SqlArithmeticExtensions.Subtract<T>(NativeWeave.Query.SqlExpression<T?> expression, T value) where T : struct, System.Numerics.INumber<T>
```

Performs native numeric subtract with explicit SQL NULL propagation.

Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. Rust owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

- **expression**: The compatible native scalar owned by the same live query.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlArithmeticExtensions.Subtract``1(NativeWeave.Query.SqlExpression{``0},NativeWeave.Query.SqlExpression{``0})`

```csharp
public static NativeWeave.Query.SqlExpression<T> NativeWeave.Query.SqlArithmeticExtensions.Subtract<T>(NativeWeave.Query.SqlExpression<T> expression, NativeWeave.Query.SqlExpression<T> value) where T : System.Numerics.INumber<T>
```

Performs native numeric subtract with explicit SQL NULL propagation.

Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. Rust owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

- **expression**: The compatible native scalar owned by the same live query.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlArithmeticExtensions.Subtract``1(NativeWeave.Query.SqlExpression{``0},``0)`

```csharp
public static NativeWeave.Query.SqlExpression<T> NativeWeave.Query.SqlArithmeticExtensions.Subtract<T>(NativeWeave.Query.SqlExpression<T> expression, T value) where T : System.Numerics.INumber<T>
```

Performs native numeric subtract with explicit SQL NULL propagation.

Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. Rust owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.

- **expression**: The compatible native scalar owned by the same live query.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlExpression`1.Between(`0,`0)`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlExpression<T>.Between(T lower, T upper)
```

Snapshots two bounds and creates native inclusive BETWEEN.

No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

- **lower**: The inclusive lower endpoint, copied before the method returns.
- **upper**: The inclusive upper endpoint, copied before the method returns.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlExpression`1.Equal(NativeWeave.Query.SqlExpression{`0})`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlExpression<T>.Equal(NativeWeave.Query.SqlExpression<T> other)
```

Applies native Equal with exact codec/source checks and SQL NULL semantics.

No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

- **other**: The compatible same-source selection or same-query expression required by this operation.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlExpression`1.Equal(`0)`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlExpression<T>.Equal(T value)
```

Applies native Equal with exact codec/source checks and SQL NULL semantics.

No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlExpression`1.GreaterThan(NativeWeave.Query.SqlExpression{`0})`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlExpression<T>.GreaterThan(NativeWeave.Query.SqlExpression<T> other)
```

Applies native GreaterThan with exact codec/source checks and SQL NULL semantics.

No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

- **other**: The compatible same-source selection or same-query expression required by this operation.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlExpression`1.GreaterThan(`0)`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlExpression<T>.GreaterThan(T value)
```

Applies native GreaterThan with exact codec/source checks and SQL NULL semantics.

No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlExpression`1.GreaterThanOrEqual(NativeWeave.Query.SqlExpression{`0})`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlExpression<T>.GreaterThanOrEqual(NativeWeave.Query.SqlExpression<T> other)
```

Applies native GreaterThanOrEqual with exact codec/source checks and SQL NULL semantics.

No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

- **other**: The compatible same-source selection or same-query expression required by this operation.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlExpression`1.GreaterThanOrEqual(`0)`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlExpression<T>.GreaterThanOrEqual(T value)
```

Applies native GreaterThanOrEqual with exact codec/source checks and SQL NULL semantics.

No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlExpression`1.In(NativeWeave.Query.QuerySelection{`0})`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlExpression<T>.In(NativeWeave.Query.QuerySelection<T> subquery)
```

Snapshots a compatible same-source single-column subquery into native IN.

No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

- **subquery**: A current same-source selection whose native plan is cloned under admission.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlExpression`1.In(System.ReadOnlySpan{`0})`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlExpression<T>.In(System.ReadOnlySpan<T> values)
```

Snapshots a finite value span; an empty span yields native FALSE.

No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

- **values**: The finite typed input values, copied before returning; the span is never retained.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlExpression`1.IsNotNull`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlExpression<T>.IsNotNull()
```

Tests SQL non-NULL explicitly.

No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlExpression`1.IsNull`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlExpression<T>.IsNull()
```

Tests SQL NULL explicitly without substituting a sentinel.

No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlExpression`1.LessThan(NativeWeave.Query.SqlExpression{`0})`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlExpression<T>.LessThan(NativeWeave.Query.SqlExpression<T> other)
```

Applies native LessThan with exact codec/source checks and SQL NULL semantics.

No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

- **other**: The compatible same-source selection or same-query expression required by this operation.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlExpression`1.LessThan(`0)`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlExpression<T>.LessThan(T value)
```

Applies native LessThan with exact codec/source checks and SQL NULL semantics.

No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlExpression`1.LessThanOrEqual(NativeWeave.Query.SqlExpression{`0})`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlExpression<T>.LessThanOrEqual(NativeWeave.Query.SqlExpression<T> other)
```

Applies native LessThanOrEqual with exact codec/source checks and SQL NULL semantics.

No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

- **other**: The compatible same-source selection or same-query expression required by this operation.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlExpression`1.LessThanOrEqual(`0)`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlExpression<T>.LessThanOrEqual(T value)
```

Applies native LessThanOrEqual with exact codec/source checks and SQL NULL semantics.

No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlExpression`1.NotEqual(NativeWeave.Query.SqlExpression{`0})`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlExpression<T>.NotEqual(NativeWeave.Query.SqlExpression<T> other)
```

Applies native NotEqual with exact codec/source checks and SQL NULL semantics.

No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

- **other**: The compatible same-source selection or same-query expression required by this operation.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlExpression`1.NotEqual(`0)`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlExpression<T>.NotEqual(T value)
```

Applies native NotEqual with exact codec/source checks and SQL NULL semantics.

No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlExpression`1.NotIn(System.ReadOnlySpan{`0})`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlExpression<T>.NotIn(System.ReadOnlySpan<T> values)
```

Snapshots a finite value span; an empty span yields native TRUE.

No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

- **values**: The finite typed input values, copied before returning; the span is never retained.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlExpression`1.NullSafeEqual(NativeWeave.Query.SqlExpression{`0})`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlExpression<T>.NullSafeEqual(NativeWeave.Query.SqlExpression<T> other)
```

Applies native NullSafeEqual with exact codec/source checks and SQL NULL semantics.

No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

- **other**: The compatible same-source selection or same-query expression required by this operation.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlExpression`1.NullSafeEqual(`0)`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlExpression<T>.NullSafeEqual(T value)
```

Applies native NullSafeEqual with exact codec/source checks and SQL NULL semantics.

No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlExpression`1.Over(NativeWeave.Query.WindowDefinition)`

```csharp
public NativeWeave.Query.SqlExpression<T> NativeWeave.Query.SqlExpression<T>.Over(NativeWeave.Query.WindowDefinition window)
```

Snapshots same-query native window state onto an eligible aggregate or rank expression.

No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

- **window**: The same-query window definition to snapshot at this call.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Average(NativeWeave.Query.SqlExpression{System.Decimal})`

```csharp
public static NativeWeave.Query.SqlExpression<System.Decimal?> NativeWeave.Query.SqlFunctions.Average(NativeWeave.Query.SqlExpression<System.Decimal> expression)
```

Aggregates native decimal inputs to nullable decimal, preserving empty-input NULL and explicit conversion failures.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Average(NativeWeave.Query.SqlExpression{System.Double})`

```csharp
public static NativeWeave.Query.SqlExpression<System.Double?> NativeWeave.Query.SqlFunctions.Average(NativeWeave.Query.SqlExpression<System.Double> expression)
```

Aggregates native double inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Average(NativeWeave.Query.SqlExpression{System.Int32})`

```csharp
public static NativeWeave.Query.SqlExpression<System.Double?> NativeWeave.Query.SqlFunctions.Average(NativeWeave.Query.SqlExpression<System.Int32> expression)
```

Aggregates native int inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Average(NativeWeave.Query.SqlExpression{System.Int64})`

```csharp
public static NativeWeave.Query.SqlExpression<System.Double?> NativeWeave.Query.SqlFunctions.Average(NativeWeave.Query.SqlExpression<System.Int64> expression)
```

Aggregates native long inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Average(NativeWeave.Query.SqlExpression{System.Nullable{System.Decimal}})`

```csharp
public static NativeWeave.Query.SqlExpression<System.Decimal?> NativeWeave.Query.SqlFunctions.Average(NativeWeave.Query.SqlExpression<System.Decimal?> expression)
```

Aggregates native decimal? inputs to nullable decimal, preserving empty-input NULL and explicit conversion failures.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Average(NativeWeave.Query.SqlExpression{System.Nullable{System.Double}})`

```csharp
public static NativeWeave.Query.SqlExpression<System.Double?> NativeWeave.Query.SqlFunctions.Average(NativeWeave.Query.SqlExpression<System.Double?> expression)
```

Aggregates native double? inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Average(NativeWeave.Query.SqlExpression{System.Nullable{System.Int32}})`

```csharp
public static NativeWeave.Query.SqlExpression<System.Double?> NativeWeave.Query.SqlFunctions.Average(NativeWeave.Query.SqlExpression<System.Int32?> expression)
```

Aggregates native int? inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Average(NativeWeave.Query.SqlExpression{System.Nullable{System.Int64}})`

```csharp
public static NativeWeave.Query.SqlExpression<System.Double?> NativeWeave.Query.SqlFunctions.Average(NativeWeave.Query.SqlExpression<System.Int64?> expression)
```

Aggregates native long? inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Average(NativeWeave.Query.SqlExpression{System.Nullable{System.Single}})`

```csharp
public static NativeWeave.Query.SqlExpression<System.Double?> NativeWeave.Query.SqlFunctions.Average(NativeWeave.Query.SqlExpression<System.Single?> expression)
```

Aggregates native float? inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Average(NativeWeave.Query.SqlExpression{System.Single})`

```csharp
public static NativeWeave.Query.SqlExpression<System.Double?> NativeWeave.Query.SqlFunctions.Average(NativeWeave.Query.SqlExpression<System.Single> expression)
```

Aggregates native float inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Case``1(NativeWeave.Query.SqlPredicate,NativeWeave.Query.SqlExpression{``0},NativeWeave.Query.SqlExpression{``0})`

```csharp
public static NativeWeave.Query.SqlExpression<T> NativeWeave.Query.SqlFunctions.Case<T>(NativeWeave.Query.SqlPredicate condition, NativeWeave.Query.SqlExpression<T> whenTrue, NativeWeave.Query.SqlExpression<T> whenFalse)
```

Creates a native CASE with same-query compatible scalar alternatives.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **condition**: The CLR branch condition, or same-query native CASE predicate required by this overload.
- **whenTrue**: The compatible same-query expression for a true SQL condition.
- **whenFalse**: The compatible same-query expression for false or unknown SQL condition.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.CoalesceReference``1(NativeWeave.Query.SqlExpression{``0},``0)`

```csharp
public static NativeWeave.Query.SqlExpression<T> NativeWeave.Query.SqlFunctions.CoalesceReference<T>(NativeWeave.Query.SqlExpression<T?> expression, T fallback) where T : class
```

Produces a non-null reference scalar with native SQL COALESCE.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **fallback**: The non-null typed fallback, snapshotted now and used by native COALESCE.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Coalesce``1(NativeWeave.Query.SqlExpression{System.Nullable{``0}},``0)`

```csharp
public static NativeWeave.Query.SqlExpression<T> NativeWeave.Query.SqlFunctions.Coalesce<T>(NativeWeave.Query.SqlExpression<T?> expression, T fallback) where T : struct
```

Produces a non-null scalar with native SQL COALESCE and an explicitly copied fallback.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **fallback**: The non-null typed fallback, snapshotted now and used by native COALESCE.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Count``1(NativeWeave.Query.SqlExpression{``0},System.Boolean)`

```csharp
public static NativeWeave.Query.SqlExpression<System.Int64> NativeWeave.Query.SqlFunctions.Count<T>(NativeWeave.Query.SqlExpression<T> expression, [System.Boolean distinct = false])
```

Counts non-null native values, optionally using DISTINCT, with exact Int64 result metadata.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **distinct**: Whether native aggregate inputs apply DISTINCT with SQL equality semantics.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.MaxNullableText(NativeWeave.Query.SqlExpression{System.String})`

```csharp
public static NativeWeave.Query.SqlExpression<System.String?> NativeWeave.Query.SqlFunctions.MaxNullableText(NativeWeave.Query.SqlExpression<System.String?> expression)
```

Returns native text maximum under SQL collation with empty-input NULL.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.MaxText(NativeWeave.Query.SqlExpression{System.String})`

```csharp
public static NativeWeave.Query.SqlExpression<System.String?> NativeWeave.Query.SqlFunctions.MaxText(NativeWeave.Query.SqlExpression<System.String> expression)
```

Returns native text maximum under SQL collation with empty-input NULL.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Max``1(NativeWeave.Query.SqlExpression{System.Nullable{``0}})`

```csharp
public static NativeWeave.Query.SqlExpression<T?> NativeWeave.Query.SqlFunctions.Max<T>(NativeWeave.Query.SqlExpression<T?> expression) where T : struct
```

Returns the native maximum with the exact scalar codec and empty-input SQL NULL.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Max``1(NativeWeave.Query.SqlExpression{``0})`

```csharp
public static NativeWeave.Query.SqlExpression<T?> NativeWeave.Query.SqlFunctions.Max<T>(NativeWeave.Query.SqlExpression<T> expression) where T : struct
```

Returns the native maximum with the exact scalar codec and empty-input SQL NULL.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.MinNullableText(NativeWeave.Query.SqlExpression{System.String})`

```csharp
public static NativeWeave.Query.SqlExpression<System.String?> NativeWeave.Query.SqlFunctions.MinNullableText(NativeWeave.Query.SqlExpression<System.String?> expression)
```

Returns native text minimum under SQL collation with empty-input NULL.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.MinText(NativeWeave.Query.SqlExpression{System.String})`

```csharp
public static NativeWeave.Query.SqlExpression<System.String?> NativeWeave.Query.SqlFunctions.MinText(NativeWeave.Query.SqlExpression<System.String> expression)
```

Returns native text minimum under SQL collation with empty-input NULL.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Min``1(NativeWeave.Query.SqlExpression{System.Nullable{``0}})`

```csharp
public static NativeWeave.Query.SqlExpression<T?> NativeWeave.Query.SqlFunctions.Min<T>(NativeWeave.Query.SqlExpression<T?> expression) where T : struct
```

Returns the native minimum with the exact scalar codec and empty-input SQL NULL.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Min``1(NativeWeave.Query.SqlExpression{``0})`

```csharp
public static NativeWeave.Query.SqlExpression<T?> NativeWeave.Query.SqlFunctions.Min<T>(NativeWeave.Query.SqlExpression<T> expression) where T : struct
```

Returns the native minimum with the exact scalar codec and empty-input SQL NULL.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Rank(NativeWeave.Query.WindowDefinition)`

```csharp
public static NativeWeave.Query.SqlExpression<System.Int64> NativeWeave.Query.SqlFunctions.Rank(NativeWeave.Query.WindowDefinition window)
```

Snapshots a native window into an exact Int64 RANK expression.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **window**: The same-query window definition to snapshot at this call.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.RowNumber(NativeWeave.Query.WindowDefinition)`

```csharp
public static NativeWeave.Query.SqlExpression<System.Int64> NativeWeave.Query.SqlFunctions.RowNumber(NativeWeave.Query.WindowDefinition window)
```

Snapshots a native window into an exact Int64 ROW_NUMBER expression.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **window**: The same-query window definition to snapshot at this call.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Sum(NativeWeave.Query.SqlExpression{System.Decimal})`

```csharp
public static NativeWeave.Query.SqlExpression<System.Decimal?> NativeWeave.Query.SqlFunctions.Sum(NativeWeave.Query.SqlExpression<System.Decimal> expression)
```

Aggregates native decimal inputs to nullable decimal, preserving empty-input NULL and explicit conversion failures.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Sum(NativeWeave.Query.SqlExpression{System.Double})`

```csharp
public static NativeWeave.Query.SqlExpression<System.Double?> NativeWeave.Query.SqlFunctions.Sum(NativeWeave.Query.SqlExpression<System.Double> expression)
```

Aggregates native double inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Sum(NativeWeave.Query.SqlExpression{System.Int32})`

```csharp
public static NativeWeave.Query.SqlExpression<System.Int64?> NativeWeave.Query.SqlFunctions.Sum(NativeWeave.Query.SqlExpression<System.Int32> expression)
```

Aggregates native int inputs to nullable long, preserving empty-input NULL and explicit conversion failures.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Sum(NativeWeave.Query.SqlExpression{System.Int64})`

```csharp
public static NativeWeave.Query.SqlExpression<System.Int64?> NativeWeave.Query.SqlFunctions.Sum(NativeWeave.Query.SqlExpression<System.Int64> expression)
```

Aggregates native long inputs to nullable long, preserving empty-input NULL and explicit conversion failures.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Sum(NativeWeave.Query.SqlExpression{System.Nullable{System.Decimal}})`

```csharp
public static NativeWeave.Query.SqlExpression<System.Decimal?> NativeWeave.Query.SqlFunctions.Sum(NativeWeave.Query.SqlExpression<System.Decimal?> expression)
```

Aggregates native decimal? inputs to nullable decimal, preserving empty-input NULL and explicit conversion failures.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Sum(NativeWeave.Query.SqlExpression{System.Nullable{System.Double}})`

```csharp
public static NativeWeave.Query.SqlExpression<System.Double?> NativeWeave.Query.SqlFunctions.Sum(NativeWeave.Query.SqlExpression<System.Double?> expression)
```

Aggregates native double? inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Sum(NativeWeave.Query.SqlExpression{System.Nullable{System.Int32}})`

```csharp
public static NativeWeave.Query.SqlExpression<System.Int64?> NativeWeave.Query.SqlFunctions.Sum(NativeWeave.Query.SqlExpression<System.Int32?> expression)
```

Aggregates native int? inputs to nullable long, preserving empty-input NULL and explicit conversion failures.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Sum(NativeWeave.Query.SqlExpression{System.Nullable{System.Int64}})`

```csharp
public static NativeWeave.Query.SqlExpression<System.Int64?> NativeWeave.Query.SqlFunctions.Sum(NativeWeave.Query.SqlExpression<System.Int64?> expression)
```

Aggregates native long? inputs to nullable long, preserving empty-input NULL and explicit conversion failures.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Sum(NativeWeave.Query.SqlExpression{System.Nullable{System.Single}})`

```csharp
public static NativeWeave.Query.SqlExpression<System.Double?> NativeWeave.Query.SqlFunctions.Sum(NativeWeave.Query.SqlExpression<System.Single?> expression)
```

Aggregates native float? inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlFunctions.Sum(NativeWeave.Query.SqlExpression{System.Single})`

```csharp
public static NativeWeave.Query.SqlExpression<System.Double?> NativeWeave.Query.SqlFunctions.Sum(NativeWeave.Query.SqlExpression<System.Single> expression)
```

Aggregates native float inputs to nullable double, preserving empty-input NULL and explicit conversion failures.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.

- **expression**: The compatible native scalar owned by the same live query.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlNullabilityExtensions.AsNullableReference``1(NativeWeave.Query.SqlExpression{``0})`

```csharp
public static NativeWeave.Query.SqlExpression<T?> NativeWeave.Query.SqlNullabilityExtensions.AsNullableReference<T>(NativeWeave.Query.SqlExpression<T> expression) where T : class
```

Returns a fresh same-query nullable reference token; already-nullable metadata rejects.

These forward-only native metadata lifts preserve the SQL value and codec configuration. They do not emit a sentinel, COALESCE or reverse conversion. Default/foreign owners reject, and repeating a reference lift rejects its already nullable descriptor.

- **expression**: The compatible native scalar owned by the same live query.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlNullabilityExtensions.AsNullable``1(NativeWeave.Query.SqlExpression{``0})`

```csharp
public static NativeWeave.Query.SqlExpression<T?> NativeWeave.Query.SqlNullabilityExtensions.AsNullable<T>(NativeWeave.Query.SqlExpression<T> expression) where T : struct
```

Returns a fresh same-query nullable scalar token, preserving the exact value and codec.

These forward-only native metadata lifts preserve the SQL value and codec configuration. They do not emit a sentinel, COALESCE or reverse conversion. Default/foreign owners reject, and repeating a reference lift rejects its already nullable descriptor.

- **expression**: The compatible native scalar owned by the same live query.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlPredicate.And(NativeWeave.Query.SqlPredicate)`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlPredicate.And(NativeWeave.Query.SqlPredicate other)
```

Combines same-query conditions with native AND.

Boolean composition uses SQL three-valued logic and performs one direct native operation.

- **other**: The compatible same-source selection or same-query expression required by this operation.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlPredicate.Not`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlPredicate.Not()
```

Negates this condition using native SQL NOT.

Boolean composition uses SQL three-valued logic and performs one direct native operation.

- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlPredicate.Or(NativeWeave.Query.SqlPredicate)`

```csharp
public NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlPredicate.Or(NativeWeave.Query.SqlPredicate other)
```

Combines same-query conditions with native OR.

Boolean composition uses SQL three-valued logic and performs one direct native operation.

- **other**: The compatible same-source selection or same-query expression required by this operation.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlStringExtensions.Collate(NativeWeave.Query.SqlExpression{System.String},System.String)`

```csharp
public static NativeWeave.Query.SqlExpression<System.String> NativeWeave.Query.SqlStringExtensions.Collate(NativeWeave.Query.SqlExpression<System.String> expression, System.String collation)
```

Applies only a collation admitted by the provider's fixed allowlist.

Literal-pattern methods snapshot and escape the supplied text before native SQL rendering; LikePattern deliberately accepts SQL wildcards. Collate resolves an exact provider allowlist, never arbitrary SQL. NULL behavior follows SQL three-valued logic.

- **expression**: The compatible native scalar owned by the same live query.
- **collation**: The exact collation name in the current provider's allowlist.
- **returns**: A same-query borrowed native scalar token with exact typed codec and SQL nullability.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlStringExtensions.ContainsLiteral(NativeWeave.Query.SqlExpression{System.String},System.String)`

```csharp
public static NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlStringExtensions.ContainsLiteral(NativeWeave.Query.SqlExpression<System.String> expression, System.String value)
```

Escapes wildcard characters and binds a literal contains pattern.

Literal-pattern methods snapshot and escape the supplied text before native SQL rendering; LikePattern deliberately accepts SQL wildcards. Collate resolves an exact provider allowlist, never arbitrary SQL. NULL behavior follows SQL three-valued logic.

- **expression**: The compatible native scalar owned by the same live query.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlStringExtensions.ContainsNullableLiteral(NativeWeave.Query.SqlExpression{System.String},System.String)`

```csharp
public static NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlStringExtensions.ContainsNullableLiteral(NativeWeave.Query.SqlExpression<System.String?> expression, System.String value)
```

Escapes literal wildcard characters and searches nullable SQL text.

Literal-pattern methods snapshot and escape the supplied text before native SQL rendering; LikePattern deliberately accepts SQL wildcards. Collate resolves an exact provider allowlist, never arbitrary SQL. NULL behavior follows SQL three-valued logic.

- **expression**: The compatible native scalar owned by the same live query.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlStringExtensions.EndsWithLiteral(NativeWeave.Query.SqlExpression{System.String},System.String)`

```csharp
public static NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlStringExtensions.EndsWithLiteral(NativeWeave.Query.SqlExpression<System.String> expression, System.String value)
```

Escapes wildcard characters and binds a literal suffix pattern.

Literal-pattern methods snapshot and escape the supplied text before native SQL rendering; LikePattern deliberately accepts SQL wildcards. Collate resolves an exact provider allowlist, never arbitrary SQL. NULL behavior follows SQL three-valued logic.

- **expression**: The compatible native scalar owned by the same live query.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlStringExtensions.Like(NativeWeave.Query.SqlExpression{System.String},System.String,System.Char)`

```csharp
public static NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlStringExtensions.Like(NativeWeave.Query.SqlExpression<System.String> expression, System.String pattern, [System.Char escape = '\\'])
```

Binds the supplied raw SQL LIKE pattern with an explicit escape character.

Literal-pattern methods snapshot and escape the supplied text before native SQL rendering; LikePattern deliberately accepts SQL wildcards. Collate resolves an exact provider allowlist, never arbitrary SQL. NULL behavior follows SQL three-valued logic.

- **expression**: The compatible native scalar owned by the same live query.
- **pattern**: An explicit SQL LIKE pattern; percent and underscore retain wildcard meaning.
- **escape**: The explicit single escape character for the SQL LIKE pattern.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlStringExtensions.LikeNullable(NativeWeave.Query.SqlExpression{System.String},System.String,System.Char)`

```csharp
public static NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlStringExtensions.LikeNullable(NativeWeave.Query.SqlExpression<System.String?> expression, System.String pattern, [System.Char escape = '\\'])
```

Uses an explicit raw LIKE pattern against nullable SQL text.

Literal-pattern methods snapshot and escape the supplied text before native SQL rendering; LikePattern deliberately accepts SQL wildcards. Collate resolves an exact provider allowlist, never arbitrary SQL. NULL behavior follows SQL three-valued logic.

- **expression**: The compatible native scalar owned by the same live query.
- **pattern**: An explicit SQL LIKE pattern; percent and underscore retain wildcard meaning.
- **escape**: The explicit single escape character for the SQL LIKE pattern.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.SqlStringExtensions.StartsWithLiteral(NativeWeave.Query.SqlExpression{System.String},System.String)`

```csharp
public static NativeWeave.Query.SqlPredicate NativeWeave.Query.SqlStringExtensions.StartsWithLiteral(NativeWeave.Query.SqlExpression<System.String> expression, System.String value)
```

Escapes wildcard characters and binds a literal prefix pattern.

Literal-pattern methods snapshot and escape the supplied text before native SQL rendering; LikePattern deliberately accepts SQL wildcards. Collate resolves an exact provider allowlist, never arbitrary SQL. NULL behavior follows SQL three-valued logic.

- **expression**: The compatible native scalar owned by the same live query.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **returns**: A same-query borrowed native predicate token with SQL three-valued semantics.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.UpdateSetters`1.Set``1(NativeWeave.Data.Column{`0,``0},NativeWeave.Query.SqlExpression{``0})`

```csharp
public NativeWeave.Query.UpdateSetters<TEntity> NativeWeave.Query.UpdateSetters<TEntity>.Set<T>(NativeWeave.Data.Column<TEntity, T> column, NativeWeave.Query.SqlExpression<T> value)
```

Stages a compatible same-query expression; assignments attach only after the callback succeeds.

The owner is valid only during the synchronous update callback. Each scalar is snapshotted immediately; duplicate, generated and primary-key assignments reject. Assignment clauses attach after callback success; no tracked entity or managed SQL expression tree is created.

- **column**: The strongly typed column from the declared table; foreign table identities reject.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: The typed owner or proxy described above, retaining its original source/query association.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.UpdateSetters`1.Set``1(NativeWeave.Data.Column{`0,``0},``0)`

```csharp
public NativeWeave.Query.UpdateSetters<TEntity> NativeWeave.Query.UpdateSetters<TEntity>.Set<T>(NativeWeave.Data.Column<TEntity, T> column, T value)
```

Copies a typed value for a nonkey, nongenerated root-column assignment.

The owner is valid only during the synchronous update callback. Each scalar is snapshotted immediately; duplicate, generated and primary-key assignments reject. Assignment clauses attach after callback success; no tracked entity or managed SQL expression tree is created.

- **column**: The strongly typed column from the declared table; foreign table identities reject.
- **value**: The typed value to snapshot now, or the compatible same-query native expression.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: The typed owner or proxy described above, retaining its original source/query association.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.WindowDefinition.OrderBy``1(NativeWeave.Query.SqlExpression{``0},NativeWeave.Query.SortDirection,NativeWeave.Query.NullOrder)`

```csharp
public NativeWeave.Query.WindowDefinition NativeWeave.Query.WindowDefinition.OrderBy<T>(NativeWeave.Query.SqlExpression<T> expression, [NativeWeave.Query.SortDirection direction = NativeWeave.Query.SortDirection.Ascending], [NativeWeave.Query.NullOrder nullOrder = NativeWeave.Query.NullOrder.Last])
```

Appends native ordering with explicit direction and SQL NULL placement.

The owning query bounds lifetime and concurrent access. Rank, RowNumber and Over snapshot native window state immediately; subsequent mutation cannot change existing expressions. Defaults and limits follow the provider capability contract.

- **expression**: The compatible native scalar owned by the same live query.
- **direction**: Explicit ascending or descending SQL order.
- **nullOrder**: Explicit placement of SQL NULL values in this ordering key.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: The typed owner or proxy described above, retaining its original source/query association.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.WindowDefinition.PartitionBy``1(NativeWeave.Query.SqlExpression{``0})`

```csharp
public NativeWeave.Query.WindowDefinition NativeWeave.Query.WindowDefinition.PartitionBy<T>(NativeWeave.Query.SqlExpression<T> expression)
```

Appends a same-query native partition expression.

The owning query bounds lifetime and concurrent access. Rank, RowNumber and Over snapshot native window state immediately; subsequent mutation cannot change existing expressions. Defaults and limits follow the provider capability contract.

- **expression**: The compatible native scalar owned by the same live query.
- **T**: The statically known CLR value or owned result type with the exact declared codec/nullability.
- **returns**: The typed owner or proxy described above, retaining its original source/query association.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `M:NativeWeave.Query.WindowDefinition.RowsBetween(NativeWeave.Query.FrameBoundary,NativeWeave.Query.FrameBoundary)`

```csharp
public NativeWeave.Query.WindowDefinition NativeWeave.Query.WindowDefinition.RowsBetween(NativeWeave.Query.FrameBoundary start, NativeWeave.Query.FrameBoundary end)
```

Sets one supported ROWS frame; invalid endpoints reject before publication.

The owning query bounds lifetime and concurrent access. Rank, RowNumber and Over snapshot native window state immediately; subsequent mutation cannot change existing expressions. Defaults and limits follow the provider capability contract.

- **start**: The supported starting ROWS frame endpoint.
- **end**: The supported ending ROWS frame endpoint.
- **returns**: The typed owner or proxy described above, retaining its original source/query association.
- **T:System.ArgumentException**: An argument, owner association or codec/nullability is incompatible.
- **T:System.InvalidOperationException**: The owner, projection or one-shot terminal is in an invalid state.
- **T:System.ObjectDisposedException**: The required native owner or its parent has closed admission.
- **T:NativeWeave.Data.DatabaseException**: Native capability, finite resource limits or provider execution rejects the operation.

## `P:NativeWeave.Query.Query`1.Row`

```csharp
public NativeWeave.Query.QueryRow<TEntity> NativeWeave.Query.Query<TEntity>.Row | getter=Public;setter=;init=
```

Gets the live typed root-column proxy.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.


## `T:NativeWeave.Query.CommonTable`1`

```csharp
NativeWeave.Query.CommonTable<T> where T : class | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=System.IDisposable
```

A frozen native CTE with an explicit declared row shape and source identity.

The snapshot is bound to one source and a declared static row shape. With imports a native retained reference. Disposal prevents new imports and does not mutate previously imported snapshots. Recursive creation requires one validated anchor/member self-reference.

- **T**: The declared CTE row class.

## `T:NativeWeave.Query.FrameBoundary`

```csharp
NativeWeave.Query.FrameBoundary | kind=Enum;static=False;sealed=True;abstract=False;base=System.Enum;interfaces=
```

Supported finite window-frame endpoint choices.

Only these ROWS endpoints are supported. Reversed or unsupported bounds fail before native state publication; finite numeric PRECEDING/FOLLOWING frames are not declared.


## `T:NativeWeave.Query.JoinPredicate`2`

```csharp
NativeWeave.Query.JoinPredicate<TLeft, TRight> where TLeft : class where TRight : class | kind=Struct;static=False;sealed=True;abstract=False;base=System.ValueType;interfaces=
```

A synchronous native JOIN ON proxy; both sides retain their declared nullability.

Use only inside the synchronous JOIN callback. Provisional right-side expressions are invalidated on commit or abort. Values and nullability stay exact; explicitly lift a non-null parent key to compare a nullable foreign key.

- **TLeft**: The root entity class.
- **TRight**: The explicitly joined entity class.

## `T:NativeWeave.Query.JoinedTable`1`

```csharp
NativeWeave.Query.JoinedTable<TEntity> where TEntity : class | kind=Struct;static=False;sealed=True;abstract=False;base=System.ValueType;interfaces=
```

An opaque inner-join alias belonging to exactly one native query and table.

The alias is a borrowed token, not an independently disposable query. Default aliases and aliases from another query reject, even if the CLR entity type matches.

- **TEntity**: The joined entity class.

## `T:NativeWeave.Query.LeftJoinedTable`1`

```csharp
NativeWeave.Query.LeftJoinedTable<TEntity> where TEntity : class | kind=Struct;static=False;sealed=True;abstract=False;base=System.ValueType;interfaces=
```

An opaque left-join alias whose projected columns require explicit nullable reads.

Use NullableValue or NullableReference to project its columns and HasMatch to distinguish absence. No default scalar sentinel or implicit object graph is introduced. Its query owns the alias lifetime.

- **TEntity**: The joined entity class.

## `T:NativeWeave.Query.LockWait`

```csharp
NativeWeave.Query.LockWait | kind=Enum;static=False;sealed=True;abstract=False;base=System.Enum;interfaces=
```

PostgreSQL row-lock conflict behavior.

Controls PostgreSQL row-lock conflicts within the same transaction and total query deadline; it does not enable transparent retries.


## `T:NativeWeave.Query.NullOrder`

```csharp
NativeWeave.Query.NullOrder | kind=Enum;static=False;sealed=True;abstract=False;base=System.Enum;interfaces=
```

Explicit SQL NULL placement.

Both providers render explicit SQL NULL placement. Ordering alone supplies no uniqueness or snapshot guarantee.


## `T:NativeWeave.Query.PreparedQuery`1`

```csharp
NativeWeave.Query.PreparedQuery<T> | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=System.IDisposable
```

An immutable source-bound native query plan with independent execution values and cursor owners.

Concurrent execution uses separate sessions from the same source. Disposing prevents new dispatch; accepted native operations retain their plan independently and complete safely.

- **T**: The owned static result type.

## `T:NativeWeave.Query.ProjectionBuilder`1`

```csharp
NativeWeave.Query.ProjectionBuilder<T> | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

A query-scoped static projection builder that freezes typed result tokens and a direct constructor.

Fields must come from its exact live query. Build freezes field order, unique names, codec/nullability and the direct materializer; later Add or Build rejects. RowReader validates the full shape before the callback, and escaping DTO values own their data.

- **T**: The statically known owned result type.

## `T:NativeWeave.Query.QueryParameter`1`

```csharp
NativeWeave.Query.QueryParameter<T> | kind=Struct;static=False;sealed=True;abstract=False;base=System.ValueType;interfaces=
```

An immutable source-generation parameter identity, distinct from a query-arena expression.

Parameter IDs are monotonically allocated within a source generation, independently of query expression tokens. A copied parameter remains an identity; it owns no native arena or value, and only declared/imported plans may execute it.

- **T**: The exact statically encoded parameter type.

## `T:NativeWeave.Query.QueryParameters`

```csharp
NativeWeave.Query.QueryParameters | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

A finite explicit parameter set; each Set copies its value and each execution takes a separate snapshot.

Set snapshots each encoded value before returning. A terminal copies the complete finite set before its first await; later Set calls do not change admitted execution. Missing, extra, foreign or codec-incompatible identities reject before native dispatch. No global parameter dictionary exists.


## `T:NativeWeave.Query.QueryRow`1`

```csharp
NativeWeave.Query.QueryRow<TEntity> where TEntity : class | kind=Struct;static=False;sealed=True;abstract=False;base=System.ValueType;interfaces=
```

A stack-independent typed root-table proxy that belongs to one live native query.

This borrowed proxy retains its query association. Default, foreign, closed or sealed owners reject. Each operation forwards immediately to a finite native arena; no managed SQL tree or captured callback is retained.

- **TEntity**: The declared root entity class.

## `T:NativeWeave.Query.QuerySelection`1`

```csharp
NativeWeave.Query.QuerySelection<T> | kind=Struct;static=False;sealed=True;abstract=False;base=System.ValueType;interfaces=
```

A borrowed typed view of a native query's current selection; it owns no second arena.

This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.

- **T**: The statically materialized owned result type.

## `T:NativeWeave.Query.Query`1`

```csharp
NativeWeave.Query.Query<TEntity> where TEntity : class | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=System.IDisposable
```

One mutable, source-bound native SeaQuery arena with a strongly typed application facade.

Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query; use Clone before a second execution. Keep this owner alive across streaming and dispose after use.

- **TEntity**: The explicitly described root entity class.

## `T:NativeWeave.Query.RowLockMode`

```csharp
NativeWeave.Query.RowLockMode | kind=Enum;static=False;sealed=True;abstract=False;base=System.Enum;interfaces=
```

PostgreSQL row lock strength requiring an explicit transaction.

Available only on PostgreSQL with an explicit live transaction. SQLite rejects the operation before provider dispatch.


## `T:NativeWeave.Query.SetOperation`

```csharp
NativeWeave.Query.SetOperation | kind=Enum;static=False;sealed=True;abstract=False;base=System.Enum;interfaces=
```

Native SQL set operation with exact compatible projection types.

Operands must have the same source, field order, aliases, codecs and nullability. Rust snapshots the right operand; SQL duplicate and NULL semantics apply.


## `T:NativeWeave.Query.SortDirection`

```csharp
NativeWeave.Query.SortDirection | kind=Enum;static=False;sealed=True;abstract=False;base=System.Enum;interfaces=
```

Native SQL ordering direction.

The native query validates each enum value before mutation. Direction is explicit; it does not change SQL NULL ordering.


## `T:NativeWeave.Query.SqlArithmeticExtensions`

```csharp
NativeWeave.Query.SqlArithmeticExtensions | kind=Class;static=True;sealed=False;abstract=False;base=System.Object;interfaces=
```

Statically numeric native SQL arithmetic; overflow, precision and division remain provider semantics.

Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. Rust owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.


## `T:NativeWeave.Query.SqlExpression`1`

```csharp
NativeWeave.Query.SqlExpression<T> | kind=Struct;static=False;sealed=True;abstract=False;base=System.ValueType;interfaces=
```

An opaque query-owned scalar token with its exact static codec and SQL nullability.

No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.

- **T**: The statically known value type.

## `T:NativeWeave.Query.SqlFunctions`

```csharp
NativeWeave.Query.SqlFunctions | kind=Class;static=True;sealed=False;abstract=False;base=System.Object;interfaces=
```

Static typed native aggregates, ranking and conditional scalar composition.

SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.


## `T:NativeWeave.Query.SqlNullabilityExtensions`

```csharp
NativeWeave.Query.SqlNullabilityExtensions | kind=Class;static=True;sealed=False;abstract=False;base=System.Object;interfaces=
```

Explicit one-way SQL nullability lifts; no SQL cast, coalesce or new parameter is emitted.

These forward-only native metadata lifts preserve the SQL value and codec configuration. They do not emit a sentinel, COALESCE or reverse conversion. Default/foreign owners reject, and repeating a reference lift rejects its already nullable descriptor.


## `T:NativeWeave.Query.SqlPredicate`

```csharp
NativeWeave.Query.SqlPredicate | kind=Struct;static=False;sealed=True;abstract=False;base=System.ValueType;interfaces=
```

An opaque same-query native SQL condition; default predicates reject.

Boolean composition uses SQL three-valued logic and performs one direct native operation.


## `T:NativeWeave.Query.SqlStringExtensions`

```csharp
NativeWeave.Query.SqlStringExtensions | kind=Class;static=True;sealed=False;abstract=False;base=System.Object;interfaces=
```

Explicit SQL string pattern and allowlisted collation operations; no CLR culture comparison.

Literal-pattern methods snapshot and escape the supplied text before native SQL rendering; LikePattern deliberately accepts SQL wildcards. Collate resolves an exact provider allowlist, never arbitrary SQL. NULL behavior follows SQL three-valued logic.


## `T:NativeWeave.Query.UpdateSetters`1`

```csharp
NativeWeave.Query.UpdateSetters<TEntity> where TEntity : class | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

A callback-scoped typed assignment collector; scalar expressions are built natively immediately.

The owner is valid only during the synchronous update callback. Each scalar is snapshotted immediately; duplicate, generated and primary-key assignments reject. Assignment clauses attach after callback success; no tracked entity or managed SQL expression tree is created.

- **TEntity**: The root entity class.

## `T:NativeWeave.Query.WindowDefinition`

```csharp
NativeWeave.Query.WindowDefinition | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

Finite mutable native window state; each Over/rank call snapshots its current definition.

The owning query bounds lifetime and concurrent access. Rank, RowNumber and Over snapshot native window state immediately; subsequent mutation cannot change existing expressions. Defaults and limits follow the provider capability contract.


