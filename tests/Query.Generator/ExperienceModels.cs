using System.Text.Json.Serialization;
using Korda.Data;

[assembly: WeaveCodec(typeof(ExperienceOrderId), typeof(ExperiencePolicies), nameof(ExperiencePolicies.OrderId))]
[assembly: WeaveCodec(typeof(decimal), typeof(ExperiencePolicies), nameof(ExperiencePolicies.Money))]
[assembly: WeaveCodec(typeof(ExperienceDetails), typeof(ExperiencePolicies), nameof(ExperiencePolicies.Json))]

public readonly record struct ExperienceOrderId(long Value);
public sealed record ExperienceDetails(string Label);

public static class ExperiencePolicies
{
    public static ValueCodec<ExperienceOrderId> OrderId { get; } = Codecs.Convert<ExperienceOrderId, long>(Codecs.Int64, static value => value.Value, static value => new(value));
    public static ValueCodec<decimal> Money { get; } = Codecs.FixedScaleMoney(2);
    public static ValueCodec<decimal> Numeric { get; } = Codecs.Decimal(18, 2);
    public static ValueCodec<ExperienceDetails> Json { get; } = Codecs.Json(ExperienceJsonContext.Default.ExperienceDetails);
}

[JsonSerializable(typeof(ExperienceDetails))]
internal partial class ExperienceJsonContext : JsonSerializerContext { }

[WeaveTable]
public sealed record ConventionOrder(long Id, string? Note);

[WeaveTable("legacy_keyless")]
public sealed record LegacyKeyless(long Id);

[WeaveTable("experience_orders", InferPrimaryKey = true)]
public sealed record ExperienceOrder(ExperienceOrderId Id, decimal Total, decimal? Discount, ExperienceDetails Details,
    [property: WeaveUseCodec(typeof(ExperiencePolicies), nameof(ExperiencePolicies.Numeric))] decimal Tax);

[WeaveProjection(DescriptorName = "ExperienceRows")]
public sealed class ExperienceRow
{
    [WeaveColumn("order_id")]
    public ExperienceOrderId Id { get; init; }
    [WeaveColumn("customer_name")]
    public string? Customer { get; init; }
    public decimal Total { get; init; }

    public ExperienceRow() { }

    [WeaveConstructor]
    public ExperienceRow([WeaveParameter(nameof(Id))] ExperienceOrderId key, string? customer, decimal total)
    { Id = key; Customer = customer; Total = total; }
}
