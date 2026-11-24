using Dapper;
using DbPerf.Campaign;
using System.Text.Json;
#if POSTGRESQL
using ProviderConnection = Npgsql.NpgsqlConnection;
#elif SQLITE
using ProviderConnection = Microsoft.Data.Sqlite.SqliteConnection;
#else
#error Select exactly one provider when building the independent ORM adapter.
#endif

namespace DbPerf.IndependentOrm;

internal sealed partial class DapperWorker : ICampaignWorker
{
    private readonly Config _config;
    private readonly Dictionary<string, ReadPlan> _plans;
    private readonly Dictionary<string, StatePlan> _statePlans;
    private ProviderConnection? _connection;
    private SessionPool? _pool;
    private bool _disposed;
    private bool _quarantined;
    private delegate void HashRow<in T>(ref Digest digest, T row);
    private static long s_activeConnections, s_openedConnections, s_closedConnections;
    private static long s_activeReaders, s_createdReaders, s_closedReaders, s_disposalFailures;
    private static int s_retirementFault;

    private DapperWorker(Config config, int workerId)
    {
        _config = config;
        _plans = config.Cases.Where(query => query.Steps.Length == 0).ToDictionary(query => query.Id,
            query => new ReadPlan(query, config.Provider, config.Mode == "prepared"), StringComparer.Ordinal);
        _statePlans = config.Cases.Where(query => query.Steps.Length != 0).ToDictionary(query => query.Id,
            query => new StatePlan(query, config.Provider, config.Mode == "prepared", workerId), StringComparer.Ordinal);
    }

    internal static async Task<ICampaignWorker> CreateAsync(Config config, int workerId)
    {
        if (config.Provider != ProviderName) throw new InvalidOperationException("Executable provider differs from the campaign config.");
        if (config.Initialize) throw new InvalidOperationException("Initialize the shared dataset through the common setup route.");
        var worker = new DapperWorker(config, workerId);
        if (config.SessionMode == "persistent") worker._connection = await OpenAsync(config).ConfigureAwait(false);
        else
        {
            worker._pool = await SharedPoolAsync(config).ConfigureAwait(false);
            if (!ReferenceEquals(worker._pool.Config, config)) throw new InvalidOperationException("A process can execute only one frozen pool config.");
        }
        return worker;
    }

    internal static AdapterInfo CreateInfo(Config config)
    {
#if SQLITE
        var library = SqliteImage.EnsureLoaded();
        string[] libraries = [library.Path];
        const string providerVersion = "Microsoft.Data.Sqlite.Core=11.0.0;SQLitePCLRaw.provider.sqlite3=2.1.11";
#else
        string[] libraries = [];
        const string providerVersion = "Npgsql=11.0.0";
#endif
        return new("dapper-aot", ProviderName, "Dapper=2.1.79;Dapper.AOT=1.1.0;" + providerVersion,
            config.Mode == "prepared"
                ? "Frozen constant SQL and typed arguments; public CacheCommand-generated DbCommand/parameter reuse with per-execution rebinding; ThreadStatic slots plus soft-16 queue per call-site; no explicit physical prepare; Npgsql auto-prepare=0"
                : "Runtime clause construction and fresh typed arguments; generated row/command factories with no CacheCommand; fresh DbCommand parameters each operation; no explicit physical prepare; Npgsql auto-prepare=0",
            config.SessionMode == "persistent" ? "one physical connection per worker; provider pools disabled" : "shared bounded physical pool of N=concurrency; pre-open and configure once outside budget, borrow/return per operation, failed owners retired; no hidden retry",
            libraries);
    }

    internal static string ProviderName =>
#if POSTGRESQL
        "postgresql";
#else
        "sqlite";
#endif

    public ValueTask<Result> ExecuteAsync(Case query, long sequence)
    {
        _ = sequence;
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _config.SessionMode == "persistent" ? ExecutePersistent(query) : ExecuteWithPool(query);
    }

    private ValueTask<Result> ExecuteOn(ProviderConnection connection, Case query)
    {
        if (query.Steps.Length != 0) return ExecuteState(connection, _statePlans[query.Id]);
        var plan = _plans[query.Id];
        return _config.Mode == "prepared" ? ExecuteReadPrepared(connection, plan) : ExecuteRead(connection, plan);
    }

