# Korda.Database.Sqlite API reference

Generated from the compiled product assembly and its XML comments by `tests/Data.Sqlite/ApiCheck`. Covers provider options, Engine preflight, and the integrated Create/UseSqlite data source factories. Exact declarations retain the pinned Design identity in `public-api.expected.json`; the historical B03/B04 ownership slice remains in `host-composition.expected.json`.

## `F:Korda.Database.Sqlite.SqliteBeginMode.Deferred`

```csharp
Korda.Database.Sqlite.SqliteBeginMode.Deferred = 0
```

**summary:** Defers writer admission until the transaction first writes.

## `F:Korda.Database.Sqlite.SqliteBeginMode.Immediate`

```csharp
Korda.Database.Sqlite.SqliteBeginMode.Immediate = 1
```

**summary:** Acquires SQLite writer admission when the tracked transaction begins.

## `F:Korda.Database.Sqlite.SqliteJournalMode.Delete`

```csharp
Korda.Database.Sqlite.SqliteJournalMode.Delete = 1
```

**summary:** Uses a rollback journal deleted after a completed transaction.

## `F:Korda.Database.Sqlite.SqliteJournalMode.Memory`

```csharp
Korda.Database.Sqlite.SqliteJournalMode.Memory = 2
```

**summary:** Stores the rollback journal in memory; file-backed crash durability is weaker.

## `F:Korda.Database.Sqlite.SqliteJournalMode.Wal`

```csharp
Korda.Database.Sqlite.SqliteJournalMode.Wal = 0
```

**summary:** Uses a write-ahead log for a file-backed database; it does not allow concurrent writers.

## `F:Korda.Database.Sqlite.SqliteOpenMode.Memory`

```csharp
Korda.Database.Sqlite.SqliteOpenMode.Memory = 3
```

**summary:** Uses a private in-memory database with Memory journal and one physical connection.

## `F:Korda.Database.Sqlite.SqliteOpenMode.ReadOnly`

```csharp
Korda.Database.Sqlite.SqliteOpenMode.ReadOnly = 2
```

**summary:** Opens an existing file without write permission.

## `F:Korda.Database.Sqlite.SqliteOpenMode.ReadWrite`

```csharp
Korda.Database.Sqlite.SqliteOpenMode.ReadWrite = 1
```

**summary:** Opens an existing writable file; a missing file fails.

## `F:Korda.Database.Sqlite.SqliteOpenMode.ReadWriteCreate`

```csharp
Korda.Database.Sqlite.SqliteOpenMode.ReadWriteCreate = 0
```

**summary:** Opens a writable file and creates it when absent.

## `F:Korda.Database.Sqlite.SqliteSynchronousMode.Full`

```csharp
Korda.Database.Sqlite.SqliteSynchronousMode.Full = 1
```

**summary:** Uses SQLite's full synchronization policy.

## `F:Korda.Database.Sqlite.SqliteSynchronousMode.Normal`

```csharp
Korda.Database.Sqlite.SqliteSynchronousMode.Normal = 0
```

**summary:** Uses SQLite's normal synchronization policy.

## `M:Korda.Database.Sqlite.SqliteBeginMode.#ctor`

```csharp
public Korda.Database.Sqlite.SqliteBeginMode.SqliteBeginMode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:Korda.Database.Sqlite.SqliteDataSource.Create(System.String,System.Action{Korda.Database.Sqlite.SqliteOptions})`

```csharp
public static Korda.Database.DataSource Korda.Database.Sqlite.SqliteDataSource.Create(string path, System.Action<Korda.Database.Sqlite.SqliteOptions>? configure = null)
```

**summary:** Creates one independently owned lazy native SQLite data source.

**param path:** The nonempty database path; no connection is opened during construction.

**param configure:** An optional synchronous options callback whose values are immediately snapshotted.

**returns:** A data source whose sessions and source should be asynchronously disposed.

**exception:** The path or options are invalid.

**exception:** is null.

**exception:** The exact packaged module cannot be negotiated.

## `M:Korda.Database.Sqlite.SqliteDataSourceBuilderExtensions.UseSqlite(Korda.Database.DataSourceBuilder,System.String,System.Action{Korda.Database.Sqlite.SqliteOptions})`

```csharp
public static Korda.Database.DataSourceBuilder Korda.Database.Sqlite.SqliteDataSourceBuilderExtensions.UseSqlite(this Korda.Database.DataSourceBuilder builder, string path, System.Action<Korda.Database.Sqlite.SqliteOptions>? configure = null)
```

**summary:** Snapshots SQLite configuration without loading a module or opening a database.

**param builder:** The single-writer source builder.

**param path:** The nonempty database path.

**param configure:** Optional synchronous configuration; exceptions prevent builder mutation.

**returns:** The supplied builder.

**exception:** The builder or path is null.

**exception:** A provider setting or path is invalid.

## `M:Korda.Database.Sqlite.SqliteJournalMode.#ctor`

```csharp
public Korda.Database.Sqlite.SqliteJournalMode.SqliteJournalMode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:Korda.Database.Sqlite.SqliteOpenMode.#ctor`

