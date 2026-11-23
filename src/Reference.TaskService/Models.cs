using Korda.Data;

namespace Korda.Reference.TaskService;

internal sealed record OperationResult(string Scope, string RequestKey, string RequestHash, string OperationId, string Tenant, string Principal, string Semantic, long ResponseVersion, long ResponseStatus, byte[] ResponseBytes, long CompletedAt);

/// <summary>Static application metadata, consumed by the precompiled C++ query and relation engine.</summary>
internal static class Operations
{
    private static readonly TableBuilder<OperationResult> Builder = new("operations");
    internal static readonly Column<OperationResult, string> Scope = Builder.Column("scope", Codecs.String, new() { IsPrimaryKey = true });
    internal static readonly Column<OperationResult, string> RequestKey = Builder.Column("request_key", Codecs.String, new() { IsPrimaryKey = true });
    internal static readonly Column<OperationResult, string> RequestHash = Builder.Column("request_hash", Codecs.String);
    internal static readonly Column<OperationResult, string> OperationId = Builder.Column("operation_id", Codecs.String);
    internal static readonly Column<OperationResult, string> Tenant = Builder.Column("tenant_id", Codecs.String);
    internal static readonly Column<OperationResult, string> Principal = Builder.Column("principal_id", Codecs.String);
    internal static readonly Column<OperationResult, string> Semantic = Builder.Column("semantic", Codecs.String);
    internal static readonly Column<OperationResult, long> ResponseVersion = Builder.Column("response_version", Codecs.Int64);
    internal static readonly Column<OperationResult, long> ResponseStatus = Builder.Column("response_status", Codecs.Int64);
    internal static readonly Column<OperationResult, byte[]> ResponseBytes = Builder.Column("response_bytes", Codecs.Bytes);
    internal static readonly Column<OperationResult, long> CompletedAt = Builder.Column("completed_at", Codecs.Int64);
    internal static readonly Table<OperationResult> Table = Builder.Build(
        row => new(row.Get(Scope), row.Get(RequestKey), row.Get(RequestHash), row.Get(OperationId), row.Get(Tenant), row.Get(Principal), row.Get(Semantic), row.Get(ResponseVersion), row.Get(ResponseStatus), row.Get(ResponseBytes), row.Get(CompletedAt)),
        (row, value) => { row.Set(Scope, value.Scope); row.Set(RequestKey, value.RequestKey); row.Set(RequestHash, value.RequestHash); row.Set(OperationId, value.OperationId); row.Set(Tenant, value.Tenant); row.Set(Principal, value.Principal); row.Set(Semantic, value.Semantic); row.Set(ResponseVersion, value.ResponseVersion); row.Set(ResponseStatus, value.ResponseStatus); row.Set(ResponseBytes, value.ResponseBytes); row.Set(CompletedAt, value.CompletedAt); });
}

internal sealed record Item(string Tenant, string ItemId, string OperationId, string Title, long CreatedAt, long Version);

/// <summary>Static application metadata, consumed by the precompiled C++ query and relation engine.</summary>
internal static class Items
{
    private static readonly TableBuilder<Item> Builder = new("items");
    internal static readonly Column<Item, string> Tenant = Builder.Column("tenant_id", Codecs.String, new() { IsPrimaryKey = true });
    internal static readonly Column<Item, string> ItemId = Builder.Column("item_id", Codecs.String, new() { IsPrimaryKey = true });
    internal static readonly Column<Item, string> OperationId = Builder.Column("operation_id", Codecs.String);
    internal static readonly Column<Item, string> Title = Builder.Column("title", Codecs.String);
    internal static readonly Column<Item, long> CreatedAt = Builder.Column("created_at", Codecs.Int64);
    internal static readonly Column<Item, long> Version = Builder.Column("version", Codecs.Int64, new() { IsConcurrencyToken = true });
    internal static readonly Table<Item> Table = Builder.Build(
        row => new(row.Get(Tenant), row.Get(ItemId), row.Get(OperationId), row.Get(Title), row.Get(CreatedAt), row.Get(Version)),
        (row, value) => { row.Set(Tenant, value.Tenant); row.Set(ItemId, value.ItemId); row.Set(OperationId, value.OperationId); row.Set(Title, value.Title); row.Set(CreatedAt, value.CreatedAt); row.Set(Version, value.Version); });
}

internal sealed record OutboxEvent(string EventId, string Tenant, string OperationId, long PayloadVersion, byte[] Payload, long DeliveryState, long AttemptCount, long AvailableAt, long LeaseUntil, long CompletedAt);

