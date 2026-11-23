using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Korda.Data;
using Korda.Database;
using Korda.Database.PostgreSql;
using Korda.Database.Sqlite;
using Korda.Query;

if (args.Contains("--require-aot") && RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("Actual Native AOT execution required.");
string connection = Environment.GetEnvironmentVariable("NW_TEST_POSTGRES") ?? throw new InvalidOperationException("Set NW_TEST_POSTGRES to the isolated PostgreSQL18 endpoint.");
string file = Path.Combine(Path.GetTempPath(), "nw-fluent-" + Guid.NewGuid().ToString("N") + ".db");
var nativeFiles = new[] { "libnw_database_sqlite.so", "libnw_database_postgresql.so" }.Select(name => NativePath(name)).ToArray();
var hashes = nativeFiles.Select(path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
try
{
    foreach (var source in new[] { SqliteDataSource.Create(file), PostgreSqlDataSource.Create(connection, options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment) })
    {
        await using (source)
        await using (var session = await source.OpenSessionAsync()) await Examples.Run(session);
    }
}
finally { File.Delete(file); }
if (!hashes.SequenceEqual(nativeFiles.Select(path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))))) throw new InvalidOperationException("Native image changed during ordinary model/DTO consumption.");
Console.WriteLine($"PASS isolated Fluent NuGet sample: filter/join/group/aggregate/window/CTE/set/parameters/paging/locking/static DTOs on both providers; AOT={!RuntimeFeature.IsDynamicCodeSupported}; native SHA256={string.Join(',', hashes)}");

static string NativePath(string filename)
{
    var nested = Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native", filename);
    return File.Exists(nested) ? nested : Path.Combine(AppContext.BaseDirectory, filename);
}

internal static class Examples
{
    internal static async Task Run(DataSession session)
    {
        static void Check(bool result, string message) { if (!result) throw new InvalidOperationException(message); }
        await session.ExecuteAsync(SqlCommand.Create("CREATE TABLE fluent_people (id BIGINT PRIMARY KEY, name TEXT NOT NULL, region TEXT NOT NULL)", kind: SqlCommandKind.Mutation));
        await session.ExecuteAsync(SqlCommand.Create("CREATE TABLE fluent_notes (id BIGINT PRIMARY KEY, person_id BIGINT NULL)", kind: SqlCommandKind.Mutation));
        await session.ExecuteAsync(SqlCommand.Create("INSERT INTO fluent_people VALUES (0,'Zero','A'),(1,'Ada','A'),(2,'Grace','B')", kind: SqlCommandKind.Mutation));
        await session.ExecuteAsync(SqlCommand.Create("INSERT INTO fluent_notes VALUES (10,1),(11,NULL)", kind: SqlCommandKind.Mutation));

        // Ordinary typed application code; values are copied during composition.
        using (var query = session.From(People.Table))
        {
            long minimum = 1;
            query.Where(row => row.GreaterThanOrEqual(People.Id, minimum)); minimum = 99;
            var rows = await query.OrderBy(People.Id).Take(2).SelectAll().ToListAsync();
            Check(rows.Select(row => row.Name).SequenceEqual(["Ada", "Grace"]), "Typed filtered entities and captured-value snapshot");
        }
        using (var query = session.From(People.Table))
        {
            var note = query.LeftJoin(Notes.Table, "note", on => on.Left(People.Id).AsNullable().Equal(on.Right(Notes.PersonId)));
            var projection = query.Projection<PersonNote>();
            var name = projection.Add(query.Column(People.Name), "name");
            var id = projection.Add(query.NullableValue(note, Notes.Id), "note_id");
            var row = projection.Build(reader => new PersonNote(reader.Get(name), reader.Get(id)));
            var rows = await query.OrderBy(People.Id).Select(row).ToArrayAsync();
            Check(rows.SequenceEqual([new PersonNote("Zero", null), new PersonNote("Ada", 10), new PersonNote("Grace", null)]), "LEFT nullable foreign key and explicit DTO constructor");
        }
        using (var query = session.From(People.Table))
        {
            var region = query.Column(People.Region);
            var rows = await query.GroupBy(region).Having(query.CountRows().GreaterThan(0L)).OrderBy(People.Region)
                .Select(region, query.CountRows(), (value, count) => new RegionCount(value, count)).ToListAsync();
            Check(rows.SequenceEqual([new RegionCount("A", 2), new RegionCount("B", 1)]), "Native grouping and aggregate widening");
        }
        using (var query = session.From(People.Table))
        {
            var window = query.Window().OrderBy(query.Column(People.Id));
            var rank = SqlFunctions.RowNumber(window); window.PartitionBy(query.Column(People.Region));
            Check((await query.OrderBy(People.Id).Select(rank).ToListAsync()).SequenceEqual([1L, 2L, 3L]), "Window snapshot");
        }
        using (var body = session.From(People.Table))
        {
            var builder = body.Projection<SelectedId>(); var token = builder.Add(body.Column(People.Id), "id");
            var selected = body.WhereGreaterThanOrEqual(People.Id, 1L).Select(builder.Build(reader => new SelectedId(reader.Get(token))));
            using var cte = CommonTable<SelectedId>.Create(SelectedIds.Table, selected);
            body.WhereEqual(People.Id, 999L);
            using var query = session.From(SelectedIds.Table);
            Check((await query.With(cte).OrderBy(SelectedIds.Id).SelectAll().ToListAsync()).Select(row => row.Id).SequenceEqual([1L, 2L]), "Frozen native CTE body");
        }
        using (var first = session.From(People.Table))
        using (var second = session.From(People.Table))
        {
            var selected = first.WhereEqual(People.Id, 0L).Select(first.Column(People.Id));
            var other = second.WhereEqual(People.Id, 2L).Select(second.Column(People.Id));
            Check((await selected.Combine(other, SetOperation.UnionAll).ToListAsync()).Order().SequenceEqual([0L, 2L]), "Native set snapshot");
        }
        using (var query = session.From(People.Table))
        {
            var minimum = query.Parameter("minimum", Codecs.Int64);
            query.Where(query.Column(People.Id).GreaterThanOrEqual(query.ParameterValue(minimum))).OrderBy(People.Id);
            using var prepared = query.SelectAll().Prepare(); query.Dispose();
            var parameters = new QueryParameters().Set(minimum, 1L);
            var pending = prepared.ToListAsync(session, parameters); parameters.Set(minimum, 2L);
            Check((await pending).Count == 2 && (await prepared.ToListAsync(session, parameters)).Single().Id == 2, "Independent prepared values before await");
        }
        using (var query = session.From(People.Table))
            Check((await query.OrderBy(People.Id).Skip(1).Take(1).SelectAll().SingleAsync()).Id == 1, "Unique paging and preserved terminal Take");
        using (var query = session.From(People.Table))
            Check((await query.OrderBy(People.Id).SeekAfter(People.Id, 1L).SelectAll().ToListAsync()).Single().Id == 2, "Unique ascending keyset paging");
        using (var query = session.From(People.Table))
            Check((await query.Select(SqlFunctions.Sum(query.Column(People.Id))).SingleAsync()) == 3L, "Nullable SQL SUM and Int64 result");
        if (session.Source.Provider == DatabaseProvider.PostgreSql)
        {
            await using var transaction = await session.BeginTransactionAsync();
            using var query = session.From(People.Table);
            Check((await query.WhereEqual(People.Id, 1L).ForUpdate(RowLockMode.Share, LockWait.NoWait).SelectAll().ToListAsync()).Count == 1, "Explicit PostgreSQL lock scope");
            await transaction.RollbackAsync();
        }
        else
        {
            using var query = session.From(People.Table);
            try { query.ForUpdate(); throw new InvalidOperationException("SQLite lock unexpectedly admitted."); }
            catch (DatabaseCapabilityException error) { Check(error.Capability == "db.row-lock.v1", "Stable unavailable capability"); }
        }
        await session.ExecuteAsync(SqlCommand.Create("DROP TABLE fluent_notes", kind: SqlCommandKind.Mutation));
        await session.ExecuteAsync(SqlCommand.Create("DROP TABLE fluent_people", kind: SqlCommandKind.Mutation));
        Console.WriteLine("PASS " + session.Source.Provider + " ordinary manual models and static projection materializers");
    }
}

