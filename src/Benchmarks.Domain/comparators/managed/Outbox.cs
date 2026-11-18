namespace NativeWeave.Reference.TaskService;

/// <summary>Equivalent claim-then-publish flow with an independent persistent SQLite deduplicating consumer.</summary>
internal sealed class OutboxWorker(DatabaseStore source, DatabaseStore consumer)
{
    internal async Task<int> RunOnceAsync(CancellationToken token)
    {
        var entries = await ClaimAsync(token); int delivered = 0;
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await PublishAsync(entry, deadline.Token); await AcknowledgeAsync(entry, true, deadline.Token); delivered++;
            }
            catch (Exception error) when (error is System.Data.Common.DbException or OperationCanceledException)
            {
                Console.Error.WriteLine("B23_OUTBOX_UNCONFIRMED " + error.GetType().Name);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await AcknowledgeAsync(entry, false, deadline.Token);
            }
        }
        return delivered;
    }

    private async Task<OutboxEntry[]> ClaimAsync(CancellationToken token)
    {
        await using var lease = await source.OpenAsync(token); await lease.BeginAsync(token);
        long now = TaskService.Now();
        var rows = await lease.QueryAsync("SELECT event_id,operation_id,payload,attempt_count FROM outbox WHERE delivery_state<2 AND available_at<=@now AND lease_until<=@now ORDER BY available_at,event_id LIMIT 16" +
            (source.IsPostgreSql ? " FOR UPDATE SKIP LOCKED" : ""), static row => new OutboxEntry(row.GetString(0), row.GetString(1), row.GetFieldValue<byte[]>(2), row.GetInt64(3)), token, ("now", now));
        List<OutboxEntry> claimed = [];
        foreach (var row in rows)
        {
            if (row.Attempts >= 16)
            {
                await lease.ExecuteAsync("UPDATE outbox SET delivery_state=3,lease_until=0 WHERE event_id=@id AND attempt_count=@attempts", token, ("id", row.EventId), ("attempts", row.Attempts));
                continue;
            }
            if (await lease.ExecuteAsync("UPDATE outbox SET delivery_state=1,attempt_count=@next,lease_until=@until WHERE event_id=@id AND attempt_count=@attempts", token,
                ("next", row.Attempts + 1), ("until", now + 30_000), ("id", row.EventId), ("attempts", row.Attempts)) != 1) throw new InvalidOperationException("Outbox claim CAS failed.");
            claimed.Add(row with { Attempts = row.Attempts + 1 });
        }
        await lease.CommitAsync(token); return [.. claimed];
    }

    private async Task PublishAsync(OutboxEntry entry, CancellationToken token)
    {
        await using var lease = await consumer.OpenAsync(token); await lease.BeginAsync(token);
        string hash = Canonical.Hash(entry.Payload);
        if (await lease.ScalarAsync("INSERT INTO receipts(event_id,payload_hash,delivery_attempts,effects) VALUES(@id,@hash,1,1) ON CONFLICT(event_id) DO NOTHING RETURNING event_id", token,
            ("id", entry.EventId), ("hash", hash)) is null)
        {
            var rows = await lease.QueryAsync("SELECT payload_hash,delivery_attempts,effects FROM receipts WHERE event_id=@id", static row => (Hash: row.GetString(0), Attempts: row.GetInt64(1), Effects: row.GetInt64(2)), token, ("id", entry.EventId));
            if (rows.Count != 1 || rows[0].Hash != hash || rows[0].Effects != 1) throw new InvalidOperationException("Consumer deduplication identity failed.");
            await lease.ExecuteAsync("UPDATE receipts SET delivery_attempts=@next WHERE event_id=@id AND delivery_attempts=@previous", token,
                ("next", rows[0].Attempts + 1), ("id", entry.EventId), ("previous", rows[0].Attempts));
        }
        await lease.CommitAsync(token);
    }

    private async Task AcknowledgeAsync(OutboxEntry entry, bool delivered, CancellationToken token)
    {
        await using var lease = await source.OpenAsync(token);
        long now = TaskService.Now(), state = delivered ? 2 : entry.Attempts >= 16 ? 3 : 0;
        long backoff = Math.Min(300_000L, 1000L << checked((int)Math.Min(entry.Attempts, 18)));
        if (await lease.ExecuteAsync("UPDATE outbox SET delivery_state=@state,lease_until=0,available_at=@available,completed_at=@completed WHERE event_id=@id AND attempt_count=@attempts", token,
            ("state", state), ("available", now + backoff), ("completed", delivered ? now : 0L), ("id", entry.EventId), ("attempts", entry.Attempts)) != 1)
            throw new InvalidOperationException("Outbox acknowledgement CAS failed.");
    }
}
