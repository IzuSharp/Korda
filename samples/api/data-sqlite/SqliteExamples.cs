using NativeWeave.Database.Sqlite;
using NativeWeave.Interop;

namespace NativeWeave.Sqlite.Samples;

/// <summary>Compiled component configuration consumers, reused by actual JIT/AOT verification.</summary>
internal static class SqliteExamples
{
    internal static SqliteOptions Minimal() => new();

    internal static SqliteOptions Advanced() => new()
    {
        Mode = SqliteOpenMode.ReadWriteCreate,
        JournalMode = SqliteJournalMode.Delete,
        Synchronous = SqliteSynchronousMode.Normal,
        BeginMode = SqliteBeginMode.Immediate,
        BusyTimeout = TimeSpan.FromMilliseconds(25),
        ForeignKeys = false
    };

    internal static SqliteOptions PrivateMemory() => new()
    {
        Mode = SqliteOpenMode.Memory,
        JournalMode = SqliteJournalMode.Memory
    };

    internal static EngineDescriptor ProviderMetadata() => SqliteDataSource.Engine;
}
