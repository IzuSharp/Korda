using System.Data.Common;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Dapper;
#if POSTGRES
using ProviderConnection = Npgsql.NpgsqlConnection;
#else
using ProviderConnection = Microsoft.Data.Sqlite.SqliteConnection;
#endif

internal sealed class DatabaseDriver : IAsyncDisposable
{
    private readonly string _connectionString;
    private int _connections;
    internal string? NativePath { get; }
    internal string? NativeHash { get; }
    internal string DatabaseVersion { get; private set; } = "unknown";
    internal string[] CompileOptions { get; private set; } = [];
#if POSTGRES
    internal string Provider => "Npgsql";
#else
    internal string Provider => "Microsoft.Data.Sqlite.Core";
#endif
    internal DatabaseDriver(Options options)
    {
#if POSTGRES
        Npgsql.NpgsqlConnectionStringBuilder builder;
        if (options.Connection.StartsWith("postgresql://",StringComparison.Ordinal))
        {
            var uri=new Uri(options.Connection);
            builder=new() { Host=uri.Host,Port=uri.Port<0?5432:uri.Port,Username=uri.UserInfo,Database=uri.AbsolutePath.Trim('/'),SslMode=Npgsql.SslMode.Disable };
        }
        else builder=new(options.Connection);
        builder.MaxPoolSize=options.Concurrency+1;builder.MinPoolSize=0;builder.Pooling=true;
        builder.MaxAutoPrepare=0;
        _connectionString=builder.ConnectionString;
#else
        if (options.NativeLibrary is null) throw new ArgumentException("--native-library must select the exact matching SQLite build.");
        var path=Path.GetFullPath(options.NativeLibrary); var handle=NativeLibrary.Load(path);
        NativeLibrary.SetDllImportResolver(typeof(SQLitePCL.SQLite3Provider_sqlite3).Assembly,(name,_,_)=>name=="sqlite3"?handle:nint.Zero);
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
        var mapped=File.ReadLines("/proc/self/maps").Select(line=>line.Split(' ',6,StringSplitOptions.RemoveEmptyEntries))
            .Where(parts=>parts.Length==6&&Path.GetFileName(parts[5])=="libsqlite3.so").Select(parts=>parts[5]).Distinct().ToArray();
        if(mapped.Length!=1||Path.GetFullPath(mapped[0])!=path) throw new InvalidOperationException("Selected SQLite native library is not the sole actually mapped sqlite3 image.");
        NativePath=mapped[0]; using(var file=File.OpenRead(NativePath)) NativeHash=Convert.ToHexStringLower(SHA256.HashData(file));
        var builder=new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource=options.Connection,Pooling=true,ForeignKeys=true,DefaultTimeout=5 };
        _connectionString=builder.ConnectionString;
#endif
    }
    internal async Task<DbConnection> OpenSessionAsync()
    {
        var connection=new ProviderConnection(_connectionString);
        try
        {
            await connection.OpenAsync();
#if !POSTGRES
            await connection.ExecuteAsync("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;");
#endif
            Interlocked.Increment(ref _connections);return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    internal async ValueTask CloseSessionAsync(DbConnection connection)
    {
        await connection.DisposeAsync();
        if(connection.State!=System.Data.ConnectionState.Closed)throw new InvalidOperationException("Disposed provider connection remained open.");
        Interlocked.Decrement(ref _connections);
    }
    internal async Task VerifyDataset(Options options)
    {
        var connection=await OpenSessionAsync();
        try
        {
            DatabaseVersion=connection.ServerVersion;
            if(await connection.ExecuteScalarAsync<long>("SELECT tenants FROM db_perf_metadata")!=options.Tenants
                ||await connection.ExecuteScalarAsync<long>("SELECT orders_per_tenant FROM db_perf_metadata")!=options.OrdersPerTenant)
                throw CorrectnessOracle.Failure("Dataset metadata differs from requested oracle inputs.", "dataset");
#if !POSTGRES
            CompileOptions=connection.Query<string>("PRAGMA compile_options").Order(StringComparer.Ordinal).ToArray();
#endif
        }
        finally { await CloseSessionAsync(connection); }
    }
    internal OwnershipCounts Read() => new(Volatile.Read(ref _connections),"Application-owned logical connections only; internal provider commands/readers/native owners not instrumented");
    public ValueTask DisposeAsync()
    {
#if POSTGRES
        Npgsql.NpgsqlConnection.ClearAllPools();
#else
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
#endif
        return ValueTask.CompletedTask;
    }
}
internal sealed record OwnershipCounts(int ApplicationConnections,string Scope);