    private async ValueTask<Result> ExecutePersistent(Case query)
    {
        if (_quarantined) throw new InvalidOperationException("The worker's physical owner could not be retired and is quarantined.");
        long suspensions = 0;
        if (_connection is null)
        {
            var opening = OpenAsync(_config);
            if (!opening.IsCompleted) suspensions++;
            _connection = await opening.ConfigureAwait(false);
        }
        try
        {
            var result = await ExecuteOn(_connection, query).ConfigureAwait(false);
            return result with { Suspensions = result.Suspensions + suspensions };
        }
        catch (Exception original)
        {
            var failed = Interlocked.Exchange(ref _connection, null)!;
            try { await CloseConnectionAsync(failed).ConfigureAwait(false); }
            catch (Exception cleanup)
            {
                _connection = failed;
                _quarantined = true;
                throw new AggregateException("Operation and physical connection retirement failed.", original, cleanup);
            }
            throw;
        }
    }

    private async ValueTask<Result> ExecuteWithPool(Case query)
    {
        var pool = _pool ?? throw new ObjectDisposedException(nameof(DapperWorker));
        var borrowing = pool.BorrowAsync();
        long suspensions = borrowing.IsCompleted ? 0 : 1;
        var lease = await borrowing.ConfigureAwait(false);
        Result result;
        try { result = await ExecuteOn(lease.Connection, query).ConfigureAwait(false); }
        catch (Exception original)
        {
            try { await pool.ReturnAsync(lease, false).ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException("Operation and physical pool retirement failed.", original, cleanup); }
            throw;
        }
        var returning = pool.ReturnAsync(lease, true);
        if (!returning.IsCompleted) suspensions++;
        await returning.ConfigureAwait(false);
        return result with { Suspensions = result.Suspensions + suspensions };
    }

    private ValueTask<Result> ExecuteRead(ProviderConnection connection, ReadPlan plan)
    {
        var prepared = _config.Mode == "prepared";
        var sql = plan.Sql(prepared);
        // Every ORM call below names its concrete result and parameter types. A generic
        // helper must not hide the Dapper call from the interceptor generator.
        if (plan.Case.Materialization == "related")
        {
            var related = plan.Related ?? new RelatedParameters96(plan.Integers);
            return ReadRelated(connection.QueryUnbufferedAsync<RelatedRow>(sql, related, commandTimeout: 5), plan.Case);
        }
        if (plan.HasText)
        {
            var literal = plan.Literal ?? plan.NewLiteral();
            return Read(connection.QueryUnbufferedAsync<long>(sql, literal, commandTimeout: 5), HashInteger);
        }
        var args = plan.Common ?? new CommonTypedArgs(plan.Integers);
        return plan.Case.Materialization switch
        {
            "scalar-int64" => Read(connection.QueryUnbufferedAsync<long>(sql, args, commandTimeout: 5), HashInteger),
            "text" => Read(connection.QueryUnbufferedAsync<TextRow>(sql, args, commandTimeout: 5), HashText),
            "money" => Read(connection.QueryUnbufferedAsync<MoneyRow>(sql, args, commandTimeout: 5), HashMoney),
            "json" => Read(connection.QueryUnbufferedAsync<JsonRow>(sql, args, commandTimeout: 5), HashJson),
            "wide" => Read(connection.QueryUnbufferedAsync<WideRow>(sql, args, commandTimeout: 5), HashWide),
            "join" => Read(connection.QueryUnbufferedAsync<JoinRow>(sql, args, commandTimeout: 5), HashJoin),
            "customer-total" => Read(connection.QueryUnbufferedAsync<CustomerTotalRow>(sql, args, commandTimeout: 5), HashCustomerTotal),
            "product-total" => Read(connection.QueryUnbufferedAsync<ProductTotalRow>(sql, args, commandTimeout: 5), HashProductTotal),
#if SQLITE
            // Dapper.AOT 1.1.0's built-in scalar row factory does not apply the
            // module decimal handler. Map its real INTEGER storage, then create
            // the equivalent nullable decimal value without an intermediate DTO.
            "nullable-money" => Read(connection.QueryUnbufferedAsync<long?>(sql, args, commandTimeout: 5), HashNullableCents),
#else
            "nullable-money" => Read(connection.QueryUnbufferedAsync<decimal?>(sql, args, commandTimeout: 5), HashNullableMoney),
#endif
            "rank" => Read(connection.QueryUnbufferedAsync<RankRow>(sql, args, commandTimeout: 5), HashRank),
            "running" => Read(connection.QueryUnbufferedAsync<RunningRow>(sql, args, commandTimeout: 5), HashRunning),
            "detail" => Read(connection.QueryUnbufferedAsync<DetailRow>(sql, args, commandTimeout: 5), HashDetail),
            _ => throw new NotSupportedException("No generated Dapper read branch for " + plan.Case.Materialization),
        };
    }

