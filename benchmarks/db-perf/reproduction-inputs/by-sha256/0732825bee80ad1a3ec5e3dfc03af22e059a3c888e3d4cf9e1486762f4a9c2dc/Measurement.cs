using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Korda.Database;

internal sealed class Worker : IAsyncDisposable
{
    private readonly DataSource _source;
    private readonly DataSession _session;
    private readonly Workload _workload;
    private readonly Options _options;
    internal readonly long[] Histogram = new long[8192];
    internal long Started, Completed, Failed, Rows, Suspensions;
    internal ulong ResultDigest;
    internal string? FirstError;
    internal IReadOnlyDictionary<string, string> Sql => _workload.Sql;
    internal IReadOnlyDictionary<string, OperationResult> Expectations => _workload.Expectations;

    private Worker(DataSource source, DataSession session, Options options)
    { _source = source; _session = session; _options = options; _workload = new(session, options); }
    internal static async Task<Worker> Create(DataSource source, Options options) => new(source, await source.OpenSessionAsync(), options);

    internal async Task Warmup()
    {
        if (_options.Verify)
            foreach (var name in Workload.Names) Validate(name, await Execute(name));
        for (var i = 0; i < _options.Warmup; i++)
        {
            var name = _workload.Name(i); Validate(name, await Execute(name));
        }
    }
    private async ValueTask<OperationResult> Execute(string name)
    {
        if (_options.SessionMode == "persistent") return await _workload.Execute(name).ConfigureAwait(false);
        await using var session = await _source.OpenSessionAsync().ConfigureAwait(false);
        return await _workload.Execute(name, session).ConfigureAwait(false);
    }
    private void Validate(string name, OperationResult result)
    {
        var expected = _workload.Expected(name);
        if (result.Rows != expected.Rows || result.Digest != expected.Digest)
            throw new InvalidOperationException($"{name}: expected {expected.Rows}/{expected.Digest:x16}, observed {result.Rows}/{result.Digest:x16}");
    }
    internal async Task Run(Task start, RunState state, long operations)
    {
        await start.ConfigureAwait(false);
        for (long i = 0; _options.Seconds > 0 ? Stopwatch.GetTimestamp() < state.Deadline : i < operations; i++)
        {
            var name = _workload.Name(i); var began = Stopwatch.GetTimestamp(); Started++;
            try
            {
                var result = await Execute(name).ConfigureAwait(false);
                Validate(name, result);
                Completed++; Rows += result.Rows; Suspensions += result.Suspensions; ResultDigest ^= result.Digest;
                var finished = Stopwatch.GetTimestamp();
                var ns = Math.Max(1UL, (ulong)((finished - began) * (1_000_000_000d / Stopwatch.Frequency)));
                var exponent = BitOperations.Log2(ns);
                var lower = 1UL << exponent;
                var fraction = (int)((ns - lower) * 128 / lower);
                Histogram[exponent * 128 + fraction]++;
                state.Record(finished, result.Rows, false);
            }
            catch (Exception error)
            {
                Failed++; FirstError ??= error.ToString();
                state.Record(Stopwatch.GetTimestamp(), 0, true);
            }
        }
    }
    public async ValueTask DisposeAsync() { _workload.Dispose(); await _session.DisposeAsync(); }
}

internal sealed class RunState
{
    internal long StartedAt, Deadline;
    internal readonly long[] SuccessBySecond, RowsBySecond, FailureBySecond;
    internal int Truncated;
    internal RunState(Options options)
    {
        var count = options.Seconds > 0 ? checked((int)Math.Ceiling(options.Seconds) + 2) : 3602;
        SuccessBySecond = new long[count]; RowsBySecond = new long[count]; FailureBySecond = new long[count];
    }
    internal void Record(long now, long rows, bool failed)
    {
        var second = (now - StartedAt) / Stopwatch.Frequency;
        if (second >= SuccessBySecond.Length) { Volatile.Write(ref Truncated, 1); return; }
        if (failed) Interlocked.Increment(ref FailureBySecond[second]);
        else { Interlocked.Increment(ref SuccessBySecond[second]); Interlocked.Add(ref RowsBySecond[second], rows); }
    }
}

