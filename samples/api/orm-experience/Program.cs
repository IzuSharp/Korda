using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Korda.Data;
using Korda.Database;
using Korda.Query;
using Microsoft.Extensions.DependencyInjection;
#if POSTGRES
using Korda.Database.PostgreSql;
#else
using Korda.Database.Sqlite;
#endif

if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("This consumer must run as Native AOT.");
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var token = deadline.Token;
await using var database = await VerificationDatabase.Create(token);
var services = new ServiceCollection();
services.AddSingleton<DataSource>(_ =>
{
    var builder = new DataSourceBuilder().WithName("orders")
        .ConfigureLimits(new() { MaxBatchRows = 32, MaxBufferedRows = 256, MaxFieldBytes = 1024 });
#if POSTGRES
    builder.UsePostgreSql(database.Connection,
        options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment);
#else
    builder.UseSqlite(database.Connection);
#endif
    return builder.Build();
});
await using var container = services.BuildServiceProvider();
var source = container.GetRequiredService<DataSource>();
await using var session = await source.OpenSessionAsync(token);
await Business.Run(source, session, token);

internal static class Business
{
    private static readonly List<string> Checks = [];
    private static void Check(string name, bool condition)
    {
        if (!condition) throw new InvalidOperationException(name);
        Checks.Add(name);
    }

    internal static async Task Run(DataSource source, DataSession session, CancellationToken token)
    {
        var model = DatabaseModel.Empty.Add(Customers.Table).Add(Orders.Table).Add(OrderLines.Table);
        var snapshot = DatabaseModel.Parse(model.ToJson());
        var draft = await source.Administration.DraftMigrationAsync(DatabaseModel.Empty, snapshot, token);
        Check("model-generates-reviewable-create-sql", draft.Changes.Count == 3 && draft.Changes.All(change => !change.IsDestructive));
        var migration = draft.ToMigration(1, "Create the reviewed order schema");
        // Deployment is explicit. The generated SQL can be written to a file and
        // reviewed before creating the trusted MigrationBundle.
        await source.Administration.MigrateAsync(new(source.Provider, [migration]), cancellationToken: token);
        await session.InsertAsync(Customers.Table, new Customer { Id = 10, Name = "Ada", Email = null }, token);
        await session.InsertAsync(Customers.Table, new Customer { Id = 20, Name = "Grace", Email = "grace@example.invalid" }, token);
        foreach (var order in new[]
        {
            new Order(7, new(1), 100, 10, 12.34m, "keep", 1, new("web", false), ""),
            new Order(7, new(2), 100, 10, 20.00m, null, 1, new("mobile", true), ""),
            new Order(7, new(3), 100, null, 9.99m, null, 1, new("web", false), ""),
            new Order(7, new(4), 101, 20, 5.00m, null, 1, new("store", false), ""),
            new Order(8, new(1), 100, 20, 99.00m, null, 1, new("other", false), "")
        }) await session.InsertAsync(Orders.Table, order, token);
        foreach (var line in new[]
        {
            new OrderLine(1, 7, new(1), "book", 2), new OrderLine(2, 7, new(1), "pen", 1),
            new OrderLine(3, 7, new(3), "paper", 3), new OrderLine(4, 8, new(1), "other tenant", 50)
        }) await session.InsertAsync(OrderLines.Table, line, token);

#if POSTGRES
        const string large = "repeat('x', 8192)";
#else
        const string large = "replace(hex(zeroblob(8192)), '00', 'x')";
#endif
        await session.ExecuteAsync(SqlCommand.Create("UPDATE orders SET \"InternalMemo\"=" + large + " WHERE \"TenantId\"=7 AND \"Id\"=3", kind: SqlCommandKind.Mutation), token);
        using var diagnostics = new NativeDiagnostics(source.Provider);
        var before = diagnostics.Read();
        var page = await Page(session, null, null, token);
        var relation = Relation<Order, OrderLine>.Create(Orders.Table, Orders.TenantId, OrderLines.Table, OrderLines.TenantId)
            .And(Orders.Id, OrderLines.OrderId);
        var details = await session.LoadRelatedAsync(page.Select(row => Key(row.TenantId, row.Id)).ToArray(), relation, token);
        var after = diagnostics.Read();
        Check("one-page-plus-one-detail-sql", after.Sql - before.Sql == 2);
        Check("page-results-and-empty-association", page.Select(row => row.Id.Value).SequenceEqual([1L, 2L])
            && page.All(row => row.Customer == "Ada") && details[0].Count == 2 && details[1].Count == 0);
        var emptyBefore = diagnostics.Read();
        var empty = await session.LoadRelatedAsync(Array.Empty<EntityKey<Order>>(), relation, token);
        Check("empty-batch-no-sql", empty.Count == 0 && diagnostics.Read().Sql == emptyBefore.Sql);
        var continuation = await Page(session, page[^1], null, token);
        Check("composite-cursor-ties-and-no-overfetch", continuation.Select(row => row.Id.Value).SequenceEqual([3L, 4L])
            && continuation[0].Customer is null && continuation[0].Total == 9.99m && continuation[0].Score == 110);
        Check("empty-final-page", (await Page(session, continuation[^1], null, token)).Count == 0);
        Check("optional-customer-filter", (await Page(session, null, 20, token)).Single().Id.Value == 4);
        await SharedDto(session, page, token);
        await Aggregate(session, token);
        await Writes(session, token);
        await SchemaReview(source, model, token);
#if SCAFFOLD_COMPILED
        using (var generated = session.From(Reverse.CustomerCopyMapping.Table))
            Check("scaffolded-entity-compiled-and-executed", (await generated.SelectAll().ToListAsync(token)).Count == 2);
        var reversedKey = Reverse.OrderCopyMapping.Table.Key(Reverse.OrderCopyMapping.TenantId, 7L)
            .And(Reverse.OrderCopyMapping.Id, new OrderId(1));
        var reversed = await session.FindAsync(Reverse.OrderCopyMapping.Table, reversedKey, token);
        Check("scaffolded-composite-key-compiled-and-executed", reversed?.Id.Value == 1 && reversed.TenantId == 7
            && reversed.Total == 12.34m && reversed.Settings == new OrderSettings("web", false));
#endif
        var native = diagnostics.Identity();
        Console.WriteLine("DB_API_RESULT " + System.Text.Json.JsonSerializer.Serialize(new Receipt(
            source.Provider.ToString(), !RuntimeFeature.IsDynamicCodeSupported,
#if DTO_EDIT
            "edited",
#else
            "original",
#endif
            Checks.ToArray(), after.Sql - before.Sql, after.Protocol - before.Protocol, native.Hash), ReceiptJson.Default.Receipt));
    }

