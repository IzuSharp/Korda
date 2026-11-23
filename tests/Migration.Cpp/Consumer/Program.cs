using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Korda.Data;
using Korda.Database;
using Korda.Database.PostgreSql;
using Korda.Database.Sqlite;
using Korda.Query;

AppDomain.CurrentDomain.UnhandledException += (_, error) =>
{
    if (error.ExceptionObject is DatabaseException database)
        Console.Error.WriteLine($"Database failure category={database.Kind}; provider-code={database.ProviderCode}; correlation={database.CorrelationId}");
};
if (args.Length != 5) throw new ArgumentException("Expected provider, database, seed|observe|continue, jit|aot, receipt path.");
var provider = args[0] switch { "sqlite" => DatabaseProvider.Sqlite, "postgresql" => DatabaseProvider.PostgreSql, _ => throw new ArgumentException("Unknown provider.") };
if (RuntimeFeature.IsDynamicCodeSupported != (args[3] == "jit")) throw new InvalidOperationException("Actual runtime differs from requested JIT/AOT mode.");
var nativeName = provider == DatabaseProvider.Sqlite ? "libnw_database_sqlite.so" : "libnw_database_postgresql.so";
var nativePath = Directory.EnumerateFiles(AppContext.BaseDirectory, nativeName, SearchOption.AllDirectories).Single();
var nativeHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(nativePath)));
var builder = provider == DatabaseProvider.Sqlite ? new DataSourceBuilder().UseSqlite(args[1])
    : new DataSourceBuilder().UsePostgreSql(args[1], options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment);
builder.ConfigureLimits(new() { MaxConnections = 2, MaxWaiters = 4, MaxBatchRows = 2, MaxBatchBytes = 8192, MaxBufferedRows = 32, MaxBufferedBytes = 65536, CommandTimeout = TimeSpan.FromSeconds(15) });
var semantic = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
DtoResult dto;
await using (var source = builder.Build())
{
    var bundle = Business.Migrations(provider);
    await source.Administration.ValidateMigrationsAsync(bundle);
    var migration = await source.Administration.MigrateAsync(bundle, new() { LockTimeout = TimeSpan.FromSeconds(5) });
    semantic["migration.initial"] = [migration.AppliedCount.ToString(CultureInfo.InvariantCulture), migration.CurrentVersion?.ToString(CultureInfo.InvariantCulture) ?? "none", migration.Completed.ToString()];
    var repeat = await source.Administration.MigrateAsync(bundle);
    Business.Check(repeat.AppliedCount == 0 && repeat.Completed, "Migration idempotence");
    semantic["migration.repeat"] = [repeat.AppliedCount.ToString(CultureInfo.InvariantCulture), repeat.CurrentVersion?.ToString(CultureInfo.InvariantCulture) ?? "none", repeat.Completed.ToString()];
    semantic["migration.history"] = (await source.Administration.GetMigrationStatusAsync(bundle)).Select(row => Business.Fields(row.Version.ToString(CultureInfo.InvariantCulture), row.Applied.ToString(), row.Checksum)).ToArray();
    semantic["catalog"] = (await source.Administration.GetTablesAsync()).Where(table => table.Name.StartsWith("migration_", StringComparison.Ordinal)).OrderBy(table => table.Name, StringComparer.Ordinal)
        .Select(table => Business.Fields(table.Name, table.Columns.Count.ToString(CultureInfo.InvariantCulture))).ToArray();
    semantic["capabilities"] = source.Capabilities.Names.ToArray();
    await using var session = await source.OpenSessionAsync();
    var model = new WorkTable(provider);
    if (args[2] == "seed") await Business.Seed(session, model, semantic);
    else if (args[2] == "continue") await Business.Continue(session, model, semantic);
    else if (args[2] != "observe") throw new ArgumentException("Unknown stage.");
    dto = await Business.Read(session, model, semantic);
}
var counts = await Business.WaitRetired(nativePath);
Business.Check(nativeHash == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(nativePath))), "Runtime did not modify native image");
var loaded = File.ReadLines("/proc/self/maps").Where(line => line.EndsWith('/' + nativeName, StringComparison.Ordinal)).Select(line => line[(line.IndexOf('/') )..]).Distinct().ToArray();
Business.Check(loaded.Length == 1 && Path.GetFullPath(loaded[0]) == Path.GetFullPath(nativePath), "Actual mapped module is the package asset");
var report = new Receipt(args[0], args[2], !RuntimeFeature.IsDynamicCodeSupported, semantic, dto.Revision, dto.Extra, nativePath, nativeHash, loaded, counts);
File.WriteAllText(args[4], JsonSerializer.Serialize(report, ReceiptJson.Default.Receipt));
Console.WriteLine($"PASS {args[0]}/{args[3]}/{args[2]} semantic groups={semantic.Count}; DTO={dto.Revision}; owners={string.Join(',', counts)}; SHA256={nativeHash}");

