using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DbPerf.Campaign;

// The harness deliberately keeps its own work in process-wide allocation/CPU
// totals. Empty-driver measurements are separate observations, never deductions.
internal static class Runner
{
    private const int HistogramBins = 8192;
    private const int DrainSeconds = 30;
    private const int OperationSeriesSeconds = 3601;
    private const long MaximumOperations = 1_000_000_000;
    private static readonly string[] ErrorNames =
    ["provider", "timeout", "canceled", "resource_limit", "conflict", "connection", "oracle_mismatch", "unexpected_outcome", "unknown"];

    internal static async Task<int> RunAsync(Config config, Func<int, Task<ICampaignWorker>> create,
        AdapterInfo info, Func<Task<JsonElement?>>? closeHook = null, Func<JsonElement>? snapshotHook = null)
    {
        Validate(config, info);
        ArgumentNullException.ThrowIfNull(create);
        if (RuntimeFeature.IsDynamicCodeSupported)
            throw new InvalidOperationException("Campaign execution requires an actual Native AOT executable.");
        string output = Path.GetFullPath(config.Output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var state = new State(config, info);
        Exception? setupError = null, closeError = null, snapshotError = null;
        JsonElement? owners = null, nativeBefore = null, nativeAfter = null;
        var workers = new Worker[config.Concurrency];
        int created = 0;
        try
        {
            for (; created < workers.Length; created++)
                workers[created] = new Worker(state, created, await create(created).ConfigureAwait(false));
        }
        catch (Exception error) { setupError = error; }

        if (setupError is null)
        {
            int started = 0;
            try
            {
                for (; started < workers.Length; started++) workers[started].Start();
            }
            catch (Exception error)
            {
                setupError = error;
                Volatile.Write(ref state.Abort, 1);
                for (int i = started; i < workers.Length; i++)
                {
                    state.Ready.Signal(); state.Done.Signal();
                }
            }
            state.ReadyHandle.WaitOne();
            foreach (var worker in workers)
                if (worker.SetupError is { } error && setupError is null) setupError = error;
        }

        ProcessSnapshot before = default, cutoff = default, drained = default, idleOpen = default, closed = default, idle = default;
        DateTimeOffset startedUtc = default, finishedUtc = default;
        FileIdentity[] identities = [];
        if (setupError is null)
        {
            try { identities = Identities(info); }
            catch (Exception error) { setupError = error; }
        }
        bool measured = setupError is null && !config.VerifyOnly;
        if (measured)
        {
            try
            {
                // Prime monitoring, counter and wait-handle paths before READY.
                _ = state.Monitor.Capture();
                _ = GC.GetTotalAllocatedBytes(true);
                _ = state.DoneHandle.WaitOne(0);
                WaitUntil(checked(Stopwatch.GetTimestamp() + SecondsTicks(.001)), null);
                Console.WriteLine("DB_PERF_PHASE READY");
                if (snapshotHook is not null) nativeBefore = snapshotHook();
                Console.WriteLine("DB_PERF_PHASE START");
                before = state.Monitor.Capture();
                startedUtc = DateTimeOffset.UtcNow;
                state.StartTimestamp = Stopwatch.GetTimestamp();
                if (config.Operations == 0)
                {
                    state.CutoffTimestamp = checked(state.StartTimestamp + SecondsTicks(config.Seconds));
                    state.DrainTimestamp = checked(state.CutoffTimestamp + SecondsTicks(DrainSeconds));
                }
                state.StartGate.Set();
                if (config.Operations > 0)
                {
                    state.DoneHandle.WaitOne();
                    long lastTerminal = state.StartTimestamp;
                    foreach (var worker in workers) lastTerminal = Math.Max(lastTerminal, worker.LastTerminalTimestamp);
                    state.CutoffTimestamp = lastTerminal;
                    state.DrainTimestamp = state.CutoffTimestamp;
                    cutoff = state.Monitor.Capture();
                    drained = cutoff;
                    Console.WriteLine("DB_PERF_PHASE END");
                }
                else
                {
                    WaitUntil(state.CutoffTimestamp, null);
                    cutoff = state.Monitor.Capture();
                    Console.WriteLine("DB_PERF_PHASE END");
                    while (!state.DoneHandle.WaitOne(0) && Stopwatch.GetTimestamp() < state.DrainTimestamp)
                    {
                        long remaining = state.DrainTimestamp - Stopwatch.GetTimestamp();
                        if (remaining > 0) state.DoneHandle.WaitOne(WaitMilliseconds(remaining));
                    }
                    if (!state.DoneHandle.WaitOne(0))
                    {
                        state.DrainDeadlineObserved = true;
                        Volatile.Write(ref state.Abort, 1);
                        state.StopGate.Set();
                        // No thread abort and no disposal of a busy owner. Adapter
                        // I/O has a finite timeout; the external watchdog records
                        // a process failure if that adapter guarantee is broken.
                        state.DoneHandle.WaitOne();
                    }
                    drained = state.Monitor.Capture();
                }
                finishedUtc = DateTimeOffset.UtcNow;
                Console.WriteLine("DB_PERF_PHASE DRAINED");
                // This hook may allocate its JSON object, so it runs only after
                // the final process-wide workload allocation counter is read.
                if (snapshotHook is not null)
                {
                    try { nativeAfter = snapshotHook(); }
                    catch (Exception error) { snapshotError = error; }
                }
                Console.WriteLine("DB_PERF_PHASE IDLE_OPEN");
                state.IdleOpenBefore = state.Monitor.Capture();
                Thread.Sleep(1000);
                idleOpen = state.Monitor.Capture();
                Console.WriteLine("DB_PERF_PHASE IDLE_OPEN_DONE");
            }
            catch (Exception error)
            {
                setupError = error;
                Volatile.Write(ref state.Abort, 1);
                state.StopGate.Set();
                state.StartGate.Set();
                state.DoneHandle.WaitOne();
                if (state.StartTimestamp != 0)
                {
                    drained = state.Monitor.Capture();
                    finishedUtc = DateTimeOffset.UtcNow;
                }
            }
        }
        else
        {
            Volatile.Write(ref state.Abort, 1);
            state.StartGate.Set();
            if (created == workers.Length && workers.Any(worker => worker.StartedThread)) state.DoneHandle.WaitOne();
        }

        state.ExitGate.Set();
        for (int i = 0; i < created; i++) workers[i].Join();
        for (int i = 0; i < created; i++)
        {
            try { await workers[i].Adapter.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { closeError ??= error; state.DisposalFailures++; }
        }
        if (closeHook is not null)
        {
            try { owners = await closeHook().ConfigureAwait(false); }
            catch (Exception error) { closeError ??= error; state.DisposalFailures++; }
        }
        if (measured && state.StartTimestamp != 0)
        {
            Console.WriteLine("DB_PERF_PHASE CLOSED");
            Console.WriteLine("DB_PERF_PHASE IDLE");
            closed = state.Monitor.Capture();
            Thread.Sleep(1000);
            idle = state.Monitor.Capture();
            Console.WriteLine("DB_PERF_PHASE IDLE_DONE");
        }
        Aggregate totals = AggregateWorkers(state, workers, created);
        bool identitiesStable = true;
        try { state.ContractAfterSha256 = HashFile(config.Manifest); }
        catch (Exception error) { snapshotError ??= error; }
        for (int i = 0; i < identities.Length; i++)
        {
            try
            {
                string hash = HashFile(identities[i].Path);
                identitiesStable &= hash == identities[i].Sha256;
                identities[i] = identities[i] with { AfterSha256 = hash };
            }
            catch (Exception error) { snapshotError ??= error; identitiesStable = false; }
        }
        bool complete = setupError is null && closeError is null && snapshotError is null && identitiesStable
            && state.ContractAfterSha256 == state.ContractSha256
            && totals.HardErrors == 0 && totals.NotStarted == 0 && totals.Unfinished == 0
            && totals.LateTerminal == 0 && totals.SeriesTruncated == 0 && !state.DrainDeadlineObserved;
        if (measured) complete &= state.Monitor.CpuAvailable;
        WriteReceipt(output, state, workers, created, totals, measured, complete, startedUtc, finishedUtc,
            before, cutoff, drained, idleOpen, closed, idle, nativeBefore, nativeAfter, owners,
            setupError, closeError, snapshotError, identities, identitiesStable);
        return complete ? 0 : 2;
    }

    private static void Validate(Config config, AdapterInfo info)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The campaign runner is for Linux x64.");
        config.ValidateArrivalSchedule();
        if (config.Concurrency is < 1 or > 128 || config.Warmup is < 0 or > 100_000
            || !double.IsFinite(config.Seconds) || config.Seconds is < 0 or > 7200
            || !double.IsFinite(config.Rate) || config.Rate is < 0 or > 1_000_000
            || config.Operations is < 0 or > MaximumOperations)
            throw new ArgumentException("Campaign limits must be finite and within the documented bounds.");
        if (!config.VerifyOnly && config.Operations == 0 && config.Seconds <= 0)
            throw new ArgumentException("A timed campaign requires positive seconds.");
        if (config.Cases.Length is < 1 or > 256 || config.Mix.Length is < 1 or > 4096)
            throw new ArgumentException("Case and mix counts exceed their finite bounds.");
        if (info.LoadedNativePaths.Length > 64) throw new ArgumentException("Too many native identities.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var query in config.Cases)
        {
            if (!ids.Add(query.Id) || query.Expected.Rows is < 0 or > MaximumOperations
                || query.Columns.Length > 4096 || query.Parameters.Length > 4096)
                throw new ArgumentException("Case identity or result bounds are invalid.");
        }
        foreach (var query in config.Mix)
            if (Array.IndexOf(config.Cases, query) < 0) throw new ArgumentException("A mix case is not in the frozen case set.");
    }

    private sealed class State : IDisposable
    {
        internal readonly Config Config;
        internal readonly AdapterInfo Info;
        internal readonly int[] MixIndices, VerificationIndices;
        internal readonly int SeriesLength;
        internal readonly long Planned;
        internal readonly long FirstPhasePlanned, RecoveryOffsetTicks;
        internal readonly PhaseMetrics[]? ArrivalPhases;
        internal readonly ManualResetEvent StartGate = new(false), StopGate = new(false), ExitGate = new(false);
        internal readonly CountdownEvent Ready, Done;
        internal readonly WaitHandle ReadyHandle, DoneHandle;
        internal readonly ProcessMonitor Monitor = new();
        internal readonly long[] Service = new long[HistogramBins], Offered = new long[HistogramBins];
        internal readonly long[] ServiceTotal = new long[HistogramBins], OfferedTotal = new long[HistogramBins];
        internal readonly CaseCount[] Cases;
        internal readonly SecondCount[] Seconds;
        internal readonly long[] Errors = new long[ErrorNames.Length];
        internal readonly string ContractSha256;
        internal string? ContractAfterSha256;
        internal readonly string ExecutablePath, ExecutableSha256;
        internal int Abort, DisposalFailures;
        internal long NextSequence = -1;
        internal bool DrainDeadlineObserved;
        internal long StartTimestamp, CutoffTimestamp = long.MaxValue, DrainTimestamp = long.MaxValue;
        internal ProcessSnapshot IdleOpenBefore;

        internal State(Config config, AdapterInfo info)
        {
            Config = config; Info = info;
            if (config.RecoveryRate is { } recoveryRate && config.RecoveryAfter is { } recoveryAfter)
            {
                FirstPhasePlanned = checked((long)Math.Floor(config.Rate * recoveryAfter));
                Planned = checked(FirstPhasePlanned + (long)Math.Floor(recoveryRate * (config.Seconds - recoveryAfter)));
                RecoveryOffsetTicks = checked((long)Math.Floor(recoveryAfter * Stopwatch.Frequency));
                if (!config.VerifyOnly) ArrivalPhases = NewPhaseMetrics();
            }
            else Planned = config.Rate > 0 ? checked((long)Math.Floor(config.Rate * config.Seconds)) : config.Operations;
            SeriesLength = config.Operations > 0 ? OperationSeriesSeconds : checked((int)Math.Ceiling(config.Seconds) + DrainSeconds + 1);
            MixIndices = config.Mix.Select(query => Array.IndexOf(config.Cases, query)).ToArray();
            VerificationIndices = MixIndices.Distinct().ToArray();
            Cases = new CaseCount[config.Cases.Length]; Seconds = new SecondCount[SeriesLength];
            Ready = new(config.Concurrency); Done = new(config.Concurrency);
            ReadyHandle = Ready.WaitHandle; DoneHandle = Done.WaitHandle;
            ContractSha256 = config.ParsedContractSha256;
            ExecutablePath = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable path.");
            ExecutableSha256 = HashFile(ExecutablePath);
        }

        public void Dispose()
        {
            Monitor.Dispose(); StartGate.Dispose(); StopGate.Dispose(); ExitGate.Dispose(); Ready.Dispose(); Done.Dispose();
        }
    }

    private sealed class Worker
    {
        private readonly State _state;
        private readonly int _id;
        private readonly Thread _thread;
        internal readonly ICampaignWorker Adapter;
        internal readonly CaseCount[] Cases;
        internal readonly SecondCount[] Seconds;
        internal readonly Result[] Verified;
        internal readonly bool[] WasVerified;
        internal readonly long[] Service = new long[HistogramBins], Offered = new long[HistogramBins];
        internal readonly long[] ServiceTotal = new long[HistogramBins], OfferedTotal = new long[HistogramBins];
        internal readonly PhaseMetrics[]? ArrivalPhases;
        internal readonly long[] Errors = new long[ErrorNames.Length];
        internal bool StartedThread;
        internal Exception? SetupError;
        internal Failure? FirstError;
        internal long LastTerminalTimestamp, AsTaskCalls, ObservedSuspensions, SeriesTruncated;
        internal long LateTerminal, LateTerminalFailures, HardErrors, MaximumLagNs;

        internal Worker(State state, int id, ICampaignWorker adapter)
        {
            _state = state; _id = id; Adapter = adapter;
            Cases = new CaseCount[state.Config.Cases.Length]; Seconds = new SecondCount[state.SeriesLength];
            if (state.ArrivalPhases is not null) ArrivalPhases = NewPhaseMetrics();
            Verified = new Result[state.Config.Cases.Length]; WasVerified = new bool[state.Config.Cases.Length];
            _thread = new Thread(Run) { IsBackground = true, Name = "dbperf-worker-" + id.ToString(CultureInfo.InvariantCulture) };
        }
        internal void Start() { _thread.Start(); StartedThread = true; }
        internal void Join() { if (StartedThread) _thread.Join(); }

        private static Result Consume(ValueTask<Result> pending)
            => pending.IsCompletedSuccessfully ? pending.Result : pending.AsTask().GetAwaiter().GetResult();

        private void Run()
        {
            try
            {
                foreach (int index in _state.VerificationIndices)
                {
                    if (Volatile.Read(ref _state.Abort) != 0) break;
                    var query = _state.Config.Cases[index];
                    var result = Consume(Adapter.ExecuteAsync(query, _id));
                    Verified[index] = result; WasVerified[index] = true;
                    if (!Matches(query, result)) throw new InvalidDataException("Oracle mismatch in " + query.Id);
                }
                if (!_state.Config.VerifyOnly)
                    for (int i = 0; i < _state.Config.Warmup && Volatile.Read(ref _state.Abort) == 0; i++)
                    {
                        long sequence = _id + (long)i * _state.Config.Concurrency;
                        var query = _state.Config.Mix[(int)(sequence % _state.Config.Mix.Length)];
                        if (!Matches(query, Consume(Adapter.ExecuteAsync(query, sequence))))
                            throw new InvalidDataException("Warmup oracle mismatch in " + query.Id);
                    }
            }
            catch (Exception error) { SetupError = error; }
            finally { _state.Ready.Signal(); }
            _state.StartGate.WaitOne();
            try
            {
                if (SetupError is null && !_state.Config.VerifyOnly && Volatile.Read(ref _state.Abort) == 0) Measure();
            }
            catch (Exception error)
            {
                HardErrors++;
                FirstError ??= new Failure(Stopwatch.GetTimestamp(), -1, -1, ErrorKind.Unknown, error, default);
                Volatile.Write(ref _state.Abort, 1); _state.StopGate.Set();
            }
            finally { _state.Done.Signal(); }
            // Keep thread startup/exit and teardown outside the measured work.
            _state.ExitGate.WaitOne();
        }

        private void Measure()
        {
            var config = _state.Config;
            long nextFixedSequence = _id;
            while (true)
            {
                if (Volatile.Read(ref _state.Abort) != 0) break;
                if (config.Rate == 0 && config.Operations == 0 && Stopwatch.GetTimestamp() >= _state.CutoffTimestamp) break;
                // Closed-loop dispatch follows one global mix sequence: a slow
                // case must not remain pinned to workers whose IDs share its
                // residue modulo concurrency. Fixed-rate workers retain their
                // exact strided arrival schedules and implicit per-worker queue.
                long sequence = config.Rate > 0 ? nextFixedSequence : Interlocked.Increment(ref _state.NextSequence);
                if (config.Rate > 0) nextFixedSequence = checked(nextFixedSequence + config.Concurrency);
                if (_state.Planned > 0 && sequence >= _state.Planned) break;
                int arrivalPhase = ArrivalPhases is not null && sequence >= _state.FirstPhasePlanned ? 1 : 0;
                var phase = ArrivalPhases?[arrivalPhase];
                long phaseCutoff = arrivalPhase == 0 && phase is not null
                    ? checked(_state.StartTimestamp + _state.RecoveryOffsetTicks) : _state.CutoffTimestamp;
                long planned = 0;
                if (config.Rate > 0)
                    planned = arrivalPhase == 1
                        ? checked(_state.StartTimestamp + _state.RecoveryOffsetTicks
                            + (long)((sequence - _state.FirstPhasePlanned) * (double)Stopwatch.Frequency / config.RecoveryRate!.Value))
                        : checked(_state.StartTimestamp + (long)(sequence * (double)Stopwatch.Frequency / config.Rate));
                if (config.Rate > 0) WaitUntil(planned, _state.StopGate);
                long started = Stopwatch.GetTimestamp();
                if (Volatile.Read(ref _state.Abort) != 0 || started >= _state.DrainTimestamp) break;
                if (config.Rate == 0 && config.Operations == 0 && started >= _state.CutoffTimestamp) break;
                if (config.Rate == 0) planned = started;
                int mix = (int)(sequence % config.Mix.Length), index = _state.MixIndices[mix];
                var query = config.Cases[index];
                ref var count = ref Cases[index];
                count.Started++;
                bool beforeCutoff = started < _state.CutoffTimestamp;
                if (beforeCutoff) count.StartedBeforeCutoff++; else count.StartedAfterCutoff++;
                bool late = config.Rate > 0 && started > planned;
                long lag = checked((long)Nanoseconds(started - planned));
                if (late) { count.Late++; MaximumLagNs = Math.Max(MaximumLagNs, lag); }
                if (phase is not null)
                {
                    phase.Count.Started++;
                    if (started < phaseCutoff) phase.Count.StartedBeforeCutoff++;
                    else phase.Count.StartedAfterCutoff++;
                    if (late) phase.Count.Late++;
                }
                int startSecond = Second(started);
                if (startSecond >= 0)
                {
                    Seconds[startSecond].Started++;
                    if (config.Rate == 0) Seconds[startSecond].Planned++;
                    if (late) Seconds[startSecond].Late++;
                }
                Result result = default;
                Exception? failure = null;
                ErrorKind? kind = null;
                try
                {
                    if (config.EmptyDriver) result = query.Expected;
                    else
                    {
                        var pending = Adapter.ExecuteAsync(query, sequence);
                        if (pending.IsCompletedSuccessfully) result = pending.Result;
                        else { AsTaskCalls++; result = pending.AsTask().GetAwaiter().GetResult(); }
                    }
                    if (result.Suspensions >= 0) ObservedSuspensions = checked(ObservedSuspensions + result.Suspensions);
                    if (!Matches(query, result)) kind = ClassifyResult(query, result);
                }
                catch (Exception error) { failure = error; kind = Classify(error); }
                long terminal = Stopwatch.GetTimestamp();
                LastTerminalTimestamp = terminal;
                int terminalSecond = Second(terminal);
                bool inWindow = terminal <= _state.CutoffTimestamp;
                bool inPhaseWindow = terminal <= phaseCutoff;
                bool outsideDrain = config.Operations == 0 && terminal > _state.DrainTimestamp;
                if (kind is { } category)
                {
                    Errors[(int)category]++;
                    if (category >= ErrorKind.OracleMismatch) HardErrors++;
                    FirstError ??= new Failure(terminal, index, sequence, category, failure, result);
                }
                if (outsideDrain)
                {
                    LateTerminal++; count.LateTerminal++;
                    if (phase is not null) phase.Count.LateTerminal++;
                    if (kind is not null) LateTerminalFailures++;
                    if (terminalSecond >= 0) Seconds[terminalSecond].LateTerminal++;
                    continue;
                }
                if (kind is not null)
                {
                    count.Failed++;
                    if (inWindow) count.FailedInWindow++;
                    if (phase is not null)
                    {
                        phase.Count.Failed++;
                        if (inPhaseWindow) phase.Count.FailedInWindow++;
                    }
                    if (terminalSecond >= 0) Seconds[terminalSecond].Failed++;
                }
                else
                {
                    count.Completed++; count.Rows = checked(count.Rows + result.Rows);
                    if (inWindow) { count.CompletedInWindow++; count.RowsInWindow = checked(count.RowsInWindow + result.Rows); }
                    else count.CompletedAfterCutoff++;
                    if (terminalSecond >= 0) { Seconds[terminalSecond].Completed++; Seconds[terminalSecond].Rows += result.Rows; }
                    ulong service = Nanoseconds(terminal - started), offered = Nanoseconds(terminal - planned);
                    ServiceTotal[HistogramIndex(service)]++; OfferedTotal[HistogramIndex(offered)]++;
                    if (inWindow) { Service[HistogramIndex(service)]++; Offered[HistogramIndex(offered)]++; }
                    if (phase is not null)
                    {
                        phase.Count.Completed++; phase.Count.Rows = checked(phase.Count.Rows + result.Rows);
                        phase.ServiceTotal[HistogramIndex(service)]++; phase.OfferedTotal[HistogramIndex(offered)]++;
                        if (inPhaseWindow)
                        {
                            phase.Count.CompletedInWindow++;
                            phase.Count.RowsInWindow = checked(phase.Count.RowsInWindow + result.Rows);
                            phase.Service[HistogramIndex(service)]++; phase.Offered[HistogramIndex(offered)]++;
                        }
                        else phase.Count.CompletedAfterCutoff++;
                    }
                }
            }
        }

        private int Second(long timestamp)
        {
            long second = (timestamp - _state.StartTimestamp) / Stopwatch.Frequency;
            if ((ulong)second < (ulong)Seconds.Length) return (int)second;
            SeriesTruncated++; return -1;
        }
    }

    private enum ErrorKind { Provider, Timeout, Canceled, ResourceLimit, Conflict, Connection, OracleMismatch, UnexpectedOutcome, Unknown }
    private readonly record struct Failure(long Timestamp, int CaseIndex, long Sequence, ErrorKind Kind, Exception? Exception, Result Actual);
    private struct CaseCount
    {
        internal long Planned, Started, StartedBeforeCutoff, StartedAfterCutoff, Completed, Failed, Late, Rows;
        internal long CompletedInWindow, FailedInWindow, RowsInWindow, CompletedAfterCutoff, LateTerminal;
        internal void Add(CaseCount value)
        {
            Planned += value.Planned; Started += value.Started; StartedBeforeCutoff += value.StartedBeforeCutoff;
            StartedAfterCutoff += value.StartedAfterCutoff; Completed += value.Completed; Failed += value.Failed;
            Late += value.Late; Rows += value.Rows; CompletedInWindow += value.CompletedInWindow;
            FailedInWindow += value.FailedInWindow; RowsInWindow += value.RowsInWindow;
            CompletedAfterCutoff += value.CompletedAfterCutoff; LateTerminal += value.LateTerminal;
        }
    }
    private struct SecondCount
    {
        internal long Planned, Started, Completed, Failed, Rows, Late, LateTerminal;
        internal void Add(SecondCount value)
        {
            Planned += value.Planned; Started += value.Started; Completed += value.Completed;
            Failed += value.Failed; Rows += value.Rows; Late += value.Late; LateTerminal += value.LateTerminal;
        }
    }
    private sealed class Aggregate
    {
        internal CaseCount Count;
        internal long NotStarted, Unfinished, HardErrors, LateTerminal, LateTerminalFailures, AsTaskCalls, Suspensions;
        internal long MaximumLagNs, SeriesTruncated, LastTerminalTimestamp;
        internal Failure? FirstError;
    }

    private sealed class PhaseMetrics
    {
        internal CaseCount Count;
        internal readonly long[] Service = new long[HistogramBins], Offered = new long[HistogramBins];
        internal readonly long[] ServiceTotal = new long[HistogramBins], OfferedTotal = new long[HistogramBins];

        internal void Add(PhaseMetrics source)
        {
            Count.Add(source.Count);
            for (int i = 0; i < HistogramBins; i++)
            {
                Service[i] += source.Service[i]; Offered[i] += source.Offered[i];
                ServiceTotal[i] += source.ServiceTotal[i]; OfferedTotal[i] += source.OfferedTotal[i];
            }
        }
    }

    private static PhaseMetrics[] NewPhaseMetrics() => [new(), new()];

    private static Aggregate AggregateWorkers(State state, Worker[] workers, int created)
    {
        var total = new Aggregate();
        for (int n = 0; n < created; n++)
        {
            var worker = workers[n];
            total.HardErrors += worker.HardErrors; total.LateTerminal += worker.LateTerminal;
            total.LateTerminalFailures += worker.LateTerminalFailures; total.AsTaskCalls += worker.AsTaskCalls;
            total.Suspensions += worker.ObservedSuspensions; total.SeriesTruncated += worker.SeriesTruncated;
            total.MaximumLagNs = Math.Max(total.MaximumLagNs, worker.MaximumLagNs);
            total.LastTerminalTimestamp = Math.Max(total.LastTerminalTimestamp, worker.LastTerminalTimestamp);
            if (worker.FirstError is { } failure && (total.FirstError is null || failure.Timestamp < total.FirstError.Value.Timestamp))
                total.FirstError = failure;
            for (int i = 0; i < state.Cases.Length; i++) state.Cases[i].Add(worker.Cases[i]);
            for (int i = 0; i < state.Seconds.Length; i++) state.Seconds[i].Add(worker.Seconds[i]);
            for (int i = 0; i < ErrorNames.Length; i++) state.Errors[i] += worker.Errors[i];
            for (int i = 0; i < HistogramBins; i++)
            {
                state.Service[i] += worker.Service[i]; state.Offered[i] += worker.Offered[i];
                state.ServiceTotal[i] += worker.ServiceTotal[i]; state.OfferedTotal[i] += worker.OfferedTotal[i];
            }
            if (state.ArrivalPhases is { } phases && worker.ArrivalPhases is { } workerPhases)
                for (int i = 0; i < phases.Length; i++) phases[i].Add(workerPhases[i]);
        }
        if (state.StartTimestamp != 0 && state.Planned > 0)
        {
            long cycles = state.Planned / state.MixIndices.Length, rest = state.Planned % state.MixIndices.Length;
            for (int i = 0; i < state.MixIndices.Length; i++) state.Cases[state.MixIndices[i]].Planned += cycles + (i < rest ? 1 : 0);
        }
        else foreach (ref var count in state.Cases.AsSpan()) count.Planned = count.Started;
        if (state.StartTimestamp != 0 && state.Config.Rate > 0)
            for (int i = 0; i < state.Seconds.Length; i++)
            {
                long first = PlannedBeforeSecond(state, i);
                long last = PlannedBeforeSecond(state, i + 1d);
                state.Seconds[i].Planned = Math.Max(0, last - first);
            }
        foreach (var count in state.Cases) total.Count.Add(count);
        total.NotStarted = total.Count.Planned - total.Count.Started;
        total.Unfinished = total.Count.Started - total.Count.Completed - total.Count.Failed;
        if (state.Service.Sum() != total.Count.CompletedInWindow || state.Offered.Sum() != total.Count.CompletedInWindow
            || state.ServiceTotal.Sum() != total.Count.Completed || state.OfferedTotal.Sum() != total.Count.Completed)
            total.HardErrors++;
        if (state.ArrivalPhases is { } arrivalPhases)
        {
            if (state.StartTimestamp != 0)
            {
                arrivalPhases[0].Count.Planned = state.FirstPhasePlanned;
                arrivalPhases[1].Count.Planned = state.Planned - state.FirstPhasePlanned;
            }
            CaseCount combined = default;
            foreach (var phase in arrivalPhases)
            {
                combined.Add(phase.Count);
                if (phase.Service.Sum() != phase.Count.CompletedInWindow || phase.Offered.Sum() != phase.Count.CompletedInWindow
                    || phase.ServiceTotal.Sum() != phase.Count.Completed || phase.OfferedTotal.Sum() != phase.Count.Completed)
                    total.HardErrors++;
            }
            if (combined.Planned != total.Count.Planned || combined.Started != total.Count.Started
                || combined.Completed != total.Count.Completed || combined.Failed != total.Count.Failed
                || combined.Rows != total.Count.Rows || combined.Late != total.Count.Late
                || combined.LateTerminal != total.Count.LateTerminal
                || state.Seconds.Sum(second => second.Planned) != total.Count.Planned)
                total.HardErrors++;
        }
        return total;
    }

    private static long PlannedBeforeSecond(State state, double second)
    {
        if (state.Config.RecoveryRate is { } recoveryRate && state.Config.RecoveryAfter is { } recoveryAfter)
            return checked(ClampedArrivals(second, state.Config.Rate, state.FirstPhasePlanned)
                + ClampedArrivals(second - recoveryAfter, recoveryRate, state.Planned - state.FirstPhasePlanned));
        return ClampedArrivals(second, state.Config.Rate, state.Planned);
    }

    private static long ClampedArrivals(double elapsedSeconds, double rate, long count)
        => Math.Clamp(checked((long)Math.Ceiling(elapsedSeconds * rate)), 0, count);

    private static bool Matches(Case query, Result result) => result.Suspensions >= 0 && result.Rows == query.Expected.Rows
        && result.Digest == query.Expected.Digest && string.Equals(result.Outcome, query.Expected.Outcome, StringComparison.Ordinal);
    private static ErrorKind ClassifyResult(Case query, Result result)
    {
        if (string.Equals(result.Outcome, query.Expected.Outcome, StringComparison.Ordinal)) return ErrorKind.OracleMismatch;
        return result.Outcome switch
        {
            "provider_error" => ErrorKind.Provider,
            "timeout" => ErrorKind.Timeout,
            "canceled" or "cancelled" => ErrorKind.Canceled,
            "resource_limit" or "rejected" => ErrorKind.ResourceLimit,
            "conflict" => ErrorKind.Conflict,
            "connection" or "connection_error" => ErrorKind.Connection,
            "invalid_result" or "oracle_mismatch" => ErrorKind.OracleMismatch,
            _ => ErrorKind.UnexpectedOutcome,
        };
    }
    private static ErrorKind Classify(Exception error) => error switch
    {
        CampaignException campaign => campaign.Category switch
        {
            "provider" or "provider_error" or "constraint" or "database" => ErrorKind.Provider,
            "timeout" => ErrorKind.Timeout,
            "canceled" or "cancelled" => ErrorKind.Canceled,
            "resource_limit" or "rejected" or "resource" => ErrorKind.ResourceLimit,
            "conflict" => ErrorKind.Conflict,
            "connection" or "connection_error" => ErrorKind.Connection,
            "invalid_result" or "oracle_mismatch" => ErrorKind.OracleMismatch,
            _ => ErrorKind.Unknown,
        },
        TimeoutException => ErrorKind.Timeout,
        OperationCanceledException => ErrorKind.Canceled,
        DbException => ErrorKind.Provider,
        InvalidDataException => ErrorKind.OracleMismatch,
        _ => ErrorKind.Unknown,
    };

    private static long SecondsTicks(double seconds) => checked((long)Math.Ceiling(seconds * Stopwatch.Frequency));
    private static int WaitMilliseconds(long ticks) => (int)Math.Clamp(Math.Ceiling(ticks * 1000d / Stopwatch.Frequency), 1, int.MaxValue);
    private static void WaitUntil(long target, WaitHandle? stop)
    {
        while (true)
        {
            if (stop is not null && stop.WaitOne(0)) return;
            if (GetMonotonicTime(1, out var deadline) != 0)
                throw new IOException("clock_gettime(CLOCK_MONOTONIC) failed: " + Marshal.GetLastPInvokeError());
            long remaining = target - Stopwatch.GetTimestamp();
            if (remaining <= 0) return;
            // Map the remaining Stopwatch interval onto CLOCK_MONOTONIC; do
            // not assume that their epochs match. Interruptible worker waits
            // use at most 50 ms slices, so abort does not wait for a distant
            // arrival. There is no spinning for the final sub-millisecond tail.
            if (stop is not null) remaining = Math.Min(remaining, SecondsTicks(.05));
            ulong nanoseconds = Nanoseconds(remaining);
            deadline.Seconds = checked(deadline.Seconds + (long)(nanoseconds / 1_000_000_000));
            deadline.Nanoseconds += (long)(nanoseconds % 1_000_000_000);
            if (deadline.Nanoseconds >= 1_000_000_000)
            {
                deadline.Seconds++;
                deadline.Nanoseconds -= 1_000_000_000;
            }
            int error;
            do { error = SleepUntilMonotonic(1, 1, in deadline, IntPtr.Zero); }
            while (error == 4); // EINTR: absolute deadline remains unchanged.
            if (error != 0) throw new IOException("clock_nanosleep(CLOCK_MONOTONIC, TIMER_ABSTIME) failed: " + error);
        }
    }
    private static ulong Nanoseconds(long ticks)
    {
        if (ticks <= 0) return 1;
        var value = (UInt128)(ulong)ticks * 1_000_000_000u / (ulong)Stopwatch.Frequency;
        return value == 0 ? 1 : checked((ulong)value);
    }
    private static int HistogramIndex(ulong value)
    {
        value = Math.Max(1, value);
        int exponent = BitOperations.Log2(value);
        ulong lower = 1UL << exponent;
        int fraction = (int)(((UInt128)(value - lower) * 128u) / lower);
        return exponent * 128 + fraction;
    }
    private static double? Percentile(long[] histogram, double quantile)
    {
        long count = histogram.Sum();
        if (count == 0) return null;
        long target = checked((long)Math.Ceiling(count * quantile)), accumulated = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            accumulated += histogram[i];
            if (accumulated >= target)
                return Math.ScaleB(1d, i / 128) * (1d + (i % 128 + 1) / 128d) / 1000d;
        }
        throw new InvalidOperationException("Histogram count differs.");
    }

    private readonly record struct FileIdentity(string Path, string Sha256, bool ObservedInMaps, string? AfterSha256 = null);
    private static FileIdentity[] Identities(AdapterInfo info)
    {
        string maps = File.ReadAllText("/proc/self/maps");
        var result = new FileIdentity[info.LoadedNativePaths.Length];
        for (int i = 0; i < result.Length; i++)
        {
            string path = Path.GetFullPath(info.LoadedNativePaths[i]);
            result[i] = new(path, HashFile(path), maps.Contains(path, StringComparison.Ordinal));
        }
        return result;
    }
    private static string HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeSpec { internal long Seconds, Nanoseconds; }
    [DllImport("libc", EntryPoint = "clock_gettime", SetLastError = true)]
    private static extern int GetMonotonicTime(int clock, out TimeSpec time);
    [DllImport("libc", EntryPoint = "clock_nanosleep")]
    private static extern int SleepUntilMonotonic(int clock, int flags, in TimeSpec deadline, IntPtr remaining);

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeValue { internal long Seconds, Microseconds; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Usage
    {
        internal TimeValue User, System;
        internal long MaxRss, SharedMemory, UnsharedData, UnsharedStack, MinorFaults, MajorFaults, Swaps;
        internal long InputBlocks, OutputBlocks, MessagesSent, MessagesReceived, Signals, VoluntarySwitches, InvoluntarySwitches;
    }
    [DllImport("libc", EntryPoint = "getrusage", SetLastError = true)]
    private static extern int GetUsage(int who, out Usage usage);

    private readonly record struct ProcessSnapshot(long Timestamp, long AllocatedBytes, int Gen0, int Gen1, int Gen2,
        long UserMicroseconds, long SystemMicroseconds, long RssBytes, long PssBytes, long PeakRssBytes);
    private sealed class ProcessMonitor : IDisposable
    {
        private readonly SafeFileHandle? _smaps;
        private readonly byte[] _buffer = new byte[8192];
        internal bool CpuAvailable = true;
        internal int ResourceReadFailures;
        internal ProcessMonitor()
        {
            try { _smaps = File.OpenHandle("/proc/self/smaps_rollup", FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        internal ProcessSnapshot Capture()
        {
            long user = -1, system = -1, peak = -1, rss = -1, pss = -1;
            if (GetUsage(0, out var usage) == 0)
            {
                user = checked(usage.User.Seconds * 1_000_000 + usage.User.Microseconds);
                system = checked(usage.System.Seconds * 1_000_000 + usage.System.Microseconds);
                peak = checked(usage.MaxRss * 1024);
            }
            else { CpuAvailable = false; ResourceReadFailures++; }
            if (_smaps is not null)
            {
                try
                {
                    int length = RandomAccess.Read(_smaps, _buffer, 0);
                    var data = _buffer.AsSpan(0, length);
                    while (!data.IsEmpty)
                    {
                        int end = data.IndexOf((byte)'\n');
                        var line = end < 0 ? data : data[..end];
                        if (line.StartsWith("Rss:"u8)) rss = Kilobytes(line[4..]);
                        else if (line.StartsWith("Pss:"u8)) pss = Kilobytes(line[4..]);
                        if (end < 0) break;
                        data = data[(end + 1)..];
                    }
                }
                catch (IOException) { ResourceReadFailures++; }
            }
            int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
            long timestamp = Stopwatch.GetTimestamp();
            long bytes = GC.GetTotalAllocatedBytes(true);
            return new(timestamp, bytes, gen0, gen1, gen2, user, system, rss, pss, peak);
        }
        private static long Kilobytes(ReadOnlySpan<byte> value)
        {
            int index = 0;
            while (index < value.Length && value[index] == (byte)' ') index++;
            long result = 0;
            while (index < value.Length && value[index] is >= (byte)'0' and <= (byte)'9')
                result = checked(result * 10 + value[index++] - (byte)'0');
            return checked(result * 1024);
        }
        public void Dispose() => _smaps?.Dispose();
    }

    private static void WriteReceipt(string output, State state, Worker[] workers, int created, Aggregate totals,
        bool measured, bool complete, DateTimeOffset startedUtc, DateTimeOffset finishedUtc,
        ProcessSnapshot before, ProcessSnapshot cutoff, ProcessSnapshot drained, ProcessSnapshot idleOpen,
        ProcessSnapshot closed, ProcessSnapshot idle, JsonElement? nativeBefore, JsonElement? nativeAfter,
        JsonElement? owners, Exception? setupError, Exception? closeError, Exception? snapshotError,
        FileIdentity[] identities, bool identitiesStable)
    {
        var config = state.Config;
        double windowSeconds = state.StartTimestamp == 0 ? 0 : Math.Max(0, (state.CutoffTimestamp - state.StartTimestamp) / (double)Stopwatch.Frequency);
        if (state.CutoffTimestamp == long.MaxValue) windowSeconds = 0;
        double actualOverrun = config.Operations > 0 || totals.LastTerminalTimestamp == 0 ? 0
            : Math.Max(0, (totals.LastTerminalTimestamp - state.DrainTimestamp) / (double)Stopwatch.Frequency);
        using var file = File.Create(output);
        using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteNumber("schema_version", 1); json.WriteString("task", "DB-Perf-01");
        json.WriteString("route", state.Info.Route); json.WriteString("provider", state.Info.Provider);
        json.WriteString("mode", config.Mode); json.WriteString("session_mode", config.SessionMode);
        json.WriteString("contract_sha256", state.ContractSha256);
        json.WriteString("contract_after_sha256", state.ContractAfterSha256);
        json.WriteBoolean("contract_hash_stable", state.ContractAfterSha256 == state.ContractSha256);
        json.WriteString("contract_hash_scope", "SHA-256 of the actual bytes parsed into Config; manifest is read again after retirement and idle observation.");
        json.WriteString("status", !complete ? "failed" : config.VerifyOnly ? "verified" : totals.Count.Failed > 0 ? "completed_with_failures" : "completed");
        json.WriteBoolean("run_complete", complete); json.WriteBoolean("slo_evaluated", false);
        json.WriteBoolean("can_qualify", complete && measured && state.StartTimestamp != 0
            && config.Operations == 0 && config.Seconds >= 1800 && !config.EmptyDriver);
        json.WriteString("qualification_scope", "Long-run eligibility only. Complete workload coverage, isolation, resource identity and SLO acceptance require external validation.");
        json.WriteBoolean("native_aot", !RuntimeFeature.IsDynamicCodeSupported); json.WriteNumber("process_id", Environment.ProcessId);
        json.WriteString("runtime", RuntimeInformation.FrameworkDescription); json.WriteString("architecture", RuntimeInformation.ProcessArchitecture.ToString());
        json.WriteString("package_version", state.Info.PackageVersion);
        json.WriteString("preparation_policy", state.Info.PreparationPolicy); json.WriteString("session_policy", state.Info.SessionPolicy);
        json.WriteString("executable", state.ExecutablePath); json.WriteString("executable_sha256", state.ExecutableSha256);
        json.WriteBoolean("library_hashes_stable", identitiesStable);
        json.WriteStartArray("native_libraries");
        foreach (var identity in identities)
        {
            json.WriteStartObject(); json.WriteString("path", identity.Path); json.WriteString("sha256", identity.Sha256);
            json.WriteString("after_sha256", identity.AfterSha256); json.WriteBoolean("observed_in_maps", identity.ObservedInMaps); json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteBoolean("measurement_started", measured && state.StartTimestamp != 0); json.WriteBoolean("verify_only", config.VerifyOnly);
        json.WriteBoolean("empty_driver", config.EmptyDriver); json.WriteNumber("concurrency", config.Concurrency);
        json.WriteNumber("warmup_operations_per_worker", config.VerifyOnly ? 0 : config.Warmup);
        json.WriteNumber("configured_seconds", config.Seconds); json.WriteBoolean("seconds_active", config.Operations == 0);
        json.WriteNumber("configured_operations", config.Operations); json.WriteNumber("rate", config.Rate);
        WriteNumber(json, "requested_recovery_rate", config.RecoveryRate);
        WriteNumber(json, "requested_recovery_after_seconds", config.RecoveryAfter);
        json.WriteString("schedule", config.Rate > 0 ? "fixed-rate global sequence worker_id+k*concurrency" : "closed-loop shared atomic sequence, dispatched to the next available worker");
        json.WriteString("wait_policy", "CLOCK_MONOTONIC clock_gettime plus remaining Stopwatch interval, absolute clock_nanosleep with EINTR retry; interruptible worker waits use at most 50 ms slices, without busy spinning.");
        json.WriteString("phase_protocol", "DB_PERF_PHASE READY: verification/warmup complete; START: before workload counters/start; END: after cutoff counters; DRAINED: after admitted APIs retire and final workload counters; IDLE_OPEN: before 1 s open-owner idle; IDLE_OPEN_DONE: after open-owner idle counters and before owner disposal; CLOSED: owners disposed; IDLE: before 1 s closed-owner idle; IDLE_DONE: after idle counters.");
        json.WriteString("queue_model", "Implicit bounded per-worker ordered sequence; no request objects; unstarted requests remain accounted at drain cutoff.");
        json.WriteString("late_definition", "actual start strictly later than planned arrival; no rounding threshold");
        json.WriteNumber("max_schedule_lag_ns", totals.MaximumLagNs);
        if (state.StartTimestamp != 0) { json.WriteString("started_utc", startedUtc); json.WriteString("finished_utc", finishedUtc); }
        json.WriteNumber("timer_frequency", Stopwatch.Frequency); json.WriteNumber("start_timestamp", state.StartTimestamp);
        json.WriteNumber("cutoff_timestamp", state.CutoffTimestamp == long.MaxValue ? 0 : state.CutoffTimestamp);
        json.WriteNumber("drain_deadline_timestamp", state.DrainTimestamp == long.MaxValue ? 0 : state.DrainTimestamp);
        json.WriteNumber("window_seconds", windowSeconds); json.WriteNumber("drain_limit_seconds", DrainSeconds);
        json.WriteNumber("actual_drain_overrun_seconds", actualOverrun);
        json.WriteBoolean("drain_deadline_observed", state.DrainDeadlineObserved);
        json.WriteString("deadline_policy", "Adapters enforce finite command/I/O deadlines. No unsafe thread abort. Late API terminals are excluded from successful completion counts, retained as late_terminal, and safely retired before owner disposal; external watchdog is separate.");
        WriteCounts(json, "counts", totals.Count);
        json.WriteNumber("unfinished_at_drain", totals.Unfinished); json.WriteNumber("late_terminal", totals.LateTerminal);
        json.WriteNumber("late_terminal_failures", totals.LateTerminalFailures); json.WriteNumber("hard_errors", totals.HardErrors);
        if (measured && state.StartTimestamp != 0)
        {
            json.WriteNumber("successful_ops_per_second", windowSeconds > 0 ? totals.Count.CompletedInWindow / windowSeconds : 0);
            json.WriteNumber("successful_rows_per_second", windowSeconds > 0 ? totals.Count.RowsInWindow / windowSeconds : 0);
        }
        json.WriteNumber("harness_as_task_calls", totals.AsTaskCalls); json.WriteNumber("adapter_reported_suspensions", totals.Suspensions);
        json.WriteString("as_task_scope", "Only incomplete/failed ValueTask results use AsTask for safe blocking. IValueTaskSource-backed values may allocate a conversion task. Harness/adapter/runtime work remains in total allocation; it is never estimated away.");
        if (measured && state.StartTimestamp != 0)
        {
            WriteLatency(json, "latency", state.Service, state.Offered);
            WriteLatency(json, "latency_total", state.ServiceTotal, state.OfferedTotal);
            json.WriteString("latency_scope", "latency: successful terminals within the scheduled window; latency_total: successful terminals through the drain deadline. Service includes actual execution/result consumption and oracle comparison; offered includes queue delay.");
        }
        json.WriteStartArray("arrival_phases");
        if (measured && state.StartTimestamp != 0 && state.ArrivalPhases is { } arrivalPhases)
            for (int i = 0; i < arrivalPhases.Length; i++)
            {
                var phase = arrivalPhases[i];
                double start = i == 0 ? 0 : config.RecoveryAfter!.Value;
                double end = i == 0 ? config.RecoveryAfter!.Value : config.Seconds;
                json.WriteStartObject(); json.WriteString("name", i == 0 ? "saturation" : "recovery");
                json.WriteNumber("start_offset_seconds", start); json.WriteNumber("end_offset_seconds", end);
                json.WriteNumber("rate", i == 0 ? config.Rate : config.RecoveryRate!.Value);
                WriteCounts(json, "counts", phase.Count);
                WriteLatency(json, "latency", phase.Service, phase.Offered);
                WriteLatency(json, "latency_total", phase.ServiceTotal, phase.OfferedTotal);
                json.WriteNumber("successful_ops_per_second", phase.Count.CompletedInWindow / (end - start));
                json.WriteNumber("successful_rows_per_second", phase.Count.RowsInWindow / (end - start));
                json.WriteEndObject();
            }
        json.WriteEndArray();
        json.WriteString("arrival_phase_scope", "Optional fixed-rate recovery keeps the same process, workers, connections/pool and queue. Requests belong to their planned arrival phase. Each phase's cutoff counters/latency use that phase's end; latency_total/completed retain its successful requests through the global drain deadline, including earlier-phase backlog. Not-started/unfinished use the global drain deadline. No reset, cancellation or drain at the phase transition; by_second shows actual terminal time across both phases. Overall window metrics remain unchanged.");
        json.WriteStartArray("by_case");
        for (int i = 0; i < state.Cases.Length; i++)
        {
            json.WriteStartObject(); json.WriteString("id", config.Cases[i].Id); json.WriteString("name", config.Cases[i].Name);
            json.WriteBoolean("selected", Array.IndexOf(state.MixIndices, i) >= 0);
            WriteCountFields(json, state.Cases[i]); json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("by_second");
        int used = state.StartTimestamp == 0 ? 0 : Math.Min(state.Seconds.Length,
            checked((int)Math.Ceiling(Math.Max(windowSeconds, Math.Max(0, (totals.LastTerminalTimestamp - state.StartTimestamp) / (double)Stopwatch.Frequency)))) + 1);
        for (int i = 0; i < used; i++)
        {
            var value = state.Seconds[i]; json.WriteStartObject(); json.WriteNumber("second", i);
            json.WriteNumber("planned", value.Planned); json.WriteNumber("started", value.Started);
            json.WriteNumber("completed", value.Completed); json.WriteNumber("failed", value.Failed);
            json.WriteNumber("rows", value.Rows); json.WriteNumber("late", value.Late); json.WriteNumber("late_terminal", value.LateTerminal); json.WriteEndObject();
        }
        json.WriteEndArray(); json.WriteNumber("by_second_capacity", state.Seconds.Length);
        json.WriteNumber("by_second_truncated_events", totals.SeriesTruncated);
        json.WriteStartObject("errors");
        for (int i = 0; i < ErrorNames.Length; i++) json.WriteNumber(ErrorNames[i], state.Errors[i]);
        json.WriteEndObject();
        json.WriteStartObject("errors_normalized");
        json.WriteNumber("connection", state.Errors[(int)ErrorKind.Connection]);
        json.WriteNumber("database", state.Errors[(int)ErrorKind.Provider] + state.Errors[(int)ErrorKind.Conflict]);
        json.WriteNumber("validation", state.Errors[(int)ErrorKind.OracleMismatch] + state.Errors[(int)ErrorKind.UnexpectedOutcome]);
        json.WriteNumber("timeout", state.Errors[(int)ErrorKind.Timeout]);
        json.WriteNumber("cancel", state.Errors[(int)ErrorKind.Canceled]);
        json.WriteNumber("resource", state.Errors[(int)ErrorKind.ResourceLimit]);
        json.WriteNumber("unknown", state.Errors[(int)ErrorKind.Unknown]);
        json.WriteEndObject();
        json.WritePropertyName("first_error");
        if (totals.FirstError is { } first)
        {
            json.WriteStartObject(); json.WriteNumber("timestamp", first.Timestamp); json.WriteNumber("sequence", first.Sequence);
            json.WriteString("category", ErrorNames[(int)first.Kind]);
            if (first.CaseIndex >= 0) json.WriteString("case_id", config.Cases[first.CaseIndex].Id);
            json.WriteNumber("actual_rows", first.Actual.Rows); json.WriteString("actual_digest", first.Actual.Digest.ToString(CultureInfo.InvariantCulture));
            json.WriteString("actual_outcome", first.Actual.Outcome); json.WriteNumber("actual_suspensions", first.Actual.Suspensions);
            WriteException(json, "exception", first.Exception); json.WriteEndObject();
        }
        else json.WriteNullValue();
        WriteException(json, "setup_error", setupError); WriteException(json, "close_hook_or_disposal_error", closeError);
        WriteException(json, "snapshot_error", snapshotError); json.WriteNumber("disposal_failures", state.DisposalFailures);
        WriteVerification(json, state, workers, created);
        if (measured && state.StartTimestamp != 0)
        {
            WriteProcess(json, "process_before", before); WriteProcess(json, "process_at_cutoff", cutoff);
            WriteProcess(json, "process_after_drain", drained); WriteProcess(json, "process_before_idle_open", state.IdleOpenBefore);
            WriteProcess(json, "process_after_idle_open", idleOpen); WriteProcess(json, "process_after_close", closed);
            WriteProcess(json, "process_after_idle", idle);
            json.WriteStartObject("cpu");
            WriteCpu(json, "window", before, cutoff, totals.Count.CompletedInWindow);
            WriteCpu(json, "drain", cutoff, drained, totals.Count.CompletedAfterCutoff);
            WriteCpu(json, "idle_open", state.IdleOpenBefore, idleOpen, 0);
            WriteCpu(json, "close", idleOpen, closed, 0); WriteCpu(json, "idle", closed, idle, 0);
            json.WriteNumber("idle_open_seconds", Elapsed(state.IdleOpenBefore, idleOpen));
            json.WriteNumber("idle_seconds", Elapsed(closed, idle)); json.WriteEndObject();
            json.WriteStartObject("managed_allocations");
            WriteAllocation(json, "window", before, cutoff, totals.Count.CompletedInWindow, totals.Count.RowsInWindow);
            WriteAllocation(json, "drain", cutoff, drained, totals.Count.CompletedAfterCutoff, totals.Count.Rows - totals.Count.RowsInWindow);
            WriteAllocation(json, "workload_total", before, drained, totals.Count.Completed, totals.Count.Rows);
            WriteAllocation(json, "idle_open", state.IdleOpenBefore, idleOpen, 0, 0);
            WriteAllocation(json, "close", idleOpen, closed, 0, 0); WriteAllocation(json, "idle", closed, idle, 0, 0);
            json.WriteString("scope", "Whole-process GC.GetTotalAllocatedBytes(true), including callbacks, worker threads, adapter and harness. Raw cumulative start/end precede division. No control subtraction. Setup/warmup/oracle/hash/JSON/snapshotHook allocations are outside workload_total. Cutoff-only ratios can include partly completed operations; workload_total includes drain and any END phase-marker work between the cutoff and drain counters.");
            json.WriteString("framework_attribution", "unproved; no estimated subtraction");
            json.WriteNull("framework_allocated_bytes"); json.WriteEndObject();
        }
        json.WriteBoolean("cpu_available", state.Monitor.CpuAvailable); json.WriteNumber("resource_read_failures", state.Monitor.ResourceReadFailures);
        json.WriteString("memory_scope", "RSS/PSS are process snapshots from smaps_rollup. ru_maxrss is the lifetime process peak, including setup/warmup. Native heap peak is unavailable here; adapter-specific allocator/engine counters may appear in owner_retirement. External /proc series is separate.");
        WriteElement(json, "native_before", nativeBefore); WriteElement(json, "native_after", nativeAfter);
        json.WriteString("native_counter_boundary", "before: after real verification/warmup, before workload GC start; after: after all admitted APIs retire and workload GC end, before idle_open/disposal. Includes drain and any explicitly invalid late retirement, not just in-window completions.");
        WriteElement(json, "owner_retirement", owners);
        json.WriteEndObject();
    }

    private static void WriteCounts(Utf8JsonWriter json, string name, CaseCount value)
    { json.WriteStartObject(name); WriteCountFields(json, value); json.WriteEndObject(); }
    private static void WriteCountFields(Utf8JsonWriter json, CaseCount value)
    {
        json.WriteNumber("planned", value.Planned); json.WriteNumber("started", value.Started);
        json.WriteNumber("completed", value.Completed); json.WriteNumber("failed", value.Failed); json.WriteNumber("retries", 0);
        json.WriteNumber("late", value.Late); json.WriteNumber("queued_at_cutoff", value.Planned - value.StartedBeforeCutoff);
        json.WriteNumber("in_flight_at_cutoff", value.StartedBeforeCutoff - value.CompletedInWindow - value.FailedInWindow);
        json.WriteNumber("started_after_cutoff", value.StartedAfterCutoff); json.WriteNumber("completed_after_cutoff", value.CompletedAfterCutoff);
        json.WriteNumber("failed_in_window", value.FailedInWindow); json.WriteNumber("failed_after_cutoff", value.Failed - value.FailedInWindow);
        json.WriteNumber("not_started", value.Planned - value.Started); json.WriteNumber("unfinished", value.Started - value.Completed - value.Failed);
        json.WriteNumber("completed_in_window", value.CompletedInWindow); json.WriteNumber("rows_in_window", value.RowsInWindow);
        json.WriteNumber("total_rows", value.Rows); json.WriteNumber("late_terminal", value.LateTerminal);
    }
    private static void WriteLatency(Utf8JsonWriter json, string name, long[] service, long[] offered)
    {
        json.WriteStartObject(name); WriteHistogram(json, "service", service); WriteHistogram(json, "offered", offered); json.WriteEndObject();
    }
    private static void WriteHistogram(Utf8JsonWriter json, string name, long[] histogram)
    {
        json.WriteStartObject(name); json.WriteNumber("samples", histogram.Sum());
        WriteNumber(json, "p50_us_upper", Percentile(histogram, .50)); WriteNumber(json, "p95_us_upper", Percentile(histogram, .95));
        WriteNumber(json, "p99_us_upper", Percentile(histogram, .99)); json.WriteStartArray("histogram_ns");
        foreach (long count in histogram) json.WriteNumberValue(count);
        json.WriteEndArray(); json.WriteEndObject();
    }
    private static void WriteProcess(Utf8JsonWriter json, string name, ProcessSnapshot value)
    {
        json.WriteStartObject(name); json.WriteNumber("timestamp", value.Timestamp); json.WriteNumber("allocated_bytes", value.AllocatedBytes);
        json.WriteNumber("gc0", value.Gen0); json.WriteNumber("gc1", value.Gen1); json.WriteNumber("gc2", value.Gen2);
        WriteNumber(json, "user_seconds", value.UserMicroseconds >= 0 ? value.UserMicroseconds / 1_000_000d : null);
        WriteNumber(json, "system_seconds", value.SystemMicroseconds >= 0 ? value.SystemMicroseconds / 1_000_000d : null);
        WriteOptionalLong(json, "rss_bytes", value.RssBytes); WriteOptionalLong(json, "pss_bytes", value.PssBytes);
        WriteOptionalLong(json, "peak_rss_bytes", value.PeakRssBytes); json.WriteEndObject();
    }
    private static void WriteCpu(Utf8JsonWriter json, string name, ProcessSnapshot before, ProcessSnapshot after, long successes)
    {
        json.WriteStartObject(name);
        double? user = before.UserMicroseconds >= 0 && after.UserMicroseconds >= 0 ? (after.UserMicroseconds - before.UserMicroseconds) / 1_000_000d : null;
        double? system = before.SystemMicroseconds >= 0 && after.SystemMicroseconds >= 0 ? (after.SystemMicroseconds - before.SystemMicroseconds) / 1_000_000d : null;
        WriteNumber(json, "user_seconds", user); WriteNumber(json, "system_seconds", system);
        WriteNumber(json, "cpu_seconds_per_success", successes > 0 ? (user + system) / successes : null); json.WriteEndObject();
    }
    private static void WriteAllocation(Utf8JsonWriter json, string name, ProcessSnapshot before, ProcessSnapshot after, long operations, long rows)
    {
        long bytes = after.AllocatedBytes - before.AllocatedBytes;
        json.WriteStartObject(name); json.WriteNumber("allocated_before", before.AllocatedBytes); json.WriteNumber("allocated_after", after.AllocatedBytes);
        json.WriteNumber("allocated_bytes_total", bytes); json.WriteNumber("successful_operations", operations); json.WriteNumber("successful_rows", rows);
        json.WriteNumber("gc0_before", before.Gen0); json.WriteNumber("gc0_after", after.Gen0); json.WriteNumber("gc0_delta", after.Gen0 - before.Gen0);
        json.WriteNumber("gc1_before", before.Gen1); json.WriteNumber("gc1_after", after.Gen1); json.WriteNumber("gc1_delta", after.Gen1 - before.Gen1);
        json.WriteNumber("gc2_before", before.Gen2); json.WriteNumber("gc2_after", after.Gen2); json.WriteNumber("gc2_delta", after.Gen2 - before.Gen2);
        WriteNumber(json, "total_bytes_per_success", operations > 0 ? bytes / (double)operations : null);
        WriteNumber(json, "total_bytes_per_row", rows > 0 ? bytes / (double)rows : null); json.WriteEndObject();
    }
    private static void WriteVerification(Utf8JsonWriter json, State state, Worker[] workers, int created)
    {
        json.WriteStartArray("verification");
        for (int worker = 0; worker < created; worker++)
            foreach (int index in state.VerificationIndices)
            {
                var query = state.Config.Cases[index]; var actual = workers[worker].Verified[index];
                json.WriteStartObject(); json.WriteNumber("worker", worker); json.WriteString("case_id", query.Id);
                json.WriteBoolean("executed", workers[worker].WasVerified[index]);
                json.WriteNumber("expected_rows", query.Expected.Rows); json.WriteString("expected_digest", query.Expected.Digest.ToString(CultureInfo.InvariantCulture));
                json.WriteString("expected_outcome", query.Expected.Outcome);
                json.WriteNumber("actual_rows", actual.Rows); json.WriteString("actual_digest", actual.Digest.ToString(CultureInfo.InvariantCulture));
                json.WriteString("actual_outcome", actual.Outcome); json.WriteBoolean("matched", workers[worker].WasVerified[index] && Matches(query, actual));
                json.WriteEndObject();
            }
        json.WriteEndArray();
    }
    private static double Elapsed(ProcessSnapshot before, ProcessSnapshot after) => (after.Timestamp - before.Timestamp) / (double)Stopwatch.Frequency;
    private static void WriteNumber(Utf8JsonWriter json, string name, double? value)
    { if (value is { } number && double.IsFinite(number)) json.WriteNumber(name, number); else json.WriteNull(name); }
    private static void WriteOptionalLong(Utf8JsonWriter json, string name, long value)
    { if (value >= 0) json.WriteNumber(name, value); else json.WriteNull(name); }
    private static void WriteElement(Utf8JsonWriter json, string name, JsonElement? value)
    { json.WritePropertyName(name); if (value is { } element) element.WriteTo(json); else json.WriteNullValue(); }
    private static void WriteException(Utf8JsonWriter json, string name, Exception? error, int depth = 0)
    {
        json.WritePropertyName(name);
        if (error is null) { json.WriteNullValue(); return; }
        json.WriteStartObject(); json.WriteString("type", error.GetType().FullName);
        string message = error.Message; json.WriteString("message", message.Length <= 4096 ? message : message[..4096]);
        json.WriteBoolean("message_truncated", message.Length > 4096);
        string? stack = error.StackTrace;
        json.WriteString("stack", stack is { Length: > 16384 } ? stack[..16384] : stack);
        json.WriteBoolean("stack_truncated", stack is { Length: > 16384 });
        if (error is CampaignException campaign)
        {
            json.WriteString("campaign_category", campaign.Category); json.WriteString("provider_code", campaign.ProviderCode);
            if (depth < 8) WriteException(json, "original_exception", campaign.InnerException, depth + 1);
            else json.WriteBoolean("inner_exception_depth_truncated", campaign.InnerException is not null);
        }
        else
        {
            if (error is DbException provider) json.WriteNumber("provider_error_code", provider.ErrorCode);
            if (error is AggregateException aggregate && depth < 8)
            {
                json.WriteStartArray("inner_exceptions");
                int count = Math.Min(aggregate.InnerExceptions.Count, 8);
                for (int i = 0; i < count; i++)
                {
                    json.WriteStartObject();
                    WriteException(json, "exception", aggregate.InnerExceptions[i], depth + 1);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteBoolean("inner_exceptions_truncated", aggregate.InnerExceptions.Count > count);
            }
            else if (error.InnerException is not null)
            {
                if (depth < 8) WriteException(json, "inner_exception", error.InnerException, depth + 1);
                else json.WriteBoolean("inner_exception_depth_truncated", true);
            }
        }
        json.WriteEndObject();
    }
}
