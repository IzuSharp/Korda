using Korda.Data;

[WeaveTable("b14_items", DescriptorName = "Items")]
public sealed partial record Item([property: WeaveKey] long Tenant, [property: WeaveKey] long Id, string? Name, [property: WeaveVersion] long Version);
[WeaveTable("b14_children", DescriptorName = "Children")]
public sealed partial record Child([property: WeaveKey] long Id, long? Tenant, long? ParentId, string? Name, long? Amount);
[WeaveTable("b14_generated", DescriptorName = "Generated")]
public sealed partial record GeneratedItem([property: WeaveKey, WeaveColumn("Id", Generated = true)] long Id);

internal static class Manual
{
    internal static readonly Table<Item> Items;
    internal static readonly Column<Item, long> Tenant, Id, Version;
    internal static readonly Column<Item, string?> Name;
    static Manual()
    {
        var builder = new TableBuilder<Item>("b14_items");
        Tenant = builder.Column("Tenant", Codecs.Int64, new() { IsPrimaryKey = true }); Id = builder.Column("Id", Codecs.Int64, new() { IsPrimaryKey = true });
        Name = builder.Column("Name", Codecs.NullableReference(Codecs.String)); Version = builder.Column("Version", Codecs.Int64, new() { IsConcurrencyToken = true });
        Items = builder.Build(row => new(row.Get(Tenant), row.Get(Id), row.Get(Name), row.Get(Version)), (row, value) => { row.Set(Tenant, value.Tenant); row.Set(Id, value.Id); row.Set(Name, value.Name); row.Set(Version, value.Version); });
    }
    internal static Relation<Item, Child> RelationWith(RowMaterializer<Child> materializer)
    {
        var b = new TableBuilder<Child>("b14_children"); var id = b.Column("Id", Codecs.Int64, new() { IsPrimaryKey = true }); var tenant = b.Column("Tenant", Codecs.Nullable(Codecs.Int64)); var parent = b.Column("ParentId", Codecs.Nullable(Codecs.Int64)); var name = b.Column("Name", Codecs.NullableReference(Codecs.String)); var amount = b.Column("Amount", Codecs.Nullable(Codecs.Int64));
        var table = b.Build(materializer, (row, v) => { row.Set(id, v.Id); row.Set(tenant, v.Tenant); row.Set(parent, v.ParentId); row.Set(name, v.Name); row.Set(amount, v.Amount); });
        return Relation<Item, Child>.CreateOptional(global::Items.Table, global::Items.Tenant, table, tenant).AndOptional(global::Items.Id, parent);
    }
}
