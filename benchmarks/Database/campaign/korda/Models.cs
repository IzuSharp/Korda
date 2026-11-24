using System.Text.Json.Serialization;
using Korda.Data;

[assembly: DbCodec(typeof(decimal), typeof(PerfCodecs), nameof(PerfCodecs.Money))]
[assembly: DbCodec(typeof(OrderSettings), typeof(PerfCodecs), nameof(PerfCodecs.Settings))]

internal static class PerfCodecs
{
#if POSTGRES
    internal static ValueCodec<decimal> Money { get; } = Codecs.Decimal(18, 2);
#else
    internal static ValueCodec<decimal> Money { get; } = Codecs.FixedScaleMoney(2);
#endif
    internal static ValueCodec<OrderSettings> Settings { get; } = Codecs.Json(PerfJson.Default.OrderSettings);
}

internal sealed record OrderSettings(string Channel, bool Gift);

[DbTable("customers", DescriptorName = "Customers")]
internal sealed record Customer([property: DbKey] long Id, string Name, string? Email);

[DbTable("orders", DescriptorName = "Orders")]
internal sealed record Order([property: DbKey] long TenantId, [property: DbKey] long Id,
    long CreatedAt, long? CustomerId, decimal Total, string? Note,
    [property: DbVersion] long Version, OrderSettings Settings, string InternalMemo);

[DbTable("order_lines", DescriptorName = "OrderLines")]
internal sealed record OrderLine([property: DbKey] long Id, long TenantId, long OrderId,
    string Product, long Quantity);

[DbProjection(DescriptorName = "TextRows")]
internal sealed record TextRow(long Id, string? Note);

[DbProjection(DescriptorName = "MoneyRows")]
internal sealed record MoneyRow(long Id, decimal Total);

[DbProjection(DescriptorName = "JsonRows")]
internal sealed record JsonRow(long Id, OrderSettings Settings);

[DbProjection(DescriptorName = "JoinRows")]
internal sealed record JoinRow(long Id, string? Name);

[DbProjection(DescriptorName = "CustomerTotals")]
internal sealed record CustomerTotal(long? CustomerId, long Count, decimal? Total);

[DbProjection(DescriptorName = "ProductTotals")]
internal sealed record ProductTotal(string Product, long? Quantity);

[DbProjection(DescriptorName = "RankRows")]
internal sealed record RankRow(long Id, long Rank);

[DbProjection(DescriptorName = "RunningRows")]
internal sealed record RunningRow(long Id, long? Quantity);

[DbProjection(DescriptorName = "DetailRows")]
internal sealed record DetailRow(long OrderId, string Name, string Product, long Quantity);

[DbTable("selected_order_ids", DescriptorName = "SelectedOrderIds")]
internal sealed record SelectedOrderId([property: DbKey] long Id);

[DbTable("perf_state", DescriptorName = "PerfStates")]
internal sealed record PerfState([property: DbKey] long Id, long Counter,
    [property: DbVersion] long Version, string? Note);

[DbTable("perf_lifecycle", DescriptorName = "LifecycleStates")]
internal sealed record LifecycleState([property: DbKey] long Id, long Counter, [property: DbVersion] long Version);

[JsonSerializable(typeof(OrderSettings))]
[JsonSerializable(typeof(InitializationReceipt))]
[JsonSerializable(typeof(NativeCounts))]
internal partial class PerfJson : JsonSerializerContext { }
