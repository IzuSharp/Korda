using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;

[module: DapperAot]
[module: TypeHandler(typeof(decimal), typeof(MoneyHandler))]
[module: TypeHandler(typeof(decimal?), typeof(NullableMoneyHandler))]
[module: TypeHandler(typeof(OrderSettings), typeof(SettingsHandler))]

public sealed record OrderSettings(string Channel, bool Gift);
public sealed record TextRow(long Id, string? Note);
public sealed record MoneyRow(long Id, decimal Total);
public sealed record JsonRow(long Id, OrderSettings Settings);
public sealed record Order(long TenantId, long Id, long CreatedAt, long? CustomerId, decimal Total,
    string? Note, long Version, OrderSettings Settings, string InternalMemo);

public sealed class MoneyHandler : DbValueHandler<decimal>
{
    protected override void SetValueCore(DbParameter parameter, decimal value)
    {
#if POSTGRES
        parameter.Value = value;
#else
        parameter.Value = checked((long)(value * 100));
#endif
    }
    protected override decimal Parse(object? value)
    {
        var result = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
#if POSTGRES
        return result;
#else
        return result / 100m;
#endif
    }
    public override decimal Parse(DbDataReader reader, int ordinal, int token)
    {
#if POSTGRES
        return reader.GetDecimal(ordinal);
#else
        return reader.GetInt64(ordinal) / 100m;
#endif
    }
}
public sealed class SettingsHandler : DbValueHandler<OrderSettings>
{
    protected override void SetValueCore(DbParameter parameter, OrderSettings value)
        => parameter.Value = JsonSerializer.Serialize(value, PerfJson.Default.OrderSettings);
    protected override OrderSettings Parse(object? value)
        => JsonSerializer.Deserialize((string)value!, PerfJson.Default.OrderSettings)!;
    public override OrderSettings Parse(DbDataReader reader, int ordinal, int token)
        => JsonSerializer.Deserialize(reader.GetString(ordinal), PerfJson.Default.OrderSettings)!;
}
public sealed class NullableMoneyHandler : DbValueHandler<decimal?>
{
    private static readonly MoneyHandler Money = new();
    protected override void SetValueCore(DbParameter parameter, decimal? value)
        => parameter.Value = value is null ? DBNull.Value :
#if POSTGRES
            value.Value;
#else
            checked((long)(value.Value * 100));
#endif
    protected override decimal? Parse(object? value) => value is null or DBNull ? null :
#if POSTGRES
        Convert.ToDecimal(value, CultureInfo.InvariantCulture);
#else
        Convert.ToDecimal(value, CultureInfo.InvariantCulture) / 100m;
#endif
    public override decimal? Parse(DbDataReader reader, int ordinal, int token)
        => reader.IsDBNull(ordinal) ? null : Money.Parse(reader, ordinal, token);
}
[JsonSerializable(typeof(OrderSettings))]
[JsonSerializable(typeof(RunReceipt))]
[JsonSerializable(typeof(LifecycleReceipt))]
[JsonSerializable(typeof(RecoveryReceipt))]
internal partial class PerfJson : JsonSerializerContext { }

public sealed record JoinRow(long Id, string? Name);
public sealed record CustomerTotal(long? CustomerId, long Count, decimal? Total);
public sealed record ProductTotal(string Product, long? Quantity);
public sealed record RankRow(long Id, long Rank);
public sealed record RunningRow(long Id, long? Quantity);
public sealed record DetailRow(long OrderId, string Name, string Product, long Quantity);
public sealed record OrderLine(long Id, long TenantId, long OrderId,
    string Product, long Quantity);
public readonly record struct IndexedLine(long Id,long TenantId,long OrderId,string Product,long Quantity,int __korda_parent_index);
