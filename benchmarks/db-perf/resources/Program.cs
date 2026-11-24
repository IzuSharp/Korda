using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Korda.Data;
using Korda.Database;
using Korda.Query;
#if POSTGRES
using Korda.Database.PostgreSql;
#else
using Korda.Database.Sqlite;
#endif

if (args.Length != 5) throw new ArgumentException("Usage: ResourceProbe <connection> <result.json> <package.nupkg> <source-commit> <concurrency>");
if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("Use the actual Native AOT executable.");
int concurrency = int.Parse(args[4]);
if (concurrency is < 8 or > 32) throw new ArgumentOutOfRangeException(nameof(concurrency));
var report = new ResourceReport
{
    SourceCommit = args[3], PackageSha256 = Hash(args[2]), ExecutableSha256 = Hash(Environment.ProcessPath!),
    ProcessId = Environment.ProcessId, NativeAot = !RuntimeFeature.IsDynamicCodeSupported,
    Runtime = RuntimeInformation.FrameworkDescription, ProcessorCount = Environment.ProcessorCount,
    ProcessorCountOverride = Environment.GetEnvironmentVariable("DOTNET_PROCESSOR_COUNT"),
    Concurrency = concurrency, StartedUtc = DateTimeOffset.UtcNow
};
DataSource? source = null;
var sessions = new DataSession?[concurrency];
PreparedQuery<long>? prepared = null;
NativeCounts? native = null;
Exception? failure = null;
var cleanupFailures = new Exception?[concurrency + 2];
int cleanupFailureCount = 0;
try
{
    // Probe scaffolding, hashes, arrays, snapshots and its own async suspension
    // are outside operation intervals. No empty-control estimate is subtracted.
    _ = Counts.Read();
    report.Memory.Add(Memory.Read("before-source"));
    var builder = new DataSourceBuilder().WithName("DB-Perf-01-resource-inventory")
        .ConfigureLimits(new DatabaseLimits { MaxConnections = concurrency, MaxWaiters = 1,
            MaxBatchRows = 32, MaxBatchBytes = 65536, MaxFieldBytes = 1024,
            MaxBufferedRows = 512, MaxQueryNodes = 512 });
#if POSTGRES
    builder.UsePostgreSql(args[0], options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment);
#else
    builder.UseSqlite(args[0]);
#endif
    var begin = Counts.Read();
    source = builder.Build();
    var end = Counts.Read();
    report.Windows.Add(new("cold-source-build", begin, end, 1, 1, 0, 0, 0, 0, 0, 0));
    report.Provider = source.Provider.ToString();
    native = new(source.Provider);
    report.NativePath = native.Path; report.NativeSha256 = Hash(native.Path);
    await Task.Yield();
    begin = Counts.Read();
    for (int i = 0; i < concurrency; i++) sessions[i] = await source.OpenSessionAsync();
    end = Counts.Read();
    report.Windows.Add(new("cold-open-sessions", begin, end, concurrency, concurrency, 0, 0, 0, 0, 0, 0));
    var session = sessions[0]!;
    await session.ExecuteAsync(SqlCommand.Create("DROP TABLE IF EXISTS resource_probe_items", kind: SqlCommandKind.Mutation));
    await session.ExecuteAsync(SqlCommand.Create("CREATE TABLE resource_probe_items (\"Id\" BIGINT PRIMARY KEY)", kind: SqlCommandKind.Mutation));
    await session.ExecuteAsync(SqlCommand.Create("INSERT INTO resource_probe_items (\"Id\") VALUES (1),(2),(3),(4),(5),(6),(7),(8)", kind: SqlCommandKind.Mutation));
    begin = Counts.Read();
    using (var query = session.From(ResourceItems.Table))
    {
        query.OrderBy(ResourceItems.Id);
        prepared = query.Select(query.Column(ResourceItems.Id)).Prepare();
    }
    end = Counts.Read();
    report.Windows.Add(new("cold-fluent-prepare-and-query-dispose", begin, end, 1, 1, 0, 0, 0, 0, 0, 0));
    const int steps = 200000;
    const string slowSql = "WITH RECURSIVE walk(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM walk WHERE n < @steps) SELECT CAST(SUM(n) AS BIGINT) AS value FROM walk";
    var command = SqlCommand.Create(slowSql, new SqlParameters().Add("steps", steps, Codecs.Int32));
    var projection = Projection<long>.Create([ResultField.Create("value", Codecs.Int64)], static row => row.Read(0, Codecs.Int64));
    report.SuspendedQuerySql = slowSql;
    report.SuspendedQuerySteps = steps;
    report.Memory.Add(Memory.Read("sessions-open-before-first-stream"));
    report.WarmOwners = SettleOwners("initial-open-session-baseline", native.Read(), expected: null);
    // Every first Move is issued on the same thread before any ValueTask is
    // consumed. The exact pending count is evidence; suspension is never assumed.
    await Burst("cold-raw-suspended-burst", concurrency, raw: true);
    report.Memory.Add(Memory.Read("after-cold-burst"));
    for (int repeat = 0; repeat < 3; repeat++)
    {
        await Burst("warm-raw-suspended-burst-" + repeat, concurrency, raw: true);
        await Burst("prepared-fluent-c1-" + repeat, 1, raw: false);
        await Burst("prepared-fluent-concurrent-" + repeat, concurrency, raw: false);
    }
    report.Memory.Add(Memory.Read("after-warm-concurrent-bursts"));
    // A separately labelled exhaustion/cancellation interval. The first queued
    // open can wait; later admission must fail or honor the caller cancellation.
    using (var cancellation = new CancellationTokenSource())
    {
        var opens = new Task<DataSession>?[3];
        var exceptions = new Exception?[3];
        await Task.Yield();
        begin = Counts.Read();
        for (int i = 0; i < opens.Length; i++)
        {
            try { opens[i] = source.OpenSessionAsync(cancellation.Token); }
            catch (Exception error) { exceptions[i] = error; }
        }
        // Give already-started native opens a finite opportunity to encounter
        // the fully occupied pool. This wait is inside the labelled interval.
        Thread.Sleep(20);
        int pendingBeforeCancel = 0;
        for (int i = 0; i < opens.Length; i++) if (opens[i] is { IsCompleted: false }) pendingBeforeCancel++;
        cancellation.Cancel();
        int rejected = 0, unexpected = 0;
        for (int i = 0; i < opens.Length; i++)
        {
            if (opens[i] is null) { rejected++; continue; }
            try { var extra = await opens[i]!; unexpected++; await extra.DisposeAsync(); }
            catch (Exception error) { rejected++; exceptions[i] = error; }
        }
        end = Counts.Read();
        report.Windows.Add(new("pool-exhaustion-and-cancel", begin, end, 3, unexpected, rejected, 0, 0, 0, 0, 0));
        report.PendingOpensBeforeCancellation = pendingBeforeCancel;
        foreach (var error in exceptions)
        {
            report.ExhaustionExceptions.Add(error?.GetType().FullName + ":" + (error as DatabaseException)?.Kind);
            if (error is not OperationCanceledException and not DatabaseException { Kind: DatabaseErrorKind.ResourceLimit })
                throw new InvalidOperationException("Unexpected failure during the exhaustion/cancellation diagnostic.", error);
            if (error is OperationCanceledException canceled)
            {
                if (canceled.CancellationToken != cancellation.Token || !canceled.CancellationToken.IsCancellationRequested)
                    throw new InvalidOperationException("Cancellation token identity was lost.", canceled);
                report.MatchingCanceledOpenResults++;
            }
            else report.ResourceLimitOpenResults++;
        }
        if (unexpected != 0 || rejected != 3) throw new InvalidOperationException("Pool exhaustion admitted an extra physical session.");
        report.PendingCancellationObserved = pendingBeforeCancel > 0 && report.MatchingCanceledOpenResults > 0;
    }
    report.OwnersAfterExhaustion = native.Read();
    SettleOwners("after-exhaustion", report.OwnersAfterExhaustion, report.WarmOwners);
    report.Memory.Add(Memory.Read("after-exhaustion"));
    report.Memory.Add(Memory.Read("idle-start"));
    using (var idleProcess = Process.GetCurrentProcess())
    {
        double user = idleProcess.UserProcessorTime.TotalSeconds;
        double system = idleProcess.PrivilegedProcessorTime.TotalSeconds;
        long timestamp = Stopwatch.GetTimestamp();
        Thread.Sleep(1000);
        idleProcess.Refresh();
        report.IdleUserSeconds = idleProcess.UserProcessorTime.TotalSeconds - user;
        report.IdleSystemSeconds = idleProcess.PrivilegedProcessorTime.TotalSeconds - system;
        report.IdleElapsedSeconds = (double)(Stopwatch.GetTimestamp() - timestamp) / Stopwatch.Frequency;
    }
    // This forced-GC retention observation follows the idle CPU boundary.
    report.Memory.Add(Memory.Read("idle-end"));

    async Task Burst(string name, int width, bool raw)
    {
        var readers = new IAsyncEnumerator<long>?[width];
        var moves = new ValueTask<bool>[width];
        var pendingAtReturn = new bool[width];
        var pendingBeforeConsumption = new bool[width];
        await Task.Yield();
        long rows = 0, sum = 0;
        int successes = 0, errors = 0, pending = 0, started = 0;
        bool singleIssuingThread = false;
        Exception? firstError = null;
        int issuingThread = Environment.CurrentManagedThreadId;
        var start = Counts.Read();
        try
        {
            for (int i = 0; i < width; i++)
            {
                started++;
                readers[i] = (raw ? sessions[i]!.StreamAsync(command, projection)
                    : prepared!.AsAsyncEnumerable(sessions[i]!)).GetAsyncEnumerator();
                moves[i] = readers[i]!.MoveNextAsync();
                pendingAtReturn[i] = !moves[i].IsCompleted;
            }
            int sameThread = Environment.CurrentManagedThreadId;
            if (sameThread != issuingThread) throw new InvalidOperationException("Synchronous launch changed thread.");
            singleIssuingThread = true;
            for (int i = 0; i < width; i++)
            {
                pendingBeforeConsumption[i] = !moves[i].IsCompleted;
                if (pendingBeforeConsumption[i]) pending++;
            }
            for (int i = 0; i < width; i++)
            {
                bool resultValidated = false, disposed = false;
                try
                {
                    long localSum = 0, localRows = 0;
                    bool more = await moves[i];
                    while (more)
                    {
                        long value = readers[i]!.Current;
                        localRows++; localSum += value; rows++; sum += value;
                        more = await readers[i]!.MoveNextAsync();
                    }
                    if (localRows != (raw ? 1 : 8) || localSum != (raw ? (long)steps * (steps + 1) / 2 : 36))
                        throw new InvalidOperationException("Resource probe result/row oracle failed.");
                    resultValidated = true;
                }
                catch (Exception error) { firstError ??= error; }
                finally
                {
                    if (readers[i] is not null)
                        try { await readers[i]!.DisposeAsync(); disposed = true; }
                        catch (Exception error) { firstError ??= error; }
                    readers[i] = null; moves[i] = default;
                }
                if (resultValidated && disposed) successes++; else errors++;
            }
        }
        catch (Exception error) { firstError ??= error; }
        finally
        {
            for (int i = 0; i < width; i++) if (readers[i] is not null)
            {
                try { await readers[i]!.DisposeAsync(); }
                catch (Exception error) { firstError ??= error; }
                readers[i] = null; moves[i] = default;
            }
            var finish = Counts.Read();
            errors = started - successes;
            int incompleteReturns = 0;
            foreach (bool value in pendingAtReturn) if (value) incompleteReturns++;
            report.Windows.Add(new(name, start, finish, started, successes, errors, rows, sum, pending, issuingThread, Environment.CurrentManagedThreadId)
            {
                PlannedOperations = width, PendingAtReturn = pendingAtReturn,
                PendingBeforeConsumption = pendingBeforeConsumption,
                SingleIssuingThread = singleIssuingThread,
                PendingAtReturnCount = incompleteReturns,
                RuntimePoolCacheCapacityForIssuingThread = report.ProcessorCount + 1,
                UnconsumedSuspensionsExceedCacheCapacity = singleIssuingThread && incompleteReturns > report.ProcessorCount + 1,
                BoundaryError = firstError?.ToString()
            });
        }
        if (firstError is not null) throw new InvalidOperationException("A business oracle failed.", firstError);
        var owners = native!.Read();
        report.NativeObservations.Add(new(name, owners));
        SettleOwners(name, owners, report.WarmOwners);
    }

}
catch (Exception error) { failure = error; }
finally
{
    try
    {
        int attempted = prepared is null ? 0 : 1, succeeded = 0;
        var start = Counts.Read();
        try { prepared?.Dispose(); succeeded = attempted; }
        catch (Exception error) { cleanupFailures[cleanupFailureCount++] = error; }
        prepared = null;
        var end = Counts.Read();
        report.Windows.Add(new("dispose-prepared", start, end, attempted, succeeded, attempted - succeeded, 0, 0, 0, 0, 0));
        await Task.Yield();
        attempted = succeeded = 0;
        start = Counts.Read();
        for (int i = 0; i < sessions.Length; i++) if (sessions[i] is not null)
        {
            attempted++;
            try { await sessions[i]!.DisposeAsync(); succeeded++; }
            catch (Exception error) { cleanupFailures[cleanupFailureCount++] = error; }
            sessions[i] = null;
        }
        end = Counts.Read();
        report.Windows.Add(new("return-all-sessions", start, end, attempted, succeeded, attempted - succeeded, 0, 0, 0, 0, 0));
        attempted = source is null ? 0 : 1; succeeded = 0;
        start = Counts.Read();
        try
        {
            if (source is not null)
            { await source.DisposeAsync(); report.SourceReleased = source.Cleanup.LocalResourcesReleased; succeeded = report.SourceReleased ? 1 : 0; }
        }
        catch (Exception error) { cleanupFailures[cleanupFailureCount++] = error; }
        end = Counts.Read();
        report.Windows.Add(new("close-source-including-public-cleanup-observation", start, end, attempted, succeeded, attempted - succeeded, 0, 0, 0, 0, 0));
        source = null;
        for (int i = 0; i < cleanupFailureCount; i++) report.CleanupErrors.Add(cleanupFailures[i]!.ToString());
        if (cleanupFailureCount != 0) failure ??= cleanupFailures[0];
        if (native is not null)
        {
            report.FinalOwners = native.Read();
            try { report.FinalOwners = SettleOwners("final-source-close", report.FinalOwners, new ulong[8]); }
            catch (Exception error) { failure ??= error; report.CleanupErrors.Add(error.ToString()); }
            finally { native.Dispose(); native = null; }
        }
        report.Memory.Add(Memory.Read("after-source-close"));
        Thread.Sleep(1000);
        report.Memory.Add(Memory.Read("after-close-idle"));
        if (!report.SourceReleased || report.FinalOwners.Length != 8 || report.FinalOwners.Take(6).Any(value => value != 0))
            failure ??= new InvalidOperationException("Final source/session/operation/cursor/buffer/handle did not drain.");
    }
    catch (Exception error) { failure ??= error; report.CleanupErrors.Add(error.ToString()); }
    finally
    {
        report.Error = failure?.ToString(); report.Passed = failure is null; report.FinishedUtc = DateTimeOffset.UtcNow;
        File.WriteAllText(args[1], JsonSerializer.Serialize(report, ProbeJson.Default.ResourceReport));
    }
}
if (failure is not null) throw new InvalidOperationException("Resource inventory failed; raw windows retained.", failure);
Console.WriteLine("RESOURCE_INVENTORY_PASS " + args[1]);

