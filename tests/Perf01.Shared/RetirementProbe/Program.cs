using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using NativeWeave.Data;
using NativeWeave.Database;
using NativeWeave.Database.PostgreSql;
using NativeWeave.Database.Sqlite;
using NativeWeave.Interop.Async;

if (args.Length != 4 || !int.TryParse(args[2], out int count) || count is < 100 or > 100_000)
    throw new ArgumentException("Usage: RetirementProbe sqlite|postgresql <path|local-uri> <count:100..100000> <new-result.json>");
if (File.Exists(args[3])) throw new IOException("Preserve existing profiling evidence.");

var builder = new DataSourceBuilder().WithName("perf01-retirement-profile")
    .ConfigureLimits(new DatabaseLimits { MaxConnections = 2, MaxBatchRows = 128 });
DataSource source = args[0] switch
{
    "sqlite" => builder.UseSqlite(args[1]).Build(),
    "postgresql" => builder.UsePostgreSql(args[1], options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment).Build(),
    _ => throw new ArgumentException("Unknown provider")
};
var select = SqlCommand.Create("SELECT CAST(1 AS BIGINT)");
var tracker = new OperationTracker(checked(count * 8 + 128));
double[] durations = new double[count];
long allocationBefore, allocationAfter, cpuBefore, cpuAfter;
int[] gcBefore, gcAfter;
using var process = Process.GetCurrentProcess();
await using (var session = await source.OpenSessionAsync())
{
    for (int i = 0; i < 20; ++i)
        if (await session.ScalarAsync(select, Codecs.Int64) != 1) throw new InvalidDataException("Warmup scalar mismatch.");
    source.TestBoundary = tracker.Boundary;
    source.TestRetirement = tracker.Retired;
    gcBefore = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
    cpuBefore = process.TotalProcessorTime.Ticks;
    for (int i = 0; i < count; ++i)
    {
        long start = Stopwatch.GetTimestamp();
        if (await session.ScalarAsync(select, Codecs.Int64) != 1) throw new InvalidDataException("Measured scalar mismatch.");
        durations[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
    cpuAfter = process.TotalProcessorTime.Ticks;
    allocationAfter = GC.GetTotalAllocatedBytes(precise: true);
    gcAfter = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    source.TestBoundary = null;
    source.TestRetirement = null;
}
await source.DisposeAsync();
if (!source.Cleanup.LocalResourcesReleased) throw new InvalidOperationException("Source did not retire.");
var modules = File.ReadLines("/proc/self/maps").Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1])
    .Where(path => path.StartsWith('/') && (Path.GetFileName(path).StartsWith("libnw_", StringComparison.Ordinal) ||
        Path.GetFileName(path).StartsWith("libnativeweave_", StringComparison.Ordinal)))
    .Distinct(StringComparer.Ordinal).ToDictionary(path => path, path =>
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }, StringComparer.Ordinal);
ProfileResult result = new(args[0], count, Stopwatch.Frequency, durations, tracker.Finish(),
    allocationAfter - allocationBefore, (cpuAfter - cpuBefore) / (double)TimeSpan.TicksPerMillisecond,
    gcAfter.Zip(gcBefore, (after, before) => after - before).ToArray(), true, modules,
    "Profiling-only observer of existing internal test boundaries; not a formal uninstrumented timing sample. Native retirement Pending count is not directly observed.");
await File.WriteAllTextAsync(args[3], JsonSerializer.Serialize(result, ProfileJson.Default.ProfileResult));
Console.WriteLine($"provider={args[0]} calls={count} native_operations={result.NativeOperations.Length} mean_ms={durations.Average():F6} released=true");

internal sealed class OperationTracker
{
    private readonly NativeObservation[] _rows;
    private int _count;
    private int _active = -1;
    private int _retired;
    internal OperationTracker(int capacity) => _rows = new NativeObservation[capacity];
    internal void Boundary(AsyncBoundary boundary)
    {
        long now = Stopwatch.GetTimestamp();
        if (boundary == AsyncBoundary.BeforeStart)
        {
            int index = _count++;
            if (index >= _rows.Length || _active != -1)
                throw new InvalidOperationException("This profiling observer requires serial native operations.");
            _rows[index].Start = now;
            Volatile.Write(ref _active, index);
            return;
        }
        int active = Volatile.Read(ref _active);
        if (active < 0) return;
        switch (boundary)
        {
            case AsyncBoundary.StartReturned: _rows[active].StartReturned = now; break;
            case AsyncBoundary.TerminalFieldsCopied: _rows[active].Terminal = now; break;
            case AsyncBoundary.BeforeTake: _rows[active].BeforeTake = now; break;
        }
    }
    internal void Retired(AsyncSnapshot snapshot)
    {
        int active = Volatile.Read(ref _active);
        if (active < 0 || !snapshot.Released || snapshot.RootLive || !snapshot.NativeQuiet ||
            snapshot.Controls != 0 || snapshot.CallbackEntries != 0 || snapshot.RetainedInputBytes != 0)
            throw new InvalidOperationException("Actual retirement invariants failed.");
        _rows[active].Retired = Stopwatch.GetTimestamp();
        _retired++;
        Volatile.Write(ref _active, -1);
    }
    internal NativeObservation[] Finish()
    {
        if (_active != -1 || _retired != _count) throw new InvalidOperationException("Incomplete operation observations.");
        return _rows[.._count];
    }
}

internal struct NativeObservation
{
    public long Start { get; set; }
    public long StartReturned { get; set; }
    public long Terminal { get; set; }
    public long BeforeTake { get; set; }
    public long Retired { get; set; }
}
internal sealed record ProfileResult(string Provider, int Calls, long StopwatchFrequency, double[] PublicCallMilliseconds,
    NativeObservation[] NativeOperations, long ManagedAllocatedBytesIncludingObserver, double ProcessCpuMilliseconds,
    int[] GcCollections, bool SourceReleased, Dictionary<string, string> LoadedNativeModules, string Scope);

[JsonSerializable(typeof(ProfileResult))]
internal partial class ProfileJson : JsonSerializerContext;
