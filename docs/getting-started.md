# Use Korda from C#

When its preview is available on nuget.org, install the provider for your .NET 11 Linux x64 application:

```sh
dotnet add package Korda.Database.Sqlite --prerelease
# For PostgreSQL:
dotnet add package Korda.Database.PostgreSql --prerelease
```

Each provider contains the shared managed assemblies, generator, build integration and its precompiled native library in one package. PostgreSQL applications need a reachable database server. For an unpublished development build, the candidate feed is `artifacts/public/feed` and its manifest records the exact versions.

For existing models, apply the [database annotation rename](guides/db-attribute-upgrade.md) before rebuilding.

## 1. Define ordinary C# models

The bundled source generator creates static descriptors. A parameterless
`[DbTable]` preserves the CLR type/property spelling and infers a nonnullable
`Id` key. Use exact names and explicit keys for an existing schema:

```csharp
using Korda.Data;

[DbTable("orders", DescriptorName = "Orders")]
public sealed record Order(
    [property: DbKey] long Id, string? Note,
    [property: DbVersion] long Version);

[DbProjection(DescriptorName = "OrderCards")]
public sealed record OrderCard(long Id, string? Note);
```

The DTO has no table and needs no mutable setters. Its generated `Bind` selects
exactly the expressions passed by the caller; its `Row` reads the same result
aliases from parameterized SQL. See [model generation](guides/query-generator.md)
for constructor selection, custom IDs and reusable decimal/JSON codec policies.

## 2. Register one datasource and open a session

Install `Microsoft.Extensions.DependencyInjection` if the application does not
already use it. The provider package works with ordinary DI registration:

```csharp
using Korda.Database;
using Korda.Database.Sqlite;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddSingleton<DataSource>(_ => new DataSourceBuilder()
    .UseSqlite("orders.db")
    .ConfigureLimits(new() { MaxBatchRows = 64, MaxBufferedRows = 256 })
    .WithName("orders")
    .Build());

await using var container = services.BuildServiceProvider();
var source = container.GetRequiredService<DataSource>();
await using var session = await source.OpenSessionAsync(token);
```

For PostgreSQL use `Korda.Database.PostgreSql` and `.UsePostgreSql(connection)`.
Configure its [TLS and explicit connection policy](guides/data-postgres.md).
`DataSource` owns the pool. Sessions are short-lived, admit one operation at a
time, and must be disposed after their queries/streams finish. The container
disposes the singleton asynchronously at shutdown.

## 3. Review schema, then query and update

Creating a datasource does not change a schema. In an explicit deployment step,
build a model snapshot, draft SQL, review it and apply the selected migration:

```csharp
var model = DatabaseModel.Empty.Add(Orders.Table);
var draft = await source.Administration.DraftMigrationAsync(
    DatabaseModel.Empty, model, token);
var migration = draft.ToMigration(1, "Create order schema");
Console.WriteLine(migration.Sql); // Review and retain this SQL before deployment.
// After review, in the deployment command:
await source.Administration.MigrateAsync(
    new MigrationBundle(source.Provider, [migration]), cancellationToken: token);
```

Retain `model.ToJson()` as the next comparison input. Removal of a column/table
requires explicitly selected destructive change IDs. The
[migration guide](guides/data-migrations.md) covers supported differences and
generating a compilable entity from an existing table.

```csharp
using Korda.Query;

using var query = session.From(Orders.Table);
query.WhereIf(minimumId.HasValue, row => row.GreaterThanOrEqual(Orders.Id, minimumId!.Value))
    .OrderBy(Orders.Id).Take(20);
var page = await query.Select(OrderCards.Bind(query,
    Id: query.Column(Orders.Id), Note: query.Column(Orders.Note))).ToListAsync(token);

var key = Orders.Table.Key(Orders.Id, orderId);
await session.UpdateCheckedAsync(Orders.Table, key,
    new Changes<Order>().Set(Orders.Note, (string?)null).Set(Orders.Version, version + 1),
    new ConcurrencyCheck<Order>().Expect(Orders.Version, version), token);
```

Omitted members are unchanged; `Set(..., null)` writes SQL NULL. A zero-row
checked write throws `ConcurrencyConflictException`. Wrap related writes in
`session.InTransactionAsync((tx, ct) => ...)`; a result is returned only after
confirmed commit. [Transaction outcomes](guides/data-transactions.md) explain
cancellation, rollback and recovery after an unknown commit result.

The executable [order/customer/detail example](../samples/api/orm-experience/)
adds a left join, aggregation, composite-key cursor, one batched detail load,
insert-returning, SQL DTO reuse and real business assertions for each provider.

## 4. Diagnose at the application boundary

Catch `DatabaseException` and log its `Kind`, `ProviderCode` and `CorrelationId`
with the application's operation ID. A `TransactionFailure` attached to a
callback exception preserves `Outcome`, `CommitAttempted`, `Cleanup` and
`RecoveryKey`. Keep parameter values and credentials out of logs. An unknown
write outcome requires reconciliation with the application's recovery identity;
`IsTransient` is not permission to replay a write.

The order example also records actual native SQL dispatches and libpq protocol
submissions for the page/detail pair. These are isolated diagnostic deltas;
protocol submissions are not a count of TCP packets or measured network round
trips. The [experience guide](guides/orm-experience.md) describes their scope.

A business method can be called directly by a console app, a standard ASP.NET Core endpoint or a standard C# gRPC method. Install and configure the chosen host independently.

## Build and publish

```sh
dotnet restore
dotnet publish -c Release -r linux-x64 -p:PublishAot=true -o publish
./publish/YourApplication
```

Native AOT needs the platform C linker prerequisites. Applications use the precompiled provider library restored from NuGet. The strict package targets validate the exact restored graph and selected native bytes. Do not suppress a closure error or substitute a native binary.

Each public package has its own CI job that restores the exact package and runs a compiled Native AOT consumer against its database. The internal compatibility suite also exercises combined graphs. See [packaging](guides/packaging-domain.md) for reproducing those checks.
