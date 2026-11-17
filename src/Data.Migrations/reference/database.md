# NativeWeave.Database migration API

Generated from the compiled product assembly and XML by `src/Build.Integration/ApiReference`.

Compiled product API compared to Design commit c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9. B08 owns explicit migration and catalog administration; docs/guides/data-migrations.md describes limits and provider semantics.

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

## `F:NativeWeave.Database.MigrationTransactionMode.NonTransactional`

```csharp
NativeWeave.Database.MigrationTransactionMode.NonTransactional = 1
```

**summary:** Requires PostgreSQL and an explicit opt-in; partial effects can remain after failure.

## `F:NativeWeave.Database.MigrationTransactionMode.Transactional`

```csharp
NativeWeave.Database.MigrationTransactionMode.Transactional = 0
```

**summary:** Commits the script and its ledger atomically; SQLite also wraps the entire selected batch.

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

## `M:NativeWeave.Database.DatabaseAdministration.GetMigrationStatusAsync(NativeWeave.Database.MigrationBundle,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<NativeWeave.Database.MigrationStatus>> NativeWeave.Database.DatabaseAdministration.GetMigrationStatusAsync(NativeWeave.Database.MigrationBundle bundle, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Reads an immutable comparison of this bundle and the successful native ledger.

**param bundle:** The exact provider-bound bundle, including previously applied entries.

**param cancellationToken:** The cancellation request retained through connection cleanup.

**returns:** Statuses in strictly ascending version order.

**exception:** The bundle/provider is invalid.

**exception:** History, checksum, provider or result limits fail.

**exception:** Cancellation is confirmed after cleanup.

**Inherited type policy:** One independent administrative connection executes at a time per source, with bounded waiting. Every outcome closes that connection before task retirement. It never uses a request session or request transaction. Result counts, strings and nested columns are charged to source buffering and field limits.

## `M:NativeWeave.Database.DatabaseAdministration.GetTablesAsync(System.String,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<NativeWeave.Database.SchemaTable>> NativeWeave.Database.DatabaseAdministration.GetTablesAsync(string? schema = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Reads bounded table and column metadata with native parameterized catalog queries.

**param schema:** A user-schema identifier; null selects PostgreSQL current_schema or SQLite main.

**param cancellationToken:** The cancellation request retained through cleanup.

**returns:** Immutable tables with columns in catalog order.

**exception:** The schema scope is invalid or names a system/attached schema.

**exception:** Provider or nested buffering/field limits fail.

**exception:** Cancellation is confirmed after cleanup.

**remarks:** This reads catalog metadata only. It performs no schema difference inference or automatic model creation.

**Inherited type policy:** One independent administrative connection executes at a time per source, with bounded waiting. Every outcome closes that connection before task retirement. It never uses a request session or request transaction. Result counts, strings and nested columns are charged to source buffering and field limits.

## `M:NativeWeave.Database.DatabaseAdministration.MigrateAsync(NativeWeave.Database.MigrationBundle,NativeWeave.Database.MigrationOptions,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task<NativeWeave.Database.MigrationReport> NativeWeave.Database.DatabaseAdministration.MigrateAsync(NativeWeave.Database.MigrationBundle bundle, NativeWeave.Database.MigrationOptions? options = null, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Applies the selected forward migration range using SQLx and its persisted checksum ledger.

**param bundle:** An immutable trusted bundle for this source's provider.

**param options:** Target and explicit nontransactional policy; null uses transactional defaults.

**param cancellationToken:** Requests cancellation while retained native work and connection closure complete.

**returns:** A confirmed completed report; a failed migration throws.

**exception:** The bundle/provider/options are invalid.

**exception:** Provider, checksum, dirty phase or finite result capacity fails.

**exception:** The native provider/profile does not support the requested migration mode.

**exception:** Cancellation is confirmed after native cleanup.

