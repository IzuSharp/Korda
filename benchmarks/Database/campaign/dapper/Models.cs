using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;

[module: DapperAot]
[module: TypeHandler(typeof(DbPerf.IndependentOrm.OrderSettings), typeof(DbPerf.IndependentOrm.SettingsHandler))]
#if SQLITE
[module: TypeHandler(typeof(decimal), typeof(DbPerf.IndependentOrm.CentsHandler))]
#endif

namespace DbPerf.IndependentOrm;

public sealed record OrderSettings(string Channel, bool Gift);
public sealed record TextRow(long Id, string? Note);
public sealed record MoneyRow(long Id, decimal Total);
public sealed record JsonRow(long Id, OrderSettings Settings);
public sealed record WideRow(long TenantId, long Id, long CreatedAt, long? CustomerId,
    decimal Total, string? Note, long Version, OrderSettings Settings, string InternalMemo);
public sealed record JoinRow(long Id, string? Name);
public sealed record CustomerTotalRow(long? CustomerId, long Count, decimal? Total);
public sealed record ProductTotalRow(string Product, long? Quantity);
public sealed record RankRow(long Id, long Rank);
public sealed record RunningRow(long Id, long? Quantity);
public sealed record DetailRow(long OrderId, string Name, string Product, long Quantity);

// The alias is part of the shared relation contract, not a Dapper global name-matching setting.
public sealed record RelatedRow(long Id, long TenantId, long OrderId, string Product,
    long Quantity, int __korda_parent_index);

[JsonSerializable(typeof(OrderSettings))]
internal partial class DapperJson : JsonSerializerContext { }

/// <summary>Declarative Dapper.AOT handler; the generated row factory calls this directly.</summary>
public sealed class SettingsHandler : DbValueHandler<OrderSettings>
{
    public override int Tokenize(DbDataReader reader, int column) => 0;

    public override OrderSettings Parse(DbDataReader reader, int column, int token) =>
        Decode(reader.GetString(column));

    protected override OrderSettings Parse(object? value) => value is string text
        ? Decode(text) : throw new InvalidOperationException("Settings must have JSON text storage.");

    private static OrderSettings Decode(string text)
    {
        var value = JsonSerializer.Deserialize(text, DapperJson.Default.OrderSettings);
        if (value is null || value.Channel is null)
            throw new InvalidOperationException("Settings JSON did not contain the declared typed value.");
        return value;
    }

    protected override void Configure(DbParameter parameter)
    {
#if POSTGRESQL
        if (parameter is not Npgsql.NpgsqlParameter postgres)
            throw new InvalidOperationException("PostgreSQL Settings requires an Npgsql parameter.");
        postgres.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb;
#else
        parameter.DbType = DbType.String;
#endif
    }

    protected override void SetValueCore(DbParameter parameter, OrderSettings value) =>
        parameter.Value = JsonSerializer.Serialize(value, DapperJson.Default.OrderSettings);
}

#if SQLITE
/// <summary>Converts the shared SQLite INTEGER cents contract to a typed decimal DTO.</summary>
public sealed class CentsHandler : DbValueHandler<decimal>
{
    public override int Tokenize(DbDataReader reader, int column)
    {
        // Empty nullable SUM can expose no non-null storage type. Parse is called only for values.
        return 0;
    }

    public override decimal Parse(DbDataReader reader, int column, int token) =>
        reader.GetInt64(column) / 100m;

    protected override decimal Parse(object? value) => value switch
    {
        long cents => cents / 100m,
        int cents => cents / 100m,
        _ => throw new InvalidOperationException("SQLite money must have integral cents storage."),
    };

    protected override void Configure(DbParameter parameter) => parameter.DbType = DbType.Int64;

    protected override void SetValueCore(DbParameter parameter, decimal value)
    {
        var scaled = value * 100m;
        var cents = checked((long)scaled);
        if (cents != scaled) throw new InvalidOperationException("Money binding would lose fractional cents.");
        parameter.Value = cents;
    }
}
#endif
