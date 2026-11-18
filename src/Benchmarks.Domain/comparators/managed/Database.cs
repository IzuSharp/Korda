using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace NativeWeave.Reference.TaskService;

/// <summary>Private bounded ADO.NET admission. It is part of this comparator, not a NativeWeave provider.</summary>
internal sealed class DatabaseStore : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly NpgsqlDataSource? _postgres;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _waiters;
    private readonly object _gate = new();
    private readonly List<Task> _retirements = [];
    private readonly List<DatabaseLease> _quarantined = [];
    private readonly int _maximum;
    internal bool IsRetired { get { lock (_gate) return _quarantined.Count == 0 && _slots.CurrentCount == _maximum && _retirements.All(task => task.IsCompletedSuccessfully); } }
    internal bool IsPostgreSql { get; }

    internal DatabaseStore(ServiceConfiguration configuration, bool consumer = false)
    {
        IsPostgreSql = !consumer && configuration.Provider == "postgresql";
        _connectionString = IsPostgreSql ? configuration.PostgreSqlConnectionString() : ServiceConfiguration.SqliteConnectionString(consumer ? configuration.ConsumerDatabase : configuration.Database);
        if (IsPostgreSql) _postgres = new NpgsqlSlimDataSourceBuilder(_connectionString).Build();
        _maximum = consumer ? 2 : 16; _slots = new(_maximum, _maximum); _waiters = new(consumer ? 4 : 64, consumer ? 4 : 64);
    }

    internal async Task<DatabaseLease> OpenAsync(CancellationToken token)
    {
        if (!_slots.Wait(0))
        {
            if (!_waiters.Wait(0)) throw new ApplicationProblem(503, "database_capacity");
            try { if (!await _slots.WaitAsync(TimeSpan.FromSeconds(5), token)) throw new ApplicationProblem(503, "database_admission_timeout"); }
            finally { _waiters.Release(); }
        }
        DbConnection? connection = null;
        try
        {
            if (_postgres is not null) connection = await _postgres.OpenConnectionAsync(token);
            else { connection = new SqliteConnection(_connectionString); await connection.OpenAsync(token); }
            return new(this, connection);
        }
        catch { if (connection is not null) await connection.DisposeAsync(); _slots.Release(); throw; }
    }

    internal async Task MigrateAsync(string resource, CancellationToken token)
    {
        await using var lease = await OpenAsync(token);
        if (!IsPostgreSql) await lease.ExecuteAsync("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", token);
        await lease.BeginAsync(token);
        await lease.ExecuteAsync("CREATE TABLE IF NOT EXISTS __b23_migrations(version BIGINT PRIMARY KEY,checksum TEXT NOT NULL)", token);
        using Stream input = typeof(DatabaseStore).Assembly.GetManifestResourceStream("NativeWeave.Reference.TaskService.Migrations." + resource + ".sql")
            ?? throw new InvalidOperationException("The explicit schema resource is missing.");
        using StreamReader reader = new(input, Canonical.Utf8);
        string sql = await reader.ReadToEndAsync(token); string hash = Canonical.Hash(Canonical.Utf8.GetBytes(sql));
        object? existing = await lease.ScalarAsync("SELECT checksum FROM __b23_migrations WHERE version=1", token);
        if (existing is null)
        {
            await lease.ExecuteAsync(sql, token);
            await lease.ExecuteAsync("INSERT INTO __b23_migrations(version,checksum) VALUES(1,@hash)", token, ("hash", hash));
        }
        else if (!Equals(existing, hash)) throw new InvalidOperationException("Comparator migration checksum mismatch.");
        await lease.CommitAsync(token);
    }

    internal async ValueTask RetireAsync(DatabaseLease lease)
    {
        Task retirement = CompleteRetirementAsync(lease);
        lock (_gate) { _retirements.RemoveAll(task => task.IsCompleted); _retirements.Add(retirement); }
        // Keep the DB admission permit and durable cleanup identity until actual retirement.
        await Task.WhenAny(retirement, Task.Delay(TimeSpan.FromMilliseconds(100)));
    }

    private async Task CompleteRetirementAsync(DatabaseLease lease)
    {
        try
        {
            try
            {
                if (lease.Transaction is not null)
                {
                    if (!lease.Committed && !lease.Unknown)
                    {
                        try
                        {
                            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                            await lease.Transaction.RollbackAsync(deadline.Token);
                            lease.ConfirmedRollback?.Invoke();
                        }
                        catch (Exception error) { Console.Error.WriteLine("B23_ROLLBACK_UNCONFIRMED " + error.GetType().Name); }
                    }
                    await lease.Transaction.DisposeAsync();
                }
            }
            finally { await lease.Connection.DisposeAsync(); }
            _slots.Release();
        }
        catch (Exception error)
        {
            // A failed close cannot recycle its permit or disappear from the shutdown receipt.
            lock (_gate) _quarantined.Add(lease);
            Console.Error.WriteLine("B23_CLEANUP_FAILURE " + error.GetType().Name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending; lock (_gate) pending = _retirements.ToArray();
        await Task.WhenAll(pending);
        if (_postgres is not null) await _postgres.DisposeAsync();
    }
}

internal sealed class DatabaseLease(DatabaseStore owner, DbConnection connection) : IAsyncDisposable
{
    internal DbConnection Connection { get; } = connection;
    internal DbTransaction? Transaction { get; private set; }
    internal bool Committed { get; private set; }
    internal bool Unknown { get; private set; }
    internal Action? ConfirmedRollback { get; set; }

    internal async Task BeginAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Transaction = Connection is SqliteConnection sqlite ? sqlite.BeginTransaction(deferred: false)
            : await Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
    }

    internal async Task CommitAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await Transaction!.CommitAsync(deadline.Token); Committed = true; }
        catch { Unknown = true; throw; }
    }

    private DbCommand Command(string sql, (string Name, object Value)[] arguments)
    {
        var command = Connection.CreateCommand(); command.CommandText = sql; command.Transaction = Transaction;
        command.CommandTimeout = owner.IsPostgreSql ? 15 : 5;
        foreach (var argument in arguments)
        {
            var parameter = command.CreateParameter(); parameter.ParameterName = argument.Name; parameter.Value = argument.Value;
            parameter.DbType = argument.Value switch { long => DbType.Int64, int => DbType.Int32, byte[] => DbType.Binary, string => DbType.String, _ => throw new InvalidOperationException("Unsupported fixed comparator parameter.") };
            command.Parameters.Add(parameter);
        }
        return command;
    }

    internal async Task<int> ExecuteAsync(string sql, CancellationToken token, params (string Name, object Value)[] arguments)
    { await using var command = Command(sql, arguments); return await command.ExecuteNonQueryAsync(token); }

    internal async Task<object?> ScalarAsync(string sql, CancellationToken token, params (string Name, object Value)[] arguments)
    { await using var command = Command(sql, arguments); object? value = await command.ExecuteScalarAsync(token); return value is DBNull ? null : value; }

    internal async Task<List<T>> QueryAsync<T>(string sql, Func<DbDataReader, T> project, CancellationToken token, params (string Name, object Value)[] arguments)
    {
        await using var command = Command(sql, arguments);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, token);
        List<T> rows = [];
        while (await reader.ReadAsync(token)) { if (rows.Count >= 128) throw new InvalidOperationException("Comparator query exceeded its row cap."); rows.Add(project(reader)); }
        return rows;
    }

    public ValueTask DisposeAsync() => owner.RetireAsync(this);
}
