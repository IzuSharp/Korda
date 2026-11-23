using System.Buffers.Binary;
using Korda.Data;

namespace Korda.Database.Sqlite;

// Maintainer/host composition seam only. B01 owns the canonical control envelope.
internal static class SqliteConfiguration
{
    internal static byte[] Snapshot(SqliteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        SqliteOpenMode mode = options.Mode;
        SqliteJournalMode journal = options.JournalMode;
        SqliteSynchronousMode synchronous = options.Synchronous;
        SqliteBeginMode begin = options.BeginMode;
        TimeSpan busy = options.BusyTimeout;
        bool foreignKeys = options.ForeignKeys;
        if (!Enum.IsDefined(mode) || !Enum.IsDefined(journal) || !Enum.IsDefined(synchronous) || !Enum.IsDefined(begin))
            throw new ArgumentOutOfRangeException(nameof(options), "SQLite option enum is not defined.");
        if (busy < TimeSpan.Zero || busy.Ticks % TimeSpan.TicksPerMillisecond != 0 || busy.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), "BusyTimeout requires whole nonnegative milliseconds within SQLite's Int32 range.");
        if (mode == SqliteOpenMode.Memory && journal != SqliteJournalMode.Memory)
            throw new ArgumentException("Memory mode requires Memory journal and one host-owned physical connection.", nameof(options));
        var nanoseconds = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(nanoseconds, checked((ulong)busy.Ticks * 100));
        return ControlRecord.Encode(9,
            new(1, 1, ControlRecord.U32((uint)mode)), new(2, 1, ControlRecord.U32((uint)journal)),
            new(3, 1, ControlRecord.U32((uint)synchronous)), new(4, 1, ControlRecord.U32((uint)begin)),
            new(5, 2, nanoseconds), new(6, 1, ControlRecord.U32(foreignKeys ? 1u : 0u)));
    }
}
