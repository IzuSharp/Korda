using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NativeWeave.Data;
using NativeWeave.Database;
using NativeWeave.Database.PostgreSql;
using NativeWeave.Database.Sqlite;

if (args.Length != 4 || !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out int pages) || pages is < 100 or > 10_000)
    throw new ArgumentException("Usage: PageProbe sqlite|postgresql <new-file|loopback-uri> <pages:100..10000> <new-result.json>");
if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("PageProbe requires actual Native AOT execution.");
if (File.Exists(args[3])) throw new IOException("Preserve existing profiling evidence.");
if (args[0] == "sqlite" && (!Path.IsPathFullyQualified(args[1]) || File.Exists(args[1])))
    throw new ArgumentException("Use a new absolute SQLite file path.");
if (args[0] == "postgresql" && (!Uri.TryCreate(args[1], UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme is not ("postgres" or "postgresql")))
    throw new ArgumentException("The profiling PostgreSQL URI must address the isolated loopback service.");

const int rowsPerPage = 100, warmupPages = 20, batchRows = 128, byteLimit = 65536;
const string table = "nw_perf01_page_probe";
const string pageSql = "SELECT id,label,amount FROM nw_perf01_page_probe WHERE id >= @first ORDER BY id LIMIT @take";
PageRow[] expected = Enumerable.Range(1, rowsPerPage).Select(id => new PageRow(id,
    "row-" + id.ToString("D4", CultureInfo.InvariantCulture), id * 17L - 7)).ToArray();
var projection = Projection<PageRow>.Create(
    [ResultField.Create("id", Codecs.Int64), ResultField.Create("label", Codecs.String), ResultField.Create("amount", Codecs.Int64)],
    static row => new(row.Read(0, Codecs.Int64), row.Read(1, Codecs.String), row.Read(2, Codecs.Int64)));
var command = SqlCommand.Create(pageSql, new SqlParameters().Add("first", 1L, Codecs.Int64).Add("take", (long)rowsPerPage, Codecs.Int64));
var builder = new DataSourceBuilder().WithName("perf01-public-page-profile").ConfigureLimits(new DatabaseLimits
{
    MaxConnections = 1, MaxBatchRows = batchRows, MaxBatchBytes = byteLimit,
    MaxBufferedRows = rowsPerPage, MaxBufferedBytes = byteLimit
});
DataSource source = args[0] switch
{
    "sqlite" => builder.UseSqlite(args[1], options =>
    {
        options.JournalMode = SqliteJournalMode.Wal; options.Synchronous = SqliteSynchronousMode.Full;
        options.BusyTimeout = TimeSpan.FromSeconds(5);
    }).Build(),
    "postgresql" => builder.UsePostgreSql(args[1], options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment).Build(),
    _ => throw new ArgumentException("Unknown provider")
};
DataSession? seedSession = null, pageSession = null, cleanupSession = null;
bool tableOwned = false, tableDropped = false, committed = false;
bool? transactionReleased = null;
int warmed = 0, completed = 0;
long allocated = 0, cpuTicks = 0, elapsedTicks = 0;
long[] pageTicks = new long[pages];
int[] collections = [0, 0, 0];
DateTimeOffset? measurementStart = null, measurementEnd = null;
Dictionary<string, string> modules = new(StringComparer.Ordinal);
Exception? failure = null;
using var process = Process.GetCurrentProcess();
try
{
    seedSession = await source.OpenSessionAsync();
    try
    {
        // CREATE deliberately rejects an existing table; cleanup owns only a
        // table created by this invocation, never another workload's table.
        await seedSession.ExecuteAsync(SqlCommand.Create("CREATE TABLE " + table + " (id BIGINT PRIMARY KEY,label TEXT NOT NULL,amount BIGINT NOT NULL)", kind: SqlCommandKind.Mutation));
        tableOwned = true;
        var transaction = await seedSession.BeginTransactionAsync();
        await using (transaction)
        {
            foreach (var row in expected)
            {
                var insert = SqlCommand.Create("INSERT INTO " + table + " (id,label,amount) VALUES (@id,@label,@amount)",
                    new SqlParameters().Add("id", row.Id, Codecs.Int64).Add("label", row.Label, Codecs.String).Add("amount", row.Amount, Codecs.Int64), SqlCommandKind.Mutation);
                if (await seedSession.ExecuteAsync(insert) != 1) throw new InvalidDataException("Seed mutation count differs.");
            }
            await transaction.CommitAsync();
            committed = transaction.Outcome == TransactionOutcome.Committed;
            if (!committed) throw new InvalidOperationException("Seed transaction commit was not confirmed.");
        }
        transactionReleased = transaction.Cleanup.LocalResourcesReleased;
        if (transactionReleased != true) throw new InvalidOperationException("Seed transaction did not retire.");
    }
    finally { await seedSession.DisposeAsync(); }
    if (!seedSession.Cleanup.LocalResourcesReleased) throw new InvalidOperationException("Seed session did not retire.");

    pageSession = await source.OpenSessionAsync();
    try
    {
        // A new logical session reads the committed dataset. The one-connection
        // pool may reuse the physical connection; this is not an external oracle.
        if (await pageSession.ScalarAsync(SqlCommand.Create("SELECT COUNT(*) FROM " + table), Codecs.Int64) != rowsPerPage)
            throw new InvalidDataException("Committed dataset count differs.");
        for (; warmed < warmupPages; warmed++) Verify(await pageSession.QueryAsync(command, projection), expected);
        modules = NativeMappings();
        if (modules.Count == 0) throw new InvalidOperationException("No actual NativeWeave native module was observed.");
        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        long cpuBefore = process.TotalProcessorTime.Ticks;
        long allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        measurementStart = DateTimeOffset.UtcNow;
        long windowStart = Stopwatch.GetTimestamp();
        try
        {
            for (; completed < pages; completed++)
            {
                long start = Stopwatch.GetTimestamp();
                Verify(await pageSession.QueryAsync(command, projection), expected);
                pageTicks[completed] = Stopwatch.GetTimestamp() - start;
            }
        }
        finally
        {
            elapsedTicks = Stopwatch.GetTimestamp() - windowStart;
            measurementEnd = DateTimeOffset.UtcNow;
            allocated = GC.GetTotalAllocatedBytes(precise: true) - allocationBefore;
            cpuTicks = process.TotalProcessorTime.Ticks - cpuBefore;
            collections = [GC.CollectionCount(0) - gc0, GC.CollectionCount(1) - gc1, GC.CollectionCount(2) - gc2];
        }
    }
    finally { await pageSession.DisposeAsync(); }
    if (!pageSession.Cleanup.LocalResourcesReleased) throw new InvalidOperationException("Page session did not retire.");
}
catch (Exception error) { failure = error; }
finally
{
    try
    {
        if (tableOwned)
        {
            cleanupSession = await source.OpenSessionAsync();
            try
            {
                await cleanupSession.ExecuteAsync(SqlCommand.Create("DROP TABLE " + table, kind: SqlCommandKind.Mutation));
                tableDropped = true;
            }
            finally { await cleanupSession.DisposeAsync(); }
        }
    }
    catch (Exception error) { failure ??= error; }
    try { await source.DisposeAsync(); }
    catch (Exception error) { failure ??= error; }
}
bool seedReleased = seedSession?.Cleanup.LocalResourcesReleased == true;
bool pageReleased = pageSession?.Cleanup.LocalResourcesReleased == true;
bool cleanupReleased = cleanupSession?.Cleanup.LocalResourcesReleased == true;
bool sourceReleased = source.Cleanup.LocalResourcesReleased;
if (!sourceReleased || !seedReleased || !pageReleased || !cleanupReleased || !tableDropped || completed != pages)
    failure ??= new InvalidOperationException("Page correctness or owned-resource drain was incomplete.");
var afterDrain = NativeMappings();
if (modules.Count != 0 && (modules.Count != afterDrain.Count || modules.Any(item => !afterDrain.TryGetValue(item.Key, out var hash) || hash != item.Value)))
    failure ??= new InvalidOperationException("Loaded NativeWeave module identity changed during the profile.");
string manifest = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "manifest.json"));
string fixture = string.Join("\n", expected.Select(row => string.Create(CultureInfo.InvariantCulture, $"{row.Id}|{row.Label}|{row.Amount}")));
var result = new PageProfile("Perf-01", "profiling-only", failure is null ? "passed" : "failed", failure?.GetType().FullName,
    args[0], pages, completed, rowsPerPage, warmed, batchRows, byteLimit, pageSql,
    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fixture))),
    measurementStart, measurementEnd, Stopwatch.Frequency, elapsedTicks, pageTicks[..completed],
    allocated, completed == 0 ? null : allocated / (double)completed, cpuTicks / (double)TimeSpan.TicksPerMillisecond, collections,
    committed, transactionReleased, seedReleased, pageReleased, cleanupReleased, sourceReleased, tableDropped,
    RuntimeFeature.IsDynamicCodeSupported, File.ReadLines("/proc/self/status").Single(line => line.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal)).Split(':', 2)[1].Trim(),
    Hash(Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.")), File.Exists(manifest) ? Hash(manifest) : null,
    afterDrain, "Serial public QueryAsync: full ordered 100-row page, owned DTO/list materialization, native stream cleanup and exact row validation. Process allocation/CPU include this whole path and runtime work. No HTTP/JSON, internal test hooks, SQL-only attribution, direct-Rust comparison or long-stage credit.");