ulong[] SettleOwners(string phase, ulong[] immediate, ulong[]? expected)
{
    // This explicit extra interval retains both immediate and settled values.
    // The initial baseline requires the known source/session counts, no active
    // operation/cursor/buffer, and two equal observations of all six owners.
    // Later phases must return to that baseline or to the final all-zero target.
    var settled = new ulong[8];
    var previous = new ulong[8];
    int polls = 0;
    var start = Counts.Read();
    long deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
    bool matched;
    do
    {
        native!.ReadInto(settled); polls++;
        matched = expected is null
            ? polls >= 2 && settled[0] == 1 && settled[1] == (ulong)concurrency
                && settled[2] == 0 && settled[3] == 0 && settled[4] == 0
                && settled[5] >= (ulong)concurrency + 1
                && settled.AsSpan(0, 6).SequenceEqual(previous.AsSpan(0, 6))
            : settled.AsSpan(0, 6).SequenceEqual(expected.AsSpan(0, 6));
        if (matched || Stopwatch.GetTimestamp() >= deadline) break;
        settled.CopyTo(previous, 0);
        Thread.Sleep(1);
    } while (true);
    var finish = Counts.Read();
    report.OwnerSettlements.Add(new(phase, immediate, settled, expected, start, finish, polls, matched));
    if (!matched) throw new TimeoutException("Native owners did not settle within the recorded five-second bound.");
    return settled;
}

