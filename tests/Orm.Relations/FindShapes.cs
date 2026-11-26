using Korda.Data;
using Korda.Database;
using Korda.Query;

internal static partial class Suite
{
    private sealed record FindRow(long Tenant, long Id, string? Name);
    private sealed record FindTable(Table<FindRow> Table, Column<FindRow, long> Tenant, Column<FindRow, long> Id)
    {
        internal EntityKey<FindRow> Key(long tenant = 1, long id = 1) => Table.Key(Tenant, tenant).And(Id, id);
    }

    private static FindTable FindDescriptor(string tag = "", Func<FindRow, FindRow>? mapped = null, string tableName = "b14_find")
    {
        var builder = new TableBuilder<FindRow>(tableName);
        var tenant = builder.Column("Tenant", Codecs.Int64, new() { IsPrimaryKey = true });
        var id = builder.Column("Id", Codecs.Int64, new() { IsPrimaryKey = true });
        var name = builder.Column("Name", Codecs.NullableReference(Codecs.String));
        var table = builder.Build(row =>
        {
            var value = new FindRow(row.Get(tenant), row.Get(id), tag + row.Get(name));
            return mapped is null ? value : mapped(value);
        }, (row, value) => { row.Set(tenant, value.Tenant); row.Set(id, value.Id); row.Set(name, value.Name); });
        return new(table, tenant, id);
    }

    private static async Task FindShapes(DatabaseProvider provider, string connection)
    {
        var assertionsBefore = Volatile.Read(ref _assertions);
        var first = FindDescriptor("first:"); var second = FindDescriptor("second:");
        Check(first.Table.MetadataHash == second.Table.MetadataHash && first.Table.Row.ShapeHash == second.Table.Row.ShapeHash,
            "Find mapper identity fixture has equal model and row metadata");
        await using (var source = Source(provider, connection, new() { MaxPreparedPlans = 1, MaxQueryNodes = 32 }))
        {
            await using var session = await source.OpenSessionAsync();
            await Execute(session, "CREATE TABLE b14_find(\"Tenant\" BIGINT NOT NULL,\"Id\" BIGINT NOT NULL,\"Name\" TEXT NULL,PRIMARY KEY(\"Tenant\",\"Id\"))");
            await session.InsertAsync(first.Table, new(1, 1, "one"));
            await session.InsertAsync(first.Table, new(2, 1, "two"));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            var before = StatementCount(source);
            Reject<ArgumentException>(() => _ = session.FindAsync(first.Table, first.Table.Key(first.Tenant, 1L), canceled.Token));
            Reject<ArgumentException>(() => _ = session.FindAsync(first.Table, second.Key(), canceled.Token));
            var cancel = await Throws<OperationCanceledException>(() => session.FindAsync(first.Table, first.Key(), canceled.Token));
            Check(cancel.CancellationToken == canceled.Token && source.FindCacheCounts == (0, 0) && StatementCount(source) == before,
                "cold pre-cancel preserves original key validation and creates no shape or SQL");

            await using (var otherSession = await source.OpenSessionAsync())
            {
                before = StatementCount(source);
                var a = session.FindAsync(first.Table, first.Key());
                var b = otherSession.FindAsync(second.Table, second.Key(2));
                await Task.WhenAll(a, b);
                Check(a.Result == new FindRow(1, 1, "first:one") && b.Result == new FindRow(2, 1, "second:two"),
                    "same-metadata mappers and independent key snapshots remain exact across two sessions and eviction");
                Check(StatementCount(source) - before == 2,
                    "each fixed Find dispatches exactly one business statement: " + (StatementCount(source) - before));
            }
            for (var i = 0; i < 40; i++)
            {
                var descriptor = FindDescriptor(i + ":");
                Check((await session.FindAsync(descriptor.Table, descriptor.Key()))!.Name == i + ":one",
                    "evicted Find shape cannot substitute a mapper with equal metadata");
                var retained = source.FindCacheCounts;
                Check(retained.Entries == 1 && retained.Bytes > 0 && retained.Bytes <= source.Limits.MaxPreparedPlanBytes,
                    "Find cache retains finite entry and byte limits");
            }
            using (var query = session.From(first.Table))
            {
                var caller = query.Parameter("caller_after_find_evictions", Codecs.Int64);
                Check(caller.Definition!.Id == 1, "private Find slots never consume source parameter identities");
                query.Where(query.Column(first.Tenant).Equal(query.ParameterValue(caller))).WhereEqual(first.Id, 1L);
                using var prepared = query.SelectAll().Prepare();
                Check((await prepared.ToListAsync(session, new QueryParameters().Set(caller, 2L))).Single().Name == "first:two",
                    "public parameter registry remains executable after more private evictions than its node limit");
            }
            using (var retained = source.AcquireFindPlan(first.Table))
            {
                before = StatementCount(source);
                using var replacement = source.AcquireFindPlan(second.Table);
                Check(StatementCount(source) == before, "cold shape compilation has no database side effect");
                Check((await retained.Execute(session, first.Key(), default))!.Name == "first:one",
                    "a caller lease remains executable after LRU cache eviction");
                Reject<ArgumentException>(() => _ = retained.Execute(session, second.Key(), default));
                await using var otherSource = Source(provider, connection);
                await using var foreignSession = await otherSource.OpenSessionAsync();
                before = StatementCount(source);
                Reject<ArgumentException>(() => _ = retained.Execute(foreignSession, first.Key(), default));
                Check(StatementCount(source) == before, "private Find shape rejects another source before SQL");
            }
            var closed = await source.OpenSessionAsync(); await closed.DisposeAsync();
            Reject<ArgumentException>(() => _ = closed.FindAsync(first.Table, first.Table.Key(first.Tenant, 1L), canceled.Token));
            Reject<ObjectDisposedException>(() => _ = closed.FindAsync(first.Table, first.Key(), canceled.Token));
            Check(await session.FindAsync(first.Table, first.Key(9)) is null, "fixed Find retains absent-key null result");
        }
        await ConfiguredFindKeys(provider, connection);
        await FindShapeByteLimits(provider, connection);
        await FindShapeLifetime(provider, connection);
        await FindShapeSchema(provider, connection);
        await using (var source = Source(provider, connection, new() { MaxPreparedPlanBytes = 1 }))
        {
            await using var session = await source.OpenSessionAsync();
            Check((await session.FindAsync(first.Table, first.Key()))!.Name == "first:one" &&
                (await session.FindAsync(second.Table, second.Key(2)))!.Name == "second:two" && source.FindCacheCounts == (0, 0),
                "an individually oversized Find shape executes without being retained");
        }
        await WaitZero(provider == DatabaseProvider.Sqlite ? Korda.Database.Sqlite.SqliteDataSource.Engine : Korda.Database.PostgreSql.PostgreSqlDataSource.Engine);
        Console.WriteLine("DB-Exec-10 " + provider + " fixed Find: mapper identity, snapshots, bounded eviction, private slots, retirement and schema passed; assertions=" +
            (Volatile.Read(ref _assertions) - assertionsBefore) + "; native owner counters [0..5,8,9]=0");
    }

