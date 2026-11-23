using Korda.Data;
using Korda.Database;
using Korda.Database.Sqlite;
using Korda.Database.PostgreSql;
using Korda.Interop;
using Korda.Interop.Async;
using Korda.Query;
using Korda.Query.Internal;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Buffers.Binary;

await Suite.Run();

internal static partial class Suite
{
    private static int _assertions, _retirements;
    internal static void Check(bool value, string why) { Interlocked.Increment(ref _assertions); if (!value) throw new InvalidOperationException(why); }
    internal static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T failure) { Interlocked.Increment(ref _assertions); return failure; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    internal static DataSource Source(DatabaseProvider provider, string connection, DatabaseLimits? limits = null)
    {
        var builder = provider == DatabaseProvider.Sqlite ? new DataSourceBuilder().UseSqlite(connection) : new DataSourceBuilder().UsePostgreSql(connection, o => o.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment);
        var source = builder.ConfigureLimits(limits ?? new() { MaxConnections = 5, MaxWaiters = 4 }).Build();
        source.TestRetirement = snapshot => { Check(snapshot.Released && !snapshot.RootLive && snapshot.Controls == 0 && snapshot.CallbackEntries == 0 && snapshot.RetainedInputBytes == 0, "separate managed retirement"); Check(snapshot.Start is AsyncStartOutcome.Skipped or AsyncStartOutcome.Rejected or AsyncStartOutcome.NotStarted || snapshot.NativeQuiet, "native quiet before public completion"); Interlocked.Increment(ref _retirements); };
        return source;
    }
    internal static EntityKey<Item> Key(long tenant, long id) => Items.Table.Key(Items.Tenant, tenant).And(Items.Id, id);
    internal static Task<long> Execute(DataSession session, string sql) => session.ExecuteAsync(SqlCommand.Create(sql, kind: SqlCommandKind.Mutation));
    internal static Task<long> Count(DataSession session, string table) => session.ScalarAsync(SqlCommand.Create("SELECT CAST(count(*) AS BIGINT) FROM " + table), Codecs.Int64);
    internal static async Task Run()
    {
        if (Environment.GetCommandLineArgs().Contains("--require-aot")) Check(!RuntimeFeature.IsDynamicCodeSupported && !RuntimeFeature.IsDynamicCodeCompiled, "actual Native AOT execution without dynamic code");
        var pg = Environment.GetEnvironmentVariable("NW_TEST_POSTGRES") ?? throw new InvalidOperationException("An actual PostgreSQL18 server is required.");
        var directory = Path.Combine(Path.GetTempPath(), "nw-b14-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        Check(Manual.Items.MetadataHash == Items.Table.MetadataHash && Manual.Items.Row.ShapeHash == Items.Row.ShapeHash, "generated and manual canonical identities");
        Check(!BulkFailure.TryGetProgress(new Exception(), out _), "nonbulk exception has no progress");
        PayloadValidation();
        BoundedCodecs();
        var nativeHashes = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native"), "libnw_database_*.so").ToDictionary(x => x, x => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x))));
        foreach (var provider in new[] { DatabaseProvider.Sqlite, DatabaseProvider.PostgreSql })
        {
            Console.WriteLine("B14 provider=" + provider); var connection = provider == DatabaseProvider.Sqlite ? Path.Combine(directory, "main.db") : pg;
            await using (var source = Source(provider, connection))
            {
                Check(source.Capabilities.Supports("db.returning.v1") && source.Capabilities.Supports("db.upsert.v1") && source.Capabilities.Supports("db.bulk-batches.v1"), "actual admitted mutation capabilities");
                await using var session = await source.OpenSessionAsync();
                await Execute(session, "CREATE TABLE b14_items(\"Tenant\" BIGINT NOT NULL,\"Id\" BIGINT NOT NULL,\"Name\" TEXT NULL,\"Version\" BIGINT NOT NULL,PRIMARY KEY(\"Tenant\",\"Id\"))");
                await Execute(session, "CREATE TABLE b14_children(\"Id\" BIGINT PRIMARY KEY,\"Tenant\" BIGINT NULL,\"ParentId\" BIGINT NULL,\"Name\" TEXT NULL,\"Amount\" BIGINT NULL)");
                await Execute(session, provider == DatabaseProvider.Sqlite ? "CREATE TABLE b14_generated(\"Id\" INTEGER PRIMARY KEY AUTOINCREMENT)" : "CREATE TABLE b14_generated(\"Id\" BIGSERIAL PRIMARY KEY)");
                await Crud(source, session);
                await Relations(source, session);
                await FluentExperience(source, session, connection);
                await BulkCases(source, session);
                await CompoundAdmission(session);
                await RawContracts(source, session);
                await SchemaMismatch(provider, connection);
            }
            await MappingAndClose(provider, connection);
            await SnapshotBudgets(provider, connection);
            if (provider == DatabaseProvider.PostgreSql) await ActualUnknownAndRollbackFailure(connection);
        }
        foreach (var (path, hash) in nativeHashes) Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == hash, "C# entities/descriptors reuse unchanged native asset");
        await WaitZero(SqliteDataSource.Engine); await WaitZero(PostgreSqlDataSource.Engine); Directory.Delete(directory, true);
        Console.WriteLine($"B14 actual Linux two-provider assertions={_assertions}; managed retirements={_retirements}; all six native owner counters=0");
    }
    private static async Task Crud(DataSource source, DataSession session)
    {
        Check(await session.InsertAsync(Items.Table, new(1, 1, "initial", 1)) == 1, "insert exact count");
        Check(await session.InsertAsync(Manual.Items, new(2, 1, "other tenant", 1)) == 1, "manual insert parity");
        Check(await session.FindAsync(Items.Table, Key(1, 1)) == new Item(1, 1, "initial", 1), "native complete-key find");
        Check(await session.FindAsync(Items.Table, Key(7, 1)) is null, "missing key is null");
        var changes = new Changes<Item>().Set(Items.Name, (string?)null).Set(Items.Version, 77L).Unset(Items.Version);
        Check(await session.UpdateAsync(Items.Table, Key(1, 1), changes) == 1, "explicit nullable assignment");
        Check(await session.FindAsync(Items.Table, Key(1, 1)) == new Item(1, 1, null, 1), "NotSet differs from NULL");
        var returned = await session.InsertReturningAsync(Items.Table, new Item(1, 2, "return", 3), Items.Row); Check(returned == new Item(1, 2, "return", 3), "exact INSERT RETURNING mapping");
        Check(await session.InsertAsync(Generated.Table, new(900)) == 1, "generated-only default values");
        var generated = await session.InsertReturningAsync(Generated.Table, new GeneratedItem(900), Generated.Row); Check(generated.Id == 2, "database generated identity returned without input mutation");
        var target = ConflictTarget<Item>.Columns(Items.Tenant).And(Items.Id);
        Check(await session.UpsertAsync(Items.Table, new(1, 1, "insert payload", 8), target, new Changes<Item>().Set(Items.Name, "update payload").Set(Items.Version, 9L)) == 1, "native atomic upsert count");
        Check(await session.FindAsync(Items.Table, Key(1, 1)) == new Item(1, 1, "update payload", 9), "upsert independent field8 values");
        await session.UpsertAsync(Items.Table, new(1, 1, "not the update", 12), target, new Changes<Item>().Set(Items.Name, (string?)null));
        Check(await session.FindAsync(Items.Table, Key(1, 1)) == new Item(1, 1, null, 9), "upsert NULL and subset update");
        var firstTarget = ConflictTarget<Item>.Columns(Items.Tenant); _ = firstTarget.And(Items.Id);
        var constraint = await Throws<DatabaseException>(() => session.UpsertAsync(Items.Table, new(3, 1, "not unique", 1), firstTarget, new Changes<Item>().Set(Items.Name, "x"))); Check(constraint.Kind is DatabaseErrorKind.Constraint or DatabaseErrorKind.Provider, "database checks real unique constraint");
        if (source.Provider == DatabaseProvider.Sqlite) await Throws<DatabaseCapabilityException>(() => session.UpsertAsync(Items.Table, new(1, 1, "x", 1), new("b14_items_pkey"), new Changes<Item>().Set(Items.Name, "x")));
        else Check(await session.UpsertAsync(Items.Table, new(1, 1, "ignored", 5), new("b14_items_pkey"), new Changes<Item>().Set(Items.Version, 10L)) == 1, "PostgreSQL named constraint target");
        var current = (await session.FindAsync(Items.Table, Key(1, 1)))!;
        await using (var a = await source.OpenSessionAsync()) await using (var b = await source.OpenSessionAsync())
        {
            async Task<bool> Writer(DataSession writer, long version)
            { try { Check(await writer.UpdateCheckedAsync(Items.Table, Key(1, 1), new Changes<Item>().Set(Items.Version, version), new ConcurrencyCheck<Item>().Expect(Items.Version, current.Version)) == 1, "CAS acknowledged one"); return true; } catch (ConcurrencyConflictException conflict) { Check(conflict.ExpectedRows == 1 && conflict.ActualRows == 0, "exact CAS counts"); return false; } }
            var winners = await Task.WhenAll(Writer(a, 101), Writer(b, 102)); Check(winners.Count(x => x) == 1, "same-version concurrent writers have one winner");
        }
        Check((await session.FindAsync(Items.Table, Key(2, 1)))!.Version == 1, "CAS preserves tenant predicate");
        var miss = await Throws<ConcurrencyConflictException>(() => session.DeleteCheckedAsync(Items.Table, Key(7, 1), new ConcurrencyCheck<Item>().Expect(Items.Version, 1L))); Check(miss.ExpectedRows == 1 && miss.ActualRows == 0, "cross-tenant checked delete conflict");
        Check(await session.DeleteAsync(Items.Table, Key(9, 9)) == 0, "ordinary delete missing is0");
        Check(await session.DeleteCheckedAsync(Items.Table, Key(1, 2), new ConcurrencyCheck<Item>().Expect(Items.Version, 3L)) == 1, "checked delete exact match");
        await Throws<ArgumentException>(() => session.UpdateAsync(Items.Table, Items.Table.Key(Items.Tenant, 1L), new Changes<Item>().Set(Items.Name, "x")));
        await Throws<ArgumentException>(() => session.UpdateAsync(Items.Table, Key(1, 1), new Changes<Item>()));
        await Throws<ArgumentException>(() => session.UpdateCheckedAsync(Items.Table, Key(1, 1), new Changes<Item>().Set(Items.Name, "x"), new ConcurrencyCheck<Item>()));
    }
    private static async Task Relations(DataSource source, DataSession session)
    {
        await session.InsertAsync(Children.Table, new(1, 1, 1, null, 5)); await session.InsertAsync(Children.Table, new(2, 1, 1, null, null)); await session.InsertAsync(Children.Table, new(3, 2, 1, "other tenant", 20)); await session.InsertAsync(Children.Table, new(4, null, 1, "partial null", 7)); await session.InsertAsync(Children.Table, new(5, 1, null, "partial null", 7)); await session.InsertAsync(Children.Table, new(6, 99, 99, "no parent row", 8));
        await session.InsertAsync(Items.Table, new(1, 88, null, 1));
        var relation = Relation<Item, Child>.CreateOptional(Items.Table, Items.Tenant, Children.Table, Children.Tenant).AndOptional(Items.Id, Children.ParentId);
        var parentHash = Items.Table.MetadataHash; var childHash = Children.Table.MetadataHash;
        var loaded = await session.LoadRelatedAsync(Key(1, 1), relation); Check(loaded.Count == 2 && loaded.All(x => x.Name is null), "composite optional relation includes present all-null business fields");
        Check((await session.LoadRelatedAsync(Key(2, 1), relation)).Single().Id == 3, "tenant remains an explicit key component");
        Check((await session.LoadRelatedAsync(Key(99, 99), relation)).Single().Id == 6, "no implicit parent existence query");
        Check((await session.LoadRelatedAsync(Key(98, 98), relation)).Count == 0, "absent child bounded empty result");
        Check(Items.Table.MetadataHash == parentHash && Children.Table.MetadataHash == childHash, "post-Build relation leaves hashes unchanged");
        var owners = new DatabaseOwner[32]; Parallel.For(0, owners.Length, i => owners[i] = source.RegisterRelation(relation)); Check(owners.All(o => ReferenceEquals(o, owners[0])) && source.RelationCacheCount == 1, "bounded concurrent cache deduplication");
        using (var q = session.From(Items.Table))
        {
            var joined = q.LeftJoin(Children.Table, "child", on => on.Left(Items.Tenant).AsNullable().Equal(on.Right(Children.Tenant)).And(on.Left(Items.Id).AsNullable().Equal(on.Right(Children.ParentId))));
            q.WhereEqual(Items.Tenant, 1L);
            var rows = await q.Select(q.Column(Items.Id), SqlFunctions.Case(q.HasMatch(joined), q.Value(true, Codecs.Boolean), q.Value(false, Codecs.Boolean)), (id, exists) => new Presence(id, exists)).ToListAsync();
            Check(rows.Count(x => x.Id == 1 && x.Exists) == 2, "actual LEFT JOIN duplicates reflect explicit child rows and presence marker");
            Check(rows.Any(x => x.Id == 88 && !x.Exists), "unmatched LEFT JOIN differs from a present row with NULL business fields");
        }
        using (var q = session.From(Children.Table)) Check(await q.WhereEqual(Children.Tenant, 1L).CountAsync() == 3, "actual count over duplicate/composite child keys");
        using (var q = session.From(Children.Table)) Check(await q.WhereEqual(Children.Tenant, 1L).Select(SqlFunctions.Sum(q.Column(Children.Amount))).SingleAsync() == 12, "actual nullable sum");
        using (var q = session.From(Items.Table))
        {
            var joined = q.LeftJoin(Children.Table, "aggregate_child", on => on.Left(Items.Tenant).AsNullable().Equal(on.Right(Children.Tenant)).And(on.Left(Items.Id).AsNullable().Equal(on.Right(Children.ParentId))));
            var key = q.Column(Items.Id); q.WhereEqual(Items.Tenant, 1L).GroupBy(key);
            var builder = q.Projection<Aggregate>(); var id = builder.Add(key, "id"); var count = builder.Add(SqlFunctions.Count(q.NullableValue(joined, Children.Id)), "count"); var sum = builder.Add(SqlFunctions.Sum(q.NullableValue(joined, Children.Amount)), "sum");
            var rows = await q.Select(builder.Build(row => new(row.Get(id), row.Get(count), row.Get(sum)))).ToListAsync();
            Check(rows.Single(x => x.Id == 1) == new Aggregate(1, 2, 5) && rows.Single(x => x.Id == 88) == new Aggregate(88, 0, null), "native LEFT JOIN grouping/count/sum matches exact nullable oracle");
        }
        var childKey = Children.Table.Key(Children.Id, 2L);
        await session.UpdateAsync(Children.Table, childKey, new Changes<Child>().Set(Children.ParentId, (long?)null)); Check((await session.LoadRelatedAsync(Key(1, 1), relation)).Count == 1, "explicit nullable FK unlink");
        await session.UpdateAsync(Children.Table, childKey, new Changes<Child>().Set(Children.ParentId, (long?)1).Set(Children.Tenant, (long?)2)); Check((await session.LoadRelatedAsync(Key(2, 1), relation)).Count == 2, "explicit FK relink retains composite tenant mapping");
        await session.UpdateAsync(Children.Table, childKey, new Changes<Child>().Set(Children.Tenant, (long?)1));
        var repeated = await session.LoadRelatedAsync(Key(1, 1), relation); Check(repeated.Select(x => x.Id).Order().SequenceEqual(loaded.Select(x => x.Id).Order()), "duplicate explicit parent loads preserve result without tracking");
    }
    private sealed record Presence(long Id, bool Exists);
    private sealed record Aggregate(long Id, long Count, long? Sum);
    internal static async IAsyncEnumerable<Item> Rows(params Item[] rows) { foreach (var row in rows) { await Task.Yield(); yield return row; } }
    private static async Task WaitZero(EngineDescriptor engine) { for (var i = 0; i < 200; i++) { if (Counts(engine).All(x => x == 0)) { Check(true, "all native owners retired"); return; } await Task.Delay(10); } throw new InvalidOperationException("native leak " + string.Join(',', Counts(engine))); }
    internal static unsafe ulong[] Counts(EngineDescriptor engine) { var call = (delegate* unmanaged[Cdecl]<uint, ulong>)EngineModuleLoader.ResolveExport(engine, "nw_db_diagnostic_count_v1"); return Enumerable.Range(0, 6).Select(i => call((uint)i)).ToArray(); }
}