    private static EntityKey<Order> Key(long tenant, OrderId id) => Orders.Table.Key(Orders.TenantId, tenant).And(Orders.Id, id);

    private static async Task<IReadOnlyList<OrderCard>> Page(DataSession session, OrderCard? last, long? customerId, CancellationToken token)
    {
        using var query = session.From(Orders.Table);
        query.WhereEqual(Orders.TenantId, 7L).WhereIf(customerId.HasValue, row => row.Equal(Orders.CustomerId, customerId));
        var customer = query.LeftJoin(Customers.Table, "customer", on => on.Left(Orders.CustomerId).Equal(on.Right(Customers.Id).AsNullable()));
        query.OrderBy(Orders.CreatedAt).ThenBy(Orders.TenantId).ThenBy(Orders.Id).Take(2);
        if (last is not null) query.SeekAfter(QueryCursor<Order>.Create(Orders.CreatedAt, last.CreatedAt).Then(Orders.TenantId, last.TenantId).Then(Orders.Id, last.Id));
        return await query.Select(OrderCards.Bind(query,
            TenantId: query.Column(Orders.TenantId), Id: query.Column(Orders.Id), CreatedAt: query.Column(Orders.CreatedAt),
            Customer: query.NullableReference(customer, Customers.Name), Total: query.Column(Orders.Total), Score: query.Column(Orders.CreatedAt).Add(10L)
#if DTO_EDIT
            , Version: query.Column(Orders.Version)
#endif
            )).ToListAsync(token);
    }