    private sealed record ConfiguredFindRow(decimal Amount, byte[] Token, string Name);
    private static async Task ConfiguredFindKeys(DatabaseProvider provider, string connection)
    {
        var builder = new TableBuilder<ConfiguredFindRow>("b14_find_configured");
        var amount = builder.Column("Amount", Codecs.FixedScaleMoney(2), new() { IsPrimaryKey = true });
        var token = builder.Column("Token", Codecs.Bytes, new() { IsPrimaryKey = true });
        var name = builder.Column("Name", Codecs.String);
        var table = builder.Build(row => new(row.Get(amount), row.Get(token), row.Get(name)),
            (row, value) => { row.Set(amount, value.Amount); row.Set(token, value.Token); row.Set(name, value.Name); });
        await using var source = Source(provider, connection); await using var session = await source.OpenSessionAsync();
        var binary = provider == DatabaseProvider.Sqlite ? "BLOB" : "BYTEA";
        await Execute(session, "CREATE TABLE b14_find_configured(\"Amount\" BIGINT NOT NULL,\"Token\" " + binary + " NOT NULL,\"Name\" TEXT NOT NULL,PRIMARY KEY(\"Amount\",\"Token\"))");
        await session.InsertAsync(table, new(1.25m, [4, 2, 0], "first"));
        await session.InsertAsync(table, new(2.50m, [9, 8, 7], "second"));
        var bytes = new byte[] { 4, 2, 0 }; var key = table.Key(amount, 1.25m).And(token, bytes);
        bytes[0] = 99;
        var pending = session.FindAsync(table, key); bytes[1] = 99;
        var first = (await pending)!;
        Check(first.Amount == 1.25m && first.Token.SequenceEqual(new byte[] { 4, 2, 0 }) && first.Name == "first",
            "configured key schema matches native bytes and EntityKey owns its original mutable binary snapshot");
        first.Token[0] = 88;
        var second = (await session.FindAsync(table, table.Key(amount, 2.50m).And(token, [9, 8, 7])))!;
        Check(second.Amount == 2.50m && second.Token.SequenceEqual(new byte[] { 9, 8, 7 }) && second.Name == "second" &&
            (await session.FindAsync(table, key))!.Token[0] == 4,
            "warm configured Find changes only ordinal values and returned DTO bytes do not alias future executions");
    }

