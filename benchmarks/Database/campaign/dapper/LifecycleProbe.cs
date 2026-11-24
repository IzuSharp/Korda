using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Dapper;
using DbPerf.Campaign;
#if POSTGRESQL
using ProviderConnection = Npgsql.NpgsqlConnection;
#else
using ProviderConnection = Microsoft.Data.Sqlite.SqliteConnection;
#endif

namespace DbPerf.IndependentOrm;

public sealed record LifecycleRow(long Id, long Counter, long Version);

internal sealed partial class DapperWorker
{
    private const string LifecycleCreateSql = "CREATE TABLE IF NOT EXISTS \"perf_lifecycle\" (\"Id\" BIGINT PRIMARY KEY, \"Counter\" BIGINT NOT NULL, \"Version\" BIGINT NOT NULL)";
    private const string LifecycleDeleteSql = "DELETE FROM \"perf_lifecycle\"";
    private const string LifecycleInsertSql = "INSERT INTO \"perf_lifecycle\" (\"Id\", \"Counter\", \"Version\") VALUES (1, 0, 1)";
    private const string LifecycleReadSql = "SELECT \"Id\", \"Counter\", \"Version\" FROM \"perf_lifecycle\" WHERE \"Id\" = @p1";
    private const string ConflictSql = "UPDATE \"perf_lifecycle\" SET \"Counter\" = 1, \"Version\" = 2 WHERE \"Id\" = 1 AND \"Version\" = @p1";
    private const string LargeResultSql = "SELECT \"Id\" FROM \"orders\" WHERE \"TenantId\" = 1 ORDER BY \"Id\"";
#if POSTGRESQL
    private const string CancellationSql = "SELECT 1::bigint FROM pg_sleep(10)";
    private const string ObserveSleepSql = "SELECT COUNT(*)::bigint FROM pg_stat_activity WHERE pid = @p1 AND state = 'active' AND wait_event = 'PgSleep'";
#else
    private const string CancellationSql = "WITH RECURSIVE numbers(x) AS (VALUES(0) UNION ALL SELECT x + 1 FROM numbers WHERE x < 2000000) SELECT SUM(x) FROM numbers";
#endif

    private sealed class LifecycleRecord(string name)
    {
        internal readonly string Name = name;
        internal string Status = "passed";
        internal readonly Dictionary<string, string?> Text = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, long> Counts = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, bool> Flags = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, LifecycleRow> States = new(StringComparer.Ordinal);
        internal string? Error;
    }

