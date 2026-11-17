using NativeWeave.Data;
using NativeWeave.Database;

internal static partial class Suite
{
    private static async Task Busy(Func<Task> action, string why)
    {
        var failure = await Throws<DatabaseException>(action);
        Check(failure.Kind == DatabaseErrorKind.ConcurrentUse, why);
    }

    private static async Task CompoundAdmission(DataSession session)
    {
        // Input is paused only after the preceding native batch has retired, so
        // the provider's ordinary command gate cannot accidentally mask a gap.
        for (var mode = 0; mode < 3; mode++)
        {
            var tenant = 70 + mode;
            DataTransaction? caller = null; Savepoint? prior = null;
            if (mode == 2) { caller = await session.BeginTransactionAsync(); prior = await caller.CreateSavepointAsync(); }
            var rows = new PausedInput(new(tenant, 1, "paused", 1));
            var deferred = session.StreamAsync(SqlCommand.Create("SELECT \"Tenant\",\"Id\",\"Name\",\"Version\" FROM b14_items"), Items.Row).GetAsyncEnumerator();
            var bulk = session.InsertManyAsync(Items.Table, rows, new() { BatchSize = 1, Atomicity = mode == 1 ? BulkAtomicity.PerBatch : BulkAtomicity.All });
            try
            {
                await rows.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Busy(() => Execute(session, "UPDATE b14_items SET \"Version\"=2 WHERE \"Tenant\"=" + tenant), "outside SQL rejected between bulk batches");
                await Busy(() => session.InsertAsync(Items.Table, new(tenant, 2, "outside", 1)), "outside CRUD rejected between bulk batches");
                await Busy(() => session.FindAsync(Items.Table, Key(tenant, 1)), "outside Fluent execution rejected during paused input");
                await Busy(() => session.BeginTransactionAsync(), "outside BEGIN rejected during paused input");
                await Busy(() => session.InsertManyAsync(Items.Table, Rows(new Item(tenant, 3, "nested", 1))), "second bulk rejected during paused input");
                await Busy(async () => { await deferred.MoveNextAsync(); }, "previously created stream still checks admission at execution");
                if (session.Transaction is { } active)
                {
                    await Busy(() => active.CommitAsync(), "outside commit rejected while bulk owns transaction");
                    await Busy(() => active.RollbackAsync(), "outside rollback rejected while bulk owns transaction");
                    await Busy(() => active.CreateSavepointAsync(), "outside savepoint creation rejected while bulk owns transaction");
                }
                else Check(mode == 1, "only PerBatch releases its closed transaction between inputs");
                if (prior is not null)
                {
                    await Busy(() => prior.RollbackAsync(), "outside prior-savepoint rollback cannot enter internal bulk scope");
                    await Busy(() => prior.ReleaseAsync(), "outside prior-savepoint release cannot enter internal bulk scope");
                    await Busy(() => prior.DisposeAsync().AsTask(), "outside savepoint disposal cannot abort internal bulk scope");
                }
            }
            finally { rows.Resume.TrySetResult(); }
            var result = await bulk;
            await deferred.DisposeAsync();
            Check(result == new BulkResult(1, 1, caller is null), "exclusive bulk completes with exact ownership result");
            Check(rows.Disposals == 1 && await TenantCount(session, tenant) == 1, "input and session admission released after success");
            if (prior is not null)
            {
                await prior.RollbackAsync(); await prior.ReleaseAsync(); await prior.DisposeAsync();
                Check(caller!.State == TransactionState.Active && await TenantCount(session, tenant) == 0, "prior savepoint remains usable after bulk lease release");
                await caller.RollbackAsync(); await caller.DisposeAsync();
            }
        }
        using (var cancellation = new CancellationTokenSource())
        {
            var rows = new PausedInput(new(73, 1, "cancel", 1));
            var bulk = session.InsertManyAsync(Items.Table, rows, new() { BatchSize = 1 }, cancellation.Token);
            await rows.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel();
            var failure = await Throws<OperationCanceledException>(() => bulk);
            Check(failure.CancellationToken == cancellation.Token && rows.Disposals == 1, "paused cancellation retains token and disposes input once");
            Check(BulkFailure.TryGetProgress(failure, out var p) && p == new BulkProgress(1, 1, false, TransactionOutcome.RolledBack), "paused cancellation rolls back on the actual engine lane");
            Check(await TenantCount(session, 73) == 0, "cancellation releases compound admission after cleanup");
        }
        var marker = new InputFailure(); var throwing = new PausedInput(new(74, 1, "throw", 1), marker);
        var faulted = session.InsertManyAsync(Items.Table, throwing, new() { BatchSize = 1 });
        await throwing.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10)); throwing.Resume.TrySetResult();
        var actual = await Throws<InputFailure>(() => faulted);
        Check(ReferenceEquals(actual, marker) && throwing.Disposals == 1 && await TenantCount(session, 74) == 0, "throwing input preserves original failure and releases session lease");
        using var early = new CancellationTokenSource(); early.Cancel();
        await Throws<OperationCanceledException>(() => session.InsertManyAsync(Items.Table, Rows(new Item(75, 1, "unadmitted", 1)), cancellationToken: early.Token));
        Check(await session.InsertAsync(Items.Table, new(75, 2, "after cancel", 1)) == 1, "early cancellation leaves no compound reservation");
    }

    private sealed class PausedInput(Item row, Exception? failure = null) : IAsyncEnumerable<Item>, IAsyncEnumerator<Item>
    {
        internal TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Disposals { get; private set; }
        private bool _yielded; private CancellationToken _token;
        public Item Current => row;
        public IAsyncEnumerator<Item> GetAsyncEnumerator(CancellationToken cancellationToken = default) { _token = cancellationToken; return this; }
        public async ValueTask<bool> MoveNextAsync()
        {
            if (!_yielded) { _yielded = true; return true; }
            Paused.TrySetResult(); await Resume.Task.WaitAsync(_token);
            if (failure is not null) throw failure;
            return false;
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
