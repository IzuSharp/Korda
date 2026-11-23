using Korda.Data;
using Korda.Database;
using Korda.Database.PostgreSql;
using Korda.Database.Sqlite;
using Korda.Interop;
using Korda.Query;

internal static partial class Suite
{
    private static async Task FluentExperience(DataSource source, DataSession session, string connection)
    {
        await ProjectionPolicies(session);
        await CompositeCursors(session);
        await RelationBatches(source, session, connection);
        if (source.Provider == DatabaseProvider.Sqlite) await RelationCollation(session);
        await TransactionCallbacks(session);
        await CallbackCancellation(session);
        if (source.Provider == DatabaseProvider.PostgreSql) await CallbackFaults(connection);
        Console.WriteLine("DB-API-01 " + source.Provider + " composite cursor, bounded indexed relations and transaction callback passed");
    }

    private static async Task ProjectionPolicies(DataSession session)
    {
        using var query = session.From(Items.Table);
        query.WhereEqual(Items.Tenant, 1L).WhereEqual(Items.Id, 1L);
        var builder = query.Projection<long>();
        var expression = query.Column(Items.Id);
        var wrong = Codecs.Convert<long, int>(Codecs.Int32, value => checked((int)value), value => value);
        var before = StatementCount(session.Source);
        await Throws<ArgumentException>(() => Task.FromResult(builder.Add(expression, "id", wrong)));
        Check(StatementCount(session.Source) == before, "same CLR type with a different DTO codec rejects before SQL");
        var selected = builder.Add(expression, "id", Codecs.Int64);
        Check(await query.Select(builder.Build(row => row.Get(selected))).SingleAsync() == 1L,
            "rejected DTO policy leaves projection alias and query usable with the exact codec");
    }

    private static async Task CompositeCursors(DataSession session)
    {
        var page = new List<Item>();
        QueryCursor<Item>? cursor = null;
        while (true)
        {
            using var query = session.From(Items.Table);
            query.OrderBy(Items.Tenant).ThenBy(Items.Id).Take(2);
            if (cursor is not null) query.SeekAfter(cursor);
            var rows = await query.SelectAll().ToListAsync();
            if (rows.Count == 0) break;
            page.AddRange(rows);
            var last = rows[^1]; cursor = QueryCursor<Item>.Create(Items.Tenant, last.Tenant).Then(Items.Id, last.Id);
        }
        Check(page.Select(item => (item.Tenant, item.Id)).SequenceEqual([(1L, 1L), (1L, 88L), (2L, 1L)]), "composite page boundaries have no duplicate or missing rows");
        using (var query = session.From(Items.Table))
        {
            query.OrderBy(Items.Tenant).ThenBy(Items.Id, SortDirection.Descending)
                .SeekAfter(QueryCursor<Item>.Create(Items.Tenant, 1L).Then(Items.Id, 1L));
            await Throws<InvalidOperationException>(() => Task.FromResult(query.OrderBy(Items.Id)));
            await Throws<InvalidOperationException>(() => Task.FromResult(query.Skip(1)));
            await Throws<InvalidOperationException>(() => Task.FromResult(query.SeekAfter(QueryCursor<Item>.Create(Items.Tenant, 1L).Then(Items.Id, 1L))));
            using var clone = query.Clone();
            await Throws<InvalidOperationException>(() => Task.FromResult(clone.ThenBy(Items.Version)));
            Check((await clone.SelectAll().ToListAsync()).Select(item => (item.Tenant, item.Id)).SequenceEqual([(2L, 1L)]), "mixed directions and cloned cursor preserve strict lexicographic order");
        }
        using (var query = session.From(Items.Table))
            Check((await query.OrderBy(Items.Tenant, SortDirection.Descending).ThenBy(Items.Id, SortDirection.Descending)
                .SeekAfter(QueryCursor<Item>.Create(Items.Tenant, 1L).Then(Items.Id, 88L)).SelectAll().ToListAsync())
                .Select(item => (item.Tenant, item.Id)).SequenceEqual([(1L, 1L)]), "descending composite cursor follows direction of both keys");
        using (var query = session.From(Items.Table))
        {
            query.OrderBy(Items.Version).ThenBy(Items.Tenant).ThenBy(Items.Id);
            using var clone = query.Clone();
            var ordered = await query.SelectAll().ToListAsync(); var first = ordered[0];
            var values = QueryCursor<Item>.Create(Items.Version, first.Version).Then(Items.Tenant, first.Tenant).Then(Items.Id, first.Id);
            Check((await clone.SeekAfter(values).SelectAll().ToListAsync()).SequenceEqual(ordered.Skip(1)), "nonkey sort prefix with full composite key tie breaker");
        }
        using (var query = session.From(Items.Table))
        {
            query.OrderBy(Items.Tenant).ThenBy(Items.Id);
            await Throws<ArgumentException>(() => Task.FromResult(query.SeekAfter(QueryCursor<Item>.Create(Items.Tenant, 1L))));
            await Throws<ArgumentException>(() => Task.FromResult(query.SeekAfter(QueryCursor<Item>.Create(Items.Id, 1L).Then(Items.Tenant, 1L))));
            await Throws<ArgumentException>(() => Task.FromResult(query.SeekAfter(QueryCursor<Item>.Create(Manual.Tenant, 1L).Then(Manual.Id, 1L))));
            query.Skip(1);
            await Throws<ArgumentException>(() => Task.FromResult(query.SeekAfter(QueryCursor<Item>.Create(Items.Tenant, 1L).Then(Items.Id, 1L))));
        }
        using (var query = session.From(Items.Table))
        {
            query.OrderBy(Items.Tenant);
            await Throws<ArgumentException>(() => Task.FromResult(query.SeekAfter(QueryCursor<Item>.Create(Items.Tenant, 1L))));
        }
        await Throws<ArgumentException>(() => Task.FromResult(QueryCursor<Item>.Create(Items.Name, "nullable is unsupported")));
        await Throws<ArgumentException>(() => Task.FromResult(QueryCursor<Item>.Create(Items.Tenant, 1L).Then(Items.Tenant, 2L)));
    }

