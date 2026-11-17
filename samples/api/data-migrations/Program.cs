using NativeWeave.Database;
using NativeWeave.Database.PostgreSql;
using NativeWeave.Database.Sqlite;

var provider = args.FirstOrDefault() == "postgresql" ? DatabaseProvider.PostgreSql : DatabaseProvider.Sqlite;
var connection = provider == DatabaseProvider.PostgreSql
    ? Environment.GetEnvironmentVariable("NW_TEST_POSTGRES") ?? throw new InvalidOperationException("Provide the PostgreSQL test URI.")
    : args.ElementAtOrDefault(1) ?? Path.Combine(Path.GetTempPath(), "nativeweave-migrations-sample.db");
// Local-development TLS opt-out is deliberate in this runnable test example.
var builder = provider == DatabaseProvider.Sqlite ? new DataSourceBuilder().UseSqlite(connection)
    : new DataSourceBuilder().UsePostgreSql(connection, options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment);
await using var source = builder.Build();
var bundle = new MigrationBundle(provider,
[
    new(1, "Create notes", "CREATE TABLE notes(id INTEGER PRIMARY KEY, body TEXT NOT NULL)"),
    new(2, "Seed note", "INSERT INTO notes VALUES(1,'explicit deployment migration')")
]);
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await source.Administration.ValidateMigrationsAsync(bundle, cancellation.Token);
var report = await source.Administration.MigrateAsync(bundle, new() { LockTimeout = TimeSpan.FromSeconds(10) }, cancellation.Token);
Console.WriteLine($"Applied={report.AppliedCount}; current={report.CurrentVersion}; complete={report.Completed}");
foreach (var status in await source.Administration.GetMigrationStatusAsync(bundle, cancellation.Token))
    Console.WriteLine($"Migration {status.Version}: applied={status.Applied}; SQLx checksum={status.Checksum}");
var table = (await source.Administration.GetTablesAsync(cancellationToken: cancellation.Token)).Single(value => value.Name == "notes");
Console.WriteLine($"Catalog {table.Schema}.{table.Name}: {table.Columns.Count} columns");
