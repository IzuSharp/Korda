using System.Data.Common;
using System.Text.Json;

namespace NativeWeave.Reference.TaskService;

/// <summary>Fixed parameterized comparator business statements; no NativeWeave facade or managed ORM substitute enters product code.</summary>
internal sealed partial class TaskService(DatabaseStore source, CursorCodec cursors, BlobStore blobs)
{
    internal static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private const string OperationColumns = "scope,request_key,request_hash,operation_id,tenant_id,principal_id,semantic,response_version,response_status,response_bytes,completed_at";

    private static OperationResult ReadOperation(DbDataReader row) => new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetString(4),
        row.GetString(5), row.GetString(6), row.GetInt64(7), row.GetInt64(8), row.GetFieldValue<byte[]>(9), row.GetInt64(10));

    private static async Task<Replay?> ClaimAsync(DatabaseLease lease, OperationResult result, CancellationToken token)
    {
        object? inserted = await lease.ScalarAsync("INSERT INTO operations(" + OperationColumns + ") VALUES(@scope,@key,@hash,@operation,@tenant,@principal,@semantic,1,@status,@response,@completed) ON CONFLICT(scope,request_key) DO NOTHING RETURNING operation_id", token,
            ("scope", result.Scope), ("key", result.RequestKey), ("hash", result.RequestHash), ("operation", result.OperationId), ("tenant", result.Tenant),
            ("principal", result.Principal), ("semantic", result.Semantic), ("status", result.ResponseStatus), ("response", result.ResponseBytes), ("completed", result.CompletedAt));
        if (inserted is not null) return null;
        var rows = await lease.QueryAsync("SELECT " + OperationColumns + " FROM operations WHERE scope=@scope AND request_key=@key", ReadOperation, token,
            ("scope", result.Scope), ("key", result.RequestKey));
        if (rows.Count != 1) throw new ApplicationProblem(503, "outcome_unconfirmed");
        var winner = rows[0];
        if (winner.Tenant != result.Tenant || winner.Principal != result.Principal || winner.Semantic != result.Semantic) throw new InvalidOperationException("Scoped winner identity mismatch.");
        if (winner.RequestHash != result.RequestHash) throw new ApplicationProblem(409, "idempotency_conflict");
        if (winner.ResponseVersion != 1) throw new InvalidOperationException("Unsupported response version.");
        return new(checked((int)winner.ResponseStatus), winner.ResponseBytes, winner.OperationId);
    }

    private static Task<int> InsertOutboxAsync(DatabaseLease lease, OperationResult result, CancellationToken token) => lease.ExecuteAsync(
        "INSERT INTO outbox(event_id,tenant_id,operation_id,payload_version,payload,delivery_state,attempt_count,available_at,lease_until,completed_at) VALUES(@event,@tenant,@operation,1,@payload,0,0,@now,0,0)", token,
        ("event", Guid.NewGuid().ToString("D")), ("tenant", result.Tenant), ("operation", result.OperationId), ("payload", result.ResponseBytes), ("now", result.CompletedAt));

    private static async Task<Replay> CommitAsync(DatabaseLease lease, OperationResult result, CancellationToken token)
    {
        try { await lease.CommitAsync(token); return new(checked((int)result.ResponseStatus), result.ResponseBytes, result.OperationId); }
        catch (Exception error) when (lease.Unknown)
        {
            Console.Error.WriteLine("B23_COMMIT_UNKNOWN " + error.GetType().Name);
            return new(503, JsonSerializer.SerializeToUtf8Bytes(new Problem("outcome_unknown", result.OperationId, "/v1/operations/" + result.OperationId), AppJson.Default.Problem), result.OperationId);
        }
    }

    internal async Task<Replay> CreateAsync(AuthIdentity identity, string key, CreateItem command, CancellationToken token)
    {
        Canonical.ValidateTitle(command.Title); key = Canonical.Key(key);
        string operation = Guid.NewGuid().ToString("D"), item = Guid.NewGuid().ToString("D"); long now = Now();
        byte[] response = JsonSerializer.SerializeToUtf8Bytes(new CreatedItem(1, operation, item, command.Title, now, 1), AppJson.Default.CreatedItem);
        var result = new OperationResult(Canonical.Scope(identity, Canonical.CreateSemantic), key, Canonical.CreateHash(identity, command.Title), operation,
            identity.Tenant, identity.Principal, Canonical.CreateSemantic, 1, 201, response, now);
        await using var lease = await source.OpenAsync(token); await lease.BeginAsync(token);
        var existing = await ClaimAsync(lease, result, token); if (existing is not null) return existing;
        await lease.ExecuteAsync("INSERT INTO items(tenant_id,item_id,operation_id,title,created_at,version) VALUES(@tenant,@item,@operation,@title,@now,1)", token,
            ("tenant", identity.Tenant), ("item", item), ("operation", operation), ("title", command.Title), ("now", now));
        await InsertOutboxAsync(lease, result, token);
        lease.ConfirmedRollback = () => blobs.ConfirmCommit(operation); blobs.ProtectCommit(result);
        var committed = await CommitAsync(lease, result, token);
        if (committed.Status != 503) blobs.ConfirmCommit(operation);
        return committed;
    }

    internal async Task<ItemPage> ListAsync(AuthIdentity identity, int limit, string? after, CancellationToken token)
    {
        if (limit is < 1 or > 100) throw new ApplicationProblem(400, "invalid_limit");
        var cursor = after is null ? ((long CreatedAt, string ItemId)?)null : cursors.Decode(identity, after);
        await using var lease = await source.OpenAsync(token);
        List<(string Name, object Value)> parameters = [("tenant", identity.Tenant), ("limit", limit)];
        string keyset = "";
        if (cursor is { } value) { keyset = " AND (created_at>@created OR (created_at=@created AND item_id>@item))"; parameters.Add(("created", value.CreatedAt)); parameters.Add(("item", value.ItemId)); }
        var rows = await lease.QueryAsync("SELECT item_id,title,created_at,version FROM items WHERE tenant_id=@tenant" + keyset + " ORDER BY created_at,item_id LIMIT @limit",
            static row => new ItemView(row.GetString(0), row.GetString(1), row.GetInt64(2), row.GetInt64(3)), token, [.. parameters]);
        return new([.. rows], rows.Count == limit ? cursors.Encode(identity, rows[^1].CreatedAt, rows[^1].ItemId) : null);
    }

    internal async Task<Replay> RecoverAsync(AuthIdentity identity, string operation, CancellationToken token)
    {
        operation = Canonical.Id(operation);
        await using var lease = await source.OpenAsync(token);
        var rows = await lease.QueryAsync("SELECT " + OperationColumns + " FROM operations WHERE operation_id=@operation AND tenant_id=@tenant AND principal_id=@principal", ReadOperation, token,
            ("operation", operation), ("tenant", identity.Tenant), ("principal", identity.Principal));
        if (rows.Count != 1) throw new ApplicationProblem(404, "outcome_unconfirmed");
        return new(200, rows[0].ResponseBytes, rows[0].OperationId);
    }

    internal async Task AuthorizeItemAsync(AuthIdentity identity, string item, CancellationToken token)
    {
        await using var lease = await source.OpenAsync(token);
        if (await lease.ScalarAsync("SELECT item_id FROM items WHERE tenant_id=@tenant AND item_id=@item", token, ("tenant", identity.Tenant), ("item", item)) is null)
            throw new ApplicationProblem(404, "not_found");
    }

    internal async Task<Attachment> AttachmentAsync(AuthIdentity identity, string item, CancellationToken token)
    {
        await using var lease = await source.OpenAsync(token);
        var rows = await lease.QueryAsync("SELECT tenant_id,item_id,content_hash,content_length,operation_id,version FROM attachments WHERE tenant_id=@tenant AND item_id=@item",
            static row => new Attachment(row.GetString(0), row.GetString(1), row.GetString(2), row.GetInt64(3), row.GetString(4), row.GetInt64(5)), token, ("tenant", identity.Tenant), ("item", item));
        return rows.Count == 1 ? rows[0] : throw new ApplicationProblem(404, "not_found");
    }

    internal async Task<Replay> BindAttachmentAsync(AuthIdentity identity, string key, string item, long expected, BlobStore.UploadLease upload, CancellationToken token)
    {
        var manifest = upload.Manifest; long now = Now(), version = checked(expected + 1);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new AttachedItem(1, manifest.OperationId, item, manifest.ContentHash!, manifest.ContentLength, version), AppJson.Default.AttachedItem);
        var result = new OperationResult(Canonical.Scope(identity, Canonical.AttachSemantic), key,
            Canonical.AttachmentHash(identity, item, manifest.ContentHash!, manifest.ContentLength, expected), manifest.OperationId,
            identity.Tenant, identity.Principal, Canonical.AttachSemantic, 1, 200, bytes, now);
        await using var lease = await source.OpenAsync(token); await lease.BeginAsync(token);
        upload.BeginDatabaseBinding(); lease.ConfirmedRollback = () => blobs.ConfirmRecoveredUpload(manifest.OperationId);
        var existing = await ClaimAsync(lease, result, token); if (existing is not null) return existing;
        int updated = await lease.ExecuteAsync("UPDATE items SET version=@next WHERE tenant_id=@tenant AND item_id=@item AND version=@expected", token,
            ("next", version), ("tenant", identity.Tenant), ("item", item), ("expected", expected));
        if (updated != 1) throw new ApplicationProblem(412, "version_conflict");
        await lease.ExecuteAsync("INSERT INTO attachments(tenant_id,item_id,content_hash,content_length,operation_id,version) VALUES(@tenant,@item,@hash,@length,@operation,@version) ON CONFLICT(tenant_id,item_id) DO UPDATE SET content_hash=excluded.content_hash,content_length=excluded.content_length,operation_id=excluded.operation_id,version=excluded.version", token,
            ("tenant", identity.Tenant), ("item", item), ("hash", manifest.ContentHash!), ("length", manifest.ContentLength), ("operation", manifest.OperationId), ("version", version));
        await InsertOutboxAsync(lease, result, token);
        return await CommitAsync(lease, result, token);
    }
}
