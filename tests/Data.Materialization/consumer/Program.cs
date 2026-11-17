using System.Runtime.CompilerServices;
using NativeWeave.Data;
using NativeWeave.Database;
using NativeWeave.Database.PostgreSql;
using NativeWeave.Database.Sqlite;
using NativeWeave.Samples.Materialization;

if (args.Contains("--require-aot") && RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("Actual AOT execution required.");
var connection = Environment.GetEnvironmentVariable("NW_TEST_POSTGRES") ?? throw new InvalidOperationException("Actual PostgreSQL18 endpoint required.");
var path = Path.Combine(Path.GetTempPath(), "nw-b12-package-" + Guid.NewGuid().ToString("N") + ".db");
foreach (var source in new[] { SqliteDataSource.Create(path), PostgreSqlDataSource.Create(connection, o => o.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment) })
{
    await using (source)
    await using (var session = await source.OpenSessionAsync())
    {
        var table = "b12_package_" + Guid.NewGuid().ToString("N");
        var binary = source.Provider == DatabaseProvider.Sqlite ? "BLOB" : "BYTEA";
        var json = source.Provider == DatabaseProvider.Sqlite ? "TEXT" : "JSONB";
        await session.ExecuteAsync(SqlCommand.Create($"CREATE TABLE {table} (id BIGINT,name TEXT,bytes {binary},optional BIGINT,settings {json})", kind: SqlCommandKind.Mutation));
        var values = new SqlParameters().Add("id", 11L, Codecs.Int64).Add("name", "NuGet 🦀", Codecs.String)
            .Add("bytes", new byte[] { 1, 2, 3 }, Codecs.Bytes).Add("optional", (long?)null, Codecs.Nullable(Codecs.Int64))
            .Add("settings", new Envelope<Settings>(new(11, "static package")), MaterializationExamples.SettingsCodec);
        await session.ExecuteAsync(SqlCommand.Create($"INSERT INTO {table} VALUES (@id,@name,@bytes,@optional,@settings)", values, SqlCommandKind.Mutation));
        var command = SqlCommand.Create($"SELECT id,name,bytes,optional,settings FROM {table}");
        var rows = await MaterializationExamples.ReadAsync(session, command);
        if (rows.Count != 1 || rows[0].Id != 11 || rows[0].Optional is not null || rows[0].Settings.Value.Tag != "static package" || rows[0].Bytes[2] != 3) throw new InvalidOperationException("Package static DTO result");
        if (await MaterializationExamples.SumAsync(session, command) != 11) throw new InvalidOperationException("Package enumeration");
        await session.ExecuteAsync(SqlCommand.Create("DROP TABLE " + table, kind: SqlCommandKind.Mutation));
    }
}
File.Delete(path);
Console.WriteLine($"PASS B12 isolated NuGet consumer: actual SQLite + PostgreSQL DTO/JSON/enumeration; AOT={!RuntimeFeature.IsDynamicCodeSupported}");
