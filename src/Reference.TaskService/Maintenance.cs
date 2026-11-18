using System.Text.Json;
using NativeWeave.Data;
using NativeWeave.Database;
using NativeWeave.Query;

namespace NativeWeave.Reference.TaskService;

internal sealed partial class TaskService
{
    /// <summary>Explicit bounded maintenance; incomplete recovery records remain protected independently of age.</summary>
    internal async Task<MaintenanceReport> MaintainAsync(CancellationToken token)
    {
        await ReconcileConfirmedAsync(token).ConfigureAwait(false);
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
                if (source.Provider == DatabaseProvider.PostgreSql) query.ForUpdate(RowLockMode.Update, LockWait.SkipLocked);
                foreach (var entry in await query.SelectAll().ToListAsync(token).ConfigureAwait(false))
                {
                    if (protectedOperations.Contains(entry.OperationId)) continue;
                    deletedOutbox += checked((int)await session.DeleteCheckedAsync(Outbox.Table, Outbox.Table.Key(Outbox.EventId, entry.EventId),
                        new ConcurrencyCheck<OutboxEvent>().Expect(Outbox.AttemptCount, entry.AttemptCount), token).ConfigureAwait(false));
                }
            }
            using (var query = session.From(Operations.Table))
            {
                query.Where(query.Column(Operations.CompletedAt).LessThan(cutoff)).OrderBy(Operations.CompletedAt).ThenBy(Operations.OperationId).Take(100);
                if (source.Provider == DatabaseProvider.PostgreSql) query.ForUpdate(RowLockMode.Update, LockWait.SkipLocked);
                foreach (var operation in await query.SelectAll().ToListAsync(token).ConfigureAwait(false))
                {
                    if (protectedOperations.Contains(operation.OperationId)) continue;
                    using var references = session.From(Outbox.Table);
                    if ((await references.WhereEqual(Outbox.OperationId, operation.OperationId).Take(1).SelectAll().ToListAsync(token).ConfigureAwait(false)).Count != 0) continue;
                    deletedOperations += checked((int)await session.DeleteAsync(Operations.Table,
                        Operations.Table.Key(Operations.Scope, operation.Scope).And(Operations.RequestKey, operation.RequestKey), token).ConfigureAwait(false));
                }
            }
            await transaction.CommitAsync(token).ConfigureAwait(false);
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

    private async Task ReconcileConfirmedAsync(CancellationToken token)
    {
        foreach (string path in Directory.EnumerateFiles(blobs.Recovery, "*.json").Take(100))
        {
            if (new FileInfo(path).Length > 8192) throw new InvalidOperationException("Recovery record is too large.");
            var record = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false), AppJson.Default.RecoveryManifest)
                ?? throw new InvalidOperationException("Invalid recovery record.");
            if (record.Version != 1 || Path.GetFileName(path) != Canonical.Id(record.OperationId) + ".json") throw new InvalidOperationException("Recovery identity mismatch.");
            var session = await source.OpenSessionAsync(token).ConfigureAwait(false);
            try
            {
                var result = await session.FindAsync(Operations.Table, Operations.Table.Key(Operations.Scope, record.Scope).And(Operations.RequestKey, record.RequestKey), token).ConfigureAwait(false);
                if (result is not null && result.RequestHash == record.RequestHash) blobs.ConfirmCommit(record.OperationId);
                // No row is unconfirmed; it never authorizes unprotected replay or expiration of the protection.
            }
            finally { await CloseSessionAsync(session).ConfigureAwait(false); }
        }
        foreach (var upload in blobs.Manifests().Where(value => value.ContentHash is not null).Take(100))
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
    }
}