    private static async Task SharedDto(DataSession session, IReadOnlyList<OrderCard> expected, CancellationToken token)
    {
        const string sql = "SELECT o.\"TenantId\",o.\"Id\" AS order_id,o.\"CreatedAt\",c.\"Name\" AS customer_name,o.\"Total\",o.\"CreatedAt\"+10 AS score"
#if DTO_EDIT
            + ",o.\"Version\""
#endif
            + " FROM orders o LEFT JOIN customers c ON o.\"CustomerId\"=c.\"Id\" WHERE o.\"TenantId\"=@tenant AND o.\"Id\"<=@maximum ORDER BY o.\"CreatedAt\",o.\"TenantId\",o.\"Id\"";
        var parameters = new SqlParameters().Add("tenant", 7L, Codecs.Int64).Add("maximum", new OrderId(2), OrderCodecs.Id);
        var actual = await session.QueryAsync(SqlCommand.Create(sql, parameters), OrderCards.Row, token);
        Check("same-immutable-dto-for-fluent-and-parameterized-sql", actual.SequenceEqual(expected));
    }

    private static async Task Aggregate(DataSession session, CancellationToken token)
    {
        using var query = session.From(Orders.Table);
        var customer = query.Column(Orders.CustomerId);
        query.WhereEqual(Orders.TenantId, 7L).GroupBy(customer).Having(query.CountRows().GreaterThan(0L));
        var groups = await query.Select(CustomerSummaries.Bind(query, CustomerId: customer, Count: query.CountRows(),
            TotalSpent: SqlFunctions.Sum(query.Column(Orders.Total)))).ToListAsync(token);
        Check("typed-grouped-aggregate-dto", groups.Count == 3 && groups.Single(row => row.CustomerId == 10).Count == 2
            && groups.Single(row => row.CustomerId == 10).TotalSpent == 32.34m
            && groups.Single(row => row.CustomerId is null).TotalSpent == 9.99m);
    }

    private static async Task Writes(DataSession session, CancellationToken token)
    {
        var key = Key(7, new(1));
        var original = await session.FindAsync(Orders.Table, key, token) ?? throw new InvalidOperationException("Missing order.");
        Check("custom-id-decimal-json-and-nullable-mapping", original.Id.Value == 1 && original.Total == 12.34m
            && original.Settings == new OrderSettings("web", false) && (await session.FindAsync(Customers.Table, Customers.Table.Key(Customers.Id, 10L), token))!.Email is null);
        await session.UpdateCheckedAsync(Orders.Table, key, new Changes<Order>().Set(Orders.Note, (string?)null).Set(Orders.Version, 2L),
            new ConcurrencyCheck<Order>().Expect(Orders.Version, 1L), token);
        var changed = (await session.FindAsync(Orders.Table, key, token))!;
        Check("omitted-fields-versus-written-null", changed.Note is null && changed.Total == original.Total && changed.Settings == original.Settings);
        try
        {
            await session.UpdateCheckedAsync(Orders.Table, key, new Changes<Order>().Set(Orders.Total, 8m), new ConcurrencyCheck<Order>().Expect(Orders.Version, 1L), token);
            throw new InvalidOperationException("Stale version accepted.");
        }
        catch (ConcurrencyConflictException) { Check("stale-version-rejected", true); }
        var returned = await session.InTransactionAsync(async (tx, ct) => await tx.InsertReturningAsync(Orders.Table,
            new Order(7, new(5), 102, null, 6.25m, null, 1, new("callback", false), ""), Orders.Table.Row, ct), cancellationToken: token);
        Check("insert-returning-and-confirmed-callback", returned.Id.Value == 5 && (await session.FindAsync(Orders.Table, Key(7, returned.Id), token)) is not null);
        var sentinel = new InvalidOperationException("business failure");
        try
        {
            await session.InTransactionAsync(async (tx, ct) =>
            {
                await tx.UpdateAsync(Orders.Table, key, new Changes<Order>().Set(Orders.Note, "rolled back"), ct);
                throw sentinel;
            }, cancellationToken: token);
        }
        catch (InvalidOperationException error) when (ReferenceEquals(error, sentinel))
        {
            Check("original-error-and-confirmed-rollback", TransactionFailure.TryGet(error, out var failure)
                && failure.Outcome == TransactionOutcome.RolledBack && !failure.CommitAttempted);
        }
        Check("rollback-business-result", (await session.FindAsync(Orders.Table, key, token))!.Note is null);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var invoked = false;
        try { await session.InTransactionAsync((_, _) => { invoked = true; return Task.CompletedTask; }, cancellationToken: canceled.Token); }
        catch (OperationCanceledException) { Check("precancel-does-not-invoke-callback", !invoked); }
    }