await using (var output = new FileStream(args[3], FileMode.CreateNew, FileAccess.Write, FileShare.None))
    await JsonSerializer.SerializeAsync(output, result, PageJson.Default.PageProfile);
if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
Console.WriteLine($"provider={args[0]} pages={completed} rows={completed * rowsPerPage} allocated_bytes={allocated} public_owners_released=true");

static void Verify(IReadOnlyList<PageRow> actual, PageRow[] expected)
{
    if (actual.Count != expected.Length) throw new InvalidDataException("Page row count differs.");
    for (int i = 0; i < expected.Length; i++)
        if (actual[i].Id != expected[i].Id || actual[i].Amount != expected[i].Amount || !string.Equals(actual[i].Label, expected[i].Label, StringComparison.Ordinal))
            throw new InvalidDataException("Ordered page DTO content differs.");
}
static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(input)); }
static Dictionary<string, string> NativeMappings() => File.ReadLines("/proc/self/maps")
    .Select(line => line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries)).Where(fields => fields.Length == 6)
    .Select(fields => fields[5]).Where(path => path.StartsWith('/') && (Path.GetFileName(path).StartsWith("libnw_", StringComparison.Ordinal) || Path.GetFileName(path).StartsWith("libnativeweave_", StringComparison.Ordinal)))
    .Distinct(StringComparer.Ordinal).ToDictionary(path => path, Hash, StringComparer.Ordinal);

internal sealed record PageRow(long Id, string Label, long Amount);
internal sealed record PageProfile(string Task, string Kind, string Status, string? FailureType, string Provider,
    int RequestedPages, int CompletedPages, int RowsPerPage, int CompletedWarmupPages, int MaxBatchRows, int ByteLimit, string Query,
    string FixtureSha256, DateTimeOffset? MeasurementStartUtc, DateTimeOffset? MeasurementEndUtc, long StopwatchFrequency,
    long ElapsedTicks, long[] FullPageTicks, long ProcessManagedAllocatedBytes, double? ManagedBytesPerCompletedPage,
    double ProcessCpuMilliseconds, int[] GcCollections, bool SeedCommitted, bool? SeedTransactionReleased,
    bool SeedSessionReleased, bool PageSessionReleased, bool CleanupSessionReleased, bool SourceReleased, bool TableDropped,
    bool DynamicCodeSupported, string CpuAffinity, string ExecutableSha256, string? BuildManifestSha256,
    Dictionary<string, string> LoadedNativeModules, string Scope);
[JsonSerializable(typeof(PageProfile))]
internal partial class PageJson : JsonSerializerContext;
