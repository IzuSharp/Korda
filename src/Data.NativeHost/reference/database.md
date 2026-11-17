# NativeWeave.Database compiled API

Generated from the compiled product assembly and XML by `src/Build.Integration/ApiReference`.

Current product declarations compared with Design commit c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9. Ownership, costs and Linux applicability are described in docs/guides/data-nativehost.md.

## `F:NativeWeave.Database.DatabaseProvider.PostgreSql`

```csharp
NativeWeave.Database.DatabaseProvider.PostgreSql = 0
```

**summary:** The SQLx PostgreSQL provider.

## `F:NativeWeave.Database.DatabaseProvider.Sqlite`

```csharp
NativeWeave.Database.DatabaseProvider.Sqlite = 1
```

**summary:** The serialized SQLx SQLite provider.

## `M:NativeWeave.Database.DataSession.BeginTransactionAsync(NativeWeave.Database.TransactionOptions,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<NativeWeave.Database.DataTransaction> NativeWeave.Database.DataSession.BeginTransactionAsync(NativeWeave.Database.TransactionOptions options, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Begins an exact native transaction without weakening unsupported isolation or read-only policy.

**param options:** Immutable isolation, effect and independent commit/cleanup deadline settings.

**param cancellationToken:** Controls admission before BEGIN dispatch.

**returns:** The transaction owner, whose outcome is independent from local cleanup.

**exception:** is null.

**exception:** An option is invalid.

**exception:** The provider or admission fails.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:NativeWeave.Database.DataSession.BeginTransactionAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<NativeWeave.Database.DataTransaction> NativeWeave.Database.DataSession.BeginTransactionAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Begins a native transaction using the provider's default isolation.

**param cancellationToken:** Controls admission before BEGIN dispatch.

**returns:** The native transaction owner.

**exception:** Admission is closed.

