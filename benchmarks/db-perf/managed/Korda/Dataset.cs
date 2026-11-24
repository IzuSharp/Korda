using System.Globalization;
using System.Text;
using Korda.Data;
using Korda.Database;

internal static class Dataset
{
    internal const int CustomersCount = 256;
    internal static long CreatedAt(long id) => 1_700_000_000L + id / 4;
    internal static long? CustomerId(long id) => id % 7 == 0 ? null : id % 10 < 7 ? 1 + id % 8 : 9 + id % 248;
    internal static long Cents(long tenant, long id) => 100 + (tenant * 1009 + id * 37) % 100_000;
    internal static string? Note(long id) => id % 5 == 0 ? null : "note-" + (id % 16).ToString(CultureInfo.InvariantCulture);
    internal static string Channel(long id) => id % 2 == 0 ? "web" : "store";
    internal static bool Gift(long id) => id % 11 == 0;
    internal static string CustomerName(long id) => "Customer-" + id.ToString("D4", CultureInfo.InvariantCulture);
    internal static int Lines(long id) => id % 5 == 0 ? 0 : 1 + (int)(id % 3);
    internal static string Product(long id, int line) => "product-" + ((id * 7 + line) % 32).ToString("D2", CultureInfo.InvariantCulture);
    internal static long Quantity(long id, int line) => 1 + (id + line) % 7;

    internal static async Task Initialize(DataSource source, Options options)
    {
        await using var session = await source.OpenSessionAsync();
#if POSTGRES
        const string money = "NUMERIC(18,2)";
        const string json = "JSONB";
#else
        const string money = "BIGINT";
        const string json = "TEXT";
        // DataSourceBuilder applies WAL/FULL to every physical connection.
#endif
        // Initialization accepts a dedicated empty database only. It never drops an existing table.
        await Execute(session, "CREATE TABLE db_perf_metadata (schema_version BIGINT NOT NULL, tenants BIGINT NOT NULL, orders_per_tenant BIGINT NOT NULL)");
        await Execute(session, "CREATE TABLE customers (\"Id\" BIGINT PRIMARY KEY, \"Name\" TEXT NOT NULL, \"Email\" TEXT NULL)");
        await Execute(session, "CREATE TABLE orders (\"TenantId\" BIGINT NOT NULL, \"Id\" BIGINT NOT NULL, \"CreatedAt\" BIGINT NOT NULL, \"CustomerId\" BIGINT NULL, \"Total\" " + money + " NOT NULL, \"Note\" TEXT NULL, \"Version\" BIGINT NOT NULL, \"Settings\" " + json + " NOT NULL, \"InternalMemo\" TEXT NOT NULL, PRIMARY KEY(\"TenantId\",\"Id\"))");
        await Execute(session, "CREATE TABLE order_lines (\"Id\" BIGINT PRIMARY KEY, \"TenantId\" BIGINT NOT NULL, \"OrderId\" BIGINT NOT NULL, \"Product\" TEXT NOT NULL, \"Quantity\" BIGINT NOT NULL)");
        await using var tx = await session.BeginTransactionAsync();
        var sql = new StringBuilder(65536);
        for (var first = 1; first <= CustomersCount; first += 128)
        {
            sql.Clear().Append("INSERT INTO customers VALUES ");
            for (var id = first; id < first + 128 && id <= CustomersCount; id++)
            {
                if (id != first) sql.Append(',');
                sql.Append('(').Append(id).Append(",'").Append(CustomerName(id)).Append("',");
                sql.Append(id % 5 == 0 ? "NULL" : "'customer-" + id.ToString(CultureInfo.InvariantCulture) + "@example.invalid'").Append(')');
            }
            await Execute(session, sql.ToString());
        }
        long lineId = 0;
        for (var tenant = 1; tenant <= options.Tenants; tenant++)
        for (var first = 1; first <= options.OrdersPerTenant; first += 64)
        {
            sql.Clear().Append("INSERT INTO orders VALUES ");
            for (var id = first; id < first + 64 && id <= options.OrdersPerTenant; id++)
            {
                if (id != first) sql.Append(',');
                sql.Append('(').Append(tenant).Append(',').Append(id).Append(',').Append(CreatedAt(id)).Append(',');
                sql.Append(CustomerId(id)?.ToString(CultureInfo.InvariantCulture) ?? "NULL").Append(',');
#if POSTGRES
                sql.Append((Cents(tenant, id) / 100m).ToString("F2", CultureInfo.InvariantCulture));
#else
                sql.Append(Cents(tenant, id));
#endif
                sql.Append(',').Append(Note(id) is { } note ? "'" + note + "'" : "NULL")
                    .Append(",1,'{\"Channel\":\"").Append(Channel(id)).Append("\",\"Gift\":")
                    .Append(Gift(id) ? "true" : "false").Append("}','").Append('m', id % 17 == 0 ? 2048 : 32).Append("')");
            }
            await Execute(session, sql.ToString());
            sql.Clear().Append("INSERT INTO order_lines VALUES ");
            var firstLine = true;
            for (var id = first; id < first + 64 && id <= options.OrdersPerTenant; id++)
            for (var line = 0; line < Lines(id); line++)
            {
                if (!firstLine) sql.Append(',');
                firstLine = false;
                sql.Append('(').Append(++lineId).Append(',').Append(tenant).Append(',').Append(id)
                    .Append(",'").Append(Product(id, line)).Append("',").Append(Quantity(id, line)).Append(')');
            }
            if (!firstLine) await Execute(session, sql.ToString());
        }
        await Execute(session, "INSERT INTO db_perf_metadata VALUES (1," + options.Tenants.ToString(CultureInfo.InvariantCulture) + "," + options.OrdersPerTenant.ToString(CultureInfo.InvariantCulture) + ")");
        await tx.CommitAsync();
        // Commit reports the transaction outcome before native retirement may
        // release the session command gate. Finish retirement before the DDL.
        await tx.DisposeAsync();
        await Execute(session, "CREATE INDEX orders_created ON orders(\"TenantId\",\"CreatedAt\",\"Id\")");
        await Execute(session, "CREATE INDEX orders_customer ON orders(\"TenantId\",\"CustomerId\",\"Id\")");
        await Execute(session, "CREATE INDEX order_lines_parent ON order_lines(\"TenantId\",\"OrderId\")");
        await Execute(session, "CREATE INDEX order_lines_product ON order_lines(\"TenantId\",\"Product\",\"OrderId\")");
        await Execute(session, "ANALYZE");
        Console.WriteLine("DB_PERF_INITIALIZED " + System.Text.Json.JsonSerializer.Serialize(new InitializationReceipt(1,
            source.Provider.ToString(), options.Tenants, options.OrdersPerTenant, lineId), PerfJson.Default.InitializationReceipt));
    }

    private static Task<long> Execute(DataSession session, string sql) => session.ExecuteAsync(SqlCommand.Create(sql, kind: SqlCommandKind.Mutation));
}

internal sealed record InitializationReceipt(int SchemaVersion, string Provider, int Tenants, int OrdersPerTenant, long Lines);