    private static async ValueTask<Result> Read<T>(IAsyncEnumerable<T> stream, HashRow<T> hash)
    {
        var digest = Digest.Create();
        long rows = 0, suspensions = 0;
        var reader = stream.GetAsyncEnumerator();
        Interlocked.Increment(ref s_createdReaders);
        Interlocked.Increment(ref s_activeReaders);
        Exception? readingError = null;
        try
        {
            while (true)
            {
                var move = reader.MoveNextAsync();
                if (!move.IsCompleted) suspensions++;
                if (!await move.ConfigureAwait(false)) break;
                hash(ref digest, reader.Current);
                rows++;
            }
        }
        catch (Exception error) { readingError = error; throw; }
        finally
        {
            try
            {
                var close = reader.DisposeAsync();
                if (!close.IsCompleted) suspensions++;
                await close.ConfigureAwait(false);
                Interlocked.Increment(ref s_closedReaders);
                Interlocked.Decrement(ref s_activeReaders);
            }
            catch (Exception cleanup)
            {
                Interlocked.Increment(ref s_disposalFailures);
                Volatile.Write(ref s_retirementFault, 1);
                if (readingError is not null) throw new AggregateException("Read and enumerator cleanup failed.", readingError, cleanup);
                throw;
            }
        }
        return new(rows, digest.Value, suspensions);
    }

    private static async ValueTask<Result> ReadRelated(IAsyncEnumerable<RelatedRow> stream, Case query)
    {
        // The relation contract returns owned groups, including empty parents, with child
        // ordering performed by the consumer. These bounded lists are inside total allocation.
        var groups = new List<RelatedRow>?[query.GroupCount];
        long rows = 0, suspensions = 0;
        var reader = stream.GetAsyncEnumerator();
        Interlocked.Increment(ref s_createdReaders);
        Interlocked.Increment(ref s_activeReaders);
        Exception? readingError = null;
        try
        {
            while (true)
            {
                var move = reader.MoveNextAsync();
                if (!move.IsCompleted) suspensions++;
                if (!await move.ConfigureAwait(false)) break;
                var row = reader.Current;
                if ((ulong)row.__korda_parent_index >= (ulong)groups.Length)
                    throw new InvalidOperationException("Related row has an out-of-range parent ordinal.");
                var parent = checked((int)row.__korda_parent_index);
                if (row.OrderId != query.ParentIds[parent] || row.TenantId != query.Parameters[parent * 3 + 1].Integer)
                    throw new InvalidOperationException("Related row has the wrong composite parent key.");
                (groups[parent] ??= []).Add(row);
                rows++;
            }
        }
        catch (Exception error) { readingError = error; throw; }
        finally
        {
            try
            {
                var close = reader.DisposeAsync();
                if (!close.IsCompleted) suspensions++;
                await close.ConfigureAwait(false);
                Interlocked.Increment(ref s_closedReaders);
                Interlocked.Decrement(ref s_activeReaders);
            }
            catch (Exception cleanup)
            {
                Interlocked.Increment(ref s_disposalFailures);
                Volatile.Write(ref s_retirementFault, 1);
                if (readingError is not null) throw new AggregateException("Read and enumerator cleanup failed.", readingError, cleanup);
                throw;
            }
        }
        var digest = Digest.Create();
        for (var parent = 0; parent < groups.Length; parent++)
        {
            var group = groups[parent];
            digest.Integer(query.ParentIds[parent]);
            digest.Integer(group?.Count ?? 0);
            if (group is null) continue;
            group.Sort(static (left, right) => left.Id.CompareTo(right.Id));
            foreach (var row in group) HashRelated(ref digest, row);
        }
        return new(rows, digest.Value, suspensions);
    }

