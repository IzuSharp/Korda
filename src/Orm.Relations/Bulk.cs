using NativeWeave.Data;

namespace NativeWeave.Database;

public sealed partial class DataSession
{
    /// <summary>Incrementally snapshots and writes finite native batches under explicit transaction ownership.</summary>
    /// <typeparam name="TEntity">The statically written entity class.</typeparam>
    /// <param name="table">The exact immutable table.</param>
    /// <param name="rows">A one-pass input, snapshotted before its next MoveNext.</param>
    /// <param name="options">Finite batch size and All or PerBatch ownership; null selects defaults.</param>
    /// <param name="cancellationToken">Controls input and write admission; cleanup uses an independent token.</param>
    /// <returns>Acknowledged rows and batches; caller-owned transactions are never reported as committed.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The option is invalid.</exception>
    /// <exception cref="InvalidOperationException">PerBatch is requested inside a caller transaction.</exception>
    /// <exception cref="CommitOutcomeUnknownException">An owned commit crossed dispatch but its outcome is unknown.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is confirmed after bounded transaction cleanup.</exception>
    /// <remarks>All inside a caller transaction creates a tracked savepoint before input consumption. Every admitted
    /// failure preserves its original type and attaches BulkProgress. Prior PerBatch commits are not rolled back.</remarks>
    public Task<BulkResult> InsertManyAsync<TEntity>(Table<TEntity> table, IAsyncEnumerable<TEntity> rows, BulkOptions? options = null, CancellationToken cancellationToken = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(table); ArgumentNullException.ThrowIfNull(rows); options ??= new();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.BatchSize);
        if (!Enum.IsDefined(options.Atomicity)) throw new ArgumentOutOfRangeException(nameof(options));
        CheckOpen(); Source.Capabilities.Require("db.bulk-batches.v1"); Source.Capabilities.Require("db.transactions.v1");
        var caller = Transaction;
        if (options.Atomicity == BulkAtomicity.PerBatch && caller is not null) throw new InvalidOperationException("PerBatch cannot own commits inside a caller transaction.");
        if (caller is not null) Source.Capabilities.Require("db.savepoints.v1");
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<BulkResult>(cancellationToken);
        // Input rows remain bounded even when a caller asks for a larger batch.
        var columns = table.Columns.Count(c => !c.Definition.Options.IsGenerated);
        var capacity = Math.Min(options.BatchSize, Source.Limits.MaxBatchRows);
        if (columns == 0) capacity = 1;
        else capacity = Math.Min(capacity, Math.Min(Source.Limits.MaxParameters / columns, (Source.Limits.MaxQueryNodes - 1) / (2 * columns)));
        if (capacity <= 0) throw new DatabaseException("No insert row fits the source parameter/node budget.", DatabaseErrorKind.ResourceLimit);
        Source.RegisterModel(table);
        var lease = ReserveBulk();
        return BulkCore(table, rows, options.Atomicity, capacity, caller, lease, cancellationToken);
    }
    private async Task<BulkResult> BulkCore<TEntity>(Table<TEntity> table, IAsyncEnumerable<TEntity> input, BulkAtomicity atomicity, int capacity, DataTransaction? caller, BulkSessionLease bulk, CancellationToken token) where TEntity : class
    {
        DataTransaction? owned = null; DataTransaction.BulkLease? transactionLease = null; Savepoint? savepoint = null; IAsyncEnumerator<TEntity>? enumerator = null;
        long affected = 0; int batches = 0; var lastOutcome = TransactionOutcome.InProgress;
        var batch = new List<ColumnValue[]>(capacity); long bytes = 0;
        async Task Flush()
        {
            if (batch.Count == 0) return;
            token.ThrowIfCancellationRequested(); CheckBulkOpen();
            if (atomicity == BulkAtomicity.PerBatch)
            { owned = await BeginCore(HostEncoding.Transaction(new TransactionOptions()), null, token, bulk).ConfigureAwait(false); transactionLease = bulk.TakeTransaction(); }
            var payload = MutationEncoding.Record(Source, table, MutationEncoding.Bytes(2, MutationEncoding.Values(Source, table, batch)));
            var count = await Mutate(table, 0, payload, token, bulk).ConfigureAwait(false);
            if (atomicity == BulkAtomicity.PerBatch)
            {
                await owned!.BulkCommitAsync(transactionLease!, token).ConfigureAwait(false); lastOutcome = owned.Outcome;
                await owned.DisposeAsync().ConfigureAwait(false); transactionLease!.Dispose(); transactionLease = null; owned = null;
            }
            affected = checked(affected + count); batches = checked(batches + 1); batch.Clear(); bytes = 0;
        }
        try
        {
            if (atomicity == BulkAtomicity.All)
            {
                if (caller is not null) { transactionLease = caller.ReserveBulk(); savepoint = await caller.BulkCreateSavepointAsync(transactionLease, token).ConfigureAwait(false); }
                else { owned = await BeginCore(HostEncoding.Transaction(new TransactionOptions()), null, token, bulk).ConfigureAwait(false); transactionLease = bulk.TakeTransaction(); }
            }
            enumerator = input.GetAsyncEnumerator(token);
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested(); CheckBulkOpen();
                var snapshot = MutationEncoding.Snapshot(Source, table, enumerator.Current);
                var rowBytes = checked(snapshot.Length * 24L + snapshot.Sum(c => (long)(c.Bytes?.Length ?? 0)));
                var headerBytes = 96L + snapshot.Length * 16L + 52L; // exact kind4 field1/field2 wrapper
                if (headerBytes + rowBytes > Source.Limits.MaxParameterBytes)
                    throw new DatabaseException("One bulk row exceeds the finite byte budget.", DatabaseErrorKind.ResourceLimit);
                if (batch.Count != 0 && (batch.Count == capacity || headerBytes + bytes + rowBytes > Source.Limits.MaxParameterBytes)) await Flush().ConfigureAwait(false);
                batch.Add(snapshot); bytes = checked(bytes + rowBytes);
                if (batch.Count == capacity) await Flush().ConfigureAwait(false);
            }
            // Input disposal is part of the atomic operation, before commit/release.
            var completedInput = enumerator; enumerator = null; await completedInput.DisposeAsync().ConfigureAwait(false);
            await Flush().ConfigureAwait(false);
            if (savepoint is not null) { await savepoint.BulkReleaseAsync(transactionLease!, token).ConfigureAwait(false); await savepoint.BulkDisposeAsync(transactionLease!).ConfigureAwait(false); savepoint = null; }
            if (owned is not null)
            {
                await owned.BulkCommitAsync(transactionLease!, token).ConfigureAwait(false); lastOutcome = owned.Outcome;
                await owned.DisposeAsync().ConfigureAwait(false); transactionLease!.Dispose(); transactionLease = null; owned = null;
            }
            return new(affected, batches, caller is null);
        }
        catch (Exception failure)
        {
            if (enumerator is not null)
            {
                try { await enumerator.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { failure.Data["NativeWeave.Database.BulkInputCleanup.v1"] = cleanup; }
            }
            if (savepoint is not null)
            {
                try { await savepoint.BulkRollbackAsync(transactionLease!, CancellationToken.None).ConfigureAwait(false); await savepoint.BulkReleaseAsync(transactionLease!, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception cleanup) { failure.Data["NativeWeave.Database.BulkCleanup.v1"] = cleanup; }
                try { await savepoint.BulkDisposeAsync(transactionLease!).ConfigureAwait(false); }
                catch (Exception cleanup) { failure.Data["NativeWeave.Database.BulkCleanup.v1"] = cleanup; }
                lastOutcome = caller!.Outcome;
                if (caller.Cleanup.Status == CleanupStatus.Failed) failure.Data["NativeWeave.Database.BulkCleanupSnapshot.v1"] = caller.Cleanup;
            }
            if (owned is not null)
            {
                try { if (owned.Outcome == TransactionOutcome.InProgress) await owned.BulkRollbackAsync(transactionLease!, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception cleanup) { failure.Data["NativeWeave.Database.BulkCleanup.v1"] = cleanup; }
                try { await owned.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { failure.Data["NativeWeave.Database.BulkCleanup.v1"] = cleanup; }
                lastOutcome = owned.Outcome;
                if (owned.Cleanup.Status == CleanupStatus.Failed) failure.Data["NativeWeave.Database.BulkCleanupSnapshot.v1"] = owned.Cleanup;
            }
            failure.Data[BulkFailure.Key] = new BulkProgress(affected, batches, atomicity == BulkAtomicity.PerBatch && batches != 0, lastOutcome);
            throw;
        }
        finally { transactionLease?.Dispose(); bulk.Dispose(); }
    }
}
