using DbPerf.Campaign;
using Korda.Data;
using Korda.Database;
using CampaignDigest = DbPerf.Campaign.Digest;

// Every logical worker owns one persistent database row. No request is replayed.
// The public API has no prepared mutation handle: both construction modes use
// fresh key/Changes/ConcurrencyCheck objects and ordinary public CRUD methods.
// Generated PerfState objects and their strings remain in end-to-end allocation.
internal static class TransactionWorkload
{
    private static readonly TransactionOptions Options = new()
    {
        CommitTimeout = TimeSpan.FromSeconds(5),
        CleanupTimeout = TimeSpan.FromSeconds(5),
    };

    internal static ValueTask<Result> ExecuteAsync(DataSession session, string name, int workerId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if ((uint)workerId >= 128) throw new ArgumentOutOfRangeException(nameof(workerId));
        long id = workerId + 1L;
        var key = PerfStates.Table.Key(PerfStates.Id, id);
        return name switch
        {
            "tx-commit" => TransactionAsync(session, key, id, commit: true),
            "tx-rollback" => TransactionAsync(session, key, id, commit: false),
            "partial-update" => PartialUpdateAsync(session, key, id),
            "stale-version" => StaleVersionAsync(session, key, id),
            _ => throw new ArgumentException("Unknown transaction workload: " + name, nameof(name)),
        };
    }

    private static async ValueTask<Result> TransactionAsync(DataSession session, EntityKey<PerfState> key,
        long id, bool commit)
    {
        long suspensions = 0;
        if (!commit)
        {
            long reset = await Observe(session.UpdateAsync(PerfStates.Table, key, All(0, 1, null)), ref suspensions).ConfigureAwait(false);
            AffectedOne(reset);
        }

        var transaction = await Observe(session.BeginTransactionAsync(Options), ref suspensions).ConfigureAwait(false);
        var digest = CampaignDigest.Create();
        long counter = commit ? 17 : 99;
        string note = commit ? "committed" : "transient";
        try
        {
            long changed = await Observe(session.UpdateAsync(PerfStates.Table, key, All(counter, 2, note)), ref suspensions).ConfigureAwait(false);
            AffectedOne(changed);
            var inside = await Observe(session.FindAsync(PerfStates.Table, key), ref suspensions).ConfigureAwait(false);
            ValidateAndHash(ref digest, inside, id, counter, 2, note);
            await Observe(commit ? transaction.CommitAsync() : transaction.RollbackAsync(), ref suspensions).ConfigureAwait(false);
        }
        catch (Exception primary)
        {
            try
            {
                // Dispose requests native cleanup/rollback where appropriate,
                // then joins retirement. An uncertain commit remains Unknown;
                // this code never sends a blind rollback or replays a write.
                await RetireAsync(transaction, expectedOutcome: null).ConfigureAwait(false);
            }
            catch (Exception cleanup)
            {
                throw new AggregateException("Transaction operation and retirement both failed.", primary, cleanup);
            }
            throw;
        }

        suspensions += await RetireAsync(transaction,
            commit ? TransactionOutcome.Committed : TransactionOutcome.RolledBack).ConfigureAwait(false);
        var persisted = await Observe(session.FindAsync(PerfStates.Table, key), ref suspensions).ConfigureAwait(false);
        ValidateAndHash(ref digest, persisted, id, commit ? 17 : 0, commit ? 2 : 1, commit ? "committed" : null);
        return new Result(2, digest.Value, suspensions);
    }

    private static async ValueTask<Result> PartialUpdateAsync(DataSession session, EntityKey<PerfState> key, long id)
    {
        long suspensions = 0;
        AffectedOne(await Observe(session.UpdateAsync(PerfStates.Table, key, All(23, 7, "base")), ref suspensions).ConfigureAwait(false));
        AffectedOne(await Observe(session.UpdateAsync(PerfStates.Table, key,
            new Changes<PerfState>().Set(PerfStates.Note, (string?)null)), ref suspensions).ConfigureAwait(false));
        var row = await Observe(session.FindAsync(PerfStates.Table, key), ref suspensions).ConfigureAwait(false);
        var digest = CampaignDigest.Create();
        ValidateAndHash(ref digest, row, id, 23, 7, null);
        return new Result(1, digest.Value, suspensions);
    }