    private static async Task RelationBatches(DataSource source, DataSession session, string connection)
    {
        var relation = Relation<Item, Child>.CreateOptional(Items.Table, Items.Tenant, Children.Table, Children.Tenant).AndOptional(Items.Id, Children.ParentId);
        List<EntityKey<Item>> keys = [Key(2, 1), Key(1, 88), Key(1, 1), Key(1, 1)];
        var before = StatementCount(source);
        var pending = session.LoadRelatedAsync(keys, relation); keys[0] = Key(99, 99); keys.Clear();
        var groups = await pending;
        Check(StatementCount(source) - before == 1, "one actual SQL execution for a nonempty relation batch");
        Check(groups.Count == 4 && groups[0].Select(child => child.Id).SequenceEqual([3L]) && groups[1].Count == 0,
            "parent collection snapshot and empty group preserve page order");
        Check(groups[2].Select(child => child.Id).Order().SequenceEqual([1L, 2L]) && groups[3].Select(child => child.Id).Order().SequenceEqual([1L, 2L]),
            "duplicate parent positions retain full composite matches and exclude partial NULL foreign keys");
        before = StatementCount(source);
        Check((await session.LoadRelatedAsync(Array.Empty<EntityKey<Item>>(), relation)).Count == 0 && StatementCount(source) == before,
            "empty relation batch executes no SQL");
        await Throws<ArgumentException>(() => session.LoadRelatedAsync([Items.Table.Key(Items.Tenant, 1L)], relation));
        await Throws<ArgumentException>(() => session.LoadRelatedAsync([Manual.Items.Key(Manual.Tenant, 1L).And(Manual.Id, 1L)], relation));
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); before = StatementCount(source);
            await Throws<OperationCanceledException>(() => session.LoadRelatedAsync([Key(1, 1)], relation, cancellation.Token));
            Check(StatementCount(source) == before, "pre-canceled relation batch does not dispatch SQL");
        }
        await using (var bounded = Source(source.Provider, connection, new() { MaxBatchRows = 1 }))
        {
            await using var current = await bounded.OpenSessionAsync();
            var error = await Throws<DatabaseException>(() => current.LoadRelatedAsync([Key(1, 1), Key(2, 1)], relation));
            Check(error.Kind == DatabaseErrorKind.ResourceLimit, "parent input batch is bounded before query execution");
        }
        await using (var bounded = Source(source.Provider, connection, new() { MaxBufferedRows = 3 }))
        {
            await using var current = await bounded.OpenSessionAsync();
            var error = await Throws<DatabaseException>(() => current.LoadRelatedAsync([Key(1, 1), Key(1, 1)], relation));
            Check(error.Kind == DatabaseErrorKind.ResourceLimit, "all duplicate parent groups share one result row budget");
        }
        var mapping = new MappingFailure();
        var failing = Manual.RelationWith(_ => throw mapping);
        Check(ReferenceEquals(await Throws<MappingFailure>(() => session.LoadRelatedAsync([Key(1, 1), Key(2, 1)], failing)), mapping),
            "batch materializer error identity survives cursor cleanup");
        Check((await session.FindAsync(Items.Table, Key(1, 1))) is not null, "batch mapping failure releases session command ownership");
    }

    private static async Task RelationCollation(DataSession session)
    {
        await Execute(session, "CREATE TABLE b14_collated_parents(\"Id\" TEXT COLLATE NOCASE NOT NULL PRIMARY KEY)");
        await Execute(session, "CREATE TABLE b14_collated_children(\"Id\" BIGINT NOT NULL PRIMARY KEY,\"ParentId\" TEXT COLLATE NOCASE NOT NULL)");
        var parents = new TableBuilder<CollatedParent>("b14_collated_parents");
        var parentId = parents.Column("Id", Codecs.String, new() { IsPrimaryKey = true, Collation = "NOCASE" });
        var parentTable = parents.Build(row => new(row.Get(parentId)), (row, item) => row.Set(parentId, item.Id));
        var children = new TableBuilder<CollatedChild>("b14_collated_children");
        var childId = children.Column("Id", Codecs.Int64, new() { IsPrimaryKey = true });
        var foreignKey = children.Column("ParentId", Codecs.String, new() { Collation = "NOCASE" });
        var childTable = children.Build(row => new(row.Get(childId), row.Get(foreignKey)),
            (row, item) => { row.Set(childId, item.Id); row.Set(foreignKey, item.ParentId); });
        await session.InsertAsync(parentTable, new("Ada"));
        await session.InsertAsync(childTable, new(1, "Ada"));
        var relation = Relation<CollatedParent, CollatedChild>.Create(parentTable, parentId, childTable, foreignKey);
        var before = StatementCount(session.Source);
        var groups = await session.LoadRelatedAsync(
            [parentTable.Key(parentId, "ada"), parentTable.Key(parentId, "ADA"), parentTable.Key(parentId, "missing")], relation);
        Check(StatementCount(session.Source) - before == 1 && groups.Count == 3
            && groups[0].Single() == new CollatedChild(1, "Ada")
            && groups[1].Single() == new CollatedChild(1, "Ada") && groups[2].Count == 0,
            "native NOCASE equality assigns differently cased parent keys to their own result positions");
    }

    private sealed record CollatedParent(string Id);
    private sealed record CollatedChild(long Id, string ParentId);

    private static async Task TransactionCallbacks(DataSession session)
    {
        Check(!TransactionFailure.TryGet(new Exception(), out _), "unrelated exception has no callback snapshot");
        DataTransaction? observed = null;
        var count = await session.InTransactionAsync(async (current, token) =>
        {
            observed = current.Transaction;
            return await current.InsertAsync(Items.Table, new(80, 1, "committed callback", 1), token);
        });
        Check(count == 1 && observed is { Outcome: TransactionOutcome.Committed, CommitAttempted: true, Cleanup.LocalResourcesReleased: true }
            && session.Transaction is null, "callback result follows confirmed commit and cleanup");
        Check((await session.FindAsync(Items.Table, Key(80, 1)))?.Name == "committed callback", "callback commit is visible in independent query");

        var marker = new InputFailure();
        var failure = await Throws<InputFailure>(() => session.InTransactionAsync<int>(async (current, token) =>
        { await current.InsertAsync(Items.Table, new(81, 1, "rolled back", 1), token); throw marker; }, new() { RecoveryKey = "callback-81" }));
        Check(ReferenceEquals(failure, marker) && TransactionFailure.TryGet(failure, out var failed)
            && failed is { Outcome: TransactionOutcome.RolledBack, CommitAttempted: false, RecoveryKey: "callback-81", Cleanup.LocalResourcesReleased: true },
            "callback error preserves identity, confirmed rollback and independent cleanup");
        Check(await session.FindAsync(Items.Table, Key(81, 1)) is null, "callback failure leaves no inserted row");

        foreach (var throwsOnRead in new[] { false, true })
        {
            var hostile = new ReadOnlyDataFailure(throwsOnRead);
            Check(!TransactionFailure.TryGet(hostile, out _), "custom exception data does not invent an observation");
            var preserved = await Throws<ReadOnlyDataFailure>(() => session.InTransactionAsync<int>((_, _) => throw hostile));
            Check(ReferenceEquals(hostile, preserved) && TransactionFailure.TryGet(preserved, out var snapshot)
                && snapshot is { Outcome: TransactionOutcome.RolledBack, CommitAttempted: false, Cleanup.LocalResourcesReleased: true },
                "read-only or throwing exception Data preserves the original error and detached transaction observation");
        }

        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); var invoked = 0;
            await Throws<OperationCanceledException>(() => session.InTransactionAsync((_, _) => { invoked++; return Task.CompletedTask; }, cancellationToken: canceled.Token));
            Check(invoked == 0 && session.Transaction is null, "pre-canceled callback never begins or invokes user code");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            var error = await Throws<OperationCanceledException>(() => session.InTransactionAsync(async (current, token) =>
            { await current.InsertAsync(Items.Table, new(82, 1, "cancel before commit", 1), token); cancellation.Cancel(); return 42; }, cancellationToken: cancellation.Token));
            Check(error.CancellationToken == cancellation.Token && TransactionFailure.TryGet(error, out var canceled)
                && canceled is { Outcome: TransactionOutcome.RolledBack, CommitAttempted: false, Cleanup.LocalResourcesReleased: true },
                "commit admission cancellation uses independent rollback cleanup");
            Check(await session.FindAsync(Items.Table, Key(82, 1)) is null, "pre-commit cancellation rolls back business writes");
        }
        await session.InTransactionAsync(async (current, token) => { await current.InsertAsync(Items.Table, new(83, 1, "no result", 1), token); });
        Check(await session.FindAsync(Items.Table, Key(83, 1)) is not null, "non-generic callback confirms commit");
        await using (var transaction = await session.BeginTransactionAsync())
        {
            await Throws<InvalidOperationException>(() => session.InTransactionAsync((_, _) => Task.CompletedTask));
            Check(transaction.State == TransactionState.Active, "nested callback rejects without completing caller transaction");
            await transaction.RollbackAsync();
        }
    }

    private static async Task CallbackCancellation(DataSession session)
    {
        using var cancellation = new CancellationTokenSource();
        var sql = session.Source.Provider == DatabaseProvider.PostgreSql
            ? "SELECT CAST(pg_sleep(10) IS NULL AS BOOLEAN)"
            : "WITH RECURSIVE numbers(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM numbers WHERE x<10000000) SELECT CAST(sum(x) AS BIGINT) FROM numbers";
        var error = await Throws<OperationCanceledException>(() => session.InTransactionAsync(async (current, token) =>
        {
            await current.InsertAsync(Items.Table, new(86, 1, "cancel active query", 1), token);
            Task pending = current.Source.Provider == DatabaseProvider.PostgreSql
                ? current.ScalarAsync(SqlCommand.Create(sql), Codecs.Boolean, token)
                : current.ScalarAsync(SqlCommand.Create(sql), Codecs.Int64, token);
            cancellation.Cancel(); await pending;
        }, cancellationToken: cancellation.Token));
        Check(error.CancellationToken == cancellation.Token && TransactionFailure.TryGet(error, out var failure)
            && failure is { Outcome: TransactionOutcome.RolledBack, CommitAttempted: false, Cleanup.LocalResourcesReleased: true },
            "canceling an admitted callback query joins retirement and rolls back");
        Check(await session.FindAsync(Items.Table, Key(86, 1)) is null, "in-flight callback cancellation leaves no write");
    }

    private static async Task CallbackFaults(string connection)
    {
        await using (var source = Source(DatabaseProvider.PostgreSql, connection))
        {
            await using var session = await source.OpenSessionAsync(); await using var killer = await source.OpenSessionAsync();
            var pid = await session.ScalarAsync(SqlCommand.Create("SELECT CAST(pg_backend_pid() AS BIGINT)"), Codecs.Int64);
            var failure = await Throws<CommitOutcomeUnknownException>(() => session.InTransactionAsync(async (current, token) =>
            {
                await current.InsertAsync(Items.Table, new(84, 1, "unknown callback", 1), token);
                Check(await killer.ScalarAsync(SqlCommand.Create("SELECT pg_terminate_backend(" + pid + ")"), Codecs.Boolean), "terminate isolated callback backend before commit confirmation");
                return 1;
            }, new() { RecoveryKey = "callback-unknown-84" }));
            Check(failure.RecoveryKey == "callback-unknown-84" && TransactionFailure.TryGet(failure, out var unknown)
                && unknown is { Outcome: TransactionOutcome.Unknown, CommitAttempted: true, RecoveryKey: "callback-unknown-84" },
                "callback preserves unknown commit and recovery identity after cleanup");
        }
        await using (var source = Source(DatabaseProvider.PostgreSql, connection))
        {
            await using var session = await source.OpenSessionAsync(); await using var killer = await source.OpenSessionAsync();
            var pid = await session.ScalarAsync(SqlCommand.Create("SELECT CAST(pg_backend_pid() AS BIGINT)"), Codecs.Int64); var marker = new InputFailure();
            var failure = await Throws<InputFailure>(() => session.InTransactionAsync<int>(async (current, token) =>
            {
                await current.InsertAsync(Items.Table, new(85, 1, "unconfirmed rollback", 1), token);
                Check(await killer.ScalarAsync(SqlCommand.Create("SELECT pg_terminate_backend(" + pid + ")"), Codecs.Boolean), "terminate isolated callback cleanup backend");
                throw marker;
            }));
            Check(ReferenceEquals(failure, marker) && TransactionFailure.TryGet(failure, out var rollback)
                && rollback is { Outcome: TransactionOutcome.InProgress, CommitAttempted: false }
                && rollback.Cleanup.Status is CleanupStatus.RollbackUnconfirmed or CleanupStatus.Failed,
                "rollback confirmation loss preserves business error and does not invent an unknown commit");
        }
    }

    private static unsafe ulong StatementCount(DataSource source)
    {
        var engine = source.Provider == DatabaseProvider.Sqlite ? SqliteDataSource.Engine : PostgreSqlDataSource.Engine;
        var count = (delegate* unmanaged[Cdecl]<uint, ulong>)EngineModuleLoader.ResolveExport(engine, "nw_db_diagnostic_count_v1");
        return count(6);
    }

    private sealed class ReadOnlyDataFailure(bool throwsOnRead) : Exception
    {
        private readonly System.Collections.IDictionary _data =
            new System.Collections.ObjectModel.ReadOnlyDictionary<object, object?>(new Dictionary<object, object?>());
        public override System.Collections.IDictionary Data => throwsOnRead ? throw new InvalidOperationException("Custom Data getter") : _data;
    }
}