**remarks:** PostgreSQL commits each transactional entry independently. SQLite commits the entire selected batch in one tracked IMMEDIATE transaction. Nontransactional PostgreSQL preserves partial effects and an applying phase; only a new explicit identical call may resume a replay-safe script. Cancellation or connection closure never proves that a previously dispatched business commit did not happen.

**Inherited type policy:** One independent administrative connection executes at a time per source, with bounded waiting. Every outcome closes that connection before task retirement. It never uses a request session or request transaction. Result counts, strings and nested columns are charged to source buffering and field limits.

## `M:NativeWeave.Database.DatabaseAdministration.ValidateMigrationsAsync(NativeWeave.Database.MigrationBundle,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task NativeWeave.Database.DatabaseAdministration.ValidateMigrationsAsync(NativeWeave.Database.MigrationBundle bundle, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Validates checksum, missing/extra history and dirty execution phases without changing schema.

**param bundle:** The complete trusted manifest expected in the database.

**param cancellationToken:** The cancellation request retained through cleanup.

**returns:** A task completing only after validation and dedicated connection retirement.

**exception:** The manifest/provider is invalid.

**exception:** The ledger is incompatible, dirty, inaccessible or exceeds bounds.

**exception:** Cancellation is confirmed after cleanup.

**Inherited type policy:** One independent administrative connection executes at a time per source, with bounded waiting. Every outcome closes that connection before task retirement. It never uses a request session or request transaction. Result counts, strings and nested columns are charged to source buffering and field limits.

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

## `M:NativeWeave.Database.Migration.#ctor(System.Int64,System.String,System.String,NativeWeave.Database.MigrationTransactionMode)`

```csharp
public NativeWeave.Database.Migration.Migration(long Version, string Description, string Sql, NativeWeave.Database.MigrationTransactionMode TransactionMode = NativeWeave.Database.MigrationTransactionMode.Transactional)
```

**summary:** Defines one trusted, immutable SQL migration.

**remarks:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

**param Version:** A positive version, strictly ascending within its bundle.

**param Description:** A nonempty diagnostic description without credentials.

**param Sql:** Exact Unicode SQL; UTF-8 bytes are checksummed without newline normalization.

**param TransactionMode:** Transactional by default; explicit transaction-control statements are rejected.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `M:NativeWeave.Database.Migration.Deconstruct(System.Int64@,System.String@,System.String@,NativeWeave.Database.MigrationTransactionMode@)`

