using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Korda.Data;
using Korda.Database;
using Korda.Query;
#if POSTGRES
using Korda.Database.PostgreSql;
#else
using Korda.Database.Sqlite;
#endif

if (args.Length != 6) throw new ArgumentException("Usage: AllocationProbe <connection> <result.json> <warmup> <iterations> <package.nupkg> <source-sha>");
if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("A real Native AOT executable is required.");
int warmup = int.Parse(args[2]), iterations = int.Parse(args[3]);
if (warmup < 50 || iterations < 1) throw new ArgumentOutOfRangeException(nameof(args), "Warmup must be at least 50; iterations must be positive.");
var builder = new DataSourceBuilder().WithName("DB-Perf-01-allocation-preflight")
    .ConfigureLimits(new DatabaseLimits { MaxConnections = 1, MaxWaiters = 4, MaxBatchRows = 128,
        MaxBatchBytes = 65_536, MaxFieldBytes = 1024, MaxBufferedRows = 512, MaxQueryNodes = 512 });
#if POSTGRES
builder.UsePostgreSql(args[0], options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment);
#else
builder.UseSqlite(args[0]);
#endif
var source = builder.Build();
var provider = source.Provider.ToString();
var started = DateTimeOffset.UtcNow;
var session = await source.OpenSessionAsync();
using var native = new NativeObservation(source.Provider);
await session.ExecuteAsync(SqlCommand.Create("DROP TABLE IF EXISTS allocation_items", kind: SqlCommandKind.Mutation));
await session.ExecuteAsync(SqlCommand.Create("CREATE TABLE allocation_items (\"Id\" BIGINT PRIMARY KEY)", kind: SqlCommandKind.Mutation));
var insert = new StringBuilder("INSERT INTO allocation_items (\"Id\") VALUES ");
for (int i = 1; i <= 256; i++) { if (i != 1) insert.Append(','); insert.Append('(').Append(i).Append(')'); }
if (await session.ExecuteAsync(SqlCommand.Create(insert.ToString(), kind: SqlCommandKind.Mutation)) != 256)
    throw new InvalidOperationException("Seed row count differs.");
PreparedQuery<long> prepared;
string sql;
using (var query = session.From(AllocationItems.Table))
{
    query.OrderBy(AllocationItems.Id);
    var selection = query.Select(query.Column(AllocationItems.Id));
    sql = selection.ToSql();
    prepared = selection.Prepare();
}

// Each measurement method suspends once BEFORE its counter boundary. Its outer
// async state is then already materialized; no per-query async helper is used.
var dynamicFull = await Measurements.Full(session, prepared, false, warmup, iterations);
var preparedFull = await Measurements.Full(session, prepared, true, warmup, iterations);
var slices = await Measurements.Slices(session, prepared, warmup, iterations);
var emptySync = Measurements.EmptySync(warmup, iterations);
var emptyAsync = await Measurements.EmptyAsync(warmup, iterations);
var idle = Measurements.Idle();
prepared.Dispose();
await session.DisposeAsync();
bool sessionReleased = session.Cleanup.LocalResourcesReleased;
await source.DisposeAsync();
bool sourceReleased = source.Cleanup.LocalResourcesReleased;
var remaining = native.Counters();
if (!sessionReleased || !sourceReleased || remaining.Take(6).Any(value => value != 0))
    throw new InvalidOperationException("Native owners did not retire after all measurements.");
