using NativeWeave.Data;
using NativeWeave.Database;
using NativeWeave.Database.Sqlite;
using NativeWeave.Query;

if (args.Contains("--require-aot") && System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
    throw new InvalidOperationException("This acceptance row requires an actual NativeAOT executable.");

string file = Path.Combine(Path.GetTempPath(), "nw-generated-" + Guid.NewGuid().ToString("N") + ".db");
try
{
    await using var source = SqliteDataSource.Create(file);
    await using var session = await source.OpenSessionAsync();
    await session.ExecuteAsync(SqlCommand.Create("CREATE TABLE students (id INTEGER PRIMARY KEY, Name TEXT NOT NULL, email TEXT, version INTEGER NOT NULL)", kind: SqlCommandKind.Mutation));
    await session.ExecuteAsync(SqlCommand.Create("INSERT INTO students VALUES (7,'Ada',NULL,2)", kind: SqlCommandKind.Mutation));
    using (var query = session.From(Students.Table))
    {
        var rows = await query.WhereEqual(Students.Id, 7L).SelectAll().ToListAsync();
        if (rows.Count != 1 || rows[0] != new Student(7, "Ada", null, 2)) throw new InvalidOperationException("Generated/manual materialization mismatch.");
    }
    // A second ordinary C# entity reuses the exact same native asset.
    await session.ExecuteAsync(SqlCommand.Create("CREATE TABLE labels (Id INTEGER PRIMARY KEY, Text TEXT NOT NULL)", kind: SqlCommandKind.Mutation));
    await session.ExecuteAsync(SqlCommand.Create("INSERT INTO labels VALUES (1,'second')", kind: SqlCommandKind.Mutation));
    using (var query = session.From(Labels.Table))
    {
        var rows = await query.SelectAll().ToListAsync();
        if (rows.Count != 1 || rows[0] != new Label(1, "second")) throw new InvalidOperationException("Second entity mismatch.");
    }
    Console.WriteLine($"Student metadata={Students.Table.MetadataHash}; projection={Students.Row.ShapeHash}; Label metadata={Labels.Table.MetadataHash}; two native results passed.");
}
finally { File.Delete(file); }

[WeaveTable("students", DescriptorName = "Students")]
internal sealed partial record Student([property: WeaveKey, WeaveColumn("id")] long Id, string Name, [property: WeaveColumn("email")] string? Email, [property: WeaveVersion, WeaveColumn("version")] long Version);
[WeaveTable("labels", DescriptorName = "Labels")]
internal sealed partial record Label([property: WeaveKey] long Id, string Text);
#if MANUAL_MAPPING
internal static class Students
{
    private static readonly TableBuilder<Student> Builder = new("students");
    internal static readonly Column<Student, long> Id = Builder.Column("id", Codecs.Int64, new() { IsPrimaryKey = true });
    private static readonly Column<Student, string> Name = Builder.Column("Name", Codecs.String);
    private static readonly Column<Student, string?> Email = Builder.Column("email", Codecs.NullableReference(Codecs.String));
    private static readonly Column<Student, long> Version = Builder.Column("version", Codecs.Int64, new() { IsConcurrencyToken = true });
    internal static readonly Table<Student> Table = Builder.Build(static row => new Student(row.Get(Id), row.Get(Name), row.Get(Email), row.Get(Version)), static (row, value) => { row.Set(Id, value.Id); row.Set(Name, value.Name); row.Set(Email, value.Email); row.Set(Version, value.Version); });
    internal static Projection<Student> Row => Table.Row;
}
internal static class Labels
{
    private static readonly TableBuilder<Label> Builder = new("labels");
    private static readonly Column<Label, long> Id = Builder.Column("Id", Codecs.Int64, new() { IsPrimaryKey = true });
    private static readonly Column<Label, string> Text = Builder.Column("Text", Codecs.String);
    internal static readonly Table<Label> Table = Builder.Build(static row => new Label(row.Get(Id), row.Get(Text)), static (row, value) => { row.Set(Id, value.Id); row.Set(Text, value.Text); });
}
#endif
