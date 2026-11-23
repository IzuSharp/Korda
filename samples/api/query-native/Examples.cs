using Korda.Data;
using Korda.Query.Internal;

// Compiled maintainer example of B11's internal integration seam. B13 supplies the
// application Fluent surface; applications never expose these owners or ABI calls.
internal static class NativeQueryExamples
{
    private sealed record Row(long Id, string Name);
    internal static void Run(Host source)
    {
        var builder = new TableBuilder<Row>("Parents");
        var id = builder.Column("Id", Codecs.Int64);
        var name = builder.Column("Name", Codecs.String);
        var table = builder.Build(row => new(row.Get(id), row.Get(name)),
            (row, value) => { row.Set(id, value.Id); row.Set(name, value.Name); });
        using var model = MetadataBindings.RegisterModel(source.Source, table);
        using var query = QueryBinding.Create(source.Session, model, table.Identity.Id);
        var key = query.Column(id.Id);
        var text = query.Column(name.Id);
        var value = query.Value(OperandEncoding.Value(source.Profile, Codecs.Int64, 3, 1L));
        var predicate = query.Binary(1, key, value);
        query.Clause(1, OperandEncoding.Clause(OperandEncoding.U64(1, predicate.In(query))));
        var expressions = new[] { key, text };
        query.Clause(6, OperandEncoding.Clause(
            OperandEncoding.Bytes(1, OperandEncoding.Tokens(query, expressions), 8),
            OperandEncoding.Bytes(2, OperandEncoding.Projection(table.Row, c => c.Id,
                expressions.Select(e => e.In(query)).ToArray()))));
        // Cloning freezes neither the source query nor its subsequent mutations.
        // The imported native shape and its values retain their independent owners.
        using var snapshot = query.CloneSelected();
        using var plan = snapshot.Seal(8);
        var rows = source.Rows(plan, table.Row, Host.Parameters(source.Profile));
        if (rows.Count != 1 || rows[0] != new Row(1, "Alice"))
            throw new InvalidOperationException("Native query sample result differs.");
    }
}
