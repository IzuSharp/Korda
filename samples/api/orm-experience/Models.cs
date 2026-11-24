using System.Text.Json.Serialization;
using Korda.Data;

[assembly: DbCodec(typeof(OrderId), typeof(OrderCodecs), nameof(OrderCodecs.Id))]
[assembly: DbCodec(typeof(decimal), typeof(OrderCodecs), nameof(OrderCodecs.Money))]
[assembly: DbCodec(typeof(OrderSettings), typeof(OrderCodecs), nameof(OrderCodecs.Settings))]

public readonly record struct OrderId(long Value);
public sealed record OrderSettings(string Channel, bool Gift);

internal static class OrderCodecs
{
    internal static ValueCodec<OrderId> Id { get; } = Codecs.Convert<OrderId, long>(Codecs.Int64, value => value.Value, value => new(value));
#if POSTGRES
    internal static ValueCodec<decimal> Money { get; } = Codecs.Decimal(18, 2);
#else
    internal static ValueCodec<decimal> Money { get; } = Codecs.FixedScaleMoney(2);
#endif
    internal static ValueCodec<OrderSettings> Settings { get; } = Codecs.Json(OrderJson.Default.OrderSettings);
}

// The explicit table spelling keeps the existing store contract; only Id-key
// inference is opted in. Other models keep explicit composite-key declarations.
[DbTable("customers", DescriptorName = "Customers", InferPrimaryKey = true)]
internal sealed class Customer
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string? Email { get; init; }
}

[DbTable("orders", DescriptorName = "Orders")]
internal sealed record Order(
    [property: DbKey] long TenantId,
    [property: DbKey] OrderId Id,
    long CreatedAt,
    long? CustomerId,
    decimal Total,
    string? Note,
    [property: DbVersion] long Version,
    OrderSettings Settings,
    string InternalMemo);

[DbTable("order_lines", DescriptorName = "OrderLines")]
internal sealed record OrderLine(
    [property: DbKey] long Id,
    long TenantId,
    OrderId OrderId,
    string Product,
    long Quantity);

[DbProjection(DescriptorName = "OrderCards")]
internal sealed record OrderCard(
    long TenantId,
    [property: DbColumn("order_id")] OrderId Id,
    long CreatedAt,
    [property: DbColumn("customer_name")] string? Customer,
    decimal Total,
    [property: DbColumn("score")] long Score
#if DTO_EDIT
    , long Version
#endif
);

[DbProjection(DescriptorName = "CustomerSummaries")]
internal sealed record CustomerSummary(long? CustomerId, long Count, decimal? TotalSpent);

[JsonSerializable(typeof(OrderSettings))]
internal partial class OrderJson : JsonSerializerContext { }