```csharp
public void NativeWeave.Database.Migration.Deconstruct(out long Version, out string Description, out string Sql, out NativeWeave.Database.MigrationTransactionMode TransactionMode)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `M:NativeWeave.Database.Migration.Equals(NativeWeave.Database.Migration)`

```csharp
public bool NativeWeave.Database.Migration.Equals(NativeWeave.Database.Migration? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `M:NativeWeave.Database.Migration.Equals(System.Object)`

```csharp
public override bool NativeWeave.Database.Migration.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `M:NativeWeave.Database.Migration.GetHashCode`

```csharp
public override int NativeWeave.Database.Migration.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `M:NativeWeave.Database.Migration.ToString`

```csharp
public override string NativeWeave.Database.Migration.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `M:NativeWeave.Database.Migration.op_Equality(NativeWeave.Database.Migration,NativeWeave.Database.Migration)`

```csharp
public static bool NativeWeave.Database.Migration.operator ==(NativeWeave.Database.Migration? left, NativeWeave.Database.Migration? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `M:NativeWeave.Database.Migration.op_Inequality(NativeWeave.Database.Migration,NativeWeave.Database.Migration)`

```csharp
public static bool NativeWeave.Database.Migration.operator !=(NativeWeave.Database.Migration? left, NativeWeave.Database.Migration? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `M:NativeWeave.Database.Migration.{Clone}$`

```csharp
public NativeWeave.Database.Migration NativeWeave.Database.Migration.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `M:NativeWeave.Database.MigrationBundle.#ctor(NativeWeave.Database.DatabaseProvider,System.Collections.Generic.IReadOnlyList{NativeWeave.Database.Migration})`

```csharp
public NativeWeave.Database.MigrationBundle.MigrationBundle(NativeWeave.Database.DatabaseProvider provider, System.Collections.Generic.IReadOnlyList<NativeWeave.Database.Migration> migrations)
```

**summary:** Creates an immutable snapshot and validates versions, strings, modes and finite encoding limits.

**param provider:** The exact provider that may consume the bundle.

**param migrations:** The trusted scripts in strictly ascending positive version order.

**exception:** The collection, an entry or a string is null.

**exception:** Versions, strings, modes or encoded limits are invalid.

**remarks:** The input collection may be changed after return; this instance retains its own snapshot.

**Inherited type policy:** Construction performs no native load or database I/O. Its SHA-256 identity covers the canonical kind12 provider and entry sequence before the hash field is added. Each entry separately contains SQL SHA-256; the native SQLx ledger retains SQLx's own SHA-384 checksum. Scripts must be trusted deployment artifacts.

## `M:NativeWeave.Database.MigrationOptions.#ctor`

```csharp
public NativeWeave.Database.MigrationOptions.MigrationOptions()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `M:NativeWeave.Database.MigrationOptions.Equals(NativeWeave.Database.MigrationOptions)`

```csharp
public bool NativeWeave.Database.MigrationOptions.Equals(NativeWeave.Database.MigrationOptions? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `M:NativeWeave.Database.MigrationOptions.Equals(System.Object)`

```csharp
public override bool NativeWeave.Database.MigrationOptions.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `M:NativeWeave.Database.MigrationOptions.GetHashCode`

```csharp
public override int NativeWeave.Database.MigrationOptions.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `M:NativeWeave.Database.MigrationOptions.ToString`

```csharp
public override string NativeWeave.Database.MigrationOptions.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `M:NativeWeave.Database.MigrationOptions.op_Equality(NativeWeave.Database.MigrationOptions,NativeWeave.Database.MigrationOptions)`

```csharp
public static bool NativeWeave.Database.MigrationOptions.operator ==(NativeWeave.Database.MigrationOptions? left, NativeWeave.Database.MigrationOptions? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `M:NativeWeave.Database.MigrationOptions.op_Inequality(NativeWeave.Database.MigrationOptions,NativeWeave.Database.MigrationOptions)`

```csharp
public static bool NativeWeave.Database.MigrationOptions.operator !=(NativeWeave.Database.MigrationOptions? left, NativeWeave.Database.MigrationOptions? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `M:NativeWeave.Database.MigrationOptions.{Clone}$`

```csharp
public NativeWeave.Database.MigrationOptions NativeWeave.Database.MigrationOptions.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `M:NativeWeave.Database.MigrationReport.#ctor(System.Int32,System.Nullable{System.Int64},System.Boolean)`

```csharp
public NativeWeave.Database.MigrationReport.MigrationReport(int AppliedCount, long? CurrentVersion, bool Completed)
```

**summary:** Reports a successfully completed explicit migration call.

**remarks:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

**param AppliedCount:** The number applied by this invocation.

**param CurrentVersion:** The last confirmed applied version, or null for an empty database.

**param Completed:** True on success; failures throw and retain their original error.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `M:NativeWeave.Database.MigrationReport.Deconstruct(System.Int32@,System.Nullable{System.Int64}@,System.Boolean@)`

```csharp
public void NativeWeave.Database.MigrationReport.Deconstruct(out int AppliedCount, out long? CurrentVersion, out bool Completed)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `M:NativeWeave.Database.MigrationReport.Equals(NativeWeave.Database.MigrationReport)`

```csharp
public bool NativeWeave.Database.MigrationReport.Equals(NativeWeave.Database.MigrationReport? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `M:NativeWeave.Database.MigrationReport.Equals(System.Object)`

```csharp
public override bool NativeWeave.Database.MigrationReport.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `M:NativeWeave.Database.MigrationReport.GetHashCode`

```csharp
public override int NativeWeave.Database.MigrationReport.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `M:NativeWeave.Database.MigrationReport.ToString`

```csharp
public override string NativeWeave.Database.MigrationReport.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `M:NativeWeave.Database.MigrationReport.op_Equality(NativeWeave.Database.MigrationReport,NativeWeave.Database.MigrationReport)`

```csharp
public static bool NativeWeave.Database.MigrationReport.operator ==(NativeWeave.Database.MigrationReport? left, NativeWeave.Database.MigrationReport? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `M:NativeWeave.Database.MigrationReport.op_Inequality(NativeWeave.Database.MigrationReport,NativeWeave.Database.MigrationReport)`

```csharp
public static bool NativeWeave.Database.MigrationReport.operator !=(NativeWeave.Database.MigrationReport? left, NativeWeave.Database.MigrationReport? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `M:NativeWeave.Database.MigrationReport.{Clone}$`

```csharp
public NativeWeave.Database.MigrationReport NativeWeave.Database.MigrationReport.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `M:NativeWeave.Database.MigrationStatus.#ctor(System.Int64,System.String,System.String,System.Boolean)`

```csharp
public NativeWeave.Database.MigrationStatus.MigrationStatus(long Version, string Description, string Checksum, bool Applied)
```

**summary:** Describes one bounded immutable bundle/ledger comparison.

**remarks:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

**param Version:** The positive migration version.

**param Description:** The immutable bundle description.

**param Checksum:** The lowercase hexadecimal SQLx SHA-384 checksum, distinct from the bundle hash.

**param Applied:** True only when the successful SQLx ledger contains this exact migration.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `M:NativeWeave.Database.MigrationStatus.Deconstruct(System.Int64@,System.String@,System.String@,System.Boolean@)`

```csharp
public void NativeWeave.Database.MigrationStatus.Deconstruct(out long Version, out string Description, out string Checksum, out bool Applied)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `M:NativeWeave.Database.MigrationStatus.Equals(NativeWeave.Database.MigrationStatus)`

```csharp
public bool NativeWeave.Database.MigrationStatus.Equals(NativeWeave.Database.MigrationStatus? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `M:NativeWeave.Database.MigrationStatus.Equals(System.Object)`

```csharp
public override bool NativeWeave.Database.MigrationStatus.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `M:NativeWeave.Database.MigrationStatus.GetHashCode`

```csharp
public override int NativeWeave.Database.MigrationStatus.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `M:NativeWeave.Database.MigrationStatus.ToString`

```csharp
public override string NativeWeave.Database.MigrationStatus.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `M:NativeWeave.Database.MigrationStatus.op_Equality(NativeWeave.Database.MigrationStatus,NativeWeave.Database.MigrationStatus)`

```csharp
public static bool NativeWeave.Database.MigrationStatus.operator ==(NativeWeave.Database.MigrationStatus? left, NativeWeave.Database.MigrationStatus? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `M:NativeWeave.Database.MigrationStatus.op_Inequality(NativeWeave.Database.MigrationStatus,NativeWeave.Database.MigrationStatus)`

```csharp
public static bool NativeWeave.Database.MigrationStatus.operator !=(NativeWeave.Database.MigrationStatus? left, NativeWeave.Database.MigrationStatus? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `M:NativeWeave.Database.MigrationStatus.{Clone}$`

```csharp
public NativeWeave.Database.MigrationStatus NativeWeave.Database.MigrationStatus.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `M:NativeWeave.Database.MigrationTransactionMode.#ctor`

```csharp
public NativeWeave.Database.MigrationTransactionMode.MigrationTransactionMode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Database.SchemaColumn.#ctor(System.String,System.String,System.Boolean,System.Boolean)`

```csharp
public NativeWeave.Database.SchemaColumn.SchemaColumn(string Name, string StoreType, bool IsNullable, bool IsPrimaryKey)
```

**summary:** Describes a provider catalog column without exposing a provider row object.

**remarks:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

**param Name:** The exact store column name.

**param StoreType:** The provider's declared/formatted type.

**param IsNullable:** Whether the provider permits SQL NULL.

**param IsPrimaryKey:** Whether the column participates in a primary key.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `M:NativeWeave.Database.SchemaColumn.Deconstruct(System.String@,System.String@,System.Boolean@,System.Boolean@)`

```csharp
public void NativeWeave.Database.SchemaColumn.Deconstruct(out string Name, out string StoreType, out bool IsNullable, out bool IsPrimaryKey)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `M:NativeWeave.Database.SchemaColumn.Equals(NativeWeave.Database.SchemaColumn)`

```csharp
public bool NativeWeave.Database.SchemaColumn.Equals(NativeWeave.Database.SchemaColumn? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `M:NativeWeave.Database.SchemaColumn.Equals(System.Object)`

```csharp
public override bool NativeWeave.Database.SchemaColumn.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `M:NativeWeave.Database.SchemaColumn.GetHashCode`

```csharp
public override int NativeWeave.Database.SchemaColumn.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `M:NativeWeave.Database.SchemaColumn.ToString`

```csharp
public override string NativeWeave.Database.SchemaColumn.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `M:NativeWeave.Database.SchemaColumn.op_Equality(NativeWeave.Database.SchemaColumn,NativeWeave.Database.SchemaColumn)`

```csharp
public static bool NativeWeave.Database.SchemaColumn.operator ==(NativeWeave.Database.SchemaColumn? left, NativeWeave.Database.SchemaColumn? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `M:NativeWeave.Database.SchemaColumn.op_Inequality(NativeWeave.Database.SchemaColumn,NativeWeave.Database.SchemaColumn)`

```csharp
public static bool NativeWeave.Database.SchemaColumn.operator !=(NativeWeave.Database.SchemaColumn? left, NativeWeave.Database.SchemaColumn? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `M:NativeWeave.Database.SchemaColumn.{Clone}$`

```csharp
public NativeWeave.Database.SchemaColumn NativeWeave.Database.SchemaColumn.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `M:NativeWeave.Database.SchemaTable.#ctor(System.String,System.String,System.Collections.Generic.IReadOnlyList{NativeWeave.Database.SchemaColumn})`

```csharp
public NativeWeave.Database.SchemaTable.SchemaTable(string? Schema, string Name, System.Collections.Generic.IReadOnlyList<NativeWeave.Database.SchemaColumn> Columns)
```

**summary:** Describes a table and an immutable, ordinally ordered column snapshot.

**remarks:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

**param Schema:** The admitted provider schema; SQLite reports main.

**param Name:** The exact table name.

**param Columns:** The bounded columns in native catalog ordinal order.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

## `M:NativeWeave.Database.SchemaTable.Deconstruct(System.String@,System.String@,System.Collections.Generic.IReadOnlyList{NativeWeave.Database.SchemaColumn}@)`

```csharp
public void NativeWeave.Database.SchemaTable.Deconstruct(out string? Schema, out string Name, out System.Collections.Generic.IReadOnlyList<NativeWeave.Database.SchemaColumn> Columns)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

## `M:NativeWeave.Database.SchemaTable.Equals(NativeWeave.Database.SchemaTable)`

```csharp
public bool NativeWeave.Database.SchemaTable.Equals(NativeWeave.Database.SchemaTable? other)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

## `M:NativeWeave.Database.SchemaTable.Equals(System.Object)`

```csharp
public override bool NativeWeave.Database.SchemaTable.Equals(object? obj)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

## `M:NativeWeave.Database.SchemaTable.GetHashCode`

```csharp
public override int NativeWeave.Database.SchemaTable.GetHashCode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

## `M:NativeWeave.Database.SchemaTable.ToString`

```csharp
public override string NativeWeave.Database.SchemaTable.ToString()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

## `M:NativeWeave.Database.SchemaTable.op_Equality(NativeWeave.Database.SchemaTable,NativeWeave.Database.SchemaTable)`

```csharp
public static bool NativeWeave.Database.SchemaTable.operator ==(NativeWeave.Database.SchemaTable? left, NativeWeave.Database.SchemaTable? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

## `M:NativeWeave.Database.SchemaTable.op_Inequality(NativeWeave.Database.SchemaTable,NativeWeave.Database.SchemaTable)`

```csharp
public static bool NativeWeave.Database.SchemaTable.operator !=(NativeWeave.Database.SchemaTable? left, NativeWeave.Database.SchemaTable? right)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

## `M:NativeWeave.Database.SchemaTable.{Clone}$`

```csharp
public NativeWeave.Database.SchemaTable NativeWeave.Database.SchemaTable.<Clone>$()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

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

## `P:NativeWeave.Database.DataSource.Administration`

```csharp
public NativeWeave.Database.DatabaseAdministration NativeWeave.Database.DataSource.Administration { get; }
```

**summary:** Gets the explicit administration facade without opening a database connection.

**remarks:** Each operation owns a separate, bounded administrative connection. Source creation and DI activation never apply migrations. Private in-memory SQLite cannot expose a separate administrative connection.

**exception:** Source admission is closed.

**Inherited type policy:** Sources support concurrent independent session opens. Closing rejects new work and retains every admitted native operation until real retirement. Dispose requests cleanup; DisposeAsync joins local safety.

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

## `P:NativeWeave.Database.Migration.Description`

```csharp
public string NativeWeave.Database.Migration.Description { get; init; }
```

**summary:** A nonempty diagnostic description without credentials.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `P:NativeWeave.Database.Migration.Sql`

```csharp
public string NativeWeave.Database.Migration.Sql { get; init; }
```

**summary:** Exact Unicode SQL; UTF-8 bytes are checksummed without newline normalization.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `P:NativeWeave.Database.Migration.TransactionMode`

```csharp
public NativeWeave.Database.MigrationTransactionMode NativeWeave.Database.Migration.TransactionMode { get; init; }
```

**summary:** Transactional by default; explicit transaction-control statements are rejected.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `P:NativeWeave.Database.Migration.Version`

```csharp
public long NativeWeave.Database.Migration.Version { get; init; }
```

**summary:** A positive version, strictly ascending within its bundle.

**Inherited type policy:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

## `P:NativeWeave.Database.MigrationBundle.ContentHash`

```csharp
public string NativeWeave.Database.MigrationBundle.ContentHash { get; }
```

**summary:** Gets the lowercase SHA-256 of the canonical provider and immutable entry sequence.

**Inherited type policy:** Construction performs no native load or database I/O. Its SHA-256 identity covers the canonical kind12 provider and entry sequence before the hash field is added. Each entry separately contains SQL SHA-256; the native SQLx ledger retains SQLx's own SHA-384 checksum. Scripts must be trusted deployment artifacts.

## `P:NativeWeave.Database.MigrationOptions.AllowNonTransactional`

```csharp
public bool NativeWeave.Database.MigrationOptions.AllowNonTransactional { get; init; }
```

**summary:** Gets whether explicitly nontransactional PostgreSQL entries may execute or be resumed.

**remarks:** A new call is explicit replay of the identical script after an unfinished phase. The script must handle any previously completed effects. NativeWeave never retries a write within the call.

**Inherited type policy:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `P:NativeWeave.Database.MigrationOptions.LockTimeout`

```csharp
public System.TimeSpan NativeWeave.Database.MigrationOptions.LockTimeout { get; init; }
```

**summary:** Gets the positive finite administrative writer/advisory-lock wait, defaulting to thirty seconds.

**Inherited type policy:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `P:NativeWeave.Database.MigrationOptions.TargetVersion`

```csharp
public long? NativeWeave.Database.MigrationOptions.TargetVersion { get; init; }
```

**summary:** Gets the last version eligible for forward application; null selects the complete bundle.

**remarks:** A value below the applied current version is rejected; no downgrade is performed.

**Inherited type policy:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `P:NativeWeave.Database.MigrationReport.AppliedCount`

```csharp
public int NativeWeave.Database.MigrationReport.AppliedCount { get; init; }
```

**summary:** The number applied by this invocation.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `P:NativeWeave.Database.MigrationReport.Completed`

```csharp
public bool NativeWeave.Database.MigrationReport.Completed { get; init; }
```

**summary:** True on success; failures throw and retain their original error.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `P:NativeWeave.Database.MigrationReport.CurrentVersion`

```csharp
public long? NativeWeave.Database.MigrationReport.CurrentVersion { get; init; }
```

**summary:** The last confirmed applied version, or null for an empty database.

**Inherited type policy:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

## `P:NativeWeave.Database.MigrationStatus.Applied`

```csharp
public bool NativeWeave.Database.MigrationStatus.Applied { get; init; }
```

**summary:** True only when the successful SQLx ledger contains this exact migration.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `P:NativeWeave.Database.MigrationStatus.Checksum`

```csharp
public string NativeWeave.Database.MigrationStatus.Checksum { get; init; }
```

**summary:** The lowercase hexadecimal SQLx SHA-384 checksum, distinct from the bundle hash.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `P:NativeWeave.Database.MigrationStatus.Description`

```csharp
public string NativeWeave.Database.MigrationStatus.Description { get; init; }
```

**summary:** The immutable bundle description.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `P:NativeWeave.Database.MigrationStatus.Version`

```csharp
public long NativeWeave.Database.MigrationStatus.Version { get; init; }
```

**summary:** The positive migration version.

**Inherited type policy:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

## `P:NativeWeave.Database.SchemaColumn.IsNullable`

```csharp
public bool NativeWeave.Database.SchemaColumn.IsNullable { get; init; }
```

**summary:** Whether the provider permits SQL NULL.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `P:NativeWeave.Database.SchemaColumn.IsPrimaryKey`

```csharp
public bool NativeWeave.Database.SchemaColumn.IsPrimaryKey { get; init; }
```

**summary:** Whether the column participates in a primary key.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `P:NativeWeave.Database.SchemaColumn.Name`

```csharp
public string NativeWeave.Database.SchemaColumn.Name { get; init; }
```

**summary:** The exact store column name.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `P:NativeWeave.Database.SchemaColumn.StoreType`

```csharp
public string NativeWeave.Database.SchemaColumn.StoreType { get; init; }
```

**summary:** The provider's declared/formatted type.

**Inherited type policy:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

## `P:NativeWeave.Database.SchemaTable.Columns`

```csharp
public System.Collections.Generic.IReadOnlyList<NativeWeave.Database.SchemaColumn> NativeWeave.Database.SchemaTable.Columns { get; init; }
```

**summary:** The bounded columns in native catalog ordinal order.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

## `P:NativeWeave.Database.SchemaTable.Name`

```csharp
public string NativeWeave.Database.SchemaTable.Name { get; init; }
```

**summary:** The exact table name.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

## `P:NativeWeave.Database.SchemaTable.Schema`

```csharp
public string? NativeWeave.Database.SchemaTable.Schema { get; init; }
```

**summary:** The admitted provider schema; SQLite reports main.

**Inherited type policy:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

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

## `T:NativeWeave.Database.DatabaseAdministration`

```csharp
public sealed class NativeWeave.Database.DatabaseAdministration
```

**summary:** Runs explicit trusted migration and catalog operations through the native provider.

**remarks:** One independent administrative connection executes at a time per source, with bounded waiting. Every outcome closes that connection before task retirement. It never uses a request session or request transaction. Result counts, strings and nested columns are charged to source buffering and field limits.

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

## `T:NativeWeave.Database.Migration`

```csharp
public sealed record class NativeWeave.Database.Migration : System.IEquatable<NativeWeave.Database.Migration>
```

**summary:** Defines one trusted, immutable SQL migration.

**remarks:** This record retains its strings without native resources. Bundle construction snapshots the collection and performs bounded UTF-8 encoding and hashing; execution is explicit.

**param Version:** A positive version, strictly ascending within its bundle.

**param Description:** A nonempty diagnostic description without credentials.

**param Sql:** Exact Unicode SQL; UTF-8 bytes are checksummed without newline normalization.

**param TransactionMode:** Transactional by default; explicit transaction-control statements are rejected.

## `T:NativeWeave.Database.MigrationBundle`

```csharp
public sealed class NativeWeave.Database.MigrationBundle
```

**summary:** Snapshots a strictly ordered, provider-bound set of explicit SQL scripts.

**remarks:** Construction performs no native load or database I/O. Its SHA-256 identity covers the canonical kind12 provider and entry sequence before the hash field is added. Each entry separately contains SQL SHA-256; the native SQLx ledger retains SQLx's own SHA-384 checksum. Scripts must be trusted deployment artifacts.

## `T:NativeWeave.Database.MigrationOptions`

```csharp
public sealed record class NativeWeave.Database.MigrationOptions : System.IEquatable<NativeWeave.Database.MigrationOptions>
```

**summary:** Controls one explicit forward migration call.

**remarks:** This immutable managed value owns no connection or native resources. An administrative call snapshots its scalar settings before admission.

## `T:NativeWeave.Database.MigrationReport`

```csharp
public sealed record class NativeWeave.Database.MigrationReport : System.IEquatable<NativeWeave.Database.MigrationReport>
```

**summary:** Reports a successfully completed explicit migration call.

**remarks:** This detached managed value owns no native resource. The administrative connection has retired before task completion; earlier confirmed entries remain committed independently on PostgreSQL.

**param AppliedCount:** The number applied by this invocation.

**param CurrentVersion:** The last confirmed applied version, or null for an empty database.

**param Completed:** True on success; failures throw and retain their original error.

## `T:NativeWeave.Database.MigrationStatus`

```csharp
public sealed record class NativeWeave.Database.MigrationStatus : System.IEquatable<NativeWeave.Database.MigrationStatus>
```

**summary:** Describes one bounded immutable bundle/ledger comparison.

**remarks:** This detached managed record owns its immutable strings. Native result buffers are released before the task returns; the source bounds aggregate record and UTF-8 byte costs.

**param Version:** The positive migration version.

**param Description:** The immutable bundle description.

**param Checksum:** The lowercase hexadecimal SQLx SHA-384 checksum, distinct from the bundle hash.

**param Applied:** True only when the successful SQLx ledger contains this exact migration.

## `T:NativeWeave.Database.MigrationTransactionMode`

```csharp
public enum NativeWeave.Database.MigrationTransactionMode : int
```

**summary:** Selects SQLx transactional execution or explicitly resumable PostgreSQL execution.

## `T:NativeWeave.Database.SchemaColumn`

```csharp
public sealed record class NativeWeave.Database.SchemaColumn : System.IEquatable<NativeWeave.Database.SchemaColumn>
```

**summary:** Describes a provider catalog column without exposing a provider row object.

**remarks:** This detached immutable record retains its names and type strings. Each column counts toward the same source result-row and encoded-byte limits as its parent table.

**param Name:** The exact store column name.

**param StoreType:** The provider's declared/formatted type.

**param IsNullable:** Whether the provider permits SQL NULL.

**param IsPrimaryKey:** Whether the column participates in a primary key.

## `T:NativeWeave.Database.SchemaTable`

```csharp
public sealed record class NativeWeave.Database.SchemaTable : System.IEquatable<NativeWeave.Database.SchemaTable>
```

**summary:** Describes a table and an immutable, ordinally ordered column snapshot.

**remarks:** Administration returns a detached read-only column list with no native lease. Table names and all nested columns share the source buffering budget; no further database access is lazy.

**param Schema:** The admitted provider schema; SQLite reports main.

**param Name:** The exact table name.

**param Columns:** The bounded columns in native catalog ordinal order.