    private static async Task<ProviderConnection> OpenAsync(Config config)
    {
        if (Volatile.Read(ref s_retirementFault) != 0)
            throw new InvalidOperationException("New physical owners are disabled after a failed retirement.");
#if POSTGRESQL
        Npgsql.NpgsqlConnectionStringBuilder options;
        if (config.Connection.StartsWith("postgresql://", StringComparison.Ordinal) || config.Connection.StartsWith("postgres://", StringComparison.Ordinal))
        {
            var uri = new Uri(config.Connection);
            if (uri.Query.Length != 0) throw new NotSupportedException("Use explicit Npgsql connection-string settings when URI query parameters are required.");
            var user = uri.UserInfo.Split(':', 2);
            options = new() { Host = uri.Host, Port = uri.Port < 0 ? 5432 : uri.Port,
                Username = Uri.UnescapeDataString(user[0]), Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')) };
            if (user.Length == 2) options.Password = Uri.UnescapeDataString(user[1]);
        }
        else options = new(config.Connection);
        options.Pooling = false;
        options.Enlist = false;
        options.MaxAutoPrepare = 0;
        options.CommandTimeout = 5;
        options.Timeout = 5;
        options.SslMode = Npgsql.SslMode.Disable;
        var connection = new ProviderConnection(options.ConnectionString);
#else
        _ = SqliteImage.EnsureLoaded();
        var options = config.Connection.Contains('=')
            ? new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(config.Connection)
            : new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(config.Connection) };
        options.Pooling = false;
        options.Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite;
        options.Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Private;
        options.DefaultTimeout = 5;
        var connection = new ProviderConnection(options.ConnectionString);
#endif
        var opened = false;
        try
        {
            await connection.OpenAsync().ConfigureAwait(false);
            Interlocked.Increment(ref s_openedConnections);
            Interlocked.Increment(ref s_activeConnections);
            opened = true;
#if POSTGRESQL
            await connection.ExecuteAsync("SET TIME ZONE 'UTC'", commandTimeout: 5).ConfigureAwait(false);
            await connection.ExecuteAsync("SET statement_timeout TO 5000", commandTimeout: 5).ConfigureAwait(false);
#else
            await connection.ExecuteAsync("PRAGMA foreign_keys=ON", commandTimeout: 5).ConfigureAwait(false);
            await connection.ExecuteAsync("PRAGMA journal_mode=WAL", commandTimeout: 5).ConfigureAwait(false);
            await connection.ExecuteAsync("PRAGMA synchronous=FULL", commandTimeout: 5).ConfigureAwait(false);
            await connection.ExecuteAsync("PRAGMA busy_timeout=5000", commandTimeout: 5).ConfigureAwait(false);
#endif
            return connection;
        }
        catch (Exception original)
        {
            try
            {
                if (opened) await CloseConnectionAsync(connection).ConfigureAwait(false);
                else await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanup)
            {
                Volatile.Write(ref s_retirementFault, 1);
                throw new AggregateException("Physical open/configuration and cleanup failed.", original, cleanup);
            }
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _pool = null; // The common runner invokes the pool close hook after all workers retire.
        var connection = Interlocked.Exchange(ref _connection, null);
        return connection is null ? ValueTask.CompletedTask : CloseConnectionAsync(connection);
    }

    private static async ValueTask CloseConnectionAsync(ProviderConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            Interlocked.Increment(ref s_closedConnections);
            Interlocked.Decrement(ref s_activeConnections);
        }
        catch
        {
            Interlocked.Increment(ref s_disposalFailures);
            Volatile.Write(ref s_retirementFault, 1);
            throw;
        }
    }

