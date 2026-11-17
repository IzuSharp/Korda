using NativeWeave.Data;
using NativeWeave.Database;
using NativeWeave.Query;
using System.Runtime.CompilerServices;

internal static partial class Suite
{
    private static async Task BulkCases(DataSource source, DataSession session)
    {
        var success = await session.InsertManyAsync(Items.Table, Rows(new(10, 1, "a", 1), new(10, 2, "b", 1), new(10, 3, null, 1)), new() { BatchSize = 2 }); Check(success == new BulkResult(3, 2, true), "All owns one confirmed commit");
        var all = await Throws<DatabaseException>(() => session.InsertManyAsync(Items.Table, Rows(new(11, 1, "a", 1), new(11, 2, "b", 1), new(11, 1, "duplicate", 1)), new() { BatchSize = 2 }));
        Check(BulkFailure.TryGetProgress(all, out var allProgress) && allProgress == new BulkProgress(2, 1, false, TransactionOutcome.RolledBack), "All failure historical completed batch progress survives rollback");
        Check(await TenantCount(session, 11) == 0, "All failed later batch rolled back preceding batch");
        var partial = await Throws<DatabaseException>(() => session.InsertManyAsync(Items.Table, Rows(new(12, 1, "a", 1), new(12, 2, "b", 1), new(12, 1, "duplicate", 1)), new() { BatchSize = 2, Atomicity = BulkAtomicity.PerBatch }));
        Check(BulkFailure.TryGetProgress(partial, out var progress) && progress == new BulkProgress(2, 1, true, TransactionOutcome.RolledBack), "PerBatch confirmed preceding commits remain distinct"); Check(await TenantCount(session, 12) == 2, "PerBatch partial commits remain");
        Check(partial.Data[BulkErrorPayload.NativeKey] is BulkProgress { RowsAffectedInCompletedBatches: 0, CompletedBatches: 0, CompletedBatchesCommitted: false }, "actual kind15 accompanies original native failure");
        await using (var tx = await session.BeginTransactionAsync())
        {
            await session.InsertAsync(Items.Table, new(20, 99, "caller", 1));
            var failed = await Throws<DatabaseException>(() => session.InsertManyAsync(Items.Table, Rows(new(20, 1, "a", 1), new(20, 2, "b", 1), new(20, 1, "dup", 1)), new() { BatchSize = 2 }));
            Check(tx.State == TransactionState.Active && tx.Outcome == TransactionOutcome.InProgress, "savepoint rollback recovers caller transaction");
            Check(BulkFailure.TryGetProgress(failed, out var p) && p == new BulkProgress(2, 1, false, TransactionOutcome.InProgress), "caller scope progress never invents commit/outer rollback");
            Check(await TenantCount(session, 20) == 1, "internal savepoint rolled back only operation rows");
            await Throws<InvalidOperationException>(() => session.InsertManyAsync(Items.Table, Rows(new Item(20, 4, "x", 1)), new() { Atomicity = BulkAtomicity.PerBatch }));
            var ok = await session.InsertManyAsync(Items.Table, Rows(new Item(20, 5, "x", 1)), new() { BatchSize = 1 }); Check(ok == new BulkResult(1, 1, false), "caller owns final commit");
            await tx.RollbackAsync();
        }
        Check(await TenantCount(session, 20) == 0, "caller can still roll back whole outer transaction");
        var marker = new InputFailure();
        var enumerator = await Throws<InputFailure>(() => session.InsertManyAsync(Items.Table, ThrowAfter(new(30, 1, "one", 1), marker), new() { BatchSize = 1 }));
        Check(ReferenceEquals(marker, enumerator) && BulkFailure.TryGetProgress(enumerator, out var ep) && ep == new BulkProgress(1, 1, false, TransactionOutcome.RolledBack), "enumerator exception identity and progress"); Check(await TenantCount(session, 30) == 0, "enumerator failure rolls back All");
        var disposal = new InputFailure(); var disposed = await Throws<InputFailure>(() => session.InsertManyAsync(Items.Table, new OneThenDispose(new(31, 1, "one", 1), () => Task.FromException(disposal)), new() { BatchSize = 1 }));
        Check(ReferenceEquals(disposal, disposed) && await TenantCount(session, 31) == 0, "input disposal failure precedes commit and preserves identity");
        using (var canceled = new CancellationTokenSource())
        {
            var failure = await Throws<OperationCanceledException>(() => session.InsertManyAsync(Items.Table, CancelAfter(new(32, 1, "one", 1), canceled), new() { BatchSize = 1 }, canceled.Token));
            Check(failure.CancellationToken == canceled.Token && BulkFailure.TryGetProgress(failure, out var cp) && cp == new BulkProgress(1, 1, false, TransactionOutcome.RolledBack), "cancellation token and completed history preserved"); Check(await TenantCount(session, 32) == 0, "cancel cleanup used independent token");
        }
        var codecFailure = await Throws<ArgumentException>(() => session.InsertManyAsync(Items.Table, Rows(new(33, 1, "ok", 1), new(33, 2, "\ud800", 1)), new() { BatchSize = 1 }));
        Check(BulkFailure.TryGetProgress(codecFailure, out var codecProgress) && codecProgress!.CompletedBatches == 1 && await TenantCount(session, 33) == 0, "codec failure rolls back and retains typed original");
        var changing = new Changes<Item>().Set(Items.Name, "copied-before-await"); var update = session.UpdateAsync(Items.Table, Key(10, 1), changing); changing.Set(Items.Name, "later"); await update; Check((await session.FindAsync(Items.Table, Key(10, 1)))!.Name == "copied-before-await", "changes copied before dispatch and await");
        var empty = await session.InsertManyAsync(Items.Table, Rows()); Check(empty == new BulkResult(0, 0, true), "empty All transaction has exact result");
        var batchFailure = await Throws<DatabaseException>(() => session.InsertManyAsync(Items.Table, Rows(new(34, 1, "a", 1), new(34, 1, "duplicate same batch", 1)), new() { BatchSize = 2 })); Check(BulkFailure.TryGetProgress(batchFailure, out var bp) && bp!.CompletedBatches == 0 && bp.RowsAffectedInCompletedBatches == 0 && await TenantCount(session, 34) == 0, "failed current batch is excluded from completed progress");
    }
    private static async Task<long> TenantCount(DataSession session, long tenant) { using var q = session.From(Items.Table); return await q.WhereEqual(Items.Tenant, tenant).CountAsync(); }
    private static async IAsyncEnumerable<Item> ThrowAfter(Item item, Exception failure) { yield return item; await Task.Yield(); throw failure; }
    private static async IAsyncEnumerable<Item> CancelAfter(Item item, CancellationTokenSource source, [EnumeratorCancellation] CancellationToken token = default) { yield return item; await Task.Yield(); source.Cancel(); token.ThrowIfCancellationRequested(); }
    private sealed class InputFailure : Exception;
    private sealed class MappingFailure : Exception;
    private sealed class OneThenDispose(Item row, Func<Task> dispose) : IAsyncEnumerable<Item>, IAsyncEnumerator<Item>
    {
        private bool _yielded; public Item Current => row; public IAsyncEnumerator<Item> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() { var result = !_yielded; _yielded = true; return ValueTask.FromResult(result); }
        public ValueTask DisposeAsync() => new(dispose());
    }
    private static async Task SchemaMismatch(DatabaseProvider provider, string connection)
    {
        await using var source = Source(provider, connection); await using var session = await source.OpenSessionAsync();
        await Execute(session, "CREATE TABLE b14_bad(\"Tenant\" BIGINT NOT NULL,\"Id\" BIGINT NOT NULL,\"Name\" TEXT,\"Version\" BIGINT NOT NULL)");
        var b = new TableBuilder<Item>("b14_bad"); var tenant = b.Column("Tenant", Codecs.Int64, new() { IsPrimaryKey = true }); var id = b.Column("Id", Codecs.Int64, new() { IsPrimaryKey = true }); var name = b.Column("Name", Codecs.NullableReference(Codecs.String)); var version = b.Column("Version", Codecs.Int64, new() { IsConcurrencyToken = true });
        var table = b.Build(row => new(row.Get(tenant), row.Get(id), row.Get(name), row.Get(version)), (row, v) => { row.Set(tenant, v.Tenant); row.Set(id, v.Id); row.Set(name, v.Name); row.Set(version, v.Version); });
        await session.InsertAsync(table, new(1, 1, "one", 1)); await session.InsertAsync(table, new(1, 1, "two", 1)); var key = table.Key(tenant, 1L).And(id, 1L);
        var multiple = await Throws<DatabaseException>(() => session.UpdateAsync(table, key, new Changes<Item>().Set(name, "both"))); Check(multiple.Kind == DatabaseErrorKind.Constraint && multiple is not ConcurrencyConflictException, "multirow declared key is schema constraint, distinct from zero CAS");
        var checkedMultiple = await Throws<DatabaseException>(() => session.UpdateCheckedAsync(table, key, new Changes<Item>().Set(name, "both2"), new ConcurrencyCheck<Item>().Expect(version, 1L))); Check(checkedMultiple.Kind == DatabaseErrorKind.Constraint && checkedMultiple is not ConcurrencyConflictException, "checked multirow remains schema failure");
        await Throws<InvalidOperationException>(() => session.FindAsync(table, key));
    }
    private static async Task MappingAndClose(DatabaseProvider provider, string connection)
    {
        var source = Source(provider, connection); var session = await source.OpenSessionAsync();
        var relation = Manual.RelationWith(_ => { source.Dispose(); throw new MappingFailure(); });
        await Throws<MappingFailure>(() => session.LoadRelatedAsync(Key(1, 1), relation));
        Check(source.RelationCacheCount == 0, "source close drops relation roots before awaiting accepted cursor cleanup");
        await session.DisposeAsync(); await source.DisposeAsync(); Check(source.Cleanup.LocalResourcesReleased, "mapping failure and source close retire all dependencies");
        await using (var decoded = Source(provider, connection))
        {
            await using var current = await decoded.OpenSessionAsync();
            var invalid = Manual.RelationWith(row => new(row.Read(0, Codecs.Int64), row.Read(1, Codecs.Nullable(Codecs.Int64)), row.Read(2, Codecs.Nullable(Codecs.Int64)), row.Read(3, Codecs.String), null));
            var failure = await Throws<DatabaseException>(() => current.LoadRelatedAsync(Key(1, 1), invalid)); Check(failure.Kind == DatabaseErrorKind.ShapeMismatch, "actual row decode failure preserves declared nullability");
            Check(await current.FindAsync(Items.Table, Key(1, 1)) is not null, "decode failure released session/cursor gate");
        }
        using var canceled = new CancellationTokenSource();
        await using (var stopping = Source(provider, connection, new() { MaxBatchRows = 1 }))
        {
            await using var current = await stopping.OpenSessionAsync();
            var cancelRelation = Manual.RelationWith(row => { canceled.Cancel(); return new(row.Read(0, Codecs.Int64), row.Read(1, Codecs.Nullable(Codecs.Int64)), row.Read(2, Codecs.Nullable(Codecs.Int64)), row.Read(3, Codecs.NullableReference(Codecs.String)), row.Read(4, Codecs.Nullable(Codecs.Int64))); });
            var failure = await Throws<OperationCanceledException>(() => current.LoadRelatedAsync(Key(1, 1), cancelRelation, canceled.Token)); Check(failure.CancellationToken == canceled.Token, "relation cancellation token and cursor retirement");
        }
        await using (var bounded = Source(provider, connection, new() { MaxQueryNodes = 3 }))
        {
            await using var current = await bounded.OpenSessionAsync();
            var exact = Relation<Item, Child>.CreateOptional(Items.Table, Items.Tenant, Children.Table, Children.Tenant).AndOptional(Items.Id, Children.ParentId);
            bounded.RegisterModel(Items.Table); bounded.RegisterModel(Children.Table); bounded.RegisterProjection(Children.Row);
            var failure = await Throws<DatabaseException>(() => Task.FromResult(bounded.RegisterRelation(exact))); Check(failure.Kind == DatabaseErrorKind.ResourceLimit, "relation shares existing combined registry budget");
            Check(bounded.RelationCacheCount == 0, "failed registration publishes no managed cache entry");
        }
        await using (var dedup = Source(provider, connection, new() { MaxQueryNodes = 3 }))
        {
            var exact = Relation<Item, Child>.CreateOptional(Items.Table, Items.Tenant, Children.Table, Children.Tenant).AndOptional(Items.Id, Children.ParentId);
            var parent = dedup.RegisterModel(Items.Table); var child = dedup.RegisterModel(Children.Table);
            var wrappers = new NativeWeave.Query.Internal.DatabaseOwner?[32];
            try
            {
                Parallel.For(0, wrappers.Length, i => wrappers[i] = NativeWeave.Query.Internal.MetadataBindings.RegisterRelation(dedup.Owner, parent, child, exact));
                Check(wrappers.All(x => x is not null) && wrappers.Distinct().Count() == 32, "actual ABI concurrently deduplicates one relation entry within exact combined three-entry budget while returning independent owners");
            }
            finally { foreach (var owner in wrappers) owner?.Dispose(); }
        }
    }
    private static async Task ActualUnknownAndRollbackFailure(string connection)
    {
        await using (var source = Source(DatabaseProvider.PostgreSql, connection))
        {
            await using var session = await source.OpenSessionAsync(); await using var killer = await source.OpenSessionAsync();
            var pid = await session.ScalarAsync(SqlCommand.Create("SELECT CAST(pg_backend_pid() AS BIGINT)"), Codecs.Int64);
            async Task Kill() { Check(await killer.ScalarAsync(SqlCommand.Create("SELECT pg_terminate_backend(" + pid + ")"), Codecs.Boolean), "actual backend terminated"); }
            var failure = await Throws<CommitOutcomeUnknownException>(() => session.InsertManyAsync(Items.Table, new OneThenDispose(new(50, 1, "unconfirmed", 1), Kill), new() { BatchSize = 1 }));
            Check(BulkFailure.TryGetProgress(failure, out var progress) && progress == new BulkProgress(1, 1, false, TransactionOutcome.Unknown), "actual post-dispatch unknown commit keeps exact original and progress");
        }
        await using (var source = Source(DatabaseProvider.PostgreSql, connection))
        {
            await using var session = await source.OpenSessionAsync(); await using var killer = await source.OpenSessionAsync(); var pid = await session.ScalarAsync(SqlCommand.Create("SELECT CAST(pg_backend_pid() AS BIGINT)"), Codecs.Int64); var original = new InputFailure();
            async Task KillThenFail() { Check(await killer.ScalarAsync(SqlCommand.Create("SELECT pg_terminate_backend(" + pid + ")"), Codecs.Boolean), "actual cleanup backend terminated"); throw original; }
            var failure = await Throws<InputFailure>(() => session.InsertManyAsync(Items.Table, new OneThenDispose(new(51, 1, "rollback fails", 1), KillThenFail), new() { BatchSize = 1 }));
            Check(ReferenceEquals(failure, original) && BulkFailure.TryGetProgress(failure, out var progress) && progress.CurrentTransactionOutcome == TransactionOutcome.InProgress, "rollback failure preserves primary and never invents unknown commit");
            Check(failure.Data.Contains("NativeWeave.Database.BulkCleanup.v1") || failure.Data.Contains("NativeWeave.Database.BulkCleanupSnapshot.v1"), "actual rollback failure retains cleanup detail");
        }
    }
}
