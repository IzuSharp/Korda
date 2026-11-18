using NativeWeave.Data;
using NativeWeave.Database;
using NativeWeave.Query;

namespace NativeWeave.Reference.TaskService;

internal sealed partial class TaskService
{
    /// <summary>Explicit bounded maintenance; incomplete recovery records remain protected independently of age.</summary>
    internal async Task<MaintenanceReport> MaintainAsync(CancellationToken token)
    {
        var progress = await ReconcileConfirmedAsync(blobs.ReadMaintenanceProgress(), token).ConfigureAwait(false);
        blobs.WriteMaintenanceProgress(progress);
        var protectedOperations = blobs.ProtectedOperations();
        int deletedOutbox = 0; int deletedOperations = 0;
        long cutoff = Now() - 7L * 24 * 60 * 60 * 1000;
        var session = await source.OpenSessionAsync(token).ConfigureAwait(false);
        try
        {
            var transaction = await session.BeginTransactionAsync(token).ConfigureAwait(false);
            using (var query = session.From(Outbox.Table))
            {
                query.WhereEqual(Outbox.DeliveryState, 2L).Where(query.Column(Outbox.CompletedAt).LessThan(cutoff)).OrderBy(Outbox.CompletedAt).ThenBy(Outbox.EventId).Take(100);
                if (progress.Outbox is { } position)
                    query.Where(query.Column(Outbox.CompletedAt).GreaterThan(position.CompletedAt)
                        .Or(query.Column(Outbox.CompletedAt).Equal(position.CompletedAt).And(query.Column(Outbox.EventId).GreaterThan(position.Id))));
                if (source.Provider == DatabaseProvider.PostgreSql) query.ForUpdate(RowLockMode.Update, LockWait.SkipLocked);
                var entries = await query.SelectAll().ToListAsync(token).ConfigureAwait(false);
                foreach (var entry in entries)
                {
                    if (protectedOperations.Contains(entry.OperationId)) continue;
                    deletedOutbox += checked((int)await session.DeleteCheckedAsync(Outbox.Table, Outbox.Table.Key(Outbox.EventId, entry.EventId),
                        new ConcurrencyCheck<OutboxEvent>().Expect(Outbox.AttemptCount, entry.AttemptCount), token).ConfigureAwait(false));
                }
                progress = progress with { Outbox = entries.Count == 100 ? new(entries[^1].CompletedAt, entries[^1].EventId) : null };
            }
            using (var query = session.From(Operations.Table))
            {
                query.Where(query.Column(Operations.CompletedAt).LessThan(cutoff)).OrderBy(Operations.CompletedAt).ThenBy(Operations.OperationId).Take(100);
                if (progress.Operations is { } position)
                    query.Where(query.Column(Operations.CompletedAt).GreaterThan(position.CompletedAt)
                        .Or(query.Column(Operations.CompletedAt).Equal(position.CompletedAt).And(query.Column(Operations.OperationId).GreaterThan(position.Id))));
                if (source.Provider == DatabaseProvider.PostgreSql) query.ForUpdate(RowLockMode.Update, LockWait.SkipLocked);
                var operations = await query.SelectAll().ToListAsync(token).ConfigureAwait(false);
                foreach (var operation in operations)
                {
                    if (protectedOperations.Contains(operation.OperationId)) continue;
                    using var references = session.From(Outbox.Table);
                    if ((await references.WhereEqual(Outbox.OperationId, operation.OperationId).Take(1).SelectAll().ToListAsync(token).ConfigureAwait(false)).Count != 0) continue;
                    deletedOperations += checked((int)await session.DeleteAsync(Operations.Table,
                        Operations.Table.Key(Operations.Scope, operation.Scope).And(Operations.RequestKey, operation.RequestKey), token).ConfigureAwait(false));
                }
                progress = progress with { Operations = operations.Count == 100 ? new(operations[^1].CompletedAt, operations[^1].OperationId) : null };
            }
            await transaction.CommitAsync(token).ConfigureAwait(false);
            // Advance only after a confirmed commit. Skipped protected rows are revisited after each finite sweep.
            blobs.WriteMaintenanceProgress(progress);
        }
        finally { await CloseSessionAsync(session).ConfigureAwait(false); }
        var collected = await blobs.CollectAsync(BlobReferencedAsync, token).ConfigureAwait(false);
        return new(deletedOutbox, deletedOperations, collected.Blobs, collected.Staging);
    }

    private async Task<bool> BlobReferencedAsync(string hash, CancellationToken token)
    {
        var session = await source.OpenSessionAsync(token).ConfigureAwait(false);
        try
        {
            using var query = session.From(Attachments.Table);
            return (await query.WhereEqual(Attachments.ContentHash, hash).Take(1).SelectAll().ToListAsync(token).ConfigureAwait(false)).Count != 0;
        }
        finally { await CloseSessionAsync(session).ConfigureAwait(false); }
    }

    private async Task<MaintenanceProgress> ReconcileConfirmedAsync(MaintenanceProgress progress, CancellationToken token)
    {
        var records = blobs.RecoveryManifests().Where(record => progress.Recovery is null || string.CompareOrdinal(record.OperationId, progress.Recovery) > 0)
            .OrderBy(record => record.OperationId, StringComparer.Ordinal).Take(100).ToArray();
        foreach (var record in records)
        {
            var session = await source.OpenSessionAsync(token).ConfigureAwait(false);
            try
            {
                var result = await session.FindAsync(Operations.Table, Operations.Table.Key(Operations.Scope, record.Scope).And(Operations.RequestKey, record.RequestKey), token).ConfigureAwait(false);
                if (result is not null && result.RequestHash == record.RequestHash) blobs.ConfirmCommit(record.OperationId);
                // No row is unconfirmed; it never authorizes unprotected replay or expiration of the protection.
            }
            finally { await CloseSessionAsync(session).ConfigureAwait(false); }
        }
        progress = progress with { Recovery = records.Length == 100 ? records[^1].OperationId : null };
        var uploads = blobs.Manifests().Where(value => value.ContentHash is not null &&
            (progress.Uploads is null || string.CompareOrdinal(value.OperationId, progress.Uploads) > 0))
            .OrderBy(value => value.OperationId, StringComparer.Ordinal).Take(100).ToArray();
        foreach (var upload in uploads)
        {
            var session = await source.OpenSessionAsync(token).ConfigureAwait(false);
            try
            {
                var identity = new AuthIdentity(upload.Tenant, upload.Principal, true);
                string scope = Canonical.Scope(identity, Canonical.AttachSemantic);
                string hash = Canonical.AttachmentHash(identity, upload.ItemId, upload.ContentHash!, upload.ContentLength, upload.ExpectedVersion);
                var result = await session.FindAsync(Operations.Table, Operations.Table.Key(Operations.Scope, scope).And(Operations.RequestKey, upload.RequestKey), token).ConfigureAwait(false);
                if (result is not null && result.RequestHash == hash) blobs.ConfirmRecoveredUpload(upload.OperationId);
            }
            finally { await CloseSessionAsync(session).ConfigureAwait(false); }
        }
        return progress with { Uploads = uploads.Length == 100 ? uploads[^1].OperationId : null };
    }
}
