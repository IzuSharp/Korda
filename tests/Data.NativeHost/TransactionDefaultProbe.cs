using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Korda.Data;
using Korda.Database;
using Korda.Database.PostgreSql;
using Korda.Database.Sqlite;

internal static class TransactionDefaultProbe
{
    private static long _retired;
    private static byte[]? _frameSink;
    private readonly record struct Counter(long Bytes, int Gen0, int Gen1, int Gen2, long Retirements, long Ticks)
    {
        internal static Counter Read()
        {
            var a = GC.CollectionCount(0); var b = GC.CollectionCount(1); var c = GC.CollectionCount(2);
            return new(GC.GetTotalAllocatedBytes(true), a, b, c, Interlocked.Read(ref _retired), Stopwatch.GetTimestamp());
        }
    }
    private readonly record struct WorkerResult(int Completed, int IncompleteBegins, int IncompleteRollbacks, int IncompleteDisposals, int ThreadChanges);
    private readonly record struct Sample(string Name, int Window, int Concurrency, int OperationsPerWorker,
        Counter BaselineStart, Counter Before, Counter After, Counter SettledAfter, WorkerResult[] Workers,
        ulong[] NativeBeforeImmediate, ulong[] NativeBefore, ulong[] NativeAfter, ulong[] NativeSettledAfter,
        int BaselinePolls, int SettlePolls);
    private readonly record struct AllocationControl(string Name, int Operations, Counter Before, Counter After);

