using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Korda.Data;
using Korda.Database;
using Korda.Database.PostgreSql;
using Korda.Database.Sqlite;
using Korda.Query;

internal static class FetchProbe
{
    private const int BatchRows = 32, DataRows = 256;
    private static long _retired, _pulled, _emptySink;
    private sealed record Item(long Id);
    private static readonly Table<Item> Table;
    private static readonly Column<Item, long> Id;
    static FetchProbe()
    {
        var builder = new TableBuilder<Item>("dbperf_fetch_items");
        Id = builder.Column("Id", Codecs.Int64, new() { IsPrimaryKey = true });
        Table = builder.Build(row => new(row.Get(Id)), (row, item) => row.Set(Id, item.Id));
    }
    private readonly record struct Counter(long Bytes, int Gen0, int Gen1, int Gen2, long Retirements, long Pulls, long Ticks)
    {
        internal static Counter Read()
        {
            var g0 = GC.CollectionCount(0); var g1 = GC.CollectionCount(1); var g2 = GC.CollectionCount(2);
            return new(GC.GetTotalAllocatedBytes(true), g0, g1, g2, Interlocked.Read(ref _retired), Interlocked.Read(ref _pulled), Stopwatch.GetTimestamp());
        }
    }
    private readonly record struct Sample(string Name, int Window, long Operations, long Rows, long Checksum,
        long IncompleteMoves, long IncompleteDisposals, Counter Before, Counter After, ulong[]? NativeBefore = null, ulong[]? NativeAfter = null,
        long MoveThreadChanges = 0);

