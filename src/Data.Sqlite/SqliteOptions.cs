namespace NativeWeave.Database.Sqlite;

/// <summary>Specifies how the C++ SQLite provider opens the configured database.</summary>
public enum SqliteOpenMode
{
    /// <summary>Opens a writable file and creates it when absent.</summary>
    ReadWriteCreate,
    /// <summary>Opens an existing writable file; a missing file fails.</summary>
    ReadWrite,
    /// <summary>Opens an existing file without write permission.</summary>
    ReadOnly,
    /// <summary>Uses a private in-memory database with Memory journal and one physical connection.</summary>
    Memory
}

/// <summary>Selects SQLite's explicitly configured journal profile on every physical connection.</summary>
public enum SqliteJournalMode
{
    /// <summary>Uses a write-ahead log for a file-backed database; it does not allow concurrent writers.</summary>
    Wal,
    /// <summary>Uses a rollback journal deleted after a completed transaction.</summary>
    Delete,
    /// <summary>Stores the rollback journal in memory; file-backed crash durability is weaker.</summary>
    Memory
}

/// <summary>Selects writer admission separately from transaction isolation.</summary>
public enum SqliteBeginMode
{
    /// <summary>Defers writer admission until the transaction first writes.</summary>
    Deferred,
    /// <summary>Acquires SQLite writer admission when the tracked transaction begins.</summary>
    Immediate
}

/// <summary>Selects the SQLite synchronization policy without claiming a filesystem durability guarantee.</summary>
public enum SqliteSynchronousMode
{
    /// <summary>Uses SQLite's normal synchronization policy.</summary>
    Normal,
    /// <summary>Uses SQLite's full synchronization policy.</summary>
    Full
}

/// <summary>Configures the precompiled sqlite3 provider.</summary>
/// <remarks>Configure before source creation. A builder snapshots these mutable values once; concurrent mutation during snapshot is unsupported. Values do not create a connection, worker or runtime. Each physical connection receives this profile. Ordinary SQLite isolation is Serializable; BEGIN mode is independent. Progress cancellation is cooperative and cannot preempt every busy wait or native instruction.</remarks>
public sealed class SqliteOptions
{
    /// <summary>Creates the explicit writable-file, WAL, Full, Deferred, five-second, foreign-key-enabled profile.</summary>
    public SqliteOptions() { }

    /// <summary>Gets or sets file access. Memory requires Memory journal and a host limit of one connection.</summary>
    public SqliteOpenMode Mode { get; set; } = SqliteOpenMode.ReadWriteCreate;

    /// <summary>Gets or sets the journal profile. Incompatible private-memory/WAL combinations reject before opening.</summary>
    public SqliteJournalMode JournalMode { get; set; } = SqliteJournalMode.Wal;

    /// <summary>Gets or sets the synchronization profile applied to every physical connection.</summary>
    public SqliteSynchronousMode Synchronous { get; set; } = SqliteSynchronousMode.Full;

    /// <summary>Gets or sets Deferred or Immediate writer admission without changing the isolation level.</summary>
    public SqliteBeginMode BeginMode { get; set; } = SqliteBeginMode.Deferred;

    /// <summary>Gets or sets the native busy timeout, from zero through Int32.MaxValue milliseconds in whole milliseconds.</summary>
    /// <remarks>Zero fails immediately on lock contention. This is a SQLite busy-handler bound, not an operation deadline. Unsupported submillisecond precision rejects rather than truncating.</remarks>
    public TimeSpan BusyTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets foreign-key enforcement on every physical connection; the default is true.</summary>
    public bool ForeignKeys { get; set; } = true;
}