    private sealed record FindTextRow(string Id, string Value);
    private static async Task FindShapeByteLimits(DatabaseProvider provider, string connection)
    {
        var first = FindDescriptor("retained:"); var second = FindDescriptor("replacement:");
        long charge;
        await using (var sizing = Source(provider, connection))
        {
            using var shape = sizing.AcquireFindPlan(first.Table); charge = shape.Charge;
        }
        await using (var bounded = Source(provider, connection, new() { MaxPreparedPlans = 4, MaxPreparedPlanBytes = charge + charge / 2 }))
        {
            await using var session = await bounded.OpenSessionAsync();
            using var held = bounded.AcquireFindPlan(first.Table);
            using var replacement = bounded.AcquireFindPlan(second.Table);
            Check(held.Charge == charge && replacement.Charge == charge && bounded.FindCacheCounts.Entries == 1 &&
                bounded.FindCacheCounts.Bytes == charge,
                "cache bytes evict an old Find even while the entry bound permits another shape");
            Check((await held.Execute(session, first.Key(), default))!.Name == "retained:one" &&
                (await replacement.Execute(session, second.Key(2), default))!.Name == "replacement:two",
                "byte eviction drops only the cache reference and leaves retained callers executable");
        }
        var builder = new TableBuilder<FindTextRow>("b14_find_text");
        var id = builder.Column("Id", Codecs.String, new() { IsPrimaryKey = true });
        var value = builder.Column("Value", Codecs.String);
        var table = builder.Build(row => new(row.Get(id), row.Get(value)),
            (row, item) => { row.Set(id, item.Id); row.Set(value, item.Value); });
        await using var source = Source(provider, connection, new() { MaxParameterBytes = 1024 });
        await using var current = await source.OpenSessionAsync();
        await Execute(current, "CREATE TABLE b14_find_text(\"Id\" TEXT PRIMARY KEY,\"Value\" TEXT NOT NULL)");
        await current.InsertAsync(table, new("short", "value"));
        var key = table.Key(id, "short");
        Check(await current.FindAsync(table, key) == new FindTextRow("short", "value"), "variable Find key warms within original logical bytes");
        var before = StatementCount(source);
        var wide = table.Key(id, new string('x', 700));
        var oldLogical = await FindFailure(() => LiteralFindOracle(current, table, wide));
        var newLogical = await FindFailure(() => current.FindAsync(table, wide));
        FindFailureParity(provider, "warm-logical", oldLogical, newLogical, synchronous: true);
        Check(oldLogical.Error is DatabaseException { Kind: DatabaseErrorKind.InvalidState } &&
            StatementCount(source) == before && source.FindCacheCounts.Entries == 1,
            "warm Find preserves original binding-quota classification and rejects before SQL");
        Check(await current.FindAsync(table, key) == new FindTextRow("short", "value"),
            "warm oversized key rejection does not consume or invalidate the immutable Find shape");
        var oversizedFrame = table.Key(id, new string('x', 1000));
        before = StatementCount(source);
        var oldFrame = await FindFailure(() => LiteralFindOracle(current, table, oversizedFrame));
        var newFrame = await FindFailure(() => current.FindAsync(table, oversizedFrame));
        FindFailureParity(provider, "oversized-expression-frame", oldFrame, newFrame, synchronous: true);
        Check(oldFrame.Error is ArgumentException && StatementCount(source) == before, "original expression-frame size error remains synchronous and dispatches no SQL");
        var compositeBuilder = new TableBuilder<FindTextRow>("b14_find_composite_budget");
        var firstKey = compositeBuilder.Column("Id", Codecs.String, new() { IsPrimaryKey = true });
        var secondKey = compositeBuilder.Column("Value", Codecs.String, new() { IsPrimaryKey = true });
        var composite = compositeBuilder.Build(row => new(row.Get(firstKey), row.Get(secondKey)),
            (row, item) => { row.Set(firstKey, item.Id); row.Set(secondKey, item.Value); });
        var mixed = composite.Key(firstKey, new string('a', 1024 - WireSchema.HeaderBytes - WireSchema.DescriptorBytes - WireSchema.CellBytes))
            .And(secondKey, new string('b', 1000));
        var oldMixed = await FindFailure(() => LiteralFindOracle(current, composite, mixed));
        var newMixed = await FindFailure(() => current.FindAsync(composite, mixed));
        Console.WriteLine("Find expected metadata admission failure: " + oldMixed.Error);
        FindFailureParity(provider, "metadata-before-key", oldMixed, newMixed, synchronous: true);
        Check(oldMixed.Error is DatabaseException { Kind: DatabaseErrorKind.ResourceLimit } && StatementCount(source) == before,
            "original model registration capacity retains priority over key admission");
        await using (var fresh = Source(provider, connection, new() { MaxParameterBytes = 1024 }))
        {
            await using var other = await fresh.OpenSessionAsync(); before = StatementCount(fresh);
            oldMixed = await FindFailure(() => LiteralFindOracle(other, composite, mixed));
            newMixed = await FindFailure(() => other.FindAsync(composite, mixed));
            FindFailureParity(provider, "composite-logical-before-later-wire", oldMixed, newMixed, synchronous: true);
            Check(oldMixed.Error is DatabaseException { Kind: DatabaseErrorKind.InvalidState } && StatementCount(fresh) == before,
                "an earlier composite-key logical rejection retains priority over a later oversized frame");
        }
        await using (var nodes = Source(provider, connection, new() { MaxParameterBytes = 1024, MaxQueryNodes = 9 }))
        {
            await using var other = await nodes.OpenSessionAsync(); before = StatementCount(nodes);
            var crossing = composite.Key(firstKey, "short").And(secondKey, new string('b', 1000));
            var oldNodes = await FindFailure(() => LiteralFindOracle(other, composite, crossing));
            var newNodes = await FindFailure(() => other.FindAsync(composite, crossing));
            FindFailureParity(provider, "root-expanded-node-before-later-wire", oldNodes, newNodes, synchronous: true);
            Check(oldNodes.Error is DatabaseException { Kind: DatabaseErrorKind.InvalidState } && StatementCount(nodes) == before,
                "the root statement's expanded node retains its original quota priority");
        }
        await using (var uncached = Source(provider, connection, new() { MaxParameterBytes = 1024, MaxPreparedPlanBytes = 1 }))
        {
            await using var other = await uncached.OpenSessionAsync();
            var failure = await FindFailure(() => other.FindAsync(table, wide));
            FindFailureParity(provider, "uncached-logical", oldLogical, failure, synchronous: true);
            Check(uncached.FindCacheCounts == (0, 0) && await other.FindAsync(table, key) is not null,
                "uncached oversized shape retains the same logical budget and failure cleanup");
        }
        foreach (var limits in new[] { new DatabaseLimits { MaxQueryNodes = 4 }, new DatabaseLimits { MaxParameters = 1 } })
        {
            await using var bounded = Source(provider, connection, limits); await using var other = await bounded.OpenSessionAsync();
            before = StatementCount(bounded);
            var oldFailure = await FindFailure(() => LiteralFindOracle(other, table, key));
            var newFailure = await FindFailure(() => other.FindAsync(table, key));
            var construction = limits.MaxQueryNodes == 4;
            FindFailureParity(provider, construction ? "cold-nodes" : "compiled-arguments", oldFailure, newFailure, synchronous: true);
            Check(StatementCount(bounded) == before, "cold Find quota rejects before SQL");
        }
    }

