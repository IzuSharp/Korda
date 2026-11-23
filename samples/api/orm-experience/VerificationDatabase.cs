using Korda.Data;
using Korda.Database;
#if POSTGRES
using Korda.Database.PostgreSql;
#endif

// Verification setup only. The tutorial uses an application's own configured
// database. Each execution here gets a new database, so migrations and their
// retained history cannot collide with another run or an existing application.
internal sealed class VerificationDatabase : IAsyncDisposable
{
    internal string Connection { get; }
#if POSTGRES
    private readonly DataSource _administrator;
    private readonly string _name;
    private VerificationDatabase(string connection, DataSource administrator, string name)
    { Connection = connection; _administrator = administrator; _name = name; }
    internal static async Task<VerificationDatabase> Create(CancellationToken token)
    {
        var connection = Environment.GetEnvironmentVariable("NW_TEST_POSTGRES")
            ?? throw new InvalidOperationException("Provide a local test PostgreSQL role with CREATEDB permission.");
        var name = "korda_db_api_" + Guid.NewGuid().ToString("N");
        var administrator = PostgreSqlDataSource.Create(connection, options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment);
        try
        {
            await using var session = await administrator.OpenSessionAsync(token);
            await session.ExecuteAsync(SqlCommand.Create("CREATE DATABASE \"" + name + "\"", kind: SqlCommandKind.Mutation), token);
            var address = new UriBuilder(connection) { Path = name };
            return new(address.Uri.AbsoluteUri, administrator, name);
        }
        catch { await administrator.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var session = await _administrator.OpenSessionAsync();
            await session.ExecuteAsync(SqlCommand.Create("DROP DATABASE \"" + _name + "\"", kind: SqlCommandKind.Mutation));
        }
        finally { await _administrator.DisposeAsync(); }
    }
#else
    private VerificationDatabase(string file) => Connection = file;
    internal static Task<VerificationDatabase> Create(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new VerificationDatabase(Path.Combine(Path.GetTempPath(), "korda-db-api-" + Guid.NewGuid().ToString("N") + ".db")));
    }
    public ValueTask DisposeAsync()
    {
        foreach (var path in new[] { Connection, Connection + "-wal", Connection + "-shm" }) File.Delete(path);
        return ValueTask.CompletedTask;
    }
#endif
}