    private static async Task SchemaReview(DataSource source, DatabaseModel model, CancellationToken token)
    {
        var destructive = await source.Administration.DraftMigrationAsync(model, DatabaseModel.Empty, token);
        var refused = false;
        try { destructive.ToMigration(2, "Review required"); }
        catch (InvalidOperationException) { refused = true; }
        Check("destructive-migration-needs-explicit-selection", refused);
        var approved = destructive.ToMigration(2, "Explicitly reviewed table removals", destructive.Changes.Select(change => change.Id).ToArray());
        Check("explicit-destructive-script-is-reviewable", approved.Sql.Contains("DROP TABLE", StringComparison.Ordinal));
        // The destructive example is inspected, not applied to business data.
        var tables = await source.Administration.GetTablesAsync(cancellationToken: token);
        var customerTable = tables.Single(table => table.Name == "customers");
        var folder = Environment.GetEnvironmentVariable("DB_API_SCAFFOLD");
        if (folder is not null)
        {
            Directory.CreateDirectory(folder);
            // The runner supplies the generated entity to a new AOT compilation.
            var generated = SchemaScaffolder.Generate(source.Provider, customerTable, new("Reverse", "CustomerCopy") { GeneratedColumns = [] });
            File.WriteAllText(Path.Combine(folder, "CustomerCopy.cs"), generated.Source);
            var orders = SchemaScaffolder.Generate(source.Provider, tables.Single(table => table.Name == "orders"),
                new("Reverse", "OrderCopy")
                {
                    GeneratedColumns = [],
                    PrimaryKeyOrder = ["TenantId", "Id"],
                    ColumnHints = [
                        new("Id", "global::OrderId", "global::OrderCodecs", nameof(OrderCodecs.Id)),
                        new("Total", "decimal", "global::OrderCodecs", nameof(OrderCodecs.Money)),
                        new("Settings", "global::OrderSettings", "global::OrderCodecs", nameof(OrderCodecs.Settings))]
                });
            File.WriteAllText(Path.Combine(folder, "OrderCopy.cs"), orders.Source);
            Check("existing-table-scaffold-written", true);
        }
    }
}

internal sealed class NativeDiagnostics : IDisposable
{
    private readonly nint _module;
    private readonly nint _counter;
    private readonly string _path;
    internal NativeDiagnostics(DatabaseProvider provider)
    {
        var name = provider == DatabaseProvider.PostgreSql ? "libnw_database_postgresql.so" : "libnw_database_sqlite.so";
        var paths = File.ReadLines("/proc/self/maps").Select(line => line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 6 && Path.GetFileName(parts[5]) == name).Select(parts => parts[5]).Distinct().ToArray();
        if (paths.Length != 1) throw new InvalidOperationException("Expected exactly one actually loaded provider image.");
        _path = paths[0]; _module = NativeLibrary.Load(_path); _counter = NativeLibrary.GetExport(_module, "nw_db_diagnostic_count_v1");
    }
    internal unsafe (ulong Sql, ulong Protocol) Read()
    {
        var count = (delegate* unmanaged[Cdecl]<uint, ulong>)_counter;
        return (count(6), count(7));
    }
    internal (string Path, string Hash) Identity() => (_path, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(_path))));
    public void Dispose() => NativeLibrary.Free(_module);
}

internal sealed record Receipt(string Provider, bool Aot, string Dto, string[] Checks, ulong SqlExecutions, ulong ProtocolSubmissions, string NativeSha256);
[System.Text.Json.Serialization.JsonSerializable(typeof(Receipt))]
internal partial class ReceiptJson : System.Text.Json.Serialization.JsonSerializerContext { }