    internal static async Task<bool> RunIfRequested(string[] args)
    {
        if (args.Length == 0 || args[0] != "--fetch-probe") return false;
        if (args.Length != 7) throw new ArgumentException("--fetch-probe <sqlite|postgresql> <connection> <output> <warmup> <iterations> <windows>");
        if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("A real Native AOT process is required.");
        var warmup = int.Parse(args[4]); var iterations = int.Parse(args[5]); var windows = int.Parse(args[6]);
        if (warmup < 64 || iterations < 1 || windows is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(args));
        Directory.CreateDirectory(args[3]);
        var builder = new DataSourceBuilder().WithName("DB-Perf-01-fetch-delegates")
            .ConfigureLimits(new DatabaseLimits { MaxConnections = 1, MaxWaiters = 4, MaxBatchRows = BatchRows,
                MaxBatchBytes = 65536, MaxFieldBytes = 1024, MaxBufferedRows = 512, MaxQueryNodes = 512 });
        if (args[1] == "sqlite") builder.UseSqlite(args[2]);
        else if (args[1] == "postgresql") builder.UsePostgreSql(args[2], o => o.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment);
        else throw new ArgumentException("Unknown provider.");
        var source = builder.Build(); var session = await source.OpenSessionAsync();
        using var native = new NativeObservation(source.Provider);
        await session.ExecuteAsync(SqlCommand.Create("DROP TABLE IF EXISTS dbperf_fetch_items", kind: SqlCommandKind.Mutation));
        await session.ExecuteAsync(SqlCommand.Create("CREATE TABLE dbperf_fetch_items (\"Id\" BIGINT PRIMARY KEY)", kind: SqlCommandKind.Mutation));
        var insert = new StringBuilder("INSERT INTO dbperf_fetch_items (\"Id\") VALUES ");
        for (var i = 1; i <= DataRows; i++) { if (i != 1) insert.Append(','); insert.Append('(').Append(i).Append(')'); }
        if (await session.ExecuteAsync(SqlCommand.Create(insert.ToString(), kind: SqlCommandKind.Mutation)) != DataRows)
            throw new InvalidOperationException("Fixture row count differs.");
        var plans = new PreparedQuery<long>[3]; var sql = new string[3]; var rowCounts = new[] { 0, 1, DataRows };
        for (var i = 0; i < plans.Length; i++)
        {
            using var query = session.From(Table);
            query.OrderBy(Id).Take(rowCounts[i]); var selection = query.Select(query.Column(Id));
            sql[i] = selection.ToSql(); plans[i] = selection.Prepare();
        }
        // A static preinstalled observer counts normal operation retirements. It
        // allocates nothing and never substitutes an internal execution path.
        source.TestRetirement = static snapshot =>
        {
            if (!snapshot.Released || snapshot.RootLive || snapshot.Controls != 0 || snapshot.CallbackEntries != 0 || snapshot.RetainedInputBytes != 0)
                throw new InvalidOperationException("A public operation retained its managed bridge owners.");
            Interlocked.Increment(ref _retired);
        };
        source.TestPullSnapshot = static snapshot =>
        {
            if (!snapshot.Published || snapshot.Consumed) return;
            if (!snapshot.SetupReturned || !snapshot.Accepted || !snapshot.Ready || !snapshot.ResultTaken
                || snapshot.Rooted || snapshot.SlotReserved || snapshot.Controls != 0 || snapshot.CallbackEntries != 0
                || !snapshot.RegistrationDrained)
                throw new InvalidOperationException("A public pull published before releasing its bridge obligations.");
            Interlocked.Increment(ref _pulled);
        };
        var full = new Sample[plans.Length][];
        for (var i = 0; i < plans.Length; i++) full[i] = await Full(session, plans[i], rowCounts[i], warmup, iterations, windows, native);
        var slices = await Slices(session, plans[2], warmup, iterations, native);
        var empty = Empty(iterations); var emptyAsync = await EmptyAsync(warmup, iterations);
        foreach (var plan in plans) plan.Dispose();
        await session.DisposeAsync(); await source.DisposeAsync(); var drained = native.Counts();
        if (!session.Cleanup.LocalResourcesReleased || !source.Cleanup.LocalResourcesReleased || drained.Take(6).Any(v => v != 0) || drained[8] != 0 || drained[9] != 0)
            throw new InvalidOperationException("The public fetch fixture did not release all owners.");
        using var file = File.Create(Path.Combine(args[3], "fetch-allocation.json"));
        using var json = new Utf8JsonWriter(file, new() { Indented = true });
        json.WriteStartObject(); json.WriteNumber("SchemaVersion", 2); json.WriteBoolean("NativeAot", true); json.WriteString("Provider", source.Provider.ToString());
        json.WriteString("ExecutableSha256", Hash(Environment.ProcessPath!)); json.WriteString("LoadedNativePath", native.Path);
        json.WriteString("LoadedNativeSha256", Hash(native.Path)); json.WriteNumber("Warmup", warmup);
        json.WriteNumber("IterationsPerWindow", iterations); json.WriteNumber("Windows", windows);
        json.WriteNumber("DataRows", DataRows); json.WriteNumber("BatchRows", BatchRows);
        json.WriteString("Boundary", "Raw whole-process all-thread GC.GetTotalAllocatedBytes(true); no subtraction. Complete queries include one-shot stream, all rows, final false and Dispose; preparations/fixture/sample arrays are outside.");
        json.WriteString("Attribution", "No business DTO/string/list/result-array within measurements. Runtime/harness async infrastructure remains in raw process totals; static retirement and pull observers have no per-operation managed allocation.");
        json.WriteString("FetchAccounting", "Schema 2 counts actual completed pull publications separately from generic operation retirements. A partial final batch carries known EOF, while an exact row boundary needs an additional pull. Each complete query must publish the expected number of pulls and retire at most one final close: a native snapshot proving both channel worker and cursor retirement can complete close directly. The supplemental interval must publish exactly one pull, retire no generic operation and consume row33. Historical schema 1 measured the removed generic-fetch route.");
        json.WriteString("Scope", "Source-project public Fluent Native AOT attribution, not final independent-package or long-run acceptance; diagnostic ticks are not a throughput comparison.");
        json.WriteStartArray("Queries");
        for (var i = 0; i < plans.Length; i++)
        {
            json.WriteStartObject(); json.WriteNumber("ResultRows", rowCounts[i]); json.WriteString("Sql", sql[i]);
            json.WriteNumber("ExpectedDataBatches", (rowCounts[i] + BatchRows - 1) / BatchRows);
            json.WriteNumber("ExpectedFetchesIncludingEos", rowCounts[i] / BatchRows + 1);
            json.WriteStartArray("Measurements"); foreach (var sample in full[i]) Write(json, sample); json.WriteEndArray(); json.WriteEndObject();
        }
        json.WriteEndArray(); json.WriteStartArray("SupplementalAndWithinBatch"); foreach (var sample in slices) Write(json, sample); json.WriteEndArray();
        json.WriteStartArray("Controls"); Write(json, empty); Write(json, emptyAsync); json.WriteEndArray();
        WriteNative(json, "NativeDrained", drained); json.WriteString("NativeCounterOrder", "sources,sessions,operations,cursors,buffers,handles,cumulative-sql,cumulative-protocol,pulls");
        json.WriteBoolean("OwnersReturnedToZero", true); json.WriteEndObject();
        Console.WriteLine($"FETCH_PROBE_PASS provider={source.Provider} windows={windows} iterations={iterations}");
        return true;
    }