    // The removed 47fe650 Find construction, retained only as a behavior oracle.
    // WhereEqual has a different bounded encoder and is not an equivalent oracle.
    private static Task<FindTextRow?> LiteralFindOracle(DataSession session, Table<FindTextRow> table, EntityKey<FindTextRow> key)
    {
        var query = session.From(table);
        try
        {
            query.Context.Run(() =>
            {
                foreach (var part in key.Values)
                {
                    var codec = part.Column.Definition.Codec; var bound = session.Source.BindCodec(codec);
                    var value = DbWire.Encode(new(PayloadKind.ExpressionValues, session.Source.Binding.SemanticProfile, new byte[32], 0, 0,
                        [new(1, codec, bound)])
                    { ExactRows = 1 }, 1, [new ReadOnlyMemory<byte>(part.Bytes!)]);
                    var expression = query.Context.Binding.Binary(1, query.Context.Binding.Column(part.Column.Definition.Id), query.Context.Binding.Value(value));
                    query.Context.Clause(1, QueryContext.U(1, expression.In(query.Context.Binding)));
                }
            });
            return Retire(query, query.SelectAll().SingleOrDefaultAsync());
        }
        catch { query.Dispose(); throw; }
        static async Task<FindTextRow?> Retire(Query<FindTextRow> query, Task<FindTextRow?> task)
        { using (query) return await task.ConfigureAwait(false); }
    }

