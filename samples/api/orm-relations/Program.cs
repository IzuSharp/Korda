using NativeWeave.Data;
using NativeWeave.Database;
using NativeWeave.Database.PostgreSql;
using NativeWeave.Database.Sqlite;

var postgres = args.FirstOrDefault() == "postgresql";
var connection = postgres
    ? Environment.GetEnvironmentVariable("NW_TEST_POSTGRES") ?? throw new InvalidOperationException("Provide an explicit PostgreSQL test URI.")
    : args.ElementAtOrDefault(1) ?? Path.Combine(Path.GetTempPath(), "nativeweave-relations-sample.db");
// TLS is disabled only for this explicitly selected local test server.
var builder = postgres ? new DataSourceBuilder().UsePostgreSql(connection, options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment)
    : new DataSourceBuilder().UseSqlite(connection);
await using var source = builder.ConfigureLimits(new() { MaxBatchRows = 64, MaxBufferedRows = 256 }).Build();
await using var session = await source.OpenSessionAsync();
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var token = cancellation.Token;
// Deliberate sample schema setup. Application source creation never runs migrations.
await session.ExecuteAsync(SqlCommand.Create("CREATE TABLE IF NOT EXISTS nw_relation_parents(tenant BIGINT NOT NULL,id BIGINT NOT NULL,name TEXT NULL,version BIGINT NOT NULL,PRIMARY KEY(tenant,id))", kind: SqlCommandKind.Mutation), token);
await session.ExecuteAsync(SqlCommand.Create("CREATE TABLE IF NOT EXISTS nw_relation_children(id BIGINT PRIMARY KEY,tenant BIGINT NULL,parent_id BIGINT NULL,label TEXT NULL,FOREIGN KEY(tenant,parent_id) REFERENCES nw_relation_parents(tenant,id))", kind: SqlCommandKind.Mutation), token);
var tenant = DateTime.UtcNow.Ticks;
await session.InsertAsync(Catalog.Parents, new Parent(tenant, 1, "parent", 1), token);
await session.InsertAsync(Catalog.Children, new Child(tenant, tenant, 1, null), token);
var key = Catalog.Parents.Key(Catalog.ParentTenant, tenant).And(Catalog.ParentId, 1L);
var changes = new Changes<Parent>().Set(Catalog.ParentName, (string?)null).Set(Catalog.ParentVersion, 2L);
await session.UpdateCheckedAsync(Catalog.Parents, key, changes,
    new ConcurrencyCheck<Parent>().Expect(Catalog.ParentVersion, 1L), token);
var parent = await session.FindAsync(Catalog.Parents, key, token);
var children = await session.LoadRelatedAsync(key, Catalog.Relation, token);
Console.WriteLine($"Provider={source.Provider}; version={parent!.Version}; children={children.Count}; nullable label={children[0].Label is null}");
await Advanced.Run(session, tenant, token);

internal sealed record Parent(long Tenant, long Id, string? Name, long Version);
internal sealed record Child(long Id, long? Tenant, long? ParentId, string? Label);

internal static class Catalog
{
    private static readonly TableBuilder<Parent> ParentBuilder = new("nw_relation_parents");
    internal static readonly Column<Parent, long> ParentTenant = ParentBuilder.Column("tenant", Codecs.Int64, new() { IsPrimaryKey = true });
    internal static readonly Column<Parent, long> ParentId = ParentBuilder.Column("id", Codecs.Int64, new() { IsPrimaryKey = true });
    internal static readonly Column<Parent, string?> ParentName = ParentBuilder.Column("name", Codecs.NullableReference(Codecs.String));
    internal static readonly Column<Parent, long> ParentVersion = ParentBuilder.Column("version", Codecs.Int64, new() { IsConcurrencyToken = true });
    internal static readonly Table<Parent> Parents = ParentBuilder.Build(
        row => new(row.Get(ParentTenant), row.Get(ParentId), row.Get(ParentName), row.Get(ParentVersion)),
        (row, entity) => { row.Set(ParentTenant, entity.Tenant); row.Set(ParentId, entity.Id); row.Set(ParentName, entity.Name); row.Set(ParentVersion, entity.Version); });
    private static readonly TableBuilder<Child> ChildBuilder = new("nw_relation_children");
    internal static readonly Column<Child, long> ChildId = ChildBuilder.Column("id", Codecs.Int64, new() { IsPrimaryKey = true });
    internal static readonly Column<Child, long?> ChildTenant = ChildBuilder.Column("tenant", Codecs.Nullable(Codecs.Int64));
    internal static readonly Column<Child, long?> ChildParentId = ChildBuilder.Column("parent_id", Codecs.Nullable(Codecs.Int64));
    internal static readonly Column<Child, string?> ChildLabel = ChildBuilder.Column("label", Codecs.NullableReference(Codecs.String));
    internal static readonly Table<Child> Children = ChildBuilder.Build(
        row => new(row.Get(ChildId), row.Get(ChildTenant), row.Get(ChildParentId), row.Get(ChildLabel)),
        (row, entity) => { row.Set(ChildId, entity.Id); row.Set(ChildTenant, entity.Tenant); row.Set(ChildParentId, entity.ParentId); row.Set(ChildLabel, entity.Label); });
    internal static readonly Relation<Parent, Child> Relation = Relation<Parent, Child>
        .CreateOptional(Parents, ParentTenant, Children, ChildTenant).AndOptional(ParentId, ChildParentId);
}