    internal static async Task<int> RunLifecycleProbeAsync(Config config, AdapterInfo adapter)
    {
        if (config.Concurrency != 2 || config.SessionMode != "per-operation" || config.EmptyDriver || config.Initialize)
            throw new ArgumentException("Lifecycle probes require --concurrency 2 --session-mode per-operation and the existing real dataset.");
        var started = DateTimeOffset.UtcNow;
        var records = new List<LifecycleRecord>();
        JsonElement? retirement = null;
        Exception? fatal = null;
        try
        {
            var pool = await SharedPoolAsync(config).ConfigureAwait(false);
            await Run("two-worker-optimistic-conflict", record => ProbeConflictAsync(pool, record)).ConfigureAwait(false);
            await Run("early-enumerator-disposal", record => ProbeEarlyEndAsync(pool, config, record)).ConfigureAwait(false);
            await Run("pre-canceled-token", record => ProbeCancellationAsync(pool, record, true, false)).ConfigureAwait(false);
#if SQLITE
            await Run("sqlite-blocking-query-control", record => ProbeCancellationAsync(pool, record, false, true)).ConfigureAwait(false);
#endif
            await Run("in-flight-token-cancellation", record => ProbeCancellationAsync(pool, record, false, false)).ConfigureAwait(false);

            async Task Run(string name, Func<LifecycleRecord, Task> action)
            {
                var record = new LifecycleRecord(name);
                records.Add(record);
                var begin = Stopwatch.GetTimestamp();
                try { await action(record).ConfigureAwait(false); }
                catch (Exception error) { record.Status = "failed"; record.Error = error.ToString(); }
                record.Counts["elapsed_stopwatch_ticks"] = Stopwatch.GetTimestamp() - begin;
                record.Counts["active_readers_after_probe"] = Interlocked.Read(ref s_activeReaders);
                record.Counts["active_transactions_after_probe"] = Interlocked.Read(ref s_activeTransactions);
                record.Counts["active_pool_leases_after_probe"] = Interlocked.Read(ref pool.ActiveLeases);
                record.Counts["active_physical_connections_after_probe"] = Interlocked.Read(ref s_activeConnections);
                record.Counts["pool_retirements_after_probe"] = Interlocked.Read(ref pool.Retirements);
                record.Counts["pool_replacements_after_probe"] = Interlocked.Read(ref pool.Replacements);
                if (s_activeReaders != 0 || s_activeTransactions != 0 || pool.ActiveLeases != 0)
                {
                    record.Status = "failed";
                    record.Text["owner_failure"] = "A logical owner remained after the probe.";
                }
                if (Volatile.Read(ref s_retirementFault) != 0)
                    throw new InvalidOperationException("A cleanup fault prevents further lifecycle probes.");
            }
        }
        catch (Exception error) { fatal = error; }
        finally
        {
            try { retirement = await CollectOwnerRetirementAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { fatal = fatal is null ? cleanup : new AggregateException(fatal, cleanup); }
        }

        var failed = fatal is not null || records.Any(record => record.Status == "failed");
        using (var stream = File.Create(config.Output))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 1);
            writer.WriteString("scope", "Independent real-ORM Native AOT lifecycle correctness probes; excluded from the mixed workload, performance ranking and allocation acceptance.");
            writer.WriteString("status", failed ? "failed" : "completed");
            writer.WriteBoolean("can_qualify", false);
            writer.WriteString("provider", config.Provider);
            writer.WriteString("route", adapter.Route);
            writer.WriteString("package_version", adapter.PackageVersion);
            writer.WriteString("contract_sha256", config.ParsedContractSha256);
            writer.WriteString("identity_and_generated_call_evidence", config.Output + ".commands.json");
            writer.WriteString("started_utc", started);
            writer.WriteString("finished_utc", DateTimeOffset.UtcNow);
            writer.WriteNumber("stopwatch_frequency", Stopwatch.Frequency);
            writer.WriteNumber("physical_pool_capacity", 2);
            writer.WriteNumber("hot_row_id", 1);
            writer.WriteString("mixed_contract_effect", "Only this explicit lifecycle branch creates and resets the dedicated benchmark perf_lifecycle table; mixed workload tables and perf_state are unchanged.");
            writer.WriteStartArray("probes");
            foreach (var record in records)
            {
                writer.WriteStartObject();
                writer.WriteString("name", record.Name);
                writer.WriteString("status", record.Status);
                foreach (var (key, value) in record.Text) writer.WriteString(key, value);
                foreach (var (key, value) in record.Counts) writer.WriteNumber(key, value);
                foreach (var (key, value) in record.Flags) writer.WriteBoolean(key, value);
                writer.WriteStartObject("observed_states");
                foreach (var (key, row) in record.States)
                {
                    writer.WriteStartObject(key);
                    writer.WriteNumber("id", row.Id); writer.WriteNumber("counter", row.Counter);
                    writer.WriteNumber("version", row.Version);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                writer.WriteString("exception", record.Error);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("owner_retirement");
            if (retirement is { } owners) owners.WriteTo(writer); else writer.WriteNullValue();
            writer.WriteString("fatal_exception", fatal?.ToString());
            writer.WriteEndObject();
        }
        return failed ? 1 : 0;
    }

    private static async Task ProbeConflictAsync(SessionPool pool, LifecycleRecord record)
    {
        record.Text["read_sql"] = LifecycleReadSql;
        record.Text["setup_create_sql"] = LifecycleCreateSql;
        record.Text["setup_delete_sql"] = LifecycleDeleteSql;
        record.Text["setup_insert_sql"] = LifecycleInsertSql;
        record.Text["checked_update_sql"] = ConflictSql;
        record.Counts["read_parameter_p1"] = 1;
        record.Text["transaction_policy"] = "Two autocommit logical workers read the same version before an asynchronous barrier releases both checked updates; no hidden retries.";
        record.Text["update_interval_scope"] = "Caller-observed Stopwatch timestamps immediately before ExecuteAsync and after its awaited terminal; includes provider execution/await scheduling, not independently measured server lock intervals.";
        var first = await pool.BorrowAsync().ConfigureAwait(false);
        SessionPool.Lease? second = null;
        var errors = new List<Exception>();
        try
        {
            second = await pool.BorrowAsync().ConfigureAwait(false);
            await SetupProbeStateAsync(first.Connection).ConfigureAwait(false);
            record.States["initial"] = await ProbeStateAsync(first.Connection).ConfigureAwait(false);
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int waiting = 0;
            async Task<int> Contender(ProviderConnection connection, string name)
            {
                var before = await ProbeStateAsync(connection).ConfigureAwait(false);
                lock (record.States) record.States[name + "_before"] = before;
                if (Interlocked.Increment(ref waiting) == 2) arrived.SetResult();
                await release.Task.ConfigureAwait(false);
                var parameters = new StateKeyArgs(before.Version);
                var start = Stopwatch.GetTimestamp();
                try { return await connection.ExecuteAsync(ConflictSql, parameters, commandTimeout: 5).ConfigureAwait(false); }
                finally
                {
                    var end = Stopwatch.GetTimestamp();
                    lock (record.Counts)
                    {
                        record.Counts[name + "_update_start_timestamp"] = start;
                        record.Counts[name + "_update_end_timestamp"] = end;
                        record.Counts[name + "_update_parameter_p1"] = before.Version;
                    }
                }
            }
            var left = Contender(first.Connection, "left");
            var right = Contender(second.Value.Connection, "right");
            var arrivals = arrived.Task;
            // A pre-read failure must not leave the other contender waiting forever.
            var terminal = await Task.WhenAny(arrivals, left, right).ConfigureAwait(false);
            if (terminal != arrivals)
            {
                release.TrySetResult();
                await Both(left, right).ConfigureAwait(false);
                throw new InvalidOperationException("A contender terminated before both pre-reads reached the barrier.");
            }
            var leftBefore = record.States["left_before"];
            var rightBefore = record.States["right_before"];
            release.SetResult();
            var affected = await Both(left, right).ConfigureAwait(false);
            record.Counts["left_affected"] = affected[0]; record.Counts["right_affected"] = affected[1];
            var overlap = Math.Min(record.Counts["left_update_end_timestamp"], record.Counts["right_update_end_timestamp"])
                - Math.Max(record.Counts["left_update_start_timestamp"], record.Counts["right_update_start_timestamp"]);
            record.Counts["update_interval_overlap_ticks"] = Math.Max(0, overlap);
            record.Flags["update_api_intervals_overlap"] = overlap > 0;
            record.Flags["both_pre_read_same_tuple"] = leftBefore == rightBefore && leftBefore == new LifecycleRow(1, 0, 1);
            var persisted = await ProbeStateAsync(first.Connection).ConfigureAwait(false);
            record.States["persisted"] = persisted;
            record.Flags["exactly_one_winner_one_stale"] = affected.Order().SequenceEqual(new[] { 0, 1 });
            var expected = new LifecycleRow(1, 1, 2);
            record.Flags["persisted_matches_winner"] = persisted == expected;
            if (!record.Flags["both_pre_read_same_tuple"] || !record.Flags["exactly_one_winner_one_stale"] || !record.Flags["persisted_matches_winner"])
                throw new InvalidOperationException("The two-worker optimistic conflict contract did not hold.");
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            if (second is { } other)
                try { await pool.ReturnAsync(other, errors.Count == 0).ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            try { await pool.ReturnAsync(first, errors.Count == 0).ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0) throw new AggregateException("Conflict probe and/or cleanup failed.", errors);

        static async Task<int[]> Both(Task<int> left, Task<int> right)
        {
            var together = Task.WhenAll(left, right);
            try { return await together.ConfigureAwait(false); }
            catch { throw together.Exception!; }
        }
    }

    private static async Task SetupProbeStateAsync(ProviderConnection connection)
    {
        _ = await connection.ExecuteAsync(LifecycleCreateSql, commandTimeout: 5).ConfigureAwait(false);
        _ = await connection.ExecuteAsync(LifecycleDeleteSql, commandTimeout: 5).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(LifecycleInsertSql, commandTimeout: 5).ConfigureAwait(false);
        if (affected != 1) throw new InvalidOperationException("Lifecycle state initialization expected one inserted row.");
    }

    private static Task<LifecycleRow> ProbeStateAsync(ProviderConnection connection)
    {
        var parameters = new StateKeyArgs(1);
        return ProbeSingleAsync(connection.QueryUnbufferedAsync<LifecycleRow>(LifecycleReadSql, parameters, commandTimeout: 5));
    }

    private static async Task<T> ProbeSingleAsync<T>(IAsyncEnumerable<T> stream)
    {
        var reader = OpenProbeReader(stream);
        Exception? primary = null;
        try
        {
            if (!await reader.MoveNextAsync().ConfigureAwait(false)) throw new InvalidOperationException("Expected exactly one lifecycle row.");
            var row = reader.Current;
            if (await reader.MoveNextAsync().ConfigureAwait(false)) throw new InvalidOperationException("Lifecycle query returned more than one row.");
            return row;
        }
        catch (Exception error) { primary = error; throw; }
        finally { await CloseProbeReaderAsync(reader, primary).ConfigureAwait(false); }
    }

    private static async Task ProbeEarlyEndAsync(SessionPool pool, Config config, LifecycleRecord record)
    {
        record.Text["sql"] = LargeResultSql;
        record.Counts["dataset_rows_available"] = config.OrdersPerTenant;
        var lease = await pool.BorrowAsync().ConfigureAwait(false);
        Exception? primary = null;
        try
        {
            var reader = OpenProbeReader(lease.Connection.QueryUnbufferedAsync<long>(LargeResultSql, commandTimeout: 5));
            Exception? readError = null;
            try
            {
                var moving = reader.MoveNextAsync();
                record.Flags["first_move_suspended"] = !moving.IsCompleted;
                if (!await moving.ConfigureAwait(false) || reader.Current != 1) throw new InvalidOperationException("Unexpected first row in the large stream.");
                record.Counts["rows_consumed"] = 1;
            }
            catch (Exception error) { readError = error; throw; }
            finally { await CloseProbeReaderAsync(reader, readError).ConfigureAwait(false); }
        }
        catch (Exception error) { primary = error; throw; }
        finally { await ReturnProbeLeaseAsync(pool, lease, primary is null, primary).ConfigureAwait(false); }
        record.Counts["readers_after_early_dispose"] = Interlocked.Read(ref s_activeReaders);
        record.Counts["leases_after_early_return"] = Interlocked.Read(ref pool.ActiveLeases);
        // Queue rotation with two slots visits both physical owners, including the
        // just-returned stream owner. A fresh pooled connection cannot mask it.
        var visitedOriginal = false;
        for (var index = 0; index < pool.Capacity; index++)
        {
            var next = await pool.BorrowAsync().ConfigureAwait(false);
            Exception? following = null;
            try
            {
                visitedOriginal |= ReferenceEquals(next.Connection, lease.Connection);
                _ = await ProbeStateAsync(next.Connection).ConfigureAwait(false);
            }
            catch (Exception error) { following = error; throw; }
            finally { await ReturnProbeLeaseAsync(pool, next, following is null, following).ConfigureAwait(false); }
        }
        record.Flags["subsequent_query_on_same_physical_owner"] = visitedOriginal;
        if (!visitedOriginal || s_activeReaders != 0 || pool.ActiveLeases != 0)
            throw new InvalidOperationException("Early termination did not return a reusable bounded-pool owner.");
    }

    private static async Task ProbeCancellationAsync(SessionPool pool, LifecycleRecord record, bool preCanceled, bool control)
    {
        record.Text["sql"] = CancellationSql;
        record.Text["token_api"] = "QueryUnbufferedAsync<T>(...).GetAsyncEnumerator(CancellationToken); no parameter-class cancellation property and no raw ADO query fallback.";
        record.Counts["command_timeout_seconds"] = 5;
        using var cancellation = new CancellationTokenSource();
        if (preCanceled) cancellation.Cancel();
        var token = control ? CancellationToken.None : cancellation.Token;
        record.Flags["token_can_be_canceled"] = token.CanBeCanceled;
        record.Flags["token_requested_before_first_move"] = token.IsCancellationRequested;
        var lease = await pool.BorrowAsync().ConfigureAwait(false);
        Exception? failure = null;
        Exception? observedCancellation = null;
        IAsyncEnumerator<long>? reader = null;
        long cancelTick = 0;
        CancellationTokenRegistration registration = default;
        var begin = Stopwatch.GetTimestamp();
        record.Counts["query_start_timestamp"] = begin;
#if POSTGRESQL
        var sawSleep = false;
#endif
        Task<bool>? pending = null;
        var pendingObserved = false;
        try
        {
            reader = OpenProbeReader(lease.Connection.QueryUnbufferedAsync<long>(CancellationSql, commandTimeout: 5), token);
            registration = token.Register(() => Interlocked.Exchange(ref cancelTick, Stopwatch.GetTimestamp()));
#if SQLITE
            if (!preCanceled && !control) cancellation.CancelAfter(TimeSpan.FromMilliseconds(25));
#endif
            var moving = reader.MoveNextAsync();
            record.Flags["first_move_suspended"] = !moving.IsCompleted;
            // This conversion belongs only to this correctness probe. It is not
            // the mixed-workload scheduler and is never subtracted from allocation.
            pending = moving.AsTask();
#if POSTGRESQL
            if (!preCanceled && !control)
            {
                var observer = await pool.BorrowAsync().ConfigureAwait(false);
                Exception? observing = null;
                try
                {
                    var parameters = new StateKeyArgs(lease.Connection.ProcessID);
                    record.Counts["postgres_backend_pid"] = parameters.p1;
                    for (var attempt = 0; attempt < 100 && !pending.IsCompleted; attempt++)
                    {
                        var active = await ProbeSingleAsync(observer.Connection.QueryUnbufferedAsync<long>(ObserveSleepSql, parameters, commandTimeout: 5)).ConfigureAwait(false);
                        if (active == 1) { sawSleep = true; break; }
                        await Task.Delay(10).ConfigureAwait(false);
                    }
                    record.Text["server_observation_sql"] = ObserveSleepSql;
                    record.Flags["server_observed_active_pg_sleep"] = sawSleep;
                }
                catch (Exception error) { observing = error; throw; }
                finally { await ReturnProbeLeaseAsync(pool, observer, observing is null, observing).ConfigureAwait(false); }
                cancellation.Cancel();
            }
#endif
            long rows = 0;
            bool firstRow;
            try { firstRow = await pending.ConfigureAwait(false); }
            finally { pendingObserved = true; }
            if (firstRow)
            {
                record.Counts["first_value"] = reader.Current;
                rows++;
                while (await reader.MoveNextAsync().ConfigureAwait(false)) rows++;
            }
            record.Counts["rows_consumed"] = rows;
        }
        catch (OperationCanceledException canceled)
        {
            observedCancellation = canceled;
            record.Text["observed_cancellation_exception"] = canceled.ToString();
            record.Flags["exception_token_equals_supplied_token"] = canceled.CancellationToken == token;
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (pending is not null && !pendingObserved)
            {
                // Never dispose an async enumerator while its pending MoveNext
                // still owns provider state, even if the observer query failed.
                try { cancellation.Cancel(); }
                catch (Exception cancellationError) { failure = failure is null ? cancellationError : new AggregateException(failure, cancellationError); }
                try { _ = await pending.ConfigureAwait(false); }
                catch (Exception drain) { failure = failure is null ? drain : new AggregateException(failure, drain); }
            }
            record.Counts["query_elapsed_stopwatch_ticks"] = Stopwatch.GetTimestamp() - begin;
            record.Counts["cancellation_timestamp"] = Interlocked.Read(ref cancelTick);
            record.Flags["token_requested_after_query"] = token.IsCancellationRequested;
            if (reader is not null)
            {
                try { await CloseProbeReaderAsync(reader, failure ?? observedCancellation).ConfigureAwait(false); }
                catch (Exception error) { failure = error; }
            }
            try { registration.Dispose(); }
            catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
            try { await ReturnProbeLeaseAsync(pool, lease, failure is null && observedCancellation is null, failure).ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
        }
        if (failure is not null) throw failure;
        record.Flags["operation_canceled_exception_observed"] = observedCancellation is not null;
        if (control)
        {
            if (observedCancellation is not null || record.Counts.GetValueOrDefault("first_value") != 2_000_001_000_000L)
                throw new InvalidOperationException("The bounded SQLite control query did not complete correctly.");
        }
        else if (preCanceled)
        {
            if (observedCancellation is null) record.Status = "cancellation_not_supported_for_this_public_path";
        }
        else
        {
#if POSTGRESQL
            if (!record.Flags["first_move_suspended"] || !sawSleep || observedCancellation is null
                || !record.Flags.GetValueOrDefault("exception_token_equals_supplied_token"))
                throw new InvalidOperationException("PG cancellation did not interrupt a verified suspended pg_sleep operation.");
#else
            record.Status = observedCancellation is null ? "in_flight_cancellation_not_observed" : "cancellation_observed_without_asynchronous_sql_interruption_proof";
            record.Text["limitation"] = "Microsoft.Data.Sqlite may block synchronously inside MoveNextAsync. A token exception after that call does not prove interruption of the SQLite VM; compare the retained uncanceled control and actual elapsed/token timing without treating timing alone as cancellation proof.";
#endif
        }
        // The same conservative policy as a failed mixed operation retires a
        // canceled physical owner. A later pool borrow may replace its slot.
        for (var index = 0; index < pool.Capacity; index++)
        {
            var next = await pool.BorrowAsync().ConfigureAwait(false);
            Exception? following = null;
            try { _ = await ProbeStateAsync(next.Connection).ConfigureAwait(false); }
            catch (Exception error) { following = error; throw; }
            finally { await ReturnProbeLeaseAsync(pool, next, following is null, following).ConfigureAwait(false); }
        }
        record.Flags["pool_usable_after_cancellation_or_control"] = true;
    }

    private static IAsyncEnumerator<T> OpenProbeReader<T>(IAsyncEnumerable<T> stream, CancellationToken token = default)
    {
        var reader = stream.GetAsyncEnumerator(token);
        Interlocked.Increment(ref s_createdReaders);
        Interlocked.Increment(ref s_activeReaders);
        return reader;
    }

    private static async Task CloseProbeReaderAsync<T>(IAsyncEnumerator<T> reader, Exception? primary)
    {
        try
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            Interlocked.Increment(ref s_closedReaders);
            Interlocked.Decrement(ref s_activeReaders);
        }
        catch (Exception cleanup)
        {
            Interlocked.Increment(ref s_disposalFailures);
            Volatile.Write(ref s_retirementFault, 1);
            if (primary is not null) throw new AggregateException("Lifecycle reader and cleanup failed.", primary, cleanup);
            throw;
        }
    }

    private static async Task ReturnProbeLeaseAsync(SessionPool pool, SessionPool.Lease lease, bool healthy, Exception? primary)
    {
        try { await pool.ReturnAsync(lease, healthy).ConfigureAwait(false); }
        catch (Exception cleanup)
        {
            if (primary is not null) throw new AggregateException("Lifecycle operation and lease retirement failed.", primary, cleanup);
            throw;
        }
    }
}