/// <summary>Static application metadata, consumed by the precompiled C++ query and relation engine.</summary>
internal static class Outbox
{
    private static readonly TableBuilder<OutboxEvent> Builder = new("outbox");
    internal static readonly Column<OutboxEvent, string> EventId = Builder.Column("event_id", Codecs.String, new() { IsPrimaryKey = true });
    internal static readonly Column<OutboxEvent, string> Tenant = Builder.Column("tenant_id", Codecs.String);
    internal static readonly Column<OutboxEvent, string> OperationId = Builder.Column("operation_id", Codecs.String);
    internal static readonly Column<OutboxEvent, long> PayloadVersion = Builder.Column("payload_version", Codecs.Int64);
    internal static readonly Column<OutboxEvent, byte[]> Payload = Builder.Column("payload", Codecs.Bytes);
    internal static readonly Column<OutboxEvent, long> DeliveryState = Builder.Column("delivery_state", Codecs.Int64);
    internal static readonly Column<OutboxEvent, long> AttemptCount = Builder.Column("attempt_count", Codecs.Int64, new() { IsConcurrencyToken = true });
    internal static readonly Column<OutboxEvent, long> AvailableAt = Builder.Column("available_at", Codecs.Int64);
    internal static readonly Column<OutboxEvent, long> LeaseUntil = Builder.Column("lease_until", Codecs.Int64);
    internal static readonly Column<OutboxEvent, long> CompletedAt = Builder.Column("completed_at", Codecs.Int64);
    internal static readonly Table<OutboxEvent> Table = Builder.Build(
        row => new(row.Get(EventId), row.Get(Tenant), row.Get(OperationId), row.Get(PayloadVersion), row.Get(Payload), row.Get(DeliveryState), row.Get(AttemptCount), row.Get(AvailableAt), row.Get(LeaseUntil), row.Get(CompletedAt)),
        (row, value) => { row.Set(EventId, value.EventId); row.Set(Tenant, value.Tenant); row.Set(OperationId, value.OperationId); row.Set(PayloadVersion, value.PayloadVersion); row.Set(Payload, value.Payload); row.Set(DeliveryState, value.DeliveryState); row.Set(AttemptCount, value.AttemptCount); row.Set(AvailableAt, value.AvailableAt); row.Set(LeaseUntil, value.LeaseUntil); row.Set(CompletedAt, value.CompletedAt); });
}

internal sealed record Attachment(string Tenant, string ItemId, string ContentHash, long ContentLength, string OperationId, long Version);

/// <summary>Static application metadata, consumed by the precompiled C++ query and relation engine.</summary>
internal static class Attachments
{
    private static readonly TableBuilder<Attachment> Builder = new("attachments");
    internal static readonly Column<Attachment, string> Tenant = Builder.Column("tenant_id", Codecs.String, new() { IsPrimaryKey = true });
    internal static readonly Column<Attachment, string> ItemId = Builder.Column("item_id", Codecs.String, new() { IsPrimaryKey = true });
    internal static readonly Column<Attachment, string> ContentHash = Builder.Column("content_hash", Codecs.String);
    internal static readonly Column<Attachment, long> ContentLength = Builder.Column("content_length", Codecs.Int64);
    internal static readonly Column<Attachment, string> OperationId = Builder.Column("operation_id", Codecs.String);
    internal static readonly Column<Attachment, long> Version = Builder.Column("version", Codecs.Int64, new() { IsConcurrencyToken = true });
    internal static readonly Table<Attachment> Table = Builder.Build(
        row => new(row.Get(Tenant), row.Get(ItemId), row.Get(ContentHash), row.Get(ContentLength), row.Get(OperationId), row.Get(Version)),
        (row, value) => { row.Set(Tenant, value.Tenant); row.Set(ItemId, value.ItemId); row.Set(ContentHash, value.ContentHash); row.Set(ContentLength, value.ContentLength); row.Set(OperationId, value.OperationId); row.Set(Version, value.Version); });
}

internal sealed record ConsumerReceipt(string EventId, string PayloadHash, long DeliveryAttempts, long Effects);

/// <summary>Static application metadata, consumed by the precompiled C++ query and relation engine.</summary>
internal static class ConsumerReceipts
{
    private static readonly TableBuilder<ConsumerReceipt> Builder = new("receipts");
    internal static readonly Column<ConsumerReceipt, string> EventId = Builder.Column("event_id", Codecs.String, new() { IsPrimaryKey = true });
    internal static readonly Column<ConsumerReceipt, string> PayloadHash = Builder.Column("payload_hash", Codecs.String);
    internal static readonly Column<ConsumerReceipt, long> DeliveryAttempts = Builder.Column("delivery_attempts", Codecs.Int64, new() { IsConcurrencyToken = true });
    internal static readonly Column<ConsumerReceipt, long> Effects = Builder.Column("effects", Codecs.Int64);
    internal static readonly Table<ConsumerReceipt> Table = Builder.Build(
        row => new(row.Get(EventId), row.Get(PayloadHash), row.Get(DeliveryAttempts), row.Get(Effects)),
        (row, value) => { row.Set(EventId, value.EventId); row.Set(PayloadHash, value.PayloadHash); row.Set(DeliveryAttempts, value.DeliveryAttempts); row.Set(Effects, value.Effects); });
}
