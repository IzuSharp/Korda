using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DbPerf.Campaign;
using Korda.Data;
using Korda.Database;
using Korda.Query;

// Correctness/failure observations run in a separate process. They never enter
// the normal 36-case throughput mixture or its allocation totals.
internal static class LifecycleProbe
{
    private readonly record struct Counters(long Bytes, int Gen0, int Gen1, int Gen2)
    {
        internal static Counters Read() => new(GC.GetTotalAllocatedBytes(true), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }
    private readonly record struct RaceResult(int Worker, long ReadVersion, long Affected, bool Conflict, long Started, long Finished);

    internal static async Task<int> RunAsync(DataSource source, Config config, NativeDiagnostics native)
    {
        if (config.Concurrency != 2) throw new ArgumentException("The lifecycle probe requires --concurrency 2.");
        var output = Path.GetFullPath(config.Output); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var file = File.Create(output);
        using var json = new Utf8JsonWriter(file, new() { Indented = true });
        json.WriteStartObject(); json.WriteNumber("schema_version", 1); json.WriteString("route", "korda");
        json.WriteString("provider", config.Provider); json.WriteString("contract_sha256", config.ParsedContractSha256);
        json.WriteString("scope", "Separate failure/lifecycle correctness process through public Fluent, generated mapping, checked CRUD and stream APIs. Allocations include harness, exceptions and business values; no framework-only or steady-state zero claim.");
        json.WriteString("executable_sha256", Hash(Environment.ProcessPath!));
        json.WriteString("native_path", native.Path); json.WriteString("native_sha256", native.Sha256);
        Exception? failure = null;
        try
        {
            await using (var setup = await source.OpenSessionAsync())
            {
                await setup.ExecuteAsync(SqlCommand.Create("CREATE TABLE IF NOT EXISTS perf_lifecycle (\"Id\" BIGINT PRIMARY KEY, \"Counter\" BIGINT NOT NULL, \"Version\" BIGINT NOT NULL)", kind: SqlCommandKind.Mutation));
                await setup.ExecuteAsync(SqlCommand.Create("DELETE FROM perf_lifecycle", kind: SqlCommandKind.Mutation));
                await setup.ExecuteAsync(SqlCommand.Create("INSERT INTO perf_lifecycle VALUES (1,0,1)", kind: SqlCommandKind.Mutation));
            }
            await ConflictAsync(source, json);
            await EarlyAsync(source, config, json);
            await CancellationAsync(source, config, native, json);
        }
        catch (Exception error) { failure = error; }
        try { await source.DisposeAsync(); }
        catch (Exception cleanup) { failure = failure is null ? cleanup : new AggregateException(failure, cleanup); }
        var retired = native.Read();
        bool ownersZero = retired.Sources == 0 && retired.Sessions == 0 && retired.Operations == 0
            && retired.Cursors == 0 && retired.Buffers == 0 && retired.Owners == 0;
        json.WritePropertyName("owner_retirement"); JsonSerializer.Serialize(json, retired, PerfJson.Default.NativeCounts);
        json.WriteBoolean("owners_returned_to_zero", ownersZero);
        json.WriteString("error", failure?.ToString()); json.WriteBoolean("passed", failure is null && ownersZero);
        json.WriteBoolean("native_aot", !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported);
        json.WriteBoolean("performance_acceptance_claimed", false); json.WriteEndObject(); json.Flush();
        return failure is null && ownersZero ? 0 : 2;
    }

    private static async Task ConflictAsync(DataSource source, Utf8JsonWriter json)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int ready = 0;
        var before = Counters.Read();
        async Task<RaceResult> Worker(int id)
        {
            await using var session = await source.OpenSessionAsync();
            var key = LifecycleStates.Table.Key(LifecycleStates.Id, 1L);
            var original = await session.FindAsync(LifecycleStates.Table, key);
            if (original is not { Counter: 0, Version: 1 }) throw new InvalidDataException("Both workers must read the initial version before either write.");
            if (Interlocked.Increment(ref ready) == 2) gate.TrySetResult();
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(5));
            long started = Stopwatch.GetTimestamp();
            try
            {
                long affected = await session.UpdateCheckedAsync(LifecycleStates.Table, key,
                    new Changes<LifecycleState>().Set(LifecycleStates.Counter, 1L).Set(LifecycleStates.Version, 2L),
                    new ConcurrencyCheck<LifecycleState>().Expect(LifecycleStates.Version, original.Version));
                return new(id, original.Version, affected, false, started, Stopwatch.GetTimestamp());
            }
            catch (ConcurrencyConflictException expected) when (expected.ExpectedRows == 1 && expected.ActualRows == 0)
            { return new(id, original.Version, 0, true, started, Stopwatch.GetTimestamp()); }
        }
        var results = await Task.WhenAll(Worker(0), Worker(1));
        var after = Counters.Read();
        await using var verification = await source.OpenSessionAsync();
        var persisted = await verification.FindAsync(LifecycleStates.Table, LifecycleStates.Table.Key(LifecycleStates.Id, 1L));
        long overlap = Math.Min(results[0].Finished, results[1].Finished) - Math.Max(results[0].Started, results[1].Started);
        bool passed = ready == 2 && overlap > 0 && results.Sum(x => x.Affected) == 1 && results.Count(x => x.Conflict) == 1
            && persisted is { Counter: 1, Version: 2 };
        json.WriteStartObject("concurrent_conflict"); json.WriteBoolean("passed", passed);
        json.WriteStartArray("workers");
        foreach (var row in results)
        {
            json.WriteStartObject(); json.WriteNumber("worker", row.Worker); json.WriteNumber("read_version", row.ReadVersion);
            json.WriteNumber("affected", row.Affected); json.WriteBoolean("conflict", row.Conflict);
            json.WriteNumber("update_started_ticks", row.Started); json.WriteNumber("update_finished_ticks", row.Finished); json.WriteEndObject();
        }
        json.WriteEndArray(); json.WriteNumber("persisted_counter", persisted?.Counter ?? -1); json.WriteNumber("persisted_version", persisted?.Version ?? -1);
        json.WriteNumber("overlap_ticks", overlap); json.WriteNumber("timer_frequency", Stopwatch.Frequency);
        WriteAllocation(json, before, after); json.WriteEndObject();
        if (!passed) throw new InvalidDataException("Concurrent checked writes did not produce exactly one winner and one stale version.");
    }

    private static async Task EarlyAsync(DataSource source, Config config, Utf8JsonWriter json)
    {
        await using var session = await source.OpenSessionAsync();
        var before = Counters.Read();
        long first;
        using (var query = session.From(Orders.Table))
        {
            query.WhereEqual(Orders.TenantId, 1L).OrderBy(Orders.Id).Take(config.OrdersPerTenant);
            var stream = query.Select(query.Column(Orders.Id)).AsAsyncEnumerable().GetAsyncEnumerator();
            try
            {
                if (!await stream.MoveNextAsync()) throw new InvalidDataException("Large stream returned no row.");
                first = stream.Current;
            }
            finally { await stream.DisposeAsync(); }
        }
        var after = Counters.Read();
        long next = await session.ScalarAsync(SqlCommand.Create("SELECT CAST(7 AS BIGINT)"), Codecs.Int64);
        json.WriteStartObject("early_enumeration"); json.WriteNumber("available_rows", config.OrdersPerTenant);
        json.WriteNumber("consumed_rows", 1); json.WriteNumber("first_value", first); json.WriteNumber("same_session_followup", next);
        json.WriteBoolean("passed", first == 1 && next == 7); WriteAllocation(json, before, after); json.WriteEndObject();
        if (first != 1 || next != 7) throw new InvalidDataException("Early enumeration did not retire the cursor before the next public query.");
    }

    private static async Task CancellationAsync(DataSource source, Config config, NativeDiagnostics native, Utf8JsonWriter json)
    {
        await using var session = await source.OpenSessionAsync();
        using var cancellation = new CancellationTokenSource();
        string sql = config.Provider == "postgresql"
            ? "SELECT CAST(1 AS BIGINT) FROM pg_sleep(10) /* DB_PERF_01_CANCEL */"
            : "WITH RECURSIVE x(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM x WHERE n<100000000) SELECT sum(n) FROM x /* DB_PERF_01_CANCEL */";
        var command = SqlCommand.Create(sql, timeout: TimeSpan.FromSeconds(5));
        long targetBackend = config.Provider == "postgresql"
            ? await session.ScalarAsync(SqlCommand.Create("SELECT CAST(pg_backend_pid() AS BIGINT)"), Codecs.Int64) : 0;
        var before = Counters.Read(); var started = Stopwatch.GetTimestamp();
        var pending = session.ScalarAsync(command, Codecs.Int64, cancellation.Token);
        bool initiallyPending = !pending.IsCompleted;
        bool serverObserved = false, pendingAtCancel = false; ulong liveOperations = 0;
        long canceledAt = 0;
        Exception? observationFailure = null;
        try
        {
            if (config.Provider == "postgresql")
            {
                await using var observer = await source.OpenSessionAsync();
                var inspect = SqlCommand.Create("SELECT CAST(count(*) AS BIGINT) FROM pg_stat_activity WHERE pid="
                    + targetBackend.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " AND state='active' AND wait_event_type='Timeout' AND wait_event='PgSleep' AND query LIKE '%DB_PERF_01_CANCEL%'");
                for (int attempt = 0; attempt < 100 && !pending.IsCompleted; attempt++)
                {
                    serverObserved = await observer.ScalarAsync(inspect, Codecs.Int64) > 0;
                    if (serverObserved) break;
                    await Task.Delay(10);
                }
            }
            else await Task.Delay(100);
            pendingAtCancel = !pending.IsCompleted; liveOperations = native.Read().Operations;
        }
        catch (Exception error) { observationFailure = error; }
        finally
        {
            // Even a failed inspector must cancel and observe the original
            // submitted operation before its session owner is disposed.
            canceledAt = Stopwatch.GetTimestamp();
            try { cancellation.Cancel(); }
            catch (Exception error) { observationFailure = observationFailure is null ? error : new AggregateException(observationFailure, error); }
        }
        Exception? terminal = null;
        try { await pending; }
        catch (Exception error) { terminal = error; }
        double cancellationWaitSeconds = Stopwatch.GetElapsedTime(canceledAt).TotalSeconds;
        var after = Counters.Read();
        if (observationFailure is not null)
            throw terminal is null ? observationFailure : new AggregateException(observationFailure, terminal);
        bool sameSessionReusable = false; string? sameSessionError = null;
        try { sameSessionReusable = await session.ScalarAsync(SqlCommand.Create("SELECT CAST(9 AS BIGINT)"), Codecs.Int64) == 9; }
        catch (Exception error) { sameSessionError = error.ToString(); }
        await session.DisposeAsync();
        await using var replacement = await source.OpenSessionAsync();
        long next = await replacement.ScalarAsync(SqlCommand.Create("SELECT CAST(11 AS BIGINT)"), Codecs.Int64);
        bool tokenMatches = terminal is OperationCanceledException canceled && canceled.CancellationToken == cancellation.Token;
        bool passed = initiallyPending && pendingAtCancel && liveOperations > 0 && tokenMatches && next == 11
            && (config.Provider != "postgresql" || serverObserved);
        json.WriteStartObject("cancellation"); json.WriteString("sql", sql); json.WriteBoolean("initially_pending", initiallyPending);
        json.WriteBoolean("pending_when_canceled", pendingAtCancel); json.WriteNumber("live_native_operation_owners_at_cancel", liveOperations);
        json.WriteNumber("target_postgres_backend_pid", targetBackend);
        json.WriteBoolean("engine_execution_observed", config.Provider == "postgresql" && serverObserved);
        json.WriteNumber("cancel_to_terminal_seconds", cancellationWaitSeconds);
        json.WriteBoolean("postgres_pg_sleep_observed", serverObserved); json.WriteString("terminal_type", terminal?.GetType().FullName);
        json.WriteString("terminal", terminal?.ToString()); json.WriteBoolean("token_matches", tokenMatches);
        json.WriteBoolean("same_session_reusable", sameSessionReusable); json.WriteString("same_session_error", sameSessionError);
        json.WriteString("session_cleanup_disposition", session.Cleanup.ConnectionDisposition.ToString());
        json.WriteBoolean("session_local_resources_released", session.Cleanup.LocalResourcesReleased);
        json.WriteNumber("fresh_session_followup", next); json.WriteBoolean("passed", passed);
        json.WriteNumber("elapsed_seconds", Stopwatch.GetElapsedTime(started).TotalSeconds);
        json.WriteString("acceptance_scope", "Pending public operation, matching cancellation token, and owner retirement; SQLite engine interruption is unproven.");
        json.WriteString("engine_observation", config.Provider == "postgresql" ? "Independent pg_stat_activity observed the target session backend PID active in Timeout/PgSleep before caller cancellation."
            : "Caller Task remained incomplete and a native operation owner remained alive after 100 ms. The owner count does not prove sqlite3_step is executing; SQLite engine entry and VM interruption are not independently instrumented.");
        WriteAllocation(json, before, after); json.WriteEndObject();
        if (!passed) throw new InvalidDataException("Caller cancellation did not establish its documented pending/token/retirement observations.", terminal);
    }

    private static void WriteAllocation(Utf8JsonWriter json, Counters before, Counters after)
    {
        json.WriteNumber("allocated_bytes_before", before.Bytes); json.WriteNumber("allocated_bytes_after", after.Bytes);
        json.WriteNumber("allocated_bytes_total", after.Bytes - before.Bytes);
        json.WriteNumber("gen0", after.Gen0 - before.Gen0); json.WriteNumber("gen1", after.Gen1 - before.Gen1); json.WriteNumber("gen2", after.Gen2 - before.Gen2);
    }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
}
