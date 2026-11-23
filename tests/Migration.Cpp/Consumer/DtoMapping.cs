using Korda.Database;
using Korda.Query;

internal sealed record WorkSummary(long Id, string Title, string? Note);

internal static class DtoMapping
{
    internal static async Task<DtoResult> Read(DataSession session, WorkTable model)
    {
        using var query = session.From(model.Table);
        var projection = query.Projection<WorkSummary>();
        var id = projection.Add(query.Column(model.Id), "id");
        var title = projection.Add(query.Column(model.Title), "title");
        var note = projection.Add(query.Column(model.Note), "note");
        var rows = await query.OrderBy(model.Id).Select(projection.Build(row => new WorkSummary(row.Get(id), row.Get(title), row.Get(note)))).ToListAsync();
        return new("v1-three-fields", rows.Select(row => Business.Fields(row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), row.Title, row.Note)).ToArray(), []);
    }
}
