using Korda.Data;
using Korda.Database;
using Korda.Query;

namespace Korda.Reference.TaskService;

/// <summary>Bounded lease claims followed by publication into a separate durable consumer transaction.</summary>
internal sealed class OutboxWorker(DataSource source, DataSource consumer)
{
    private const int BatchSize = 16;
    private const int MaximumAttempts = 16;
    private static readonly Projection<string> ReceiptId = Projection<string>.Create(
        [ResultField.Create("event_id", Codecs.String)], row => row.Read(0, Codecs.String));

    internal async Task<int> RunOnceAsync(CancellationToken token)
    {
        var claimed = await ClaimAsync(token).ConfigureAwait(false);
        int delivered = 0;
        foreach (var entry in claimed)
        {
            token.ThrowIfCancellationRequested();
            // The source claim session/transaction has already closed before external publication.
            try
            {
                using var publishDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await PublishAsync(entry, publishDeadline.Token).ConfigureAwait(false);
                await AcknowledgeAsync(entry, true, publishDeadline.Token).ConfigureAwait(false);
                delivered++;
            }
            catch (Exception error) when (error is DatabaseException or OperationCanceledException)
            {
                // The independent consumer may already have committed. Preserve retryability and event identity.
                Console.Error.WriteLine("outbox_publish_unconfirmed event=" + entry.EventId + " type=" + error.GetType().Name);
                try
                {
                    using var retryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await AcknowledgeAsync(entry, false, retryDeadline.Token).ConfigureAwait(false);
                }
                catch (Exception cleanup) when (cleanup is DatabaseException or OperationCanceledException)
                { Console.Error.WriteLine("outbox_ack_unconfirmed event=" + entry.EventId + " type=" + cleanup.GetType().Name); }
            }
        }
        return delivered;
    }

    private async Task<OutboxEvent[]> ClaimAsync(CancellationToken token)
    {
        var session = await source.OpenSessionAsync(token).ConfigureAwait(false);
        try
        {
            using var transaction = await session.BeginTransactionAsync(token).ConfigureAwait(false);
            long now = TaskService.Now();
            using var query = session.From(Outbox.Table);
            query.Where(query.Column(Outbox.DeliveryState).LessThan(2L)
                .And(query.Column(Outbox.AvailableAt).LessThanOrEqual(now))
                .And(query.Column(Outbox.LeaseUntil).LessThanOrEqual(now)))
                .OrderBy(Outbox.AvailableAt).ThenBy(Outbox.EventId).Take(BatchSize);
            if (source.Provider == DatabaseProvider.PostgreSql) query.ForUpdate(RowLockMode.Update, LockWait.SkipLocked);
            var candidates = await query.SelectAll().ToListAsync(token).ConfigureAwait(false);
            List<OutboxEvent> result = [];
            foreach (var entry in candidates)
            {
                var leased = entry with { AttemptCount = checked(entry.AttemptCount + 1), DeliveryState = 1, LeaseUntil = now + 30_000 };
                if (entry.AttemptCount >= MaximumAttempts)
                {
                    await session.UpdateCheckedAsync(Outbox.Table, Outbox.Table.Key(Outbox.EventId, entry.EventId),
                        new Changes<OutboxEvent>().Set(Outbox.DeliveryState, 3L).Set(Outbox.LeaseUntil, 0L),
                        new ConcurrencyCheck<OutboxEvent>().Expect(Outbox.AttemptCount, entry.AttemptCount), token).ConfigureAwait(false);
                    continue;
                }
                await session.UpdateCheckedAsync(Outbox.Table, Outbox.Table.Key(Outbox.EventId, entry.EventId),
                    new Changes<OutboxEvent>().Set(Outbox.AttemptCount, leased.AttemptCount).Set(Outbox.DeliveryState, 1L).Set(Outbox.LeaseUntil, leased.LeaseUntil),
                    new ConcurrencyCheck<OutboxEvent>().Expect(Outbox.AttemptCount, entry.AttemptCount), token).ConfigureAwait(false);
                result.Add(leased);
            }
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return [.. result];
        }
        finally { await TaskService.CloseSessionAsync(session).ConfigureAwait(false); }
    }

    private async Task PublishAsync(OutboxEvent entry, CancellationToken token)
    {
        var session = await consumer.OpenSessionAsync(token).ConfigureAwait(false);
        try
        {
            using var transaction = await session.BeginTransactionAsync(new() { RecoveryKey = entry.EventId }, token).ConfigureAwait(false);
            string payloadHash = Canonical.Hash(entry.Payload);
            var claim = SqlCommand.Create("INSERT INTO receipts(event_id,payload_hash,delivery_attempts,effects) VALUES(@id,@hash,1,1) ON CONFLICT(event_id) DO NOTHING RETURNING event_id",
                new SqlParameters().Add("id", entry.EventId, Codecs.String).Add("hash", payloadHash, Codecs.String));
            var inserted = await session.QueryAsync(claim, ReceiptId, token).ConfigureAwait(false);
            if (inserted.Count == 0)
            {
                var receipt = await session.FindAsync(ConsumerReceipts.Table, ConsumerReceipts.Table.Key(ConsumerReceipts.EventId, entry.EventId), token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("A durable deduplication winner was not visible.");
                if (receipt.PayloadHash != payloadHash || receipt.Effects != 1) throw new InvalidOperationException("An event identity was reused with a different effect.");
                await session.UpdateCheckedAsync(ConsumerReceipts.Table, ConsumerReceipts.Table.Key(ConsumerReceipts.EventId, entry.EventId),
                    new Changes<ConsumerReceipt>().Set(ConsumerReceipts.DeliveryAttempts, checked(receipt.DeliveryAttempts + 1)),
                    new ConcurrencyCheck<ConsumerReceipt>().Expect(ConsumerReceipts.DeliveryAttempts, receipt.DeliveryAttempts), token).ConfigureAwait(false);
            }
            else if (inserted.Count != 1 || inserted[0] != entry.EventId) throw new InvalidOperationException("Consumer claim cardinality failed.");
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }
        finally { await TaskService.CloseSessionAsync(session).ConfigureAwait(false); }
    }

    private async Task AcknowledgeAsync(OutboxEvent entry, bool delivered, CancellationToken token)
    {
        var session = await source.OpenSessionAsync(token).ConfigureAwait(false);
        try
        {
            long now = TaskService.Now();
            long state = delivered ? 2 : entry.AttemptCount >= MaximumAttempts ? 3 : 0;
            long backoff = Math.Min(300_000L, 1000L << checked((int)Math.Min(entry.AttemptCount, 18L)));
            await session.UpdateCheckedAsync(Outbox.Table, Outbox.Table.Key(Outbox.EventId, entry.EventId),
                new Changes<OutboxEvent>().Set(Outbox.DeliveryState, state).Set(Outbox.LeaseUntil, 0L)
                    .Set(Outbox.AvailableAt, now + backoff).Set(Outbox.CompletedAt, delivered ? now : 0L),
                new ConcurrencyCheck<OutboxEvent>().Expect(Outbox.AttemptCount, entry.AttemptCount), token).ConfigureAwait(false);
        }
        finally { await TaskService.CloseSessionAsync(session).ConfigureAwait(false); }
    }
}