static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }

[DbTable("resource_probe_items", DescriptorName = "ResourceItems")]
internal sealed record ResourceItem([property: DbKey] long Id);
internal readonly record struct Counts(long AllocatedBytes, int Gen0, int Gen1, int Gen2, long Timestamp)
{
    internal static Counts Read()
    {
        int a = GC.CollectionCount(0), b = GC.CollectionCount(1), c = GC.CollectionCount(2);
        return new(GC.GetTotalAllocatedBytes(true), a, b, c, Stopwatch.GetTimestamp());
    }
}
internal sealed record Window(string Name, Counts Start, Counts End, int Operations, int Succeeded, int Failed,
    long Rows, long Checksum, int PendingFirstMovesBeforeAnyConsumption, int IssuingThread, int EndingThread)
{
    public long RawAllocatedBytes => End.AllocatedBytes - Start.AllocatedBytes;
    public int PlannedOperations { get; init; } = Operations;
    public bool[] PendingAtReturn { get; init; } = [];
    public bool[] PendingBeforeConsumption { get; init; } = [];
    public bool SingleIssuingThread { get; init; }
    public int PendingAtReturnCount { get; init; }
    public int RuntimePoolCacheCapacityForIssuingThread { get; init; }
    public bool UnconsumedSuspensionsExceedCacheCapacity { get; init; }
    public string? BoundaryError { get; init; }
    public long? FrameworkAllocatedBytes => null;
    public string FrameworkStatus => "Unproved exact split; all-process raw bytes retained without subtraction.";
}
internal sealed record OwnerObservation(string Phase, ulong[] Counters);
internal sealed record OwnerSettlement(string Phase, ulong[] Immediate, ulong[] Settled, ulong[]? Expected, Counts Start, Counts End, int Polls, bool Matched);
internal sealed record Memory(string Phase, long Timestamp, long ManagedLiveBytesAfterForcedGc, int ThreadPoolThreads,
    long RssBytes, long PeakRssBytes, long? PssBytes, double UserSeconds, double SystemSeconds,
    Counts BeforeForcedGc, Counts AfterForcedGc)
{
    internal static Memory Read(string phase)
    {
        var before = Counts.Read();
        long managed = GC.GetTotalMemory(forceFullCollection: true);
        var after = Counts.Read();
        using var process = Process.GetCurrentProcess();
        long? pss = null;
        if (File.Exists("/proc/self/smaps_rollup"))
            foreach (var line in File.ReadLines("/proc/self/smaps_rollup"))
                if (line.StartsWith("Pss:", StringComparison.Ordinal))
                    pss = long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) * 1024;
        return new(phase, Stopwatch.GetTimestamp(), managed, ThreadPool.ThreadCount, process.WorkingSet64,
            process.PeakWorkingSet64, pss, process.UserProcessorTime.TotalSeconds, process.PrivilegedProcessorTime.TotalSeconds, before, after);
    }
}
internal sealed class ResourceReport
{
    public string Schema { get; set; } = "DB-Perf-01.public-resource-inventory.v1";
    public string Scope { get; set; } = "Separate cold, bounded concurrent, exhaustion and retention diagnostic; no throughput/long-segment credit. Raw process bytes include runtime/continuations. No control subtraction or whole-framework zero claim. Forced GC occurs only in explicitly labelled memory observations outside allocation intervals.";
    public string BusinessOutput { get; set; } = "Int64 rows/checksums; no required DTO/string/list/result-array inside query intervals. Exception identities are formatted after the separately labelled exhaustion interval.";
    public string SetupBoundary { get; set; } = "Builder configuration, fixed command/projection/parameter construction, fixture DDL and probe arrays are outside their named operation windows. These are narrower diagnostic boundaries, not a complete lifecycle allocation total.";
    public string RetentionScope { get; set; } = "Whole process after forced GC, including probe report collections, possible live async locals and runtime caches. RSS/PSS samples here follow GC; observer time series and process peak RSS are retained separately. Heap differences are not attributed exclusively to a framework pool.";
    public string SuspensionScope { get; set; } = "Same-thread first Move calls are not consumed until all are issued. Actual incomplete returns are counted. Exceeding ProcessorCount+1 is a cache-capacity pressure observation; it proves pooled Move box misses only for a candidate independently shown to use the exact .NET 11 pooling builder. It does not assert that the before implementation has such a pool.";
    public string SourceCommit { get; set; } = "";
    public int ProcessId { get; set; }
    public bool NativeAot { get; set; }
    public string PackageSha256 { get; set; } = "";
    public string ExecutableSha256 { get; set; } = "";
    public string Provider { get; set; } = "";
    public string NativePath { get; set; } = "";
    public string NativeSha256 { get; set; } = "";
    public string Runtime { get; set; } = "";
    public int ProcessorCount { get; set; }
    public string? ProcessorCountOverride { get; set; }
    public int Concurrency { get; set; }
    public string SuspendedQuerySql { get; set; } = "";
    public int SuspendedQuerySteps { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset FinishedUtc { get; set; }
    public List<Window> Windows { get; set; } = [];
    public List<Memory> Memory { get; set; } = [];
    public List<OwnerObservation> NativeObservations { get; set; } = [];
    public List<OwnerSettlement> OwnerSettlements { get; set; } = [];
    public List<string> ExhaustionExceptions { get; set; } = [];
    public List<string> CleanupErrors { get; set; } = [];
    public int PendingOpensBeforeCancellation { get; set; }
    public int MatchingCanceledOpenResults { get; set; }
    public int ResourceLimitOpenResults { get; set; }
    public int ExhaustionAdmissionWaitMilliseconds { get; set; } = 20;
    public bool PendingCancellationObserved { get; set; }
    public ulong[] WarmOwners { get; set; } = [];
    public ulong[] OwnersAfterExhaustion { get; set; } = [];
    public ulong[] FinalOwners { get; set; } = [];
    public double IdleUserSeconds { get; set; }
    public double IdleSystemSeconds { get; set; }
    public double IdleElapsedSeconds { get; set; }
    public bool SourceReleased { get; set; }
    public bool Passed { get; set; }
    public string? Error { get; set; }
}
[JsonSerializable(typeof(ResourceReport))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class ProbeJson : JsonSerializerContext;

internal sealed class NativeCounts : IDisposable
{
    private readonly nint _module, _counter;
    internal string Path { get; }
    internal NativeCounts(DatabaseProvider provider)
    {
        string name = provider == DatabaseProvider.PostgreSql ? "libnw_database_postgresql.so" : "libnw_database_sqlite.so";
        var paths = File.ReadLines("/proc/self/maps").Select(line => line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 6 && System.IO.Path.GetFileName(parts[5]) == name).Select(parts => parts[5]).Distinct().ToArray();
        if (paths.Length != 1) throw new InvalidOperationException("Expected exactly one loaded native provider.");
        Path = paths[0]; _module = NativeLibrary.Load(Path);
        try { _counter = NativeLibrary.GetExport(_module, "nw_db_diagnostic_count_v1"); }
        catch { NativeLibrary.Free(_module); throw; }
    }
    internal unsafe ulong[] Read()
    {
        var values = new ulong[8]; ReadInto(values); return values;
    }
    internal unsafe void ReadInto(Span<ulong> values)
    {
        if (values.Length != 8) throw new ArgumentException("Eight native counters are required.", nameof(values));
        var call = (delegate* unmanaged[Cdecl]<uint, ulong>)_counter;
        for (uint i = 0; i < 8; i++) values[(int)i] = call(i);
    }
    public void Dispose() => NativeLibrary.Free(_module);
}