internal sealed class NativeDiagnostics : IDisposable
{
    private readonly nint _module, _count;
    internal string Path { get; }
    internal string Sha256 { get; }
    internal NativeDiagnostics(DatabaseProvider provider)
    {
        var name = provider == DatabaseProvider.PostgreSql ? "libnw_database_postgresql.so" : "libnw_database_sqlite.so";
        var files = File.ReadLines("/proc/self/maps").Select(line => line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 6 && System.IO.Path.GetFileName(parts[5]) == name).Select(parts => parts[5]).Distinct().ToArray();
        if (files.Length != 1) throw new InvalidOperationException("Expected one actually loaded provider library.");
        Path = files[0];
        using (var file = File.OpenRead(Path)) Sha256 = Convert.ToHexStringLower(SHA256.HashData(file));
        _module = NativeLibrary.Load(Path); _count = NativeLibrary.GetExport(_module, "nw_db_diagnostic_count_v1");
    }
    internal unsafe NativeCounts Read()
    {
        var count = (delegate* unmanaged[Cdecl]<uint, ulong>)_count;
        return new(count(0), count(1), count(2), count(3), count(4), count(5), count(6), count(7));
    }
    public void Dispose() => NativeLibrary.Free(_module);
}

internal sealed record NativeCounts(ulong Sources, ulong Sessions, ulong Operations, ulong Cursors,
    ulong Buffers, ulong Owners, ulong SqlExecutions, ulong ProtocolSubmissions);
internal sealed record SecondCount(int Second, long Completed, long Rows, long Failed);
internal sealed record Latencies(double P50MicrosecondsUpper, double P95MicrosecondsUpper, double P99MicrosecondsUpper,
    string Method, long[] Histogram);
internal sealed record ProcessReading(double UserCpuSeconds, double SystemCpuSeconds, long RssBytes, long PeakRssBytes)
{
    internal static ProcessReading Read(Process process)
    {
        process.Refresh();
        return new(process.UserProcessorTime.TotalSeconds, process.PrivilegedProcessorTime.TotalSeconds,
            process.WorkingSet64, process.PeakWorkingSet64);
    }
}
internal sealed record RunReceipt(int SchemaVersion, string Route, string Provider, bool NativeAot,
    string PackageVersion, string ExecutableSha256, string LoadedNativePath, string LoadedNativeSha256,
    string Case, string Mode, string SessionMode, string Scheduling, int Concurrency, int WarmupPerWorker,
    int RequestedRows, int BatchRows, int Tenants, int OrdersPerTenant, string StartedUtc, string FinishedUtc,
    double EffectiveSeconds, long Planned, long Started, long Completed, long Failed, long Retries, long Rows,
    long ActualStreamSuspensions, long AllocatedBefore, long AllocatedAfter, long AllocatedBytes,
    double BytesPerSuccessfulOperation, double BytesPerRow, int Gen0, int Gen1, int Gen2,
    string AllocationAttribution, string EmptyDriverScope, bool EmptyDriver,
    ProcessReading ProcessBefore, ProcessReading ProcessAfter, double UserCpuSeconds, double SystemCpuSeconds,
    double CpuSecondsPerSuccessfulOperation, NativeCounts Before, NativeCounts After, NativeCounts Drained,
    bool OwnersReturnedToZero, Latencies Latencies, SecondCount[] Seconds, bool TimeSeriesTruncated,
    Dictionary<string, string> ActualDiagnosticSql, Dictionary<string, OperationResult> ExpectedResults, string[] Errors);