**exception:** The provider or single-command admission rejects BEGIN.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:NativeWeave.Database.DataSession.CloseAsync(System.TimeSpan,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<NativeWeave.Database.DatabaseCloseResult> NativeWeave.Database.DataSession.CloseAsync(System.TimeSpan waitBudget, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Starts closure, then waits within an independent finite caller budget.

**param waitBudget:** A nonnegative finite wait; zero observes without waiting.

**param cancellationToken:** Cancels only the wait after native cleanup starts.

**returns:** A truthful snapshot; timeout cannot imply native retirement.

**exception:** The budget is invalid.

**exception:** The caller stops waiting.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:NativeWeave.Database.DataSession.Dispose`

```csharp
public void NativeWeave.Database.DataSession.Dispose()
```

**summary:** Closes admission and transfers cleanup to the bounded native owner without blocking.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:NativeWeave.Database.DataSession.DisposeAsync`

```csharp
public System.Threading.Tasks.ValueTask NativeWeave.Database.DataSession.DisposeAsync()
```

**summary:** Closes admission and joins actual native local safety.

**returns:** The asynchronous safety join.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `M:NativeWeave.Database.DataSession.ExecuteAsync(NativeWeave.Data.SqlCommand,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<long> NativeWeave.Database.DataSession.ExecuteAsync(NativeWeave.Data.SqlCommand command, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
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

## `M:NativeWeave.Database.DataSession.QueryAsync``1(NativeWeave.Data.SqlCommand,NativeWeave.Data.Projection{``0},System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<T>> NativeWeave.Database.DataSession.QueryAsync<T>(NativeWeave.Data.SqlCommand command, NativeWeave.Data.Projection<T> projection, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
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

## `M:NativeWeave.Database.DataSession.ScalarAsync``1(NativeWeave.Data.SqlCommand,NativeWeave.Data.ValueCodec{``0},System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<T> NativeWeave.Database.DataSession.ScalarAsync<T>(NativeWeave.Data.SqlCommand command, NativeWeave.Data.ValueCodec<T> codec, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
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

## `M:NativeWeave.Database.DataSession.StreamAsync``1(NativeWeave.Data.SqlCommand,NativeWeave.Data.Projection{``0},System.Threading.CancellationToken)`

```csharp
public System.Collections.Generic.IAsyncEnumerable<T> NativeWeave.Database.DataSession.StreamAsync<T>(NativeWeave.Data.SqlCommand command, NativeWeave.Data.Projection<T> projection, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
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

## `M:NativeWeave.Database.DataSource.CanConnectAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<bool> NativeWeave.Database.DataSource.CanConnectAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Opens and closes a real session, returning false only for provider connection failures.

**param cancellationToken:** The connection test cancellation token.

**returns:** True when a native session was established; false for a provider connection failure.

**exception:** Admission is closed.

**exception:** Cancellation is confirmed.

**remarks:** Configuration, loading, resource, panic and bridge failures retain their exception types.

**Inherited type policy:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

## `M:NativeWeave.Database.DataSource.CloseAsync(System.TimeSpan,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<NativeWeave.Database.DatabaseCloseResult> NativeWeave.Database.DataSource.CloseAsync(System.TimeSpan waitBudget, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Starts closure and waits only within the caller's independent budget.

**param waitBudget:** A finite nonnegative wait; zero returns the current observation.

**param cancellationToken:** Cancels this wait while native cleanup continues.

**returns:** A truthful cleanup observation; expired waits report Pending and unreleased local resources.

**exception:** The budget is invalid.

**exception:** The wait is canceled after closure starts.

**Inherited type policy:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

## `M:NativeWeave.Database.DataSource.Dispose`

```csharp
public void NativeWeave.Database.DataSource.Dispose()
```

**summary:** Closes admission and requests bounded native cleanup without blocking for I/O.

**Inherited type policy:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

## `M:NativeWeave.Database.DataSource.DisposeAsync`

```csharp
public System.Threading.Tasks.ValueTask NativeWeave.Database.DataSource.DisposeAsync()
```

**summary:** Closes admission and joins actual local safety, even beyond an application policy deadline.

**returns:** The native cleanup join.

**Inherited type policy:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

## `M:NativeWeave.Database.DataSource.OpenSessionAsync(System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<NativeWeave.Database.DataSession> NativeWeave.Database.DataSource.OpenSessionAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Acquires an independent physical session through bounded native pool admission.

**param cancellationToken:** Cancels admission or requests accepted native work to stop.

**returns:** A session that must be disposed; its ownership is independent from this operation.

**exception:** The source has closed admission.

**exception:** Admission or the provider fails.

**exception:** The native operation confirms cancellation.

**Inherited type policy:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

## `M:NativeWeave.Database.DataSourceBuilder.#ctor`

```csharp
public NativeWeave.Database.DataSourceBuilder.DataSourceBuilder()
```

**summary:** Creates an unconfigured builder without loading a module.

**Inherited type policy:** Configuration snapshots values. Build negotiates the packaged module and creates a lazy native pool; it opens no database connection. Independent builds create independent source generations.

## `M:NativeWeave.Database.DataSourceBuilder.Build`

```csharp
public NativeWeave.Database.DataSource NativeWeave.Database.DataSourceBuilder.Build()
```

**summary:** Negotiates the selected precompiled engine and creates an independently owned lazy pool.

**returns:** A source to dispose asynchronously when its sessions have finished.

**exception:** No provider was configured.

**exception:** The packaged module fails preflight.

**exception:** The native provider rejects its immutable configuration.

**remarks:** This call starts an engine-local runtime but performs no connection or migration I/O.

**Inherited type policy:** Configuration snapshots values. Build negotiates the packaged module and creates a lazy native pool; it opens no database connection. Independent builds create independent source generations.

## `M:NativeWeave.Database.DataSourceBuilder.ConfigureLimits(NativeWeave.Database.DatabaseLimits)`

```csharp
public NativeWeave.Database.DataSourceBuilder NativeWeave.Database.DataSourceBuilder.ConfigureLimits(NativeWeave.Database.DatabaseLimits limits)
```

**summary:** Copies and validates finite resource limits for subsequent builds.

**param limits:** The immutable limit settings.

**returns:** This builder.

**exception:** is null.

**exception:** A value exceeds the native finite profile.

**Inherited type policy:** Configuration snapshots values. Build negotiates the packaged module and creates a lazy native pool; it opens no database connection. Independent builds create independent source generations.

## `M:NativeWeave.Database.DataSourceBuilder.WithName(System.String)`

```csharp
public NativeWeave.Database.DataSourceBuilder NativeWeave.Database.DataSourceBuilder.WithName(string name)
```

**summary:** Sets the diagnostic source identity without including connection credentials.

**param name:** A nonempty strict-Unicode name of at most 256 UTF-8 bytes.

**returns:** This builder.

**exception:** The name is empty, contains NUL, or is invalid Unicode.

**exception:** is null.

**exception:** The name exceeds its byte limit.

**Inherited type policy:** Configuration snapshots values. Build negotiates the packaged module and creates a lazy native pool; it opens no database connection. Independent builds create independent source generations.

## `M:NativeWeave.Database.DatabaseCapabilities.Supports(System.String)`

```csharp
public bool NativeWeave.Database.DatabaseCapabilities.Supports(string capability)
```

**summary:** Tests one case-sensitive stable capability name without a native call.

**param capability:** The requested name; an unknown or empty name returns false.

**returns:** True only if the actual module advertised the capability.

**exception:** is null.

**Inherited type policy:** Unknown names return false. A capability name does not authorize a different provider or an arbitrary SQL feature.

## `M:NativeWeave.Database.DatabaseLimits.#ctor`

```csharp
public NativeWeave.Database.DatabaseLimits.DatabaseLimits()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `M:NativeWeave.Database.DatabaseLimits.Equals(NativeWeave.Database.DatabaseLimits)`

```csharp
public bool NativeWeave.Database.DatabaseLimits.Equals(NativeWeave.Database.DatabaseLimits? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `M:NativeWeave.Database.DatabaseLimits.Equals(System.Object)`

```csharp
public override bool NativeWeave.Database.DatabaseLimits.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `M:NativeWeave.Database.DatabaseLimits.GetHashCode`

```csharp
public override int NativeWeave.Database.DatabaseLimits.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `M:NativeWeave.Database.DatabaseLimits.ToString`

```csharp
public override string NativeWeave.Database.DatabaseLimits.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `M:NativeWeave.Database.DatabaseLimits.op_Equality(NativeWeave.Database.DatabaseLimits,NativeWeave.Database.DatabaseLimits)`

```csharp
public static bool NativeWeave.Database.DatabaseLimits.operator ==(NativeWeave.Database.DatabaseLimits? left, NativeWeave.Database.DatabaseLimits? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `M:NativeWeave.Database.DatabaseLimits.op_Inequality(NativeWeave.Database.DatabaseLimits,NativeWeave.Database.DatabaseLimits)`

```csharp
public static bool NativeWeave.Database.DatabaseLimits.operator !=(NativeWeave.Database.DatabaseLimits? left, NativeWeave.Database.DatabaseLimits? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `M:NativeWeave.Database.DatabaseLimits.{Clone}$`

```csharp
public NativeWeave.Database.DatabaseLimits NativeWeave.Database.DatabaseLimits.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `M:NativeWeave.Database.DatabaseProvider.#ctor`

```csharp
public NativeWeave.Database.DatabaseProvider.DatabaseProvider()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `P:NativeWeave.Database.DataSession.Cleanup`

```csharp
public NativeWeave.Database.DatabaseCloseResult NativeWeave.Database.DataSession.Cleanup { get; }
```

**summary:** Gets the current cleanup observation, retained after disposal.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `P:NativeWeave.Database.DataSession.Source`

```csharp
public NativeWeave.Database.DataSource NativeWeave.Database.DataSession.Source { get; }
```

**summary:** Gets the creating data source, retained while this physical session remains owned.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `P:NativeWeave.Database.DataSession.Transaction`

```csharp
public NativeWeave.Database.DataTransaction? NativeWeave.Database.DataSession.Transaction { get; }
```

**summary:** Gets the last active transaction, or null after its native state has closed.

**Inherited type policy:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `P:NativeWeave.Database.DataSource.Capabilities`

```csharp
public NativeWeave.Database.DatabaseCapabilities NativeWeave.Database.DataSource.Capabilities { get; }
```

**summary:** Gets the actual negotiated provider capability snapshot.

**Inherited type policy:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

## `P:NativeWeave.Database.DataSource.Cleanup`

```csharp
public NativeWeave.Database.DatabaseCloseResult NativeWeave.Database.DataSource.Cleanup { get; }
```

**summary:** Gets the current cleanup observation, retained after disposal.

**Inherited type policy:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

## `P:NativeWeave.Database.DataSource.Limits`

```csharp
public NativeWeave.Database.DatabaseLimits NativeWeave.Database.DataSource.Limits { get; }
```

**summary:** Gets the immutable source limits.

**Inherited type policy:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

## `P:NativeWeave.Database.DataSource.Name`

```csharp
public string NativeWeave.Database.DataSource.Name { get; }
```

**summary:** Gets the immutable noncredential diagnostic name.

**Inherited type policy:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

## `P:NativeWeave.Database.DataSource.Provider`

```csharp
public NativeWeave.Database.DatabaseProvider NativeWeave.Database.DataSource.Provider { get; }
```

**summary:** Gets the selected native provider.

**Inherited type policy:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

## `P:NativeWeave.Database.DatabaseCapabilities.Names`

```csharp
public System.Collections.Generic.IReadOnlyList<string> NativeWeave.Database.DatabaseCapabilities.Names { get; }
```

**summary:** Gets the immutable ordinal capability-name snapshot.

**Inherited type policy:** Unknown names return false. A capability name does not authorize a different provider or an arbitrary SQL feature.

## `P:NativeWeave.Database.DatabaseCapabilities.Profile`

```csharp
public string NativeWeave.Database.DatabaseCapabilities.Profile { get; }
```

**summary:** Gets the stable selected semantic profile.

**Inherited type policy:** Unknown names return false. A capability name does not authorize a different provider or an arbitrary SQL feature.

## `P:NativeWeave.Database.DatabaseCapabilities.Provider`

```csharp
public NativeWeave.Database.DatabaseProvider NativeWeave.Database.DatabaseCapabilities.Provider { get; }
```

**summary:** Gets the selected provider.

**Inherited type policy:** Unknown names return false. A capability name does not authorize a different provider or an arbitrary SQL feature.

## `P:NativeWeave.Database.DatabaseLimits.AdmissionTimeout`

```csharp
public System.TimeSpan NativeWeave.Database.DatabaseLimits.AdmissionTimeout { get; init; }
```

**summary:** Gets the positive finite pool admission timeout; defaults to fifteen seconds.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:NativeWeave.Database.DatabaseLimits.CommandTimeout`

```csharp
public System.TimeSpan NativeWeave.Database.DatabaseLimits.CommandTimeout { get; init; }
```

**summary:** Gets the positive default total command timeout; defaults to thirty seconds.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:NativeWeave.Database.DatabaseLimits.MaxBatchBytes`

```csharp
public long NativeWeave.Database.DatabaseLimits.MaxBatchBytes { get; init; }
```

**summary:** Gets the entire encoded batch budget including descriptors; defaults to 1 MiB.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:NativeWeave.Database.DatabaseLimits.MaxBatchRows`

```csharp
public int NativeWeave.Database.DatabaseLimits.MaxBatchRows { get; init; }
```

**summary:** Gets the maximum rows in one transferred batch; defaults to 256.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:NativeWeave.Database.DatabaseLimits.MaxBufferedBytes`

```csharp
public long NativeWeave.Database.DatabaseLimits.MaxBufferedBytes { get; init; }
```

**summary:** Gets the buffered encoded-byte budget; defaults to 16 MiB. Zero prohibits buffered demand.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:NativeWeave.Database.DatabaseLimits.MaxBufferedRows`

```csharp
public int NativeWeave.Database.DatabaseLimits.MaxBufferedRows { get; init; }
```

**summary:** Gets the maximum buffered result rows; defaults to 10,000. Zero prohibits buffered demand.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:NativeWeave.Database.DatabaseLimits.MaxConnections`

```csharp
public int NativeWeave.Database.DatabaseLimits.MaxConnections { get; init; }
```

**summary:** Gets the maximum simultaneous physical sessions; defaults to 32.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:NativeWeave.Database.DatabaseLimits.MaxFieldBytes`

```csharp
public long NativeWeave.Database.DatabaseLimits.MaxFieldBytes { get; init; }
```

**summary:** Gets the general field byte limit; defaults to 8 MiB. An inline field must also fit its batch.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:NativeWeave.Database.DatabaseLimits.MaxParameterBytes`

```csharp
public long NativeWeave.Database.DatabaseLimits.MaxParameterBytes { get; init; }
```

**summary:** Gets the total encoded parameter budget; defaults to 4 MiB.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:NativeWeave.Database.DatabaseLimits.MaxParameters`

```csharp
public int NativeWeave.Database.DatabaseLimits.MaxParameters { get; init; }
```

**summary:** Gets the maximum typed parameters per operation; defaults to 999.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:NativeWeave.Database.DatabaseLimits.MaxPreparedPlanBytes`

```csharp
public long NativeWeave.Database.DatabaseLimits.MaxPreparedPlanBytes { get; init; }
```

**summary:** Gets the source-owned prepared shape byte limit; defaults to 8 MiB.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:NativeWeave.Database.DatabaseLimits.MaxPreparedPlans`

```csharp
public int NativeWeave.Database.DatabaseLimits.MaxPreparedPlans { get; init; }
```

**summary:** Gets the source-owned prepared shape entry limit; defaults to 128.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:NativeWeave.Database.DatabaseLimits.MaxQueryDepth`

```csharp
public int NativeWeave.Database.DatabaseLimits.MaxQueryDepth { get; init; }
```

**summary:** Gets the maximum native query depth; defaults to 64.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:NativeWeave.Database.DatabaseLimits.MaxQueryNodes`

```csharp
public int NativeWeave.Database.DatabaseLimits.MaxQueryNodes { get; init; }
```

**summary:** Gets the maximum query and registration nodes; defaults to 4,096.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `P:NativeWeave.Database.DatabaseLimits.MaxWaiters`

```csharp
public int NativeWeave.Database.DatabaseLimits.MaxWaiters { get; init; }
```

**summary:** Gets the maximum bounded session waiters; defaults to 128.

**Inherited type policy:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `T:NativeWeave.Database.DataSession`

```csharp
public sealed class NativeWeave.Database.DataSession : System.IDisposable, System.IAsyncDisposable
```

**summary:** Owns one physical native session with fail-fast single-command admission.

**remarks:** A cursor and transaction control retain the same session gate. A session is not a concurrent command queue. Every query maps synchronously into owned CLR values and joins cleanup before its public Task completes.

## `T:NativeWeave.Database.DataSource`

```csharp
public sealed class NativeWeave.Database.DataSource : System.IDisposable, System.IAsyncDisposable
```

**summary:** Owns one lazy Rust pool, bounded runtime and source-generation metadata registry.

**remarks:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

## `T:NativeWeave.Database.DataSourceBuilder`

```csharp
public sealed class NativeWeave.Database.DataSourceBuilder
```

**summary:** A single-writer builder for one explicitly selected native database engine.

**remarks:** Configuration snapshots values. Build negotiates the packaged module and creates a lazy native pool; it opens no database connection. Independent builds create independent source generations.

## `T:NativeWeave.Database.DatabaseCapabilities`

```csharp
public sealed class NativeWeave.Database.DatabaseCapabilities
```

**summary:** An immutable snapshot of the capabilities actually negotiated for this provider module.

**remarks:** Unknown names return false. A capability name does not authorize a different provider or an arbitrary SQL feature.

## `T:NativeWeave.Database.DatabaseLimits`

```csharp
public sealed record class NativeWeave.Database.DatabaseLimits : System.IEquatable<NativeWeave.Database.DatabaseLimits>
```

**summary:** Immutable finite limits snapshotted by a data source before native creation.

**remarks:** These are admission and encoded-byte budgets, not measurements of CLR heap or process memory. Provider hard limits also apply. Increasing a value never selects an unbounded fallback.

## `T:NativeWeave.Database.DatabaseProvider`

```csharp
public enum NativeWeave.Database.DatabaseProvider : int
```

**summary:** The selected independently packaged Rust database provider.