    internal static async Task<bool> RunIfRequested(string[] args)
    {
        if (args.Length == 0 || args[0] != "--transaction-default-probe") return false;
        if (args.Length != 6) throw new ArgumentException("--transaction-default-probe <sqlite|postgresql> <connection> <output> <operations-per-worker> <windows>");
        if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("A real Native AOT process is required.");
        var iterations = int.Parse(args[4]); var windows = int.Parse(args[5]);
        if (iterations < 1 || windows is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(args));
        Directory.CreateDirectory(args[3]);
        var arrayControls = ArrayControls();
        var builder = new DataSourceBuilder().WithName("DB-Perf-01-default-transaction")
            .ConfigureLimits(new DatabaseLimits { MaxConnections = 4, MaxWaiters = 16 });
        if (args[1] == "sqlite") builder.UseSqlite(args[2]);
        else if (args[1] == "postgresql") builder.UsePostgreSql(args[2], options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment);
        else throw new ArgumentException("Unknown provider.");
        var source = builder.Build(); var sessions = new DataSession[4];
        for (var i = 0; i < sessions.Length; i++) sessions[i] = await source.OpenSessionAsync();
        for (var i = 0; i < sessions.Length; i++)
        for (var j = 0; j < i; j++)
            if (ReferenceEquals(sessions[i], sessions[j])) throw new InvalidOperationException("Concurrent workers share a session.");
        using var native = new NativeObservation(source.Provider);
        source.TestRetirement = static snapshot =>
        {
            if (!snapshot.Released || snapshot.RootLive || snapshot.Controls != 0 || snapshot.CallbackEntries != 0 || snapshot.RetainedInputBytes != 0)
                throw new InvalidOperationException("Transaction operation retained managed bridge owners.");
            Interlocked.Increment(ref _retired);
        };
        var explicitDefaults = new TransactionOptions();
        // This reference frame is produced independently of DataSession's default
        // entry point. It does not access or warm a future private default holder.
        var referenceFrame = HostEncoding.Transaction(explicitDefaults);
        File.WriteAllBytes(Path.Combine(args[3], "explicit-default-control.bin"), referenceFrame);
        var coldDefault = await Window(sessions, explicitDefaults, false, 1, 1, -1, "first-default-transaction", native);
        var coldExplicit = await Window(sessions, explicitDefaults, true, 1, 1, -1, "first-explicit-transaction", native);
        var samples = new Sample[windows * 4]; var at = 0;
        foreach (var concurrency in new[] { 1, 4 })
        foreach (var useExplicit in new[] { false, true })
        {
            var name = useExplicit ? "explicit-reused-default-options" : "default-no-options";
            _ = await Window(sessions, explicitDefaults, useExplicit, concurrency, 16, -1, "warmup", native);
            for (var window = 0; window < windows; window++)
            {
                samples[at++] = await Window(sessions, explicitDefaults, useExplicit, concurrency, iterations, window, name, native);
                // Public same-session reuse is checked after the complete join,
                // outside allocation windows and without changing the fixture.
                for (var i = 0; i < concurrency; i++)
                    if (await sessions[i].ScalarAsync(SqlCommand.Create("SELECT CAST(1 AS BIGINT)"), Codecs.Int64) != 1)
                        throw new InvalidOperationException("Session was not usable after transaction retirement.");
            }
        }
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); var admission = sessions[0].BeginTransactionAsync(canceled.Token);
            if (!admission.IsCanceled) throw new InvalidOperationException("Pre-canceled default admission changed.");
            try { await admission; throw new InvalidOperationException("Expected canceled admission."); }
            catch (OperationCanceledException failure) when (failure.CancellationToken == canceled.Token) { }
        }
        var finalImmediate = new ulong[8]; var drained = new ulong[8]; var zero = new ulong[6];
        var finalBefore = Counter.Read();
        foreach (var session in sessions) await session.DisposeAsync();
        await source.DisposeAsync(); var finalAfter = Counter.Read(); native.Fill(finalImmediate);
        var finalPolls = Settle(native, drained, zero, "final-source-close", -1); var finalSettled = Counter.Read();
        if (!source.Cleanup.LocalResourcesReleased || sessions.Any(s => !s.Cleanup.LocalResourcesReleased) || drained.Take(6).Any(value => value != 0))
            throw new InvalidOperationException("Default-transaction fixture did not release all owners.");
        try { _ = sessions[0].BeginTransactionAsync(); throw new InvalidOperationException("Disposed default admission was accepted."); }
        catch (ObjectDisposedException) { }
        using var file = File.Create(Path.Combine(args[3], "transaction-allocation.json"));
        using var json = new Utf8JsonWriter(file, new() { Indented = true });
        json.WriteStartObject(); json.WriteBoolean("NativeAot", true); json.WriteString("Provider", source.Provider.ToString());
        json.WriteString("ExecutableSha256", Hash(Environment.ProcessPath!)); json.WriteString("LoadedNativePath", native.Path);
        json.WriteString("LoadedNativeSha256", Hash(native.Path)); json.WriteNumber("OperationsPerWorker", iterations); json.WriteNumber("Windows", windows);
        json.WriteNumber("WarmupOperationsPerWorker", 16); json.WriteNumber("DefaultFrameDataBytes", referenceFrame.Length);
        json.WriteString("DefaultFrameSha256", Convert.ToHexStringLower(SHA256.HashData(referenceFrame)));
        json.WriteStartArray("ArraySizeControls");
        foreach (var control in arrayControls)
        {
            json.WriteStartObject(); json.WriteString("Name", control.Name); json.WriteNumber("Operations", control.Operations);
            json.WriteNumber("AllocatedBefore", control.Before.Bytes); json.WriteNumber("AllocatedAfter", control.After.Bytes);
            json.WriteNumber("AllocatedBytes", control.After.Bytes - control.Before.Bytes);
            json.WriteNumber("Gen0", control.After.Gen0 - control.Before.Gen0); json.WriteNumber("Gen1", control.After.Gen1 - control.Before.Gen1); json.WriteNumber("Gen2", control.After.Gen2 - control.Before.Gen2); json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteString("Boundary", "Dual raw all-thread GC.GetTotalAllocatedBytes(true) boundaries: complete public Begin/rollback/Dispose/outcome checks, then physical drain to the same stable warm owner counts. No subtraction. Sessions, options, barriers, worker tasks, WhenAll, count buffers, samples and result arrays are prepared before each interval.");
        json.WriteString("PhysicalSettle", "Immediate native counters are preserved. At both window ingress and egress, use preallocated counters, require two consecutive matching six-owner reads, yield between reads (Sleep(1) every16 polls), at most5seconds. Public raw and public-plus-settle raw are both reported. Original v1 instant-equality failure remains unclassified; source supports a possible native worker tail but does not prove which counter differed in v1.");
        json.WriteString("ColdBoundary", "First no-options transaction and first explicit-options transaction after source/session setup and independent reference-frame encoding; reported separately without inferring isolated static-holder bytes from whole-operation totals.");
        json.WriteString("Attribution", "No business output allocations; runtime/harness continuations remain in raw process totals. Explicit control reuses one immutable default-valued options instance outside all intervals; its ordinary encoder path remains measured.");
        json.WriteString("Scope", "Source-project public Native AOT attribution; not final independent-package or long-run acceptance. Incomplete observations are not exact suspensions; thread changes prove asynchronous continuation.");
        json.WriteStartArray("Cold"); Write(json, coldDefault); Write(json, coldExplicit); json.WriteEndArray();
        json.WriteStartArray("Measurements"); foreach (var sample in samples) Write(json, sample); json.WriteEndArray();
        json.WriteStartObject("FinalClose");
        WriteCounter(json, "Before", finalBefore); WriteCounter(json, "AfterApi", finalAfter); WriteCounter(json, "AfterSettle", finalSettled);
        json.WriteNumber("AllocatedBefore", finalBefore.Bytes); json.WriteNumber("AllocatedAfterApi", finalAfter.Bytes);
        json.WriteNumber("AllocatedAfterSettle", finalSettled.Bytes); json.WriteNumber("PublicRawBytes", finalAfter.Bytes - finalBefore.Bytes);
        json.WriteNumber("PhysicalSettleAdditionalBytes", finalSettled.Bytes - finalAfter.Bytes);
        json.WriteNumber("PublicAndSettleRawBytes", finalSettled.Bytes - finalBefore.Bytes);
        json.WriteNumber("SettleTicks", finalSettled.Ticks - finalAfter.Ticks); json.WriteNumber("SettlePolls", finalPolls);
        json.WriteNumber("PublicGen0", finalAfter.Gen0 - finalBefore.Gen0); json.WriteNumber("PublicGen1", finalAfter.Gen1 - finalBefore.Gen1); json.WriteNumber("PublicGen2", finalAfter.Gen2 - finalBefore.Gen2);
        json.WriteNumber("SettleGen0", finalSettled.Gen0 - finalAfter.Gen0); json.WriteNumber("SettleGen1", finalSettled.Gen1 - finalAfter.Gen1); json.WriteNumber("SettleGen2", finalSettled.Gen2 - finalAfter.Gen2);
        WriteNative(json, "NativeImmediate", finalImmediate); WriteNative(json, "NativeSettled", drained); json.WriteEndObject();
        WriteNative(json, "NativeDrained", drained); json.WriteBoolean("OwnersReturnedToZero", true);
        json.WriteString("NativeCounterOrder", "sources,sessions,operations,cursors,buffers,handles,cumulative-sql,cumulative-protocol"); json.WriteEndObject();
        Console.WriteLine($"TRANSACTION_DEFAULT_PROBE_PASS provider={source.Provider} windows={windows} operationsPerWorker={iterations}");
        return true;
    }

    private static AllocationControl[] ArrayControls()
    {
        const int operations = 10000;
        var samples = new AllocationControl[6];
        for (var i = 0; i < operations; i++) _frameSink = new byte[104];
        for (var window = 0; window < 3; window++)
        {
            var before = Counter.Read(); for (var i = 0; i < operations; i++) _frameSink = Array.Empty<byte>(); var after = Counter.Read();
            samples[window * 2] = new("empty-reference-control", operations, before, after);
            before = Counter.Read(); for (var i = 0; i < operations; i++) _frameSink = new byte[104]; after = Counter.Read();
            samples[window * 2 + 1] = new("new-byte-array-104", operations, before, after);
        }
        if (_frameSink!.Length != 104) throw new InvalidOperationException("Array-size control was not consumed.");
        return samples;
    }

    private static async Task<Sample> Window(DataSession[] sessions, TransactionOptions options, bool useExplicit,
        int concurrency, int iterations, int window, string name, NativeObservation native)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new WorkerResult[concurrency]; var workers = new Task[concurrency];
        var nativeBeforeImmediate = new ulong[8]; var nativeBefore = new ulong[8];
        var nativeAfter = new ulong[8]; var nativeSettledAfter = new ulong[8];
        // The fixture owns exactly one source and four open sessions; no query,
        // cursor, buffer or operation is allowed to become a new warm baseline.
        var expectedOwners = new ulong[] { 1, (ulong)sessions.Length, 0, 0, 0, (ulong)sessions.Length + 1 };
        var ingressName = name + "/ingress";
        for (var i = 0; i < concurrency; i++) workers[i] = Worker(sessions[i], options, useExplicit, iterations, gate.Task, results, i);
        var completion = Task.WhenAll(workers); await Task.Yield();
        var baselineStart = Counter.Read(); native.Fill(nativeBeforeImmediate);
        var baselinePolls = Settle(native, nativeBefore, expectedOwners, ingressName, window);
        var before = Counter.Read();
        gate.SetResult(); await completion.ConfigureAwait(false);
        var after = Counter.Read(); native.Fill(nativeAfter);
        var settlePolls = Settle(native, nativeSettledAfter, nativeBefore, name, window); var settledAfter = Counter.Read();
        if (results.Sum(result => result.Completed) != concurrency * iterations) throw new InvalidOperationException("Transaction operation accounting differs.");
        return new(name, window, concurrency, iterations, baselineStart, before, after, settledAfter, results,
            nativeBeforeImmediate, nativeBefore, nativeAfter, nativeSettledAfter, baselinePolls, settlePolls);
    }
    private static int Settle(NativeObservation native, ulong[] values, ulong[] expected, string name, int window)
    {
        var deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency; var consecutive = 0; var polls = 0;
        while (true)
        {
            native.Fill(values); polls++; var match = true;
            for (var i = 0; i < 6; i++) if (values[i] != expected[i]) match = false;
            consecutive = match ? consecutive + 1 : 0;
            if (consecutive == 2) return polls;
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new InvalidOperationException($"Physical native-owner drain exceeded5s; name={name}; window={window}; expected=[{string.Join(',', expected)}]; observed=[{string.Join(',', values)}]; polls={polls}; processRawBytes={GC.GetTotalAllocatedBytes(true)}.");
            if ((polls & 15) == 0) Thread.Sleep(1); else Thread.Yield();
        }
    }
    private static async Task Worker(DataSession session, TransactionOptions options, bool useExplicit, int iterations,
        Task gate, WorkerResult[] results, int index)
    {
        await gate.ConfigureAwait(false);
        var completed = 0; var begins = 0; var rollbacks = 0; var disposals = 0; var threadChanges = 0;
        for (var i = 0; i < iterations; i++)
        {
            var thread = Environment.CurrentManagedThreadId;
            var beginning = useExplicit ? session.BeginTransactionAsync(options) : session.BeginTransactionAsync();
            if (!beginning.IsCompletedSuccessfully) begins++;
            var transaction = await beginning.ConfigureAwait(false);
            if (thread != Environment.CurrentManagedThreadId) threadChanges++;
            try
            {
                thread = Environment.CurrentManagedThreadId; var rollback = transaction.RollbackAsync();
                if (!rollback.IsCompletedSuccessfully) rollbacks++;
                await rollback.ConfigureAwait(false); if (thread != Environment.CurrentManagedThreadId) threadChanges++;
            }
            finally
            {
                thread = Environment.CurrentManagedThreadId; var disposal = transaction.DisposeAsync();
                if (!disposal.IsCompletedSuccessfully) disposals++;
                await disposal.ConfigureAwait(false); if (thread != Environment.CurrentManagedThreadId) threadChanges++;
            }
            if (transaction.Outcome != TransactionOutcome.RolledBack || !transaction.Cleanup.LocalResourcesReleased)
                throw new InvalidOperationException("Rollback outcome or complete retirement differs.");
            completed++;
        }
        results[index] = new(completed, begins, rollbacks, disposals, threadChanges);
    }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
    private static void Write(Utf8JsonWriter json, Sample sample)
    {
        json.WriteStartObject(); json.WriteString("Name", sample.Name); json.WriteNumber("Window", sample.Window);
        WriteCounter(json, "BaselineStart", sample.BaselineStart); WriteCounter(json, "Before", sample.Before);
        WriteCounter(json, "AfterApi", sample.After); WriteCounter(json, "AfterSettle", sample.SettledAfter);
        json.WriteNumber("Concurrency", sample.Concurrency); json.WriteNumber("OperationsPerWorker", sample.OperationsPerWorker);
        json.WriteNumber("Operations", sample.Workers.Sum(result => result.Completed));
        json.WriteNumber("IncompleteBegins", sample.Workers.Sum(result => result.IncompleteBegins));
        json.WriteNumber("IncompleteRollbacks", sample.Workers.Sum(result => result.IncompleteRollbacks));
        json.WriteNumber("IncompleteDisposals", sample.Workers.Sum(result => result.IncompleteDisposals));
        json.WriteNumber("ThreadChanges", sample.Workers.Sum(result => result.ThreadChanges));
        json.WriteNumber("AllocatedBefore", sample.Before.Bytes); json.WriteNumber("AllocatedAfter", sample.After.Bytes);
        json.WriteNumber("AllocatedBytes", sample.After.Bytes - sample.Before.Bytes);
        json.WriteNumber("AllocatedAfterSettle", sample.SettledAfter.Bytes);
        json.WriteNumber("PhysicalSettleAdditionalBytes", sample.SettledAfter.Bytes - sample.After.Bytes);
        json.WriteNumber("PublicAndSettleRawBytes", sample.SettledAfter.Bytes - sample.Before.Bytes);
        json.WriteNumber("BaselinePreparationRawBytes", sample.Before.Bytes - sample.BaselineStart.Bytes);
        json.WriteNumber("BaselinePreparationTicks", sample.Before.Ticks - sample.BaselineStart.Ticks);
        json.WriteNumber("BaselinePolls", sample.BaselinePolls); json.WriteNumber("SettlePolls", sample.SettlePolls);
        json.WriteNumber("SettleTicks", sample.SettledAfter.Ticks - sample.After.Ticks);
        json.WriteNumber("Gen0", sample.After.Gen0 - sample.Before.Gen0); json.WriteNumber("Gen1", sample.After.Gen1 - sample.Before.Gen1); json.WriteNumber("Gen2", sample.After.Gen2 - sample.Before.Gen2);
        json.WriteNumber("SettleGen0", sample.SettledAfter.Gen0 - sample.After.Gen0); json.WriteNumber("SettleGen1", sample.SettledAfter.Gen1 - sample.After.Gen1); json.WriteNumber("SettleGen2", sample.SettledAfter.Gen2 - sample.After.Gen2);
        json.WriteNumber("Retirements", sample.After.Retirements - sample.Before.Retirements);
        json.WriteNumber("StartedTicks", sample.Before.Ticks); json.WriteNumber("FinishedTicks", sample.After.Ticks);
        WriteNative(json, "NativeBeforeImmediate", sample.NativeBeforeImmediate); WriteNative(json, "NativeBefore", sample.NativeBefore);
        WriteNative(json, "NativeAfter", sample.NativeAfter); WriteNative(json, "NativeSettledAfter", sample.NativeSettledAfter); json.WriteEndObject();
    }
    private static void WriteNative(Utf8JsonWriter json, string name, ulong[] values)
    { json.WriteStartArray(name); foreach (var value in values) json.WriteNumberValue(value); json.WriteEndArray(); }
    private static void WriteCounter(Utf8JsonWriter json, string name, Counter value)
    {
        json.WriteStartObject(name); json.WriteNumber("AllocatedBytes", value.Bytes);
        json.WriteNumber("Gen0", value.Gen0); json.WriteNumber("Gen1", value.Gen1); json.WriteNumber("Gen2", value.Gen2);
        json.WriteNumber("Retirements", value.Retirements); json.WriteNumber("Ticks", value.Ticks); json.WriteEndObject();
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
            var result = new ulong[8]; Fill(result); return result;
        }
        internal unsafe void Fill(ulong[] result)
        { var count = (delegate* unmanaged[Cdecl]<uint, ulong>)_counter; for (uint i = 0; i < result.Length; i++) result[i] = count(i); }
        public void Dispose() => NativeLibrary.Free(_module);
    }
}