    private static async Task<(Exception Error, bool Synchronous)> FindFailure(Func<Task<FindTextRow?>> execute)
    {
        Task<FindTextRow?> pending;
        try { pending = execute(); }
        catch (Exception error) { return (error, true); }
        try { await pending; }
        catch (Exception error) { return (error, false); }
        throw new InvalidOperationException("Expected Find failure.");
    }

    private static void FindFailureParity(DatabaseProvider provider, string label,
        (Exception Error, bool Synchronous) old, (Exception Error, bool Synchronous) current, bool synchronous)
    {
        var oldKind = (old.Error as DatabaseException)?.Kind; var newKind = (current.Error as DatabaseException)?.Kind;
        Console.WriteLine($"Find-budget {provider} {label}: old={old.Error.GetType().Name}/{oldKind}/sync:{old.Synchronous}; new={current.Error.GetType().Name}/{newKind}/sync:{current.Synchronous}");
        Check(old.Error.GetType() == current.Error.GetType() && oldKind == newKind &&
            old.Synchronous == synchronous && current.Synchronous == synchronous, "Find preserves exact original exception category and timing: " + label);
    }

    private static async Task FindShapeLifetime(DatabaseProvider provider, string connection)
    {
        var source = Source(provider, connection); var session = await source.OpenSessionAsync();
        var marker = new MappingFailure();
        var descriptor = FindDescriptor(mapped: _ => { source.Dispose(); throw marker; });
        var failure = await Throws<MappingFailure>(() => session.FindAsync(descriptor.Table, descriptor.Key()));
        Check(ReferenceEquals(failure, marker) && source.FindCacheCounts == (0, 0),
            "source close drops cached Find roots while the accepted pull preserves the original mapper failure");
        await session.DisposeAsync(); await source.DisposeAsync();
        Check(source.Cleanup.LocalResourcesReleased, "Find mapper failure and source close join actual retirement");
        using var cancellation = new CancellationTokenSource();
        await using var currentSource = Source(provider, connection);
        await using var current = await currentSource.OpenSessionAsync();
        var cancelTable = FindDescriptor(mapped: row => { cancellation.Cancel(); return row; });
        var cancel = await Throws<OperationCanceledException>(() => current.FindAsync(cancelTable.Table, cancelTable.Key(), cancellation.Token));
        Check(cancel.CancellationToken == cancellation.Token && await current.FindAsync(cancelTable.Table, cancelTable.Key()) is not null,
            "Find cancellation retains its token, retires the pull and leaves the cached shape and session reusable");
    }

    private static async Task FindShapeSchema(DatabaseProvider provider, string connection)
    {
        var descriptor = FindDescriptor(tableName: "b14_find_schema");
        await using var source = Source(provider, connection); await using var session = await source.OpenSessionAsync();
        await Execute(session, "CREATE TABLE b14_find_schema(\"Tenant\" BIGINT NOT NULL,\"Id\" BIGINT NOT NULL,\"Name\" TEXT NULL,PRIMARY KEY(\"Tenant\",\"Id\"))");
        await session.InsertAsync(descriptor.Table, new(1, 1, "before"));
        Check((await session.FindAsync(descriptor.Table, descriptor.Key()))!.Name == "before", "Find schema fixture is warm");
        await Execute(session, "DROP TABLE b14_find_schema");
        var binary = provider == DatabaseProvider.Sqlite ? "BLOB" : "BYTEA";
        await Execute(session, "CREATE TABLE b14_find_schema(\"Tenant\" BIGINT NOT NULL,\"Id\" BIGINT NOT NULL,\"Name\" " + binary + " NULL,PRIMARY KEY(\"Tenant\",\"Id\"))");
        var literal = provider == DatabaseProvider.Sqlite ? "X'0102'" : "decode('0102','hex')";
        await Execute(session, "INSERT INTO b14_find_schema VALUES(1,1," + literal + ")");
        var failure = await Throws<DatabaseException>(() => session.FindAsync(descriptor.Table, descriptor.Key()));
        Check(failure.Kind is DatabaseErrorKind.ShapeMismatch or DatabaseErrorKind.Provider,
            "cached Find never bypasses provider or row-schema validation after DDL changes");
        Check(await session.ScalarAsync(SqlCommand.Create("SELECT CAST(1 AS BIGINT)"), Codecs.Int64) == 1,
            "rejected warm schema retires the cursor and returns a reusable session");
    }
}