    private static async ValueTask<Result> StaleVersionAsync(DataSession session, EntityKey<PerfState> key, long id)
    {
        long suspensions = 0;
        AffectedOne(await Observe(session.UpdateAsync(PerfStates.Table, key, All(23, 2, "base")), ref suspensions).ConfigureAwait(false));
        bool conflict = false;
        try
        {
            await Observe(session.UpdateCheckedAsync(PerfStates.Table, key,
                new Changes<PerfState>().Set(PerfStates.Counter, 99L),
                new ConcurrencyCheck<PerfState>().Expect(PerfStates.Version, 1L)), ref suspensions).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException expected)
        {
            if (expected.ExpectedRows != 1 || expected.ActualRows != 0)
                throw new InvalidDataException("Stale-version conflict returned unexpected affected-row evidence.", expected);
            conflict = true;
        }
        if (!conflict) throw new InvalidDataException("Stale-version update unexpectedly succeeded.");
        var row = await Observe(session.FindAsync(PerfStates.Table, key), ref suspensions).ConfigureAwait(false);
        var digest = CampaignDigest.Create();
        ValidateAndHash(ref digest, row, id, 23, 2, "base");
        return new Result(1, digest.Value, suspensions, "conflict");
    }

    private static Changes<PerfState> All(long counter, long version, string? note)
        => new Changes<PerfState>().Set(PerfStates.Counter, counter).Set(PerfStates.Version, version).Set(PerfStates.Note, note);

    private static void AffectedOne(long actual)
    {
        if (actual != 1) throw new InvalidDataException("Expected exactly one affected perf_state row, observed " + actual);
    }

    private static void ValidateAndHash(ref CampaignDigest digest, PerfState? row, long id,
        long counter, long version, string? note)
    {
        if (row is null || row.Id != id || row.Counter != counter || row.Version != version
            || !string.Equals(row.Note, note, StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected perf_state identity or persisted values for worker row " + id);
        // Id is validated separately; the independent contract hashes exactly
        // these three selected state fields, in this order, once per FindAsync.
        digest.Integer(row.Counter); digest.Integer(row.Version); digest.Text(row.Note);
    }

    private static Task<T> Observe<T>(Task<T> pending, ref long suspensions)
    { if (!pending.IsCompleted) suspensions++; return pending; }

    private static Task Observe(Task pending, ref long suspensions)
    { if (!pending.IsCompleted) suspensions++; return pending; }

    private static async ValueTask<long> RetireAsync(DataTransaction transaction, TransactionOutcome? expectedOutcome)
    {
        var pending = transaction.DisposeAsync();
        long suspensions = pending.IsCompleted ? 0 : 1;
        await pending.ConfigureAwait(false);
        var cleanup = transaction.Cleanup;
        if (!cleanup.LocalResourcesReleased || cleanup.Status != CleanupStatus.Succeeded || cleanup.DeadlineExceeded)
            throw new InvalidDataException("Transaction cleanup did not establish successful retirement: status="
                + cleanup.Status + ", local_resources_released=" + cleanup.LocalResourcesReleased
                + ", disposition=" + cleanup.ConnectionDisposition + ", deadline_exceeded=" + cleanup.DeadlineExceeded
                + ", outcome=" + transaction.Outcome,
                new DatabaseCleanupException("Transaction retirement failed.", cleanup));
        if (expectedOutcome is { } expected && (transaction.Outcome != expected
            || transaction.CommitAttempted != (expected == TransactionOutcome.Committed)))
            throw new InvalidDataException("Transaction outcome or commit-attempt evidence differs from the completed operation.");
        return suspensions;
    }
}