```csharp
public Korda.Database.Sqlite.SqliteOpenMode.SqliteOpenMode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:Korda.Database.Sqlite.SqliteOptions.#ctor`

```csharp
public Korda.Database.Sqlite.SqliteOptions.SqliteOptions()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:Korda.Database.Sqlite.SqliteSynchronousMode.#ctor`

```csharp
public Korda.Database.Sqlite.SqliteSynchronousMode.SqliteSynchronousMode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `P:Korda.Database.Sqlite.SqliteDataSource.Engine`

```csharp
public static Korda.Interop.EngineDescriptor Korda.Database.Sqlite.SqliteDataSource.Engine { get; }
```

**summary:** Gets the immutable descriptor of the complete SQLite Database Host asset.

**remarks:** The canonical database bootstrap negotiates only capabilities implemented by this asset. Missing or mismatched assets fail inspection before runtime admission.

## `P:Korda.Database.Sqlite.SqliteOptions.BeginMode`

```csharp
public Korda.Database.Sqlite.SqliteBeginMode Korda.Database.Sqlite.SqliteOptions.BeginMode { get; set; }
```

**summary:** Gets or sets Deferred or Immediate writer admission without changing the isolation level.

## `P:Korda.Database.Sqlite.SqliteOptions.BusyTimeout`

```csharp
public System.TimeSpan Korda.Database.Sqlite.SqliteOptions.BusyTimeout { get; set; }
```

**summary:** Gets or sets the native busy timeout, from zero through Int32.MaxValue milliseconds in whole milliseconds.

**remarks:** Zero fails immediately on lock contention. This is a SQLite busy-handler bound, not an operation deadline. Unsupported submillisecond precision rejects rather than truncating.

## `P:Korda.Database.Sqlite.SqliteOptions.ForeignKeys`

```csharp
public bool Korda.Database.Sqlite.SqliteOptions.ForeignKeys { get; set; }
```

**summary:** Gets or sets foreign-key enforcement on every physical connection; the default is true.

## `P:Korda.Database.Sqlite.SqliteOptions.JournalMode`

```csharp
public Korda.Database.Sqlite.SqliteJournalMode Korda.Database.Sqlite.SqliteOptions.JournalMode { get; set; }
```

**summary:** Gets or sets the journal profile. Incompatible private-memory/WAL combinations reject before opening.

## `P:Korda.Database.Sqlite.SqliteOptions.Mode`

```csharp
public Korda.Database.Sqlite.SqliteOpenMode Korda.Database.Sqlite.SqliteOptions.Mode { get; set; }
```

**summary:** Gets or sets file access. Memory requires Memory journal and a host limit of one connection.

## `P:Korda.Database.Sqlite.SqliteOptions.Synchronous`

```csharp
public Korda.Database.Sqlite.SqliteSynchronousMode Korda.Database.Sqlite.SqliteOptions.Synchronous { get; set; }
```

**summary:** Gets or sets the synchronization profile applied to every physical connection.

## `T:Korda.Database.Sqlite.SqliteBeginMode`

```csharp
public enum Korda.Database.Sqlite.SqliteBeginMode : int
```

**summary:** Selects writer admission separately from transaction isolation.

## `T:Korda.Database.Sqlite.SqliteDataSource`

```csharp
public static class Korda.Database.Sqlite.SqliteDataSource
```

**summary:** Creates SQLite datasources backed by the maintainer-built C++ Database Host.

**remarks:** Descriptor access is pure metadata. Inspect verifies the packaged asset without creating a runtime or opening a connection; Create owns the configured runtime and datasource.

## `T:Korda.Database.Sqlite.SqliteDataSourceBuilderExtensions`

```csharp
public static class Korda.Database.Sqlite.SqliteDataSourceBuilderExtensions
```

**summary:** Configures the explicitly selected independently packaged SQLite engine.

**remarks:** The builder remains single-writer. Configuration snapshots managed values without I/O; Build negotiates the separately packaged native engine and creates the source owner.

## `T:Korda.Database.Sqlite.SqliteJournalMode`

```csharp
public enum Korda.Database.Sqlite.SqliteJournalMode : int
```

**summary:** Selects SQLite's explicitly configured journal profile on every physical connection.

## `T:Korda.Database.Sqlite.SqliteOpenMode`

```csharp
public enum Korda.Database.Sqlite.SqliteOpenMode : int
```

**summary:** Specifies how the C++ SQLite provider opens the configured database.

## `T:Korda.Database.Sqlite.SqliteOptions`

```csharp
public sealed class Korda.Database.Sqlite.SqliteOptions
```

**summary:** Configures the precompiled sqlite3 provider.

**remarks:** Configure before source creation. A builder snapshots these mutable values once; concurrent mutation during snapshot is unsupported. Values do not create a connection, worker or runtime. Each physical connection receives this profile. Ordinary SQLite isolation is Serializable; BEGIN mode is independent. Progress cancellation is cooperative and cannot preempt every busy wait or native instruction.

## `T:Korda.Database.Sqlite.SqliteSynchronousMode`

```csharp
public enum Korda.Database.Sqlite.SqliteSynchronousMode : int
```

**summary:** Selects the SQLite synchronization policy without claiming a filesystem durability guarantee.

