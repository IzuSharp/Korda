using Korda.Data;
using Korda.Database;

internal sealed record WorkItem(long Id, string Title, string? Note, decimal Score, DateTimeOffset Occurred, byte[] Payload, long Version);
internal sealed record Comment(long Id, long? WorkId, string? Body);
internal sealed record SelectedId(long Id);
internal sealed record Joined(long Id, long? CommentId);
internal sealed record GroupCount(long Version, long Count);
internal sealed record DtoResult(string Revision, string[] Core, string[] Extra);

internal sealed class WorkTable
{
    internal Column<WorkItem, long> Id { get; }
    internal Column<WorkItem, string> Title { get; }
    internal Column<WorkItem, string?> Note { get; }
    internal Column<WorkItem, decimal> Score { get; }
    internal Column<WorkItem, DateTimeOffset> Occurred { get; }
    internal Column<WorkItem, byte[]> Payload { get; }
    internal Column<WorkItem, long> Version { get; }
    internal Table<WorkItem> Table { get; }

    internal WorkTable(DatabaseProvider provider)
    {
        var builder = new TableBuilder<WorkItem>("migration_work");
        Id = builder.Column("id", Codecs.Int64, new() { IsPrimaryKey = true });
        Title = builder.Column("title", Codecs.String);
        Note = builder.Column("note", Codecs.NullableReference(Codecs.String));
        Score = builder.Column("score", provider == DatabaseProvider.PostgreSql ? Codecs.Decimal(18, 4) : Codecs.DecimalText);
        Occurred = builder.Column("occurred", Codecs.UtcInstant);
        Payload = builder.Column("payload", Codecs.Bytes);
        Version = builder.Column("version", Codecs.Int64, new() { IsConcurrencyToken = true });
        Table = builder.Build(row => new(row.Get(Id), row.Get(Title), row.Get(Note), row.Get(Score), row.Get(Occurred), row.Get(Payload), row.Get(Version)),
            (row, value) => { row.Set(Id, value.Id); row.Set(Title, value.Title); row.Set(Note, value.Note); row.Set(Score, value.Score); row.Set(Occurred, value.Occurred); row.Set(Payload, value.Payload); row.Set(Version, value.Version); });
    }
}

internal static class Comments
{
    private static readonly TableBuilder<Comment> Builder = new("migration_comment");
    internal static readonly Column<Comment, long> Id = Builder.Column("id", Codecs.Int64, new() { IsPrimaryKey = true });
    internal static readonly Column<Comment, long?> WorkId = Builder.Column("work_id", Codecs.Nullable(Codecs.Int64));
    internal static readonly Column<Comment, string?> Body = Builder.Column("body", Codecs.NullableReference(Codecs.String));
    internal static readonly Table<Comment> Table = Builder.Build(row => new(row.Get(Id), row.Get(WorkId), row.Get(Body)),
        (row, value) => { row.Set(Id, value.Id); row.Set(WorkId, value.WorkId); row.Set(Body, value.Body); });
}

internal static class SelectedIds
{
    private static readonly TableBuilder<SelectedId> Builder = new("migration_selected");
    internal static readonly Column<SelectedId, long> Id = Builder.Column("id", Codecs.Int64);
    internal static readonly Table<SelectedId> Table = Builder.Build(row => new(row.Get(Id)), (row, value) => row.Set(Id, value.Id));
}
