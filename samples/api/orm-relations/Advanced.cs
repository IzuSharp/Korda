using NativeWeave.Data;
using NativeWeave.Database;
using System.Runtime.CompilerServices;

internal static class Advanced
{
    internal static async Task Run(DataSession session, long tenant, CancellationToken token)
    {
        var parentKey = Catalog.Parents.Key(Catalog.ParentTenant, tenant).And(Catalog.ParentId, 1L);
        var childKey = Catalog.Children.Key(Catalog.ChildId, tenant);
        // Foreign-key changes remain explicit ordinary writes, with both tenant/key components visible.
        await session.UpdateAsync(Catalog.Children, childKey, new Changes<Child>().Set(Catalog.ChildParentId, (long?)null), token);
        if ((await session.LoadRelatedAsync(parentKey, Catalog.Relation, token)).Count != 0) throw new InvalidOperationException("Unlink failed.");
        await session.UpdateAsync(Catalog.Children, childKey,
            new Changes<Child>().Set(Catalog.ChildTenant, (long?)tenant).Set(Catalog.ChildParentId, (long?)1), token);
        await session.UpsertAsync(Catalog.Parents, new Parent(tenant, 1, "insert-only value", 90),
            ConflictTarget<Parent>.Columns(Catalog.ParentTenant).And(Catalog.ParentId),
            new Changes<Parent>().Set(Catalog.ParentName, "independent update"), token);
        if ((await session.FindAsync(Catalog.Parents, parentKey, token))!.Version != 2) throw new InvalidOperationException("An omitted upsert assignment changed version.");
        var returned = await session.InsertReturningAsync(Catalog.Parents, new Parent(tenant, 3, "returning", 1), Catalog.Parents.Row, token);
        await session.DeleteCheckedAsync(Catalog.Parents, Catalog.Parents.Key(Catalog.ParentTenant, tenant).And(Catalog.ParentId, returned.Id),
            new ConcurrencyCheck<Parent>().Expect(Catalog.ParentVersion, returned.Version), token);
        await using (var transaction = await session.BeginTransactionAsync(token))
        {
            // The bulk owns one tracked savepoint. The outer caller owns commit/rollback.
            var result = await session.InsertManyAsync(Catalog.Parents, Input(tenant, token), new() { BatchSize = 1 }, token);
            if (result != new BulkResult(2, 2, false)) throw new InvalidOperationException("Caller transaction ownership changed.");
            await transaction.RollbackAsync(token);
            Console.WriteLine($"Caller bulk acknowledged={result.RowsAffected}; operation committed={result.CommittedByOperation}; outer outcome={transaction.Outcome}");
        }
        try
        {
            await session.InsertManyAsync(Catalog.Parents, DuplicateInput(tenant + 1, token),
                new() { BatchSize = 1, Atomicity = BulkAtomicity.PerBatch }, token);
            throw new InvalidOperationException("Expected a duplicate-key failure.");
        }
        catch (DatabaseException error) when (BulkFailure.TryGetProgress(error, out var progress))
        {
            Console.WriteLine($"Partial bulk completed={progress.CompletedBatches}; prior commits={progress.CompletedBatchesCommitted}; current={progress.CurrentTransactionOutcome}");
            if (progress != new BulkProgress(1, 1, true, TransactionOutcome.RolledBack)) throw;
        }
    }
    private static async IAsyncEnumerable<Parent> Input(long tenant, [EnumeratorCancellation] CancellationToken token)
    {
        for (var i = 10; i < 12; i++) { await Task.Yield(); token.ThrowIfCancellationRequested(); yield return new(tenant, i, "bounded input", 1); }
    }
    private static async IAsyncEnumerable<Parent> DuplicateInput(long tenant, [EnumeratorCancellation] CancellationToken token)
    {
        yield return new(tenant, 1, "committed", 1);
        await Task.Yield(); token.ThrowIfCancellationRequested();
        yield return new(tenant, 1, "duplicate", 1);
    }
}