internal sealed record Person(long Id, string Name, string Region);
internal sealed record Note(long Id, long? PersonId);
internal sealed record PersonNote(string Name, long? NoteId);
internal sealed record RegionCount(string Region, long Count);
internal sealed record SelectedId(long Id);
internal static class People
{
    private static readonly TableBuilder<Person> Builder = new("fluent_people");
    internal static readonly Column<Person, long> Id = Builder.Column("id", Codecs.Int64, new() { IsPrimaryKey = true });
    internal static readonly Column<Person, string> Name = Builder.Column("name", Codecs.String);
    internal static readonly Column<Person, string> Region = Builder.Column("region", Codecs.String);
    internal static readonly Table<Person> Table = Builder.Build(static row => new(row.Get(Id), row.Get(Name), row.Get(Region)), static (row, value) => { row.Set(Id, value.Id); row.Set(Name, value.Name); row.Set(Region, value.Region); });
}
internal static class Notes
{
    private static readonly TableBuilder<Note> Builder = new("fluent_notes");
    internal static readonly Column<Note, long> Id = Builder.Column("id", Codecs.Int64, new() { IsPrimaryKey = true });
    internal static readonly Column<Note, long?> PersonId = Builder.Column("person_id", Codecs.Nullable(Codecs.Int64));
    internal static readonly Table<Note> Table = Builder.Build(static row => new(row.Get(Id), row.Get(PersonId)), static (row, value) => { row.Set(Id, value.Id); row.Set(PersonId, value.PersonId); });
}
internal static class SelectedIds
{
    private static readonly TableBuilder<SelectedId> Builder = new("scoped_person_ids");
    internal static readonly Column<SelectedId, long> Id = Builder.Column("id", Codecs.Int64);
    internal static readonly Table<SelectedId> Table = Builder.Build(static row => new(row.Get(Id)), static (row, value) => row.Set(Id, value.Id));
}
