using NativeWeave.Data;
using NativeWeave.Database;
using NativeWeave.Database.Sqlite;
using NativeWeave.Database.PostgreSql;

// Both providers execute the same ordinary application code. PostgreSQL requires
// NW_TEST_POSTGRES for this local example; production keeps the verified TLS default.
var builder = new DataSourceBuilder().ConfigureLimits(new DatabaseLimits
{
    MaxConnections = 2,
    MaxBatchRows = 2,
    MaxBufferedRows = 32,
    MaxBufferedBytes = 64 * 1024
}).WithName("host-guide");
var provider = args.FirstOrDefault() ?? "sqlite";
var file = Path.Combine(Path.GetTempPath(), "nativeweave-host-" + Guid.NewGuid().ToString("N") + ".db");
if (provider == "postgresql")
    builder.UsePostgreSql(Environment.GetEnvironmentVariable("NW_TEST_POSTGRES") ?? throw new InvalidOperationException("Set NW_TEST_POSTGRES for the isolated local PostgreSQL example."), options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment);
else if (provider == "sqlite") builder.UseSqlite(file);
else throw new ArgumentException("Choose sqlite or postgresql.");

await using (var source = builder.Build())
{
    await using var session = await source.OpenSessionAsync();
    var table = "host_sample_" + Guid.NewGuid().ToString("N");
    await session.ExecuteAsync(SqlCommand.Create("CREATE TABLE " + table + " (id BIGINT PRIMARY KEY, name TEXT NOT NULL)", kind: SqlCommandKind.Mutation));
    try
    {
        // Minimal: typed values are bound in Rust. The unique table identifier is
        // application-owned trusted SQL, never a value hole from external input.
        await session.ExecuteAsync(SqlCommand.Create("INSERT INTO " + table + " VALUES (@id,@name)",
            new SqlParameters().Add("id", 1L, Codecs.Int64).Add("name", "Ada", Codecs.String), SqlCommandKind.Mutation));
        var rows = Projection<Person>.Create([ResultField.Create("id", Codecs.Int64), ResultField.Create("name", Codecs.String)],
            row => new Person(row.Read(0, Codecs.Int64), row.Read(1, Codecs.String)));
        var people = await session.QueryAsync(SqlCommand.Create("SELECT id,name FROM " + table + " ORDER BY id"), rows);
        if (people.Count != 1 || people[0] != new Person(1, "Ada")) throw new InvalidOperationException("Minimal mapping failed.");

        // Advanced: an explicit transaction and savepoint own the same session;
        // disposing every cursor precedes further commands on that session.
        await using (var transaction = await session.BeginTransactionAsync(new TransactionOptions { RecoveryKey = "host-example" }))
        {
            await session.ExecuteAsync(SqlCommand.Create("INSERT INTO " + table + " VALUES (2,'Grace')", kind: SqlCommandKind.Mutation));
            await using (var savepoint = await transaction.CreateSavepointAsync())
            {
                await session.ExecuteAsync(SqlCommand.Create("INSERT INTO " + table + " VALUES (3,'temporary')", kind: SqlCommandKind.Mutation));
                await savepoint.RollbackAsync();
                await savepoint.ReleaseAsync();
            }
            await transaction.CommitAsync();
            if (transaction.Outcome != TransactionOutcome.Committed) throw new InvalidOperationException("Commit was not confirmed.");
        }
        await foreach (var person in session.StreamAsync(SqlCommand.Create("SELECT id,name FROM " + table + " ORDER BY id"), rows))
        {
            Console.WriteLine($"{source.Provider}: {person.Id} {person.Name}");
            break; // await foreach disposes the native cursor even on early exit.
        }
        if (await session.ScalarAsync(SqlCommand.Create("SELECT COUNT(*) FROM " + table), Codecs.Int64) != 2)
            throw new InvalidOperationException("Savepoint or early-cursor cleanup failed.");
        Console.WriteLine("minimal, transaction, savepoint, scalar and early stream exit passed");
    }
    finally { await session.ExecuteAsync(SqlCommand.Create("DROP TABLE " + table, kind: SqlCommandKind.Mutation)); }
}
if (provider == "sqlite") File.Delete(file);
internal sealed record Person(long Id, string Name);