    private static async Task<Sample[]> Full(DataSession session, PreparedQuery<long> plan, int resultRows,
        int warmup, int iterations, int windows, NativeObservation native)
    {
        var samples = new Sample[windows]; await Task.Yield();
        var name = "prepared-complete-rows-" + resultRows;
        for (var window = -1; window < windows; window++)
        {
            var count = window < 0 ? warmup : iterations;
            long rows = 0, checksum = 0, incomplete = 0, disposalIncomplete = 0, threadChanges = 0;
            var nativeBefore = native.Counts(); var before = Counter.Read();
            for (var i = 0; i < count; i++)
            {
                var reader = plan.AsAsyncEnumerable(session).GetAsyncEnumerator();
                try
                {
                    var row = 0;
                    while (true)
                    {
                        var thread = Environment.CurrentManagedThreadId;
                        var next = reader.MoveNextAsync(); if (!next.IsCompletedSuccessfully) incomplete++;
                        var moved = await next.ConfigureAwait(false);
                        if (thread != Environment.CurrentManagedThreadId) threadChanges++;
                        if (!moved) break;
                        row++; if (reader.Current != row) throw new InvalidOperationException("Public row order/value differs.");
                        checksum += reader.Current;
                    }
                    if (row != resultRows) throw new InvalidOperationException("Public query row count differs.");
                    rows += row;
                }
                finally
                {
                    var dispose = reader.DisposeAsync(); if (!dispose.IsCompletedSuccessfully) disposalIncomplete++;
                    await dispose.ConfigureAwait(false);
                }
            }
            var after = Counter.Read(); var nativeAfter = native.Counts(); SameOwners(nativeBefore, nativeAfter);
            if (rows != (long)count * resultRows || checksum != (long)count * resultRows * (resultRows + 1) / 2)
                throw new InvalidOperationException("Complete result aggregate differs.");
            if (after.Pulls - before.Pulls != (long)count * (resultRows / BatchRows + 1)
                || after.Retirements - before.Retirements < 0 || after.Retirements - before.Retirements > count)
                throw new InvalidOperationException("A complete query did not use its expected pulls and at most one final close.");
            if (window >= 0) samples[window] = new(name, window, count, rows, checksum, incomplete, disposalIncomplete, before, after, nativeBefore, nativeAfter, threadChanges);
        }
        return samples;
    }

