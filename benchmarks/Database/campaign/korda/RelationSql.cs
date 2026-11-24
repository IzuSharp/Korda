using Korda.Data;
using Korda.Database;
using Korda.Query;

// An unexecuted public-Fluent reconstruction of RelationBatch.SelectParent,
// checked against that implementation and server SQL logs. This is outside
// measurement; measured relations still use the normal LoadRelatedAsync API.
internal static class RelationSql
{
    internal static string Export(DataSession session,int count)
    {
        using var first=session.From(OrderLines.Table);
        var selection=Select(first,1,0);
        for(var index=1;index<count;index++)
        {
            using var next=session.From(OrderLines.Table);
            selection=selection.Combine(Select(next,index+1,index));
        }
        return selection.ToSql();
    }

    private static QuerySelection<RelationSqlRow> Select(Query<OrderLine> query,long order,int index)
    {
        query.WhereEqual(OrderLines.TenantId,1L).WhereEqual(OrderLines.OrderId,order);
        var shape=query.Projection<RelationSqlRow>();
        var id=shape.Add(query.Column(OrderLines.Id),"Id");
        var tenant=shape.Add(query.Column(OrderLines.TenantId),"TenantId");
        var parent=shape.Add(query.Column(OrderLines.OrderId),"OrderId");
        var product=shape.Add(query.Column(OrderLines.Product),"Product");
        var quantity=shape.Add(query.Column(OrderLines.Quantity),"Quantity");
        var ordinal=shape.Add(query.Value(index,Codecs.Int32),"__korda_parent_index");
        return query.Select(shape.Build(row=>new(row.Get(id),row.Get(tenant),row.Get(parent),row.Get(product),row.Get(quantity),row.Get(ordinal))));
    }

    private sealed record RelationSqlRow(long Id,long TenantId,long OrderId,string Product,long Quantity,int Index);
}