var ended = DateTimeOffset.UtcNow;
// File IO, hashes, JSON, diagnostic counters and console output stay outside all
// allocation intervals. Native identity comes from the image actually mapped.
using (var output = File.Create(args[1]))
using (var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
{
    json.WriteStartObject();
    json.WriteString("schema", "DB-Perf-01.allocation-preflight.v1");
    json.WriteString("status", "measured-baseline; exact-framework-attribution-unproved");
    json.WriteString("provider", provider); json.WriteBoolean("native_aot", true);
    json.WriteString("source_commit", args[5]); json.WriteString("started_utc", started); json.WriteString("ended_utc", ended);
    json.WriteNumber("process_id", Environment.ProcessId); json.WriteString("runtime", RuntimeInformation.FrameworkDescription);
    json.WriteString("architecture", RuntimeInformation.ProcessArchitecture.ToString());
    json.WriteString("package_path", Path.GetFullPath(args[4])); json.WriteString("package_sha256", Hash(args[4]));
    json.WriteString("executable_path", Environment.ProcessPath); json.WriteString("executable_sha256", Hash(Environment.ProcessPath!));
    json.WriteString("loaded_native_path", native.Path); json.WriteString("loaded_native_sha256", Hash(native.Path));
    json.WriteString("sql", sql); json.WriteNumber("dataset_rows", 256); json.WriteNumber("batch_rows", 128);
    json.WriteNumber("expected_query_checksum", 32896); json.WriteNumber("warmup_per_scenario", warmup);
    json.WriteNumber("measured_iterations", iterations);
    json.WriteString("allocation_scope", "GC.GetTotalAllocatedBytes(true), all managed threads; raw starts/ends retained; no control subtraction");
    json.WriteString("framework_attribution", "unproved: process totals include runtime and harness continuation infrastructure; source allocation sites are separately audited");
    json.WriteString("business_output", "Int64 values and numeric checksums only; no DTO/string/list/result-array is produced inside measurement intervals");
    json.WriteString("timing_scope", "diagnostic elapsed ticks only; other compilation processes may run on this host; no throughput comparison");
    json.WriteString("counter_capture", "GC counts read before precise allocated-byte count; Stopwatch timestamp and managed thread ID are value observations");
    json.WriteStartArray("complete_queries"); WriteFull(json, dynamicFull); WriteFull(json, preparedFull); json.WriteEndArray();
    json.WriteStartArray("within_first_batch"); foreach (var item in slices) WriteInterval(json, item.WithinBatch); json.WriteEndArray();
    json.WriteStartArray("supplemental_fetch_including_first_row"); foreach (var item in slices) WriteInterval(json, item.Fetch); json.WriteEndArray();
    json.WriteStartArray("empty_sync_control"); foreach (var item in emptySync) WriteInterval(json, item); json.WriteEndArray();
    json.WriteStartObject("empty_async_control");
    json.WriteString("scope", "Task.Yield control, not equivalent to a native operation; never subtracted");
    WriteIntervalProperties(json, emptyAsync); json.WriteEndObject();
    json.WriteStartObject("source_open_idle_control"); WriteIntervalProperties(json, idle); json.WriteEndObject();
    json.WriteBoolean("session_local_resources_released", sessionReleased); json.WriteBoolean("source_local_resources_released", sourceReleased);
    json.WriteStartArray("native_counters_after_source_close"); foreach (var value in remaining) json.WriteNumberValue(value); json.WriteEndArray();
    json.WriteString("native_counter_order", "sources,sessions,operations,cursors,buffers,handles,cumulative_sql_dispatches,cumulative_protocol_submissions; network_round_trips_unmeasured");
    json.WriteEndObject();
}
Console.WriteLine($"ALLOCATION_PREFLIGHT_PASS provider={provider} warmup={warmup} iterations={iterations} result={args[1]}");

static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
static void WriteCounter(Utf8JsonWriter json, string name, Counter value)
{
    json.WriteStartObject(name); json.WriteNumber("allocated_bytes", value.Allocated);
    json.WriteNumber("gc0", value.Gc0); json.WriteNumber("gc1", value.Gc1); json.WriteNumber("gc2", value.Gc2);
    json.WriteNumber("timestamp", value.Timestamp); json.WriteNumber("managed_thread_id", value.Thread); json.WriteEndObject();
}
static void WriteInterval(Utf8JsonWriter json, Interval value) { json.WriteStartObject(); WriteIntervalProperties(json, value); json.WriteEndObject(); }
static void WriteIntervalProperties(Utf8JsonWriter json, Interval value)
{
    WriteCounter(json, "start", value.Start); WriteCounter(json, "end", value.End);
    json.WriteNumber("allocated_bytes_total", value.End.Allocated - value.Start.Allocated);
    json.WriteNumber("operations", value.Operations); json.WriteNumber("rows", value.Rows); json.WriteNumber("checksum", value.Checksum);
    json.WriteNumber("incomplete_move_observations", value.Incomplete); json.WriteNumber("await_thread_changes", value.ThreadChanges);
    json.WriteNumber("gc0_delta", value.End.Gc0 - value.Start.Gc0); json.WriteNumber("gc1_delta", value.End.Gc1 - value.Start.Gc1); json.WriteNumber("gc2_delta", value.End.Gc2 - value.Start.Gc2);
    if (value.Operations > 0) json.WriteNumber("total_bytes_per_operation", (double)(value.End.Allocated - value.Start.Allocated) / value.Operations);
    if (value.Rows > 0) json.WriteNumber("total_bytes_per_row", (double)(value.End.Allocated - value.Start.Allocated) / value.Rows);
    json.WriteNull("framework_allocated_bytes");
    json.WriteString("framework_status", "unproved; no estimated subtraction");
}
static void WriteFull(Utf8JsonWriter json, FullResult value)
{
    json.WriteStartObject(); json.WriteString("name", value.Name);
    json.WriteString("boundary", "construction or prepared stream creation through all 256 rows, final false, enumerator disposal and dynamic query disposal");
    WriteIntervalProperties(json, value.Interval); json.WriteEndObject();
}

[DbTable("allocation_items", DescriptorName = "AllocationItems")]
internal sealed record AllocationItem([property: DbKey] long Id);
internal readonly record struct Counter(long Allocated, int Gc0, int Gc1, int Gc2, long Timestamp, int Thread)
{
    internal static Counter Read()
    {
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        return new(GC.GetTotalAllocatedBytes(true), g0, g1, g2, Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId);
    }
}
internal readonly record struct Interval(Counter Start, Counter End, long Operations, long Rows, long Checksum, long Incomplete, long ThreadChanges);
internal readonly record struct FullResult(string Name, Interval Interval);
internal readonly record struct SliceResult(Interval WithinBatch, Interval Fetch);

internal static class Measurements
{
    internal static async Task<FullResult> Full(DataSession session, PreparedQuery<long> prepared, bool reuse, int warmup, int iterations)
    {
        await Task.Yield();
        Counter start = default; long checksum = 0, rows = 0, incomplete = 0, threadChanges = 0;
        for (int i = -warmup; i < iterations; i++)
        {
            if (i == 0) { checksum = rows = incomplete = threadChanges = 0; start = Counter.Read(); }
            using var query = reuse ? null : session.From(AllocationItems.Table);
            var stream = reuse ? prepared.AsAsyncEnumerable(session)
                : query!.OrderBy(AllocationItems.Id).Select(query.Column(AllocationItems.Id)).AsAsyncEnumerable();
            await using var reader = stream.GetAsyncEnumerator();
            int count = 0; long sum = 0;
            while (true)
            {
                int beforeThread = Environment.CurrentManagedThreadId;
                var move = reader.MoveNextAsync(); if (!move.IsCompletedSuccessfully) incomplete++;
                bool next = await move.ConfigureAwait(false);
                if (beforeThread != Environment.CurrentManagedThreadId) threadChanges++;
                if (!next) break;
                count++; if (reader.Current != count) throw new InvalidOperationException("Row order/value differs.");
                sum += reader.Current;
            }
            if (count != 256 || sum != 32896) throw new InvalidOperationException("Full query result differs.");
            rows += count; checksum += sum;
        }
        var end = Counter.Read();
        if (rows != iterations * 256L || checksum != iterations * 32896L) throw new InvalidOperationException("Aggregate result differs.");
        return new(reuse ? "prepared_parameterless_complete" : "dynamic_complete", new(start, end, iterations, rows, checksum, incomplete, threadChanges));
    }

    internal static async Task<SliceResult[]> Slices(DataSession session, PreparedQuery<long> prepared, int warmup, int iterations)
    {
        var samples = new SliceResult[iterations];
        await Task.Yield();
        for (int i = -warmup; i < iterations; i++)
        {
            await using var reader = prepared.AsAsyncEnumerable(session).GetAsyncEnumerator();
            if (!await reader.MoveNextAsync().ConfigureAwait(false) || reader.Current != 1) throw new InvalidOperationException("Missing first row.");
            var start = Counter.Read();
            long sum = SyncAdvance(reader, 127, 2);
            var end = Counter.Read();
            var first = new Interval(start, end, 127, 127, sum, 0, 0);
            var fetchStart = Counter.Read(); int beforeThread = Environment.CurrentManagedThreadId;
            var fetching = reader.MoveNextAsync(); bool incomplete = !fetching.IsCompletedSuccessfully;
            bool fetched = await fetching.ConfigureAwait(false); long firstSecondBatch = reader.Current;
            int threadChanges = beforeThread == Environment.CurrentManagedThreadId ? 0 : 1;
            var fetchEnd = Counter.Read();
            if (!fetched || firstSecondBatch != 129) throw new InvalidOperationException("Supplemental fetch row differs.");
            sum += firstSecondBatch + SyncAdvance(reader, 127, 130) + 1;
            if (await reader.MoveNextAsync().ConfigureAwait(false) || sum != 32896) throw new InvalidOperationException("Slice query result/termination differs.");
            // The final false and Dispose are outside the narrow intervals but
            // mandatory here; Full() measures their complete-query cost.
            if (i >= 0) samples[i] = new(first, new(fetchStart, fetchEnd, 1, 1, firstSecondBatch, incomplete ? 1 : 0, threadChanges));
        }
        return samples;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long SyncAdvance(IAsyncEnumerator<long> reader, int rows, long expected)
    {
        long sum = 0;
        for (int i = 0; i < rows; i++)
        {
            var move = reader.MoveNextAsync();
            if (!move.IsCompletedSuccessfully) throw new InvalidOperationException("The claimed within-batch path suspended.");
            if (!move.Result || reader.Current != expected + i) throw new InvalidOperationException("Within-batch row differs.");
            sum += reader.Current;
        }
        return sum;
    }

    internal static Interval[] EmptySync(int warmup, int iterations)
    {
        var samples = new Interval[iterations]; var reader = new EmptyEnumerator();
        for (int i = -warmup; i < iterations; i++)
        {
            reader.Reset(); var start = Counter.Read(); long sum = SyncAdvance(reader, 127, 2); var end = Counter.Read();
            if (i >= 0) samples[i] = new(start, end, 127, 127, sum, 0, 0);
        }
        return samples;
    }
    internal static async Task<Interval> EmptyAsync(int warmup, int iterations)
    {
        await Task.Yield(); Counter start = default; long threadChanges = 0;
        for (int i = -warmup; i < iterations; i++)
        {
            if (i == 0) { start = Counter.Read(); threadChanges = 0; }
            int thread = Environment.CurrentManagedThreadId; await Task.Yield();
            if (thread != Environment.CurrentManagedThreadId) threadChanges++;
        }
        var end = Counter.Read(); return new(start, end, iterations, 0, 0, iterations, threadChanges);
    }
    internal static Interval Idle()
    {
        var start = Counter.Read(); Thread.Sleep(100); var end = Counter.Read(); return new(start, end, 0, 0, 0, 0, 0);
    }
    private sealed class EmptyEnumerator : IAsyncEnumerator<long>
    {
        private long _value;
        internal void Reset() => _value = 1;
        public long Current => _value;
        public ValueTask<bool> MoveNextAsync() { _value++; return new(true); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class NativeObservation : IDisposable
{
    private readonly nint _module, _counter;
    internal string Path { get; }
    internal NativeObservation(DatabaseProvider provider)
    {
        string expected = provider == DatabaseProvider.PostgreSql ? "libnw_database_postgresql.so" : "libnw_database_sqlite.so";
        var paths = File.ReadLines("/proc/self/maps").Select(line => line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 6 && System.IO.Path.GetFileName(parts[5]) == expected).Select(parts => parts[5]).Distinct().ToArray();
        if (paths.Length != 1) throw new InvalidOperationException("Expected exactly one actually loaded provider image.");
        Path = paths[0]; _module = NativeLibrary.Load(Path); _counter = NativeLibrary.GetExport(_module, "nw_db_diagnostic_count_v1");
    }
    internal unsafe ulong[] Counters()
    {
        var count = (delegate* unmanaged[Cdecl]<uint, ulong>)_counter; var result = new ulong[8];
        for (uint i = 0; i < result.Length; i++) result[i] = count(i); return result;
    }
    public void Dispose() => NativeLibrary.Free(_module);
}
