using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Korda.Data;
using Korda.Database;

internal sealed class Worker : IAsyncDisposable
{
    private readonly DataSource _source;
    private readonly DataSession _session;
    private readonly Workload _workload;
    private readonly Options _options;
    private static int _nextIdentity;
    private readonly int _identity=Interlocked.Increment(ref _nextIdentity);
    private readonly Dictionary<string,string> _sqlIdentities;
    private string _phase="construction";
    private bool _initialSessionReleased;
    private Exception? _executionFailure;
    internal readonly long[] Histogram = new long[8192];
    internal long Started, Completed, Failed, Rows, Suspensions;
    internal readonly long[] CaseStarted = new long[Workload.Names.Length], CaseSucceeded = new long[Workload.Names.Length],
        CaseFailed = new long[Workload.Names.Length], CaseRows = new long[Workload.Names.Length], CaseSuspensions = new long[Workload.Names.Length];
    internal ulong ResultDigest;
    internal string? FirstError;
    internal IReadOnlyDictionary<string, string> Sql => _workload.Sql;
    internal string[] ActiveNames => _workload.ActiveNames;
    internal IReadOnlyDictionary<string, OperationResult> Expectations => _workload.Expectations;

    private Worker(DataSource source, DataSession session, Options options)
    {
        _source = source; _session = session; _options = options; _workload = new(session, options);
        _sqlIdentities=_workload.Sql.ToDictionary(pair=>pair.Key,
            pair=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(pair.Value))),StringComparer.Ordinal);
    }
    internal void SetPhase(string phase)=>_phase=phase;
    internal static async Task<Worker> Create(DataSource source, Options options)
    {
        var session=await source.OpenSessionAsync();
        try
        {
            var worker=new Worker(source,session,options);
            if(options.SessionMode=="per-operation"){await session.DisposeAsync();worker._initialSessionReleased=true;}
            return worker;
        }
        catch{await session.DisposeAsync();throw;}
    }

    internal async Task Warmup()
    {
        SetPhase("warmup");
        if (_options.Verify)
            foreach (var name in Workload.Names) await Execute(name);
        for (var i = 0; i < _options.Warmup; i++)
        {
            var name = _workload.Name(i); await Execute(name);
        }
    }
    private async ValueTask<OperationResult> Execute(string name)
    {
        if (_options.SessionMode == "persistent")
        {
            try { var result = await _workload.Execute(name).ConfigureAwait(false); Validate(name, result); return result; }
            catch (DatabaseException error) when (error.Kind is DatabaseErrorKind.Conversion or DatabaseErrorKind.ShapeMismatch
                or DatabaseErrorKind.UnsupportedCapability or DatabaseErrorKind.ConcurrentUse
                or DatabaseErrorKind.InvalidState or DatabaseErrorKind.UnknownOutcome)
            { throw CorrectnessOracle.Failure("The fixed result violated its declared type or shape.", name, error); }
            catch(DatabaseException error){throw Detail(error,name,_session);}
            catch(Exception error){CorrectnessOracle.ObserveContractFailure(error,name);throw;}
        }
        var session = await _source.OpenSessionAsync().ConfigureAwait(false);
        _executionFailure = null;
        try
        {
            try { var result = await _workload.Execute(name, session).ConfigureAwait(false); Validate(name, result); return result; }
            catch (DatabaseException error) when (error.Kind is DatabaseErrorKind.Conversion or DatabaseErrorKind.ShapeMismatch
                or DatabaseErrorKind.UnsupportedCapability or DatabaseErrorKind.ConcurrentUse
                or DatabaseErrorKind.InvalidState or DatabaseErrorKind.UnknownOutcome)
            { throw CorrectnessOracle.Failure("The fixed result violated its declared type, shape or owner contract.", name, error); }
            catch(DatabaseException error){throw Detail(error,name,session);}
        }
        catch(Exception error)
        {
            CorrectnessOracle.ObserveContractFailure(error,name);
            _executionFailure=error;throw;
        }
        finally
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch(Exception cleanup)
            {
                CorrectnessOracle.ObserveCleanupFailure(cleanup,name);
                if(_executionFailure is {} primary)
                    throw new AggregateException("Workload failure and session retirement failure.",primary,cleanup);
                throw;
            }
            finally { _executionFailure=null; }
        }
    }
    private DatabaseException Detail(DatabaseException error,string name,DataSession session)=>new(
        $"DB_PERF_FAILURE phase={_phase} case={name} worker={_identity} sourceManagedIdentity={RuntimeHelpers.GetHashCode(_source)} " +
        $"sessionManagedIdentity={RuntimeHelpers.GetHashCode(session)} sessionMode={_options.SessionMode} kind={error.Kind} " +
        $"providerCode={error.ProviderCode??"<none>"} correlation={error.CorrelationId} transient={error.IsTransient} " +
        $"sqlOrPublicOperationContractSha256={_sqlIdentities[name]}. Managed identities do not identify a physical backend connection.",
        error.Kind,error.ProviderCode,error.CorrelationId,error);
    internal async ValueTask<OperationResult> ExecuteScheduled(long operationIndex)
    {
        var name = _workload.Name(operationIndex); var index = Array.IndexOf(Workload.Names, name);
        CaseStarted[index]++;
        try
        {
            var result = await Execute(name).ConfigureAwait(false);
            CaseSucceeded[index]++; CaseRows[index] += result.Rows; CaseSuspensions[index] += result.Suspensions;
            return result;
        }
        catch { CaseFailed[index]++; throw; }
    }
    private void Validate(string name, OperationResult result)
    {
        var expected = _workload.Expected(name);
        if (result.Rows != expected.Rows || result.Digest != expected.Digest)
            throw CorrectnessOracle.Failure($"{name}: expected {expected.Rows}/{expected.Digest:x16}, observed {result.Rows}/{result.Digest:x16}", name);
    }
    internal async Task Run(Task start, RunState state, long operations)
    {
        await start.ConfigureAwait(false);
        for (long i = 0; _options.Seconds > 0 ? Stopwatch.GetTimestamp() < state.Deadline : i < operations; i++)
        {
            var name = _workload.Name(i); var began = Stopwatch.GetTimestamp(); Started++;
            try
            {
                var result = await ExecuteScheduled(i).ConfigureAwait(false);
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
    public async ValueTask DisposeAsync() { _workload.Dispose(); if(!_initialSessionReleased) await _session.DisposeAsync(); }
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
    private readonly bool _pullSupported, _commandsSupported;
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
        _pullSupported = NativeLibrary.TryGetExport(_module, "nw_db_cursor_pull_create_v1", out _);
        _commandsSupported = NativeLibrary.TryGetExport(_module, "nw_db_command_request_v1", out _);
    }
    internal unsafe NativeCounts Read()
    {
        var count = (delegate* unmanaged[Cdecl]<uint, ulong>)_count;
        return new(count(0), count(1), count(2), count(3), count(4), count(5), count(6), count(7),
            _pullSupported ? count(8) : null, _commandsSupported ? count(9) : null);
    }
    public void Dispose() => NativeLibrary.Free(_module);
}

internal sealed record NativeCounts(ulong Sources, ulong Sessions, ulong Operations, ulong Cursors,
    ulong Buffers, ulong Owners, ulong SqlExecutions, ulong ProtocolSubmissions, ulong? PullChannels, ulong? CommandCompletions);
internal sealed record SecondCount(int Second, long Completed, long Rows, long Failed);
internal sealed record Latencies(double P50MicrosecondsUpper, double P95MicrosecondsUpper, double P99MicrosecondsUpper,
    string Method, long[] Histogram);
internal sealed record ProcessReading(double UserCpuSeconds, double SystemCpuSeconds, long RssBytes, long PeakRssBytes, long? PssBytes)
{
    internal static ProcessReading Read(Process process)
    {
        process.Refresh();
        return new(process.UserProcessorTime.TotalSeconds, process.PrivilegedProcessorTime.TotalSeconds,
            process.WorkingSet64, process.PeakWorkingSet64, ReadPss());
    }
    private static long? ReadPss()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/smaps_rollup"))
                if (line.StartsWith("Pss:",StringComparison.Ordinal))
                    return long.Parse(line.Split(' ',StringSplitOptions.RemoveEmptyEntries)[1],System.Globalization.CultureInfo.InvariantCulture)*1024;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}
internal sealed record CaseCount(string Name,long Started,long Succeeded,long Failed,long Rows,long Suspensions);
internal sealed record IdleReading(string StartedUtc,string FinishedUtc,double RequestedSeconds,double ObservedSeconds,
    ProcessReading Before,ProcessReading After,double UserCpuSeconds,double SystemCpuSeconds,long ManagedBytesBefore,long ManagedBytesAfter,
    string Scope);
internal sealed record RunReceipt(int SchemaVersion, string Route, string Provider, bool NativeAot,
    string PackageVersion, string ExecutableSha256, string LoadedNativePath, string LoadedNativeSha256,
    string Case, string[] ActualCaseIds, Dictionary<string,string> CaseModeContracts, string Mode, string SessionMode, string TransactionContract, string Scheduling, int Concurrency, int WarmupPerWorker,
    int RequestedRows, int BatchRows, int Tenants, int OrdersPerTenant, string StartedUtc, string FinishedUtc,
    long SteadyStartMonotonicNs, long SteadyEndMonotonicNs,
    double EffectiveSeconds, long Planned, long Started, long Completed, long Failed, long Retries, long Rows,
    long ActualStreamSuspensions, long AllocatedBefore, long AllocatedAfter, long AllocatedBytes,
    double BytesPerSuccessfulOperation, double BytesPerRow, int Gen0, int Gen1, int Gen2,
    string AllocationAttribution, string EmptyDriverScope, bool EmptyDriver,
    ProcessReading ProcessBefore, ProcessReading ProcessAfter, double UserCpuSeconds, double SystemCpuSeconds,
    double CpuSecondsPerSuccessfulOperation, NativeCounts Before, NativeCounts After, NativeCounts Drained,
    bool OwnersReturnedToZero, Latencies Latencies, SecondCount[] Seconds, bool TimeSeriesTruncated,
    Dictionary<string, string> ActualDiagnosticSql, Dictionary<string, OperationResult> ExpectedResults, string[] Errors, OfferedReceipt? OfferedLoad, CaseCount[] Cases, IdleReading? Idle,
    bool TimingComparisonEligible, NativeAllocationProfileReceipt? NativeAllocationProfile)
{
    public long OracleFailuresBefore { get; init; }
    public long OracleFailuresAfter { get; init; }
    public long OracleFailures { get; init; }
    public OracleFailure? FirstOracleFailure { get; init; }
}

internal static class Measurement
{
    internal static async Task<RunReceipt> Run(DataSource source, Options options, NativeDiagnostics native)
    {
        using var allocationProfile = NativeAllocationProfile.Create(options.AllocationProfile);
        var workers = new Worker[options.Concurrency];
        try
        {
            for (var i = 0; i < workers.Length; i++) workers[i] = await Worker.Create(source, options);
            CorrectnessOracle.SetPhase("warmup");
            await Task.WhenAll(workers.Select(worker => worker.Warmup()));
            CorrectnessOracle.RequireClean();
            CorrectnessOracle.SetPhase(options.Rate > 0 ? "offered" : "closed-loop");
            foreach(var worker in workers)worker.SetPhase(options.Rate>0?"offered":"closed-loop");
            var state = new RunState(options);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var offered = options.Rate > 0 ? new OfferedLoad(options.Seconds, options.Rate, options.QueueCapacity,
                workers.Select(worker => new Func<long, ValueTask<OperationResult>>(worker.ExecuteScheduled)).ToArray()) : null;
            var tasks = new Task[offered is null ? workers.Length : 0];
            for (var i = 0; i < tasks.Length; i++)
                tasks[i] = workers[i].Run(start.Task, state, options.Operations / workers.Length + (i < options.Operations % workers.Length ? 1 : 0));
            var completed = offered?.Completion ?? Task.WhenAll(tasks);
            // All harness objects, worker state machines, histograms and the continuation gate exist before the boundary.
            // The real asynchronous query/consumer state created by each operation remains in the raw total.
            using var process = Process.GetCurrentProcess();
            var beforeNative = native.Read(); var beforeProcess = ProcessReading.Read(process);
            var startedUtc = DateTimeOffset.UtcNow.ToString("O");
            allocationProfile?.CaptureBefore();
            var before0 = GC.CollectionCount(0); var before1 = GC.CollectionCount(1); var before2 = GC.CollectionCount(2);
            var beforeOracle = CorrectnessOracle.Failures;
            var beforeBytes = GC.GetTotalAllocatedBytes(precise: true);
            state.StartedAt = Stopwatch.GetTimestamp();
            state.Deadline = state.StartedAt + (long)(options.Seconds * Stopwatch.Frequency);
            if (offered is null) start.SetResult();
            else { offered.Start(); state.StartedAt = offered.StartedTicks; state.Deadline = offered.DeadlineTicks; }
            await completed.ConfigureAwait(false);
            var endedAt = Stopwatch.GetTimestamp();
            var afterBytes = GC.GetTotalAllocatedBytes(precise: true);
            var afterOracle = CorrectnessOracle.Failures;
            var after0 = GC.CollectionCount(0); var after1 = GC.CollectionCount(1); var after2 = GC.CollectionCount(2);
            allocationProfile?.CaptureAfter();
            var finishedUtc = DateTimeOffset.UtcNow.ToString("O");
            var afterProcess = ProcessReading.Read(process); var afterNative = native.Read();
            var elapsed = (endedAt - state.StartedAt) / (double)Stopwatch.Frequency;
            var schedule = offered?.Snapshot();
            var success = schedule?.Succeeded ?? workers.Sum(worker => worker.Completed); var rows = schedule?.Rows ?? workers.Sum(worker => worker.Rows);
            var started = schedule?.Started ?? workers.Sum(worker => worker.Started); var failed = schedule?.Failed ?? workers.Sum(worker => worker.Failed);
            var suspended = schedule?.Suspensions ?? workers.Sum(worker => worker.Suspensions);
            var errors = schedule?.Errors ?? workers.Where(worker => worker.FirstError is not null).Select(worker => worker.FirstError!).ToArray();
            var histogram = new long[8192];
            foreach (var worker in workers)
                for (var i = 0; i < histogram.Length; i++) histogram[i] += worker.Histogram[i];
            if (schedule is not null) histogram = schedule.SuccessServiceHistogram;
            var caseIds = workers[0].ActiveNames.ToArray();
            var caseCounts = caseIds.Distinct(StringComparer.Ordinal).Select(name =>
            {
                var i=Array.IndexOf(Workload.Names,name);
                return new CaseCount(name,workers.Sum(worker=>worker.CaseStarted[i]),workers.Sum(worker=>worker.CaseSucceeded[i]),
                    workers.Sum(worker=>worker.CaseFailed[i]),workers.Sum(worker=>worker.CaseRows[i]),workers.Sum(worker=>worker.CaseSuspensions[i]));
            }).ToArray();
            var caseModes = caseIds.Distinct(StringComparer.Ordinal).ToDictionary(name => name, name => name is "tx-commit" or "tx-rollback" or "partial-update" or "stale-conflict"?"Public CRUD and transaction controls have no prepared-plan overload; construction included in both requested modes":name=="related"?"LoadRelatedAsync rebuilds each bounded batch; no prepared overload":name=="raw-sql"?options.Mode=="prepared"?"immutable SqlCommand reuse; no PreparedQuery":"new SqlCommand each operation":options.Mode=="prepared"?"PreparedQuery<T> plan reuse":"Fluent graph construction each operation");
            var sql = workers[0].Sql.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var expectations = workers[0].Expectations.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            CorrectnessOracle.SetPhase("drain");
            for (var i = 0; i < workers.Length; i++) { await workers[i].DisposeAsync(); workers[i] = null!; }
            await source.DisposeAsync();
            var drained = native.Read();
            var ownersZero = drained is { Sources: 0, Sessions: 0, Operations: 0, Cursors: 0, Buffers: 0, Owners: 0 }
                && drained.PullChannels is null or 0 && drained.CommandCompletions is null or 0;
            allocationProfile?.CaptureAfterDrain();
            IdleReading? idle = null;
            if(options.IdleSeconds > 0)
            {
                var idleBefore=ProcessReading.Read(process); var managedBefore=GC.GetTotalMemory(false);
                var idleStartedUtc=DateTimeOffset.UtcNow.ToString("O"); var idleStarted=Stopwatch.GetTimestamp();
                await Task.Delay(TimeSpan.FromSeconds(options.IdleSeconds)).ConfigureAwait(false);
                var idleFinished=Stopwatch.GetTimestamp(); var idleFinishedUtc=DateTimeOffset.UtcNow.ToString("O");
                var managedAfter=GC.GetTotalMemory(false);var idleAfter=ProcessReading.Read(process);
                idle=new(idleStartedUtc,idleFinishedUtc,options.IdleSeconds,(idleFinished-idleStarted)/(double)Stopwatch.Frequency,
                    idleBefore,idleAfter,idleAfter.UserCpuSeconds-idleBefore.UserCpuSeconds,idleAfter.SystemCpuSeconds-idleBefore.SystemCpuSeconds,
                    managedBefore,managedAfter,"After workload/session/source drain; no forced GC; outside effective duration and raw allocation boundary; observer costs included");
            }
            var seconds = new List<SecondCount>();
            if (schedule is not null)
                foreach (var second in schedule.Seconds) seconds.Add(new(second.Second, second.Succeeded, second.Rows, second.Failed));
            else
                for (var i = 0; i < Math.Min(state.SuccessBySecond.Length, Math.Ceiling(elapsed) + 1); i++)
                    seconds.Add(new(i, state.SuccessBySecond[i], state.RowsBySecond[i], state.FailureBySecond[i]));
            var path = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable identity.");
            string executableHash;
            using (var file = File.OpenRead(path)) executableHash = Convert.ToHexStringLower(SHA256.HashData(file));
            var version = typeof(DataSource).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
            return new(1, "korda-public-fluent", source.Provider.ToString(), true, version,
                executableHash, native.Path, native.Sha256, options.Case, caseIds, caseModes, options.Mode, options.SessionMode,
#if POSTGRES
                "READ COMMITTED, read-write; provider transaction completion awaited",
#else
                "Serializable, BEGIN IMMEDIATE; WAL, synchronous FULL, foreign keys enabled, 5-second busy timeout",
#endif
                schedule is null ? "closed-loop; next operation planned after preceding completion; no external arrival rate" : "fixed offered rate; bounded FIFO admission; explicit rejection; scheduled-to-complete latency in OfferedLoad", options.Concurrency,
                options.Warmup, options.Rows, options.BatchRows, options.Tenants, options.OrdersPerTenant,
                startedUtc, finishedUtc, MonotonicNs(state.StartedAt), MonotonicNs(endedAt), elapsed, schedule?.Planned ?? (options.Seconds > 0 ? started : options.Operations), started, success, failed, 0, rows,
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
                seconds.ToArray(), schedule?.TimeSeriesTruncated ?? state.Truncated != 0, sql, expectations, errors, schedule, caseCounts, idle,
                allocationProfile is null, allocationProfile?.CreateReceipt())
            {
                OracleFailuresBefore = beforeOracle, OracleFailuresAfter = afterOracle,
                OracleFailures = CorrectnessOracle.Failures, FirstOracleFailure = CorrectnessOracle.FirstFailure
            };
        }
        finally
        {
            foreach (var worker in workers) if (worker is not null) await worker.DisposeAsync();
        }
    }
    private static long MonotonicNs(long ticks) => checked((long)((Int128)ticks * 1_000_000_000 / Stopwatch.Frequency));
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