internal static class Measurement
{
    internal static async Task<RunReceipt> Run(DataSource source, Options options, NativeDiagnostics native)
    {
        var workers = new Worker[options.Concurrency];
        try
        {
            for (var i = 0; i < workers.Length; i++) workers[i] = await Worker.Create(source, options);
            await Task.WhenAll(workers.Select(worker => worker.Warmup()));
            var state = new RunState(options);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = new Task[workers.Length];
            for (var i = 0; i < workers.Length; i++)
                tasks[i] = workers[i].Run(start.Task, state, options.Operations / workers.Length + (i < options.Operations % workers.Length ? 1 : 0));
            var completed = Task.WhenAll(tasks);
            // All harness objects, worker state machines, histograms and the continuation gate exist before the boundary.
            // The real asynchronous query/consumer state created by each operation remains in the raw total.
            using var process = Process.GetCurrentProcess();
            var beforeNative = native.Read(); var beforeProcess = ProcessReading.Read(process);
            var startedUtc = DateTimeOffset.UtcNow.ToString("O");
            var before0 = GC.CollectionCount(0); var before1 = GC.CollectionCount(1); var before2 = GC.CollectionCount(2);
            var beforeBytes = GC.GetTotalAllocatedBytes(precise: true);
            state.StartedAt = Stopwatch.GetTimestamp();
            state.Deadline = state.StartedAt + (long)(options.Seconds * Stopwatch.Frequency);
            start.SetResult();
            await completed.ConfigureAwait(false);
            var endedAt = Stopwatch.GetTimestamp();
            var afterBytes = GC.GetTotalAllocatedBytes(precise: true);
            var after0 = GC.CollectionCount(0); var after1 = GC.CollectionCount(1); var after2 = GC.CollectionCount(2);
            var finishedUtc = DateTimeOffset.UtcNow.ToString("O");
            var afterProcess = ProcessReading.Read(process); var afterNative = native.Read();
            var elapsed = (endedAt - state.StartedAt) / (double)Stopwatch.Frequency;
            var success = workers.Sum(worker => worker.Completed); var rows = workers.Sum(worker => worker.Rows);
            var started = workers.Sum(worker => worker.Started); var failed = workers.Sum(worker => worker.Failed);
            var suspended = workers.Sum(worker => worker.Suspensions);
            var errors = workers.Where(worker => worker.FirstError is not null).Select(worker => worker.FirstError!).ToArray();
            var histogram = new long[8192];
            foreach (var worker in workers)
                for (var i = 0; i < histogram.Length; i++) histogram[i] += worker.Histogram[i];
            var sql = workers[0].Sql.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var expectations = workers[0].Expectations.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            for (var i = 0; i < workers.Length; i++) { await workers[i].DisposeAsync(); workers[i] = null!; }
            await source.DisposeAsync();
            var drained = native.Read();
            var ownersZero = drained is { Sources: 0, Sessions: 0, Operations: 0, Cursors: 0, Buffers: 0, Owners: 0 };
            var seconds = new List<SecondCount>();
            for (var i = 0; i < Math.Min(state.SuccessBySecond.Length, Math.Ceiling(elapsed) + 1); i++)
                seconds.Add(new(i, state.SuccessBySecond[i], state.RowsBySecond[i], state.FailureBySecond[i]));
            var path = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable identity.");
            string executableHash;
            using (var file = File.OpenRead(path)) executableHash = Convert.ToHexStringLower(SHA256.HashData(file));
            var version = typeof(DataSource).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
            return new(1, "korda-public-fluent", source.Provider.ToString(), true, version,
                executableHash, native.Path, native.Sha256, options.Case, options.Mode, options.SessionMode,
                "closed-loop; next operation planned after preceding completion; no external arrival rate", options.Concurrency,
                options.Warmup, options.Rows, options.BatchRows, options.Tenants, options.OrdersPerTenant,
                startedUtc, finishedUtc, elapsed, options.Seconds > 0 ? started : options.Operations, started, success, failed, 0, rows,
                suspended, beforeBytes, afterBytes, afterBytes - beforeBytes, success == 0 ? 0 : (afterBytes - beforeBytes) / (double)success,
                rows == 0 ? 0 : (afterBytes - beforeBytes) / (double)rows, after0 - before0, after1 - before1, after2 - before2,
                "whole-process end-to-end total; business DTO/string/JSON and harness async state included; framework-only attribution unproven",
                "same workers/gate/validation/metrics; returns precomputed result; does not model asynchronous I/O continuations", options.EmptyDriver,
                beforeProcess, afterProcess, afterProcess.UserCpuSeconds - beforeProcess.UserCpuSeconds,
                afterProcess.SystemCpuSeconds - beforeProcess.SystemCpuSeconds,
                success == 0 ? 0 : (afterProcess.UserCpuSeconds + afterProcess.SystemCpuSeconds - beforeProcess.UserCpuSeconds - beforeProcess.SystemCpuSeconds) / success,
                beforeNative, afterNative, drained, ownersZero,
                new(Quantile(histogram, success, .50), Quantile(histogram, success, .95), Quantile(histogram, success, .99),
                    "base-2 histogram with 128 subdivisions per exponent; reported upper bounds (<0.782% relative bin width)", histogram),
                seconds.ToArray(), state.Truncated != 0, sql, expectations, errors);
        }
        finally
        {
            foreach (var worker in workers) if (worker is not null) await worker.DisposeAsync();
        }
    }
    private static double Quantile(long[] histogram, long total, double quantile)
    {
        if (total == 0) return 0;
        var desired = (long)Math.Ceiling(total * quantile); long count = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            count += histogram[i];
            if (count >= desired)
            {
                var lower = Math.Pow(2, i / 128);
                return (lower + lower * ((i % 128 + 1) / 128d)) / 1000;
            }
        }
        return 0;
    }
}