internal sealed record Receipt(string Provider, string Stage, bool Aot, SortedDictionary<string, string[]> Semantic, string DtoRevision, string[] DtoExtra, string NativePath, string NativeSha256, string[] LoadedNative, ulong[] RetiredCounts);
[JsonSerializable(typeof(Receipt))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class ReceiptJson : JsonSerializerContext;

internal static class Business
{
    internal const long LargeId = 9007199254740993;
    internal static readonly DateTimeOffset Instant = new(2024, 2, 29, 23, 59, 58, TimeSpan.Zero);
    internal static string Fields(params string?[] values) => string.Join('|', values.Select(value => value is null ? "null" : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))));
    internal static void Check(bool value, string assertion) { if (!value) throw new InvalidOperationException(assertion); }
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static WorkItem Item(long id, string title, string? note = null) => new(id, title, note, 123456789.0123m, Instant.AddTicks(1234560), [0, 1, 127, 128, 255], 1);
    internal static MigrationBundle Migrations(DatabaseProvider provider)
    {
        var types = provider == DatabaseProvider.PostgreSql ? ("NUMERIC(18,4)", "TIMESTAMPTZ", "BYTEA") : ("TEXT", "INTEGER", "BLOB");
        return new(provider, [
            new(1, "Create business work", $"CREATE TABLE migration_work(id BIGINT PRIMARY KEY, title TEXT NOT NULL, note TEXT NULL, score {types.Item1} NOT NULL, occurred {types.Item2} NOT NULL, payload {types.Item3} NOT NULL, version BIGINT NOT NULL)"),
            new(2, "Create explicit relation", "CREATE TABLE migration_comment(id BIGINT PRIMARY KEY, work_id BIGINT NULL REFERENCES migration_work(id), body TEXT NULL)"),
            new(3, "Index explicit relation", "CREATE INDEX migration_comment_work ON migration_comment(work_id)")]);
    }
    private static async IAsyncEnumerable<WorkItem> Rows(params WorkItem[] rows)
    { foreach (var row in rows) { await Task.Yield(); yield return row; } }
    internal static async Task Seed(DataSession session, WorkTable model, SortedDictionary<string, string[]> result)
    {
        var bulk = await session.InsertManyAsync(model.Table, Rows(Item(1, "O'Reilly 数据庫", null), Item(2, "second", ""), Item(3, "third", "combining e\u0301\nline"), Item(LargeId, "large integer", "last")), new() { BatchSize = 2 });
        Check(bulk.RowsAffected == 4 && bulk.Batches == 2 && bulk.CommittedByOperation, "Seed bulk committed exactly four rows in two batches");
        result["seed.bulk"] = [Number(bulk.RowsAffected), bulk.Batches.ToString(CultureInfo.InvariantCulture), bulk.CommittedByOperation.ToString()];
        await session.InsertAsync(Comments.Table, new(10, 1, null));
        await session.InsertAsync(Comments.Table, new(11, 1, "comment"));
        await session.InsertAsync(Comments.Table, new(12, null, "unlinked"));
    }
    internal static async Task Continue(DataSession session, WorkTable model, SortedDictionary<string, string[]> result)
    {
        var key = model.Table.Key(model.Id, 1L);
        var changed = await session.UpdateCheckedAsync(model.Table, key, new Changes<WorkItem>().Set(model.Title, "continued by same C#").Set(model.Version, 2L), new ConcurrencyCheck<WorkItem>().Expect(model.Version, 1L));
        Check(changed == 1, "Optimistic update affected one");
        try { await session.UpdateCheckedAsync(model.Table, key, new Changes<WorkItem>().Set(model.Title, "stale"), new ConcurrencyCheck<WorkItem>().Expect(model.Version, 1L)); throw new InvalidOperationException("Stale write admitted"); }
        catch (ConcurrencyConflictException error) { Check(error.ExpectedRows == 1 && error.ActualRows == 0, "Stable optimistic conflict counts"); result["mutation.stale"] = [Number(error.ExpectedRows), Number(error.ActualRows)]; }
        await using (var transaction = await session.BeginTransactionAsync(new TransactionOptions { RecoveryKey = "migration-commit" }))
        {
            await session.InsertAsync(model.Table, Item(10, "committed"));
            await using (var savepoint = await transaction.CreateSavepointAsync())
            {
                await session.InsertAsync(model.Table, Item(90, "savepoint rollback"));
                await savepoint.RollbackAsync();
                await savepoint.ReleaseAsync();
            }
            await transaction.CommitAsync();
            Check(transaction.Outcome == TransactionOutcome.Committed, "Known committed outcome");
            result["transaction.commit"] = [transaction.Outcome.ToString()];
        }
        await using (var transaction = await session.BeginTransactionAsync())
        {
            await session.InsertAsync(model.Table, Item(99, "transaction rollback"));
            await transaction.RollbackAsync();
            Check(transaction.Outcome == TransactionOutcome.RolledBack, "Known rollback outcome");
            result["transaction.rollback"] = [transaction.Outcome.ToString()];
        }
        var bulk = await session.InsertManyAsync(model.Table, Rows(Item(11, "bulk one"), Item(12, "bulk two", "explicit note")), new() { BatchSize = 1, Atomicity = BulkAtomicity.PerBatch });
        Check(bulk.RowsAffected == 2 && bulk.Batches == 2 && bulk.CommittedByOperation, "PerBatch acknowledgement");
        result["mutation.bulk"] = [Number(bulk.RowsAffected), bulk.Batches.ToString(CultureInfo.InvariantCulture), bulk.CommittedByOperation.ToString()];
        var removed = await session.DeleteAsync(model.Table, model.Table.Key(model.Id, 3L));
        Check(removed == 1, "Delete affected one");
        result["mutation.affected"] = [Number(changed), Number(removed)];
    }
    internal static async Task<DtoResult> Read(DataSession session, WorkTable model, SortedDictionary<string, string[]> result)
    {
        using (var query = session.From(model.Table))
        {
            var rows = await query.OrderBy(model.Id).SelectAll().ToListAsync();
            result["entity.columns"] = ["id:Int64", "title:String", "note:NullableString", "score:Decimal", "occurred:UtcInstant", "payload:Bytes", "version:Int64"];
            result["entity.rows"] = rows.Select(row => Fields(Number(row.Id), row.Title, row.Note, row.Score.ToString("0.0000", CultureInfo.InvariantCulture), row.Occurred.ToString("O", CultureInfo.InvariantCulture), Convert.ToHexStringLower(row.Payload), Number(row.Version))).ToArray();
            Check(rows.Any(row => row.Id == LargeId) && rows.All(row => row.Score == 123456789.0123m && row.Payload.SequenceEqual(new byte[] { 0, 1, 127, 128, 255 })), "Exact numeric and binary roundtrip");
        }
        var dto = await DtoMapping.Read(session, model); result["dto.rows"] = dto.Core;
        var idProjection = Projection<long>.Create([ResultField.Create("id", Codecs.Int64)], row => row.Read(0, Codecs.Int64));
        var raw = SqlCommand.Create("SELECT id FROM migration_work WHERE id >= @minimum AND (@minimum = @again OR title = '@ignored') ORDER BY id", new SqlParameters().Add("again", 2L, Codecs.Int64).Add("minimum", 2L, Codecs.Int64));
        result["sql.named-reordered-repeat"] = (await session.QueryAsync(raw, idProjection)).Select(Number).ToArray();
        result["sql.interpolated"] = [Number(await session.ScalarAsync(SqlCommand.Parameterized($"SELECT COUNT(*) FROM migration_work WHERE id >= {2L}"), Codecs.Int64))];
        using (var query = session.From(model.Table))
        {
            var parameter = query.Parameter("minimum", Codecs.Int64);
            query.Where(query.Column(model.Id).GreaterThanOrEqual(query.ParameterValue(parameter))).OrderBy(model.Id);
            using var prepared = query.SelectAll().Prepare(); query.Dispose();
            var parameters = new QueryParameters().Set(parameter, 2L);
            var pending = prepared.ToListAsync(session, parameters); parameters.Set(parameter, LargeId);
            result["prepared.snapshot"] = (await pending).Select(row => Number(row.Id)).ToArray();
            result["prepared.rebound"] = (await prepared.ToListAsync(session, parameters)).Select(row => Number(row.Id)).ToArray();
            Check(result["prepared.rebound"].SequenceEqual([Number(LargeId)]), "Prepared independent invocation values");
        }
        using (var query = session.From(model.Table)) result["paging.offset"] = (await query.OrderBy(model.Id).Skip(1).Take(2).SelectAll().ToListAsync()).Select(row => Number(row.Id)).ToArray();
        using (var query = session.From(model.Table)) result["paging.seek"] = (await query.OrderBy(model.Id).SeekAfter(model.Id, 2L).SelectAll().ToListAsync()).Select(row => Number(row.Id)).ToArray();
        var relation = Relation<WorkItem, Comment>.CreateOptional(model.Table, model.Id, Comments.Table, Comments.WorkId);
        result["relation.load"] = (await session.LoadRelatedAsync(model.Table.Key(model.Id, 1L), relation)).OrderBy(row => row.Id).Select(row => Fields(Number(row.Id), row.Body)).ToArray();
        using (var query = session.From(model.Table))
        {
            var comment = query.LeftJoin(Comments.Table, "comment", on => on.Left(model.Id).AsNullable().Equal(on.Right(Comments.WorkId)));
            result["relation.join"] = (await query.OrderBy(model.Id).Select(query.Column(model.Id), query.NullableValue(comment, Comments.Id), (id, child) => new Joined(id, child)).ToListAsync())
                .OrderBy(row => row.Id).ThenBy(row => row.CommentId).Select(row => Fields(Number(row.Id), row.CommentId is { } child ? Number(child) : null)).ToArray();
        }
        using (var query = session.From(model.Table))
        {
            var version = query.Column(model.Version);
            result["group"] = (await query.GroupBy(version).Having(query.CountRows().GreaterThan(0L)).OrderBy(model.Version).Select(version, query.CountRows(), (value, count) => new GroupCount(value, count)).ToListAsync()).Select(row => Fields(Number(row.Version), Number(row.Count))).ToArray();
        }
        using (var query = session.From(model.Table))
        {
            var window = query.Window().OrderBy(query.Column(model.Id));
            var rank = SqlFunctions.RowNumber(window); window.PartitionBy(query.Column(model.Version));
            result["window.snapshot"] = (await query.OrderBy(model.Id).Select(rank).ToListAsync()).Select(Number).ToArray();
        }
        using (var body = session.From(model.Table))
        {
            var projection = body.Projection<SelectedId>(); var id = projection.Add(body.Column(model.Id), "id");
            using var cte = CommonTable<SelectedId>.Create(SelectedIds.Table, body.WhereGreaterThanOrEqual(model.Id, 2L).Select(projection.Build(row => new SelectedId(row.Get(id)))));
            body.WhereEqual(model.Id, -1L);
            using var query = session.From(SelectedIds.Table);
            result["cte.snapshot"] = (await query.With(cte).OrderBy(SelectedIds.Id).SelectAll().ToListAsync()).Select(row => Number(row.Id)).ToArray();
        }
        using (var first = session.From(model.Table))
        using (var second = session.From(model.Table))
            result["set.union-all"] = (await first.WhereEqual(model.Id, 1L).Select(first.Column(model.Id)).Combine(second.WhereEqual(model.Id, LargeId).Select(second.Column(model.Id)), SetOperation.UnionAll).ToListAsync()).Order().Select(Number).ToArray();
        await foreach (var id in session.StreamAsync(SqlCommand.Create("SELECT id FROM migration_work ORDER BY id"), idProjection))
        { result["stream.early"] = [Number(id)]; break; }
        result["stream.reuse-count"] = [Number(await session.ScalarAsync(SqlCommand.Create("SELECT COUNT(*) FROM migration_work"), Codecs.Int64))];
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try { await session.ScalarAsync(SqlCommand.Create("SELECT COUNT(*) FROM migration_work"), Codecs.Int64, cancellation.Token); throw new InvalidOperationException("Pre-cancel was ignored"); }
            catch (OperationCanceledException) { result["error.pre-cancel"] = ["OperationCanceledException"]; }
        }
        try { await session.ScalarAsync(SqlCommand.Create("SELECT COUNT(*) FROM migration_work", timeout: TimeSpan.Zero), Codecs.Int64); throw new InvalidOperationException("Zero timeout was ignored"); }
        catch (TimeoutException) { result["error.timeout"] = ["TimeoutException"]; }
        if (session.Source.Provider == DatabaseProvider.Sqlite)
        {
            using var query = session.From(model.Table);
            try { query.ForUpdate(); throw new InvalidOperationException("Unsupported lock admitted"); }
            catch (DatabaseCapabilityException error) { result["provider.lock"] = [error.Capability]; }
        }
        else
        {
            await using var transaction = await session.BeginTransactionAsync();
            using var query = session.From(model.Table);
            result["provider.lock"] = (await query.WhereEqual(model.Id, 1L).ForUpdate(RowLockMode.Share, LockWait.NoWait).SelectAll().ToListAsync()).Select(row => Number(row.Id)).ToArray();
            await transaction.RollbackAsync();
        }
        return dto;
    }
    private static unsafe ulong[] Counts(nint module)
    {
        var function = (delegate* unmanaged[Cdecl]<uint, ulong>)NativeLibrary.GetExport(module, "nw_db_diagnostic_count_v1");
        var counts = new ulong[6]; for (uint i = 0; i < counts.Length; i++) counts[i] = function(i); return counts;
    }
    internal static async Task<ulong[]> WaitRetired(string path)
    {
        var module = NativeLibrary.Load(path);
        try
        {
            for (var attempt = 0; attempt < 300; attempt++)
            {
                var counts = Counts(module); if (counts.All(value => value == 0)) return counts;
                await Task.Delay(10);
            }
            throw new InvalidOperationException("Native owners did not retire: " + string.Join(',', Counts(module)));
        }
        finally { NativeLibrary.Free(module); }
    }
}