    private static async Task<Sample[]> Slices(DataSession session, PreparedQuery<long> plan, int warmup, int iterations, NativeObservation native)
    {
        var samples = new Sample[iterations * 2]; var ownersBefore = native.Counts(); await Task.Yield();
        for (var i = -warmup; i < iterations; i++)
        {
            await using var reader = plan.AsAsyncEnumerable(session).GetAsyncEnumerator();
            if (!await reader.MoveNextAsync().ConfigureAwait(false) || reader.Current != 1)
                throw new InvalidOperationException("First row differs.");
            var before = Counter.Read(); var sum = SyncRows(reader, BatchRows - 1, 2); var after = Counter.Read();
            var within = new Sample("within-first-batch", i, BatchRows - 1, BatchRows - 1, sum, 0, 0, before, after);
            var fetchBefore = Counter.Read(); var thread = Environment.CurrentManagedThreadId;
            var move = reader.MoveNextAsync(); var incomplete = !move.IsCompletedSuccessfully;
            var moved = await move.ConfigureAwait(false); var value = reader.Current; var fetchAfter = Counter.Read();
            var threadChanges = thread == Environment.CurrentManagedThreadId ? 0 : 1;
            if (!moved || value != BatchRows + 1 || fetchAfter.Pulls - fetchBefore.Pulls != 1
                || fetchAfter.Retirements != fetchBefore.Retirements)
                throw new InvalidOperationException("Supplemental fetch did not return row33 through exactly one pull and no generic operation retirement.");
            var fetch = new Sample("supplemental-fetch-and-first-row", i, 1, 1, value, incomplete ? 1 : 0, 0, fetchBefore, fetchAfter, MoveThreadChanges: threadChanges);
            var row = BatchRows + 1; sum += 1 + value;
            while (await reader.MoveNextAsync().ConfigureAwait(false))
            { row++; if (reader.Current != row) throw new InvalidOperationException("Remaining row differs."); sum += reader.Current; }
            if (row != DataRows || sum != (long)DataRows * (DataRows + 1) / 2)
                throw new InvalidOperationException("Supplemental query was not fully consumed.");
            if (i >= 0) { samples[i * 2] = within; samples[i * 2 + 1] = fetch; }
        }
        SameOwners(ownersBefore, native.Counts()); return samples;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long SyncRows(IAsyncEnumerator<long> reader, int count, long expected)
    {
        long sum = 0;
        for (var i = 0; i < count; i++)
        {
            var move = reader.MoveNextAsync();
            if (!move.IsCompletedSuccessfully || !move.Result || reader.Current != expected + i)
                throw new InvalidOperationException("A claimed in-batch row was not synchronous or had the wrong value.");
            sum += reader.Current;
        }
        return sum;
    }
    private static Sample Empty(int iterations)
    {
        var before = Counter.Read(); long sum = 0; for (var i = 0; i < iterations; i++) sum += i; _emptySink = sum;
        var after = Counter.Read(); return new("empty-sync", 0, iterations, 0, _emptySink, 0, 0, before, after);
    }
    private static async Task<Sample> EmptyAsync(int warmup, int iterations)
    {
        for (var i = 0; i < warmup; i++) await Task.Yield();
        var before = Counter.Read(); for (var i = 0; i < iterations; i++) await Task.Yield(); var after = Counter.Read();
        return new("task-yield-control-not-native-equivalent", 0, iterations, 0, 0, iterations, 0, before, after);
    }
    private static void SameOwners(ulong[] before, ulong[] after)
    {
        for (var i = 0; i < 6; i++) if (before[i] != after[i]) throw new InvalidOperationException("Live native owners did not return to the warm baseline.");
        if (before[8] != after[8]) throw new InvalidOperationException("Native pull objects did not return to the warm baseline.");
    }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
    private static void Write(Utf8JsonWriter json, Sample sample)
    {
        json.WriteStartObject(); json.WriteString("Name", sample.Name); json.WriteNumber("Window", sample.Window);
        json.WriteNumber("Operations", sample.Operations); json.WriteNumber("Rows", sample.Rows); json.WriteNumber("Checksum", sample.Checksum);
        json.WriteNumber("IncompleteMoves", sample.IncompleteMoves); json.WriteNumber("IncompleteDisposals", sample.IncompleteDisposals);
        json.WriteNumber("MoveThreadChanges", sample.MoveThreadChanges);
        json.WriteNumber("AllocatedBefore", sample.Before.Bytes); json.WriteNumber("AllocatedAfter", sample.After.Bytes);
        json.WriteNumber("AllocatedBytes", sample.After.Bytes - sample.Before.Bytes);
        json.WriteNumber("Gen0", sample.After.Gen0 - sample.Before.Gen0); json.WriteNumber("Gen1", sample.After.Gen1 - sample.Before.Gen1); json.WriteNumber("Gen2", sample.After.Gen2 - sample.Before.Gen2);
        json.WriteNumber("RetirementsBefore", sample.Before.Retirements); json.WriteNumber("RetirementsAfter", sample.After.Retirements);
        json.WriteNumber("Retirements", sample.After.Retirements - sample.Before.Retirements);
        json.WriteNumber("PullsBefore", sample.Before.Pulls); json.WriteNumber("PullsAfter", sample.After.Pulls);
        json.WriteNumber("Pulls", sample.After.Pulls - sample.Before.Pulls);
        json.WriteNumber("StartedTicks", sample.Before.Ticks); json.WriteNumber("FinishedTicks", sample.After.Ticks);
        WriteNative(json, "NativeBefore", sample.NativeBefore); WriteNative(json, "NativeAfter", sample.NativeAfter); json.WriteEndObject();
    }
    private static void WriteNative(Utf8JsonWriter json, string name, ulong[]? values)
    {
        if (values is null) { json.WriteNull(name); return; }
        json.WriteStartArray(name); foreach (var value in values) json.WriteNumberValue(value); json.WriteEndArray();
    }
    private sealed class NativeObservation : IDisposable
    {
        private readonly nint _module, _counter;
        internal string Path { get; }
        internal NativeObservation(DatabaseProvider provider)
        {
            var expected = provider == DatabaseProvider.PostgreSql ? "libnw_database_postgresql.so" : "libnw_database_sqlite.so";
            var paths = File.ReadLines("/proc/self/maps").Select(line => line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length == 6 && System.IO.Path.GetFileName(parts[5]) == expected).Select(parts => parts[5]).Distinct().ToArray();
            if (paths.Length != 1) throw new InvalidOperationException("Exactly one loaded provider image is required.");
            Path = paths[0]; _module = NativeLibrary.Load(Path); _counter = NativeLibrary.GetExport(_module, "nw_db_diagnostic_count_v1");
        }
        internal unsafe ulong[] Counts()
        {
            var count = (delegate* unmanaged[Cdecl]<uint, ulong>)_counter; var result = new ulong[10];
            for (uint i = 0; i < result.Length; i++) result[i] = count(i); return result;
        }
        public void Dispose() => NativeLibrary.Free(_module);
    }
}
