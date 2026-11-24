using System.Text.Json.Serialization;
using Korda.Data;

[assembly: DbCodec(typeof(ExperienceOrderId), typeof(ExperiencePolicies), nameof(ExperiencePolicies.OrderId))]
[assembly: DbCodec(typeof(decimal), typeof(ExperiencePolicies), nameof(ExperiencePolicies.Money))]
[assembly: DbCodec(typeof(ExperienceDetails), typeof(ExperiencePolicies), nameof(ExperiencePolicies.Json))]

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

[DbTable]
public sealed record ConventionOrder(long Id, string? Note);

[DbTable("legacy_keyless")]
public sealed record LegacyKeyless(long Id);

[DbTable("experience_orders", InferPrimaryKey = true)]
public sealed record ExperienceOrder(ExperienceOrderId Id, decimal Total, decimal? Discount, ExperienceDetails Details,
    [property: DbUseCodec(typeof(ExperiencePolicies), nameof(ExperiencePolicies.Numeric))] decimal Tax);

[DbProjection(DescriptorName = "ExperienceRows")]
public sealed class ExperienceRow
{
    [DbColumn("order_id")]
    public ExperienceOrderId Id { get; init; }
    [DbColumn("customer_name")]
    public string? Customer { get; init; }
    public decimal Total { get; init; }

    public ExperienceRow() { }

    [DbConstructor]
    public ExperienceRow([DbParameter(nameof(Id))] ExperienceOrderId key, string? customer, decimal total)
    { Id = key; Customer = customer; Total = total; }
}
