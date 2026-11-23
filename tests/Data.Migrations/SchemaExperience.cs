using Korda.Data;
using Korda.Database;
using Korda.Database.PostgreSql;
using Korda.Database.Sqlite;
using Korda.Interop;

internal static partial class Suite
{
    private static string? _sqliteDropReviewId;
    private static async Task SchemaExperience(DataSource source, string connection)
    {
        Check(source.Capabilities.Supports("db.schema.draft.v1"), "actual native schema-draft capability");
        var before = SchemaSqlCount(source);
        var empty = DatabaseModel.Empty;
        var table = SchemaTableModel();
        var initial = empty.Add(table);
        Check(empty.ToJson() == "{\"version\":1,\"models\":[]}", "adding a table leaves the prior immutable snapshot unchanged");
        Check(DatabaseModel.Parse(initial.ToJson()).ToJson() == initial.ToJson(), "persisted model round trip preserves canonical metadata");
        var unusualSnapshot = empty.Add(SchemaTableModel(name: "quoted\"\u0008\u2028name")).ToJson();
        Check(DatabaseModel.Parse(" \n" + unusualSnapshot + " \n").ToJson() == unusualSnapshot, "snapshot parsing preserves canonical Unicode and control-character escape spelling");
        await Throws<ArgumentException>(() => Task.FromResult(initial.Add(table)));
        await Throws<ArgumentException>(() => Task.FromResult(DatabaseModel.Parse("{\"version\":2,\"models\":[]}")));
        var created = await source.Administration.DraftMigrationAsync(empty, initial);
        Check(created.Provider == source.Provider && created.Changes.Count == 1 && created.Changes[0].Kind == MigrationChangeKind.CreateTable
            && !created.Changes[0].IsDestructive && created.Changes[0].Sql.Contains("PRIMARY KEY (\"Id\")", StringComparison.Ordinal), "native create draft retains the explicit key and provider");
        Check(created.Changes is System.Collections.ObjectModel.ReadOnlyCollection<MigrationChange>, "draft changes are immutable");
        var repeated = await source.Administration.DraftMigrationAsync(empty, DatabaseModel.Parse(initial.ToJson()));
        Check(created.Changes.SequenceEqual(repeated.Changes), "repeated compilation yields stable SQL and review IDs");
        var same = await source.Administration.DraftMigrationAsync(initial, initial);
        Check(same.Changes.Count == 0, "unchanged model produces no migration");
        await Throws<InvalidOperationException>(() => Task.FromResult(same.ToMigration(1, "Nothing to apply")));

        var withNote = empty.Add(SchemaTableModel(note: true));
        var added = await source.Administration.DraftMigrationAsync(initial, withNote);
        Check(added.Changes.Count == 1 && added.Changes[0].Kind == MigrationChangeKind.AddColumn && !added.Changes[0].IsDestructive
            && added.Changes[0].Sql == "ALTER TABLE \"review_items\" ADD COLUMN \"Note\" TEXT;", "nullable column addition needs no invented backfill");
        var required = empty.Add(SchemaTableModel(note: true, required: true));
        var rejected = await Throws<ArgumentException>(() => source.Administration.DraftMigrationAsync(initial, required));
        Check(rejected.Message.Contains("review_items", StringComparison.Ordinal) && rejected.Message.Contains("Note", StringComparison.Ordinal), "unsupported native difference retains offending table and column after retirement");
        await Throws<ArgumentException>(() => source.Administration.DraftMigrationAsync(withNote, required));
        await Throws<ArgumentException>(() => source.Administration.DraftMigrationAsync(initial, empty.Add(SchemaTableModel(narrowId: true))));
        await Throws<ArgumentException>(() => source.Administration.DraftMigrationAsync(initial, empty.Add(SchemaTableModel(generated: true))));

        var removed = await source.Administration.DraftMigrationAsync(withNote, initial);
        Check(removed.Changes.Count == 1 && removed.Changes[0].Kind == MigrationChangeKind.DropColumn && removed.Changes[0].IsDestructive, "removed nonkey column is explicitly destructive");
        await Throws<InvalidOperationException>(() => Task.FromResult(removed.ToMigration(2, "Unreviewed drop")));
        await Throws<ArgumentException>(() => Task.FromResult(removed.ToMigration(2, "Unknown approval", ["not-a-change"])));
        var id = removed.Changes[0].Id;
        await Throws<ArgumentException>(() => Task.FromResult(removed.ToMigration(2, "Repeated approval", [id, id])));
        Check(removed.ToMigration(2, "Reviewed drop", [id]).Sql == "ALTER TABLE \"review_items\" DROP COLUMN \"Note\";", "exact individual approval creates the reviewed drop script");
        var dropped = await source.Administration.DraftMigrationAsync(initial, empty);
        Check(dropped.Changes.Single().Kind == MigrationChangeKind.DropTable && dropped.Changes[0].IsDestructive, "table removal cannot hide as a safe operation");
        if (source.Provider == DatabaseProvider.Sqlite) _sqliteDropReviewId = dropped.Changes[0].Id;
        else Check(_sqliteDropReviewId is not null && _sqliteDropReviewId != dropped.Changes[0].Id, "identical DROP SQL has distinct versioned provider review identities");
        await Throws<InvalidOperationException>(() => Task.FromResult(dropped.ToMigration(3, "Unreviewed table removal")));
        var identity = await source.Administration.DraftMigrationAsync(empty, empty.Add(SchemaTableModel(generated: true)));
        Check(identity.Changes[0].Sql.Contains(source.Provider == DatabaseProvider.Sqlite ? "PRIMARY KEY AUTOINCREMENT" : "GENERATED BY DEFAULT AS IDENTITY", StringComparison.Ordinal), "sole generated integer key has explicit provider identity SQL");
        var quoted = await source.Administration.DraftMigrationAsync(empty, empty.Add(SchemaTableModel(name: "review\"quoted")));
        Check(quoted.Changes[0].Sql.StartsWith("CREATE TABLE \"review\"\"quoted\" (", StringComparison.Ordinal), "native dialect quotes embedded identifier characters");
        if (source.Provider == DatabaseProvider.Sqlite)
            await Throws<ArgumentException>(() => source.Administration.DraftMigrationAsync(empty, initial.Add(SchemaTableModel(name: "REVIEW_ITEMS"))));
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            var error = await Throws<OperationCanceledException>(() => source.Administration.DraftMigrationAsync(empty, initial, canceled.Token));
            Check(error.CancellationToken == canceled.Token, "pre-canceled draft preserves token identity");
        }
        await using (var bounded = Source(source.Provider, connection, new() { MaxBufferedRows = 1 }))
        {
            var two = initial.Add(SchemaTableModel(name: "review_second"));
            var error = await Throws<DatabaseException>(() => bounded.Administration.DraftMigrationAsync(empty, two));
            Check(error.Kind == DatabaseErrorKind.ResourceLimit, "draft changes share the finite buffered-row budget");
        }
        await using (var bounded = Source(source.Provider, connection, new() { MaxQueryNodes = 1 }))
        {
            var error = await Throws<DatabaseException>(() => bounded.Administration.DraftMigrationAsync(empty, initial));
            Check(error.Kind == DatabaseErrorKind.ResourceLimit, "table and columns share native model-node admission budget");
        }
        await ScaffoldExperience(source.Provider);
        Check(SchemaSqlCount(source) == before, "model drafts, rejected diffs and pure scaffolding perform zero provider SQL dispatches");
        Console.WriteLine("DB-API-01 " + source.Provider + " reviewable schema drafts and scaffold safety passed");
    }

    private static Table<DraftEntity> SchemaTableModel(bool note = false, bool required = false, bool generated = false, bool narrowId = false, string name = "review_items")
    {
        var table = new TableBuilder<DraftEntity>(name);
        var key = new ColumnOptions { IsPrimaryKey = true, IsGenerated = generated };
        if (narrowId) table.Column("Id", Codecs.Int32, key); else table.Column("Id", Codecs.Int64, key);
        if (note) { if (required) table.Column("Note", Codecs.String); else table.Column("Note", Codecs.NullableReference(Codecs.String)); }
        return table.Build(_ => new(), (_, _) => { });
    }

    private static async Task ScaffoldExperience(DatabaseProvider provider)
    {
        var integer = provider == DatabaseProvider.Sqlite ? "INTEGER" : "bigint";
        var catalog = new SchemaTable(null, "customers", [new("Id", integer, false, true), new("Name", "TEXT", true, false)]);
        var options = new SchemaScaffoldOptions("Review", "Customer") { GeneratedColumns = [] };
        var generated = SchemaScaffolder.Generate(provider, catalog, options);
        Check(generated.Source.Contains("public long Id", StringComparison.Ordinal) && generated.Source.Contains("public string? Name", StringComparison.Ordinal)
            && generated.Notes.Count > 0, "catalog nullability and explicit store names survive scaffolding");
        await Throws<ArgumentException>(() => Task.FromResult(SchemaScaffolder.Generate(provider, catalog, new("Review", "Customer"))));
        await Throws<ArgumentException>(() => Task.FromResult(SchemaScaffolder.Generate(provider, catalog, options with { Namespace = "global::Review" })));
        await Throws<ArgumentException>(() => Task.FromResult(SchemaScaffolder.Generate(provider, catalog, options with { TypeName = "Bad; class Escape {}" })));
        await Throws<ArgumentException>(() => Task.FromResult(SchemaScaffolder.Generate(provider, catalog, options with { TypeName = "__arglist" })));
        await Throws<ArgumentException>(() => Task.FromResult(SchemaScaffolder.Generate(provider, catalog, options with { GeneratedColumns = ["missing"] })));
        var nullableKey = catalog with { Columns = [new("Id", integer, true, true)] };
        await Throws<ArgumentException>(() => Task.FromResult(SchemaScaffolder.Generate(provider, nullableKey, options)));
        var composite = catalog with { Columns = [new("Id", integer, false, true), new("Tenant", integer, false, true)] };
        await Throws<ArgumentException>(() => Task.FromResult(SchemaScaffolder.Generate(provider, composite, options)));
        var ordered = SchemaScaffolder.Generate(provider, composite, options with { PrimaryKeyOrder = ["Tenant", "Id"] });
        Check(ordered.Source.Contains("WeaveConstructorAttribute", StringComparison.Ordinal) &&
            ordered.Source.IndexOf("nameof(Tenant)", StringComparison.Ordinal) < ordered.Source.IndexOf("nameof(Id)", StringComparison.Ordinal), "selected constructor explicitly carries the reviewed composite-key order");
        var money = catalog with { Columns = [new("Amount", "NUMERIC(18,2)", false, false)] };
        await Throws<ArgumentException>(() => Task.FromResult(SchemaScaffolder.Generate(provider, money, options)));
        await Throws<ArgumentException>(() => Task.FromResult(SchemaScaffolder.Generate(provider, money, options with { ColumnHints = [new("Amount", "decimal")] })));
        var configured = SchemaScaffolder.Generate(provider, money, options with { ColumnHints = [new("Amount", "decimal", "Review.Codecs", "Money")] });
        Check(configured.Source.Contains("[global::Korda.Data.WeaveUseCodecAttribute(typeof(Review.Codecs), \"Money\")]", StringComparison.Ordinal), "ambiguous money mapping requires reusable explicit codec source");
        var unusual = catalog with { Name = "table\"\u2028name", Columns = [new("class", "TEXT", true, false)] };
        var escaped = SchemaScaffolder.Generate(provider, unusual, options).Source;
        Check(escaped.Contains("table\\\"\\u2028name", StringComparison.Ordinal) && escaped.Contains("string? Column1", StringComparison.Ordinal), "C# line terminators and keyword column names cannot break generated source");
        var reserved = catalog with { Columns = [new("Table", "TEXT", true, false), new("Row", "TEXT", true, false), new("CustomerMapping", "TEXT", true, false)] };
        Check(SchemaScaffolder.Generate(provider, reserved, options).Source.Contains("string? Column3", StringComparison.Ordinal), "generator-reserved descriptor members receive safe fallback property names");
        await Throws<ArgumentException>(() => Task.FromResult(SchemaScaffolder.Generate(provider, catalog, options with { ColumnHints = [new("Name", "string", PropertyName: "Table")] })));
        if (provider == DatabaseProvider.Sqlite)
        {
            var typeless = catalog with { Columns = [new("Value", "", true, false)] };
            await Throws<ArgumentException>(() => Task.FromResult(SchemaScaffolder.Generate(provider, typeless, options)));
            Check(SchemaScaffolder.Generate(provider, typeless, options with { ColumnHints = [new("Value", "string")] }).Source.Contains("string? Value", StringComparison.Ordinal), "typeless SQLite columns require an explicit reviewed CLR interpretation");
            var duplicate = catalog with { Columns = [new("Name", "TEXT", true, false), new("name", "TEXT", true, false)] };
            await Throws<ArgumentException>(() => Task.FromResult(SchemaScaffolder.Generate(provider, duplicate, options)));
        }
    }

    private static unsafe ulong SchemaSqlCount(DataSource source)
    {
        var engine = source.Provider == DatabaseProvider.Sqlite ? SqliteDataSource.Engine : PostgreSqlDataSource.Engine;
        var count = (delegate* unmanaged[Cdecl]<uint, ulong>)EngineModuleLoader.ResolveExport(engine, "nw_db_diagnostic_count_v1");
        return count(6);
    }
    private sealed class DraftEntity { }
}