    internal static async Task<JsonElement?> CollectOwnerRetirementAsync()
    {
        var pool = s_pool is { IsCompletedSuccessfully: true } completed ? completed.Result : null;
        if (pool is not null) await pool.DisposeAsync().ConfigureAwait(false);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("scope", "Adapter-observed successful physical opens and disposals, and ORM async-enumerator ownership; provider internal owners are not independently enumerated.");
            writer.WriteBoolean("provider_pooling", false);
            writer.WriteNumber("active_physical_connections", Interlocked.Read(ref s_activeConnections));
            writer.WriteNumber("opened_physical_connections", Interlocked.Read(ref s_openedConnections));
            writer.WriteNumber("closed_physical_connections", Interlocked.Read(ref s_closedConnections));
            writer.WriteNumber("active_orm_readers", Interlocked.Read(ref s_activeReaders));
            writer.WriteNumber("created_orm_readers", Interlocked.Read(ref s_createdReaders));
            writer.WriteNumber("closed_orm_readers", Interlocked.Read(ref s_closedReaders));
            writer.WriteNumber("disposal_failures", Interlocked.Read(ref s_disposalFailures));
            writer.WriteBoolean("new_physical_owners_disabled_after_retirement_fault", Volatile.Read(ref s_retirementFault) != 0);
            writer.WriteNumber("active_transactions", Interlocked.Read(ref s_activeTransactions));
            writer.WriteNumber("created_transactions", Interlocked.Read(ref s_createdTransactions));
            writer.WriteNumber("closed_transactions", Interlocked.Read(ref s_closedTransactions));
            ThreadPool.GetMaxThreads(out var maximumWorkerThreads, out var maximumIoThreads);
            writer.WriteStartObject("command_cache_runtime_bounds");
            writer.WriteNumber("threadpool_threads_observed_after_worker_retirement", ThreadPool.ThreadCount);
            writer.WriteNumber("threadpool_max_worker_threads", maximumWorkerThreads);
            writer.WriteNumber("threadpool_max_io_threads", maximumIoThreads);
            writer.WriteNumber("cache_soft_queue_capacity_per_prepared_site", 16);
            writer.WriteNumber("compiled_prepared_cache_call_sites", 36);
            writer.WriteString("scope", "Source-derived structure and actual public runtime thread limits; private ThreadStatic/queue contents are not reflected or modified. Bounds must include all participating live threads and N in-flight returns.");
            writer.WriteEndObject();
            if (pool is not null)
            {
                writer.WriteStartObject("adapter_physical_pool");
                writer.WriteNumber("capacity", pool.Capacity);
                writer.WriteNumber("active_leases", Interlocked.Read(ref pool.ActiveLeases));
                writer.WriteNumber("acquisitions", Interlocked.Read(ref pool.Acquisitions));
                writer.WriteNumber("returns", Interlocked.Read(ref pool.Returns));
                writer.WriteNumber("retirements", Interlocked.Read(ref pool.Retirements));
                writer.WriteNumber("replacements", Interlocked.Read(ref pool.Replacements));
                writer.WriteNumber("quarantined_slots", Interlocked.Read(ref pool.QuarantinedSlots));
                writer.WriteEndObject();
            }
#if SQLITE
            var memory = SqliteImage.SnapshotMemory();
            writer.WriteStartObject("sqlite_engine_memory");
            writer.WriteNumber("current_bytes", memory.Current);
            writer.WriteNumber("process_lifetime_highwater_bytes", memory.Highwater);
            writer.WriteNumber("retained_process_library_owners", 1);
            writer.WriteString("scope", "sqlite3_memory_used/sqlite3_memory_highwater(0), including initialization; this is SQLite allocator accounting, not the whole native heap. Zero may mean memory-status tracking is disabled.");
            writer.WriteEndObject();
#endif
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
        return document.RootElement.Clone();
    }

    private static void HashInteger(ref Digest digest, long row) => digest.Integer(row);
    private static void HashText(ref Digest digest, TextRow row) { digest.Integer(row.Id); digest.Text(row.Note); }
    private static void HashMoney(ref Digest digest, MoneyRow row) { digest.Integer(row.Id); digest.Money(row.Total); }
    private static void HashJson(ref Digest digest, JsonRow row) { digest.Integer(row.Id); digest.Text(row.Settings.Channel); digest.Boolean(row.Settings.Gift); }
    private static void HashJoin(ref Digest digest, JoinRow row) { digest.Integer(row.Id); digest.Text(row.Name); }
    private static void HashNullableMoney(ref Digest digest, decimal? row) => digest.NullableMoney(row);
#if SQLITE
    private static void HashNullableCents(ref Digest digest, long? row) =>
        digest.NullableMoney(row is { } cents ? cents / 100m : null);
#endif
    private static void HashCustomerTotal(ref Digest digest, CustomerTotalRow row) { digest.NullableInteger(row.CustomerId); digest.Integer(row.Count); digest.NullableMoney(row.Total); }
    private static void HashProductTotal(ref Digest digest, ProductTotalRow row) { digest.Text(row.Product); digest.NullableInteger(row.Quantity); }
    private static void HashRank(ref Digest digest, RankRow row) { digest.Integer(row.Id); digest.Integer(row.Rank); }
    private static void HashRunning(ref Digest digest, RunningRow row) { digest.Integer(row.Id); digest.NullableInteger(row.Quantity); }
    private static void HashDetail(ref Digest digest, DetailRow row) { digest.Integer(row.OrderId); digest.Text(row.Name); digest.Text(row.Product); digest.Integer(row.Quantity); }
    private static void HashRelated(ref Digest digest, RelatedRow row) { digest.Integer(row.Id); digest.Integer(row.TenantId); digest.Integer(row.OrderId); digest.Text(row.Product); digest.Integer(row.Quantity); }
    private static void HashWide(ref Digest digest, WideRow row)
    {
        digest.Integer(row.TenantId); digest.Integer(row.Id); digest.Integer(row.CreatedAt); digest.NullableInteger(row.CustomerId);
        digest.Money(row.Total); digest.Text(row.Note); digest.Integer(row.Version); digest.Text(row.Settings.Channel);
        digest.Boolean(row.Settings.Gift); digest.Text(row.InternalMemo);
    }
}
