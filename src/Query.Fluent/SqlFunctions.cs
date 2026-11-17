using NativeWeave.Data;
using NativeWeave.Query.Internal;
using static NativeWeave.Query.QueryContext;

namespace NativeWeave.Query;

/// <summary>Static typed native aggregates, ranking and conditional scalar composition.</summary>
/// <remarks>SQL NULL, collation, overflow and conversion semantics remain explicit provider behavior.</remarks>
public static class SqlFunctions
{
    internal static SqlExpression<TResult> Aggregate<TResult>(QueryContext owner, uint operation, ExpressionReference? input, ValueCodec<TResult> codec, bool distinct = false, WindowDefinition? window = null) => owner.Run(() =>
    {
        var fields = new List<ControlField> { N(1, operation) };
        if (input is { } value) fields.Add(U(2, value.In(owner.Binding)));
        fields.Add(N(3, distinct ? 1u : 0u));
        if (window is not null) { owner.Same(window.Owner); fields.Add(U(4, window.Token)); }
        fields.Add(N(5, owner.Source.BindCodec(codec.Descriptor)));
        return new SqlExpression<TResult>(owner, owner.Ref(owner.Clause(13, [.. fields])), codec);
    });
    /// <summary>Produces a non-null scalar with native SQL COALESCE and an explicitly copied fallback.</summary>
    public static SqlExpression<T> Coalesce<T>(SqlExpression<T?> expression, T fallback) where T : struct
    {
        var owner = expression.Owner;
        var codec = new ValueCodec<T>(expression.Codec.Descriptor with { Nullable = false }, v => expression.Codec.Encode(v)!, b => expression.Codec.DecodeScalar(b)!.Value);
        return owner.Run(() => new SqlExpression<T>(owner, owner.Binary(14, expression.Reference, owner.Value(fallback, codec).Reference), codec));
    }
    /// <summary>Produces a non-null reference scalar with native SQL COALESCE.</summary>
    public static SqlExpression<T> CoalesceReference<T>(SqlExpression<T?> expression, T fallback) where T : class
    {
        var owner = expression.Owner; ArgumentNullException.ThrowIfNull(fallback);
        var codec = new ValueCodec<T>(expression.Codec.Descriptor with { Nullable = false }, v => expression.Codec.Encode(v)!, b => expression.Codec.DecodeScalar(b)!);
        return owner.Run(() => new SqlExpression<T>(owner, owner.Binary(14, expression.Reference, owner.Value(fallback, codec).Reference), codec));
    }
    /// <summary>Counts non-null native values, optionally using DISTINCT, with exact Int64 result metadata.</summary>
    public static SqlExpression<long> Count<T>(SqlExpression<T> expression, bool distinct = false) => Aggregate(expression.Owner, 1, expression.Reference, Codecs.Int64, distinct);
    /// <summary>Aggregates native int inputs to nullable long, preserving empty-input NULL and explicit conversion failures.</summary>
    public static SqlExpression<long?> Sum(SqlExpression<int> expression) => Aggregate(expression.Owner, 2, expression.Reference, Codecs.Nullable(Codecs.Int64));
    /// <summary>Aggregates native int? inputs to nullable long, preserving empty-input NULL and explicit conversion failures.</summary>
    public static SqlExpression<long?> Sum(SqlExpression<int?> expression) => Aggregate(expression.Owner, 2, expression.Reference, Codecs.Nullable(Codecs.Int64));
    /// <summary>Aggregates native long inputs to nullable long, preserving empty-input NULL and explicit conversion failures.</summary>
    public static SqlExpression<long?> Sum(SqlExpression<long> expression) => Aggregate(expression.Owner, 2, expression.Reference, Codecs.Nullable(Codecs.Int64));
    /// <summary>Aggregates native long? inputs to nullable long, preserving empty-input NULL and explicit conversion failures.</summary>
    public static SqlExpression<long?> Sum(SqlExpression<long?> expression) => Aggregate(expression.Owner, 2, expression.Reference, Codecs.Nullable(Codecs.Int64));
    /// <summary>Aggregates native decimal inputs to nullable decimal, preserving empty-input NULL and explicit conversion failures.</summary>
    public static SqlExpression<decimal?> Sum(SqlExpression<decimal> expression) => Aggregate(expression.Owner, 2, expression.Reference, Codecs.Nullable(expression.Codec));
    /// <summary>Aggregates native decimal? inputs to nullable decimal, preserving empty-input NULL and explicit conversion failures.</summary>
    public static SqlExpression<decimal?> Sum(SqlExpression<decimal?> expression) => Aggregate(expression.Owner, 2, expression.Reference, expression.Codec);
    /// <summary>Aggregates native double inputs to nullable double, preserving empty-input NULL and explicit conversion failures.</summary>
    public static SqlExpression<double?> Sum(SqlExpression<double> expression) => Aggregate(expression.Owner, 2, expression.Reference, Codecs.Nullable(Codecs.Double));
    /// <summary>Aggregates native double? inputs to nullable double, preserving empty-input NULL and explicit conversion failures.</summary>
    public static SqlExpression<double?> Sum(SqlExpression<double?> expression) => Aggregate(expression.Owner, 2, expression.Reference, Codecs.Nullable(Codecs.Double));
    /// <summary>Aggregates native float inputs to nullable double, preserving empty-input NULL and explicit conversion failures.</summary>
    public static SqlExpression<double?> Sum(SqlExpression<float> expression) => Aggregate(expression.Owner, 2, expression.Reference, Codecs.Nullable(Codecs.Double));
    /// <summary>Aggregates native float? inputs to nullable double, preserving empty-input NULL and explicit conversion failures.</summary>
    public static SqlExpression<double?> Sum(SqlExpression<float?> expression) => Aggregate(expression.Owner, 2, expression.Reference, Codecs.Nullable(Codecs.Double));
    /// <summary>Aggregates native int inputs to nullable double, preserving empty-input NULL and explicit conversion failures.</summary>
    public static SqlExpression<double?> Average(SqlExpression<int> expression) => Aggregate(expression.Owner, 3, expression.Reference, Codecs.Nullable(Codecs.Double));
    /// <summary>Aggregates native int? inputs to nullable double, preserving empty-input NULL and explicit conversion failures.</summary>
    public static SqlExpression<double?> Average(SqlExpression<int?> expression) => Aggregate(expression.Owner, 3, expression.Reference, Codecs.Nullable(Codecs.Double));
    /// <summary>Aggregates native long inputs to nullable double, preserving empty-input NULL and explicit conversion failures.</summary>
    public static SqlExpression<double?> Average(SqlExpression<long> expression) => Aggregate(expression.Owner, 3, expression.Reference, Codecs.Nullable(Codecs.Double));
    /// <summary>Aggregates native long? inputs to nullable double, preserving empty-input NULL and explicit conversion failures.</summary>
    public static SqlExpression<double?> Average(SqlExpression<long?> expression) => Aggregate(expression.Owner, 3, expression.Reference, Codecs.Nullable(Codecs.Double));
    /// <summary>Aggregates native decimal inputs to nullable decimal, preserving empty-input NULL and explicit conversion failures.</summary>
    public static SqlExpression<decimal?> Average(SqlExpression<decimal> expression) => Aggregate(expression.Owner, 3, expression.Reference, Codecs.Nullable(expression.Codec));
    /// <summary>Aggregates native decimal? inputs to nullable decimal, preserving empty-input NULL and explicit conversion failures.</summary>
    public static SqlExpression<decimal?> Average(SqlExpression<decimal?> expression) => Aggregate(expression.Owner, 3, expression.Reference, expression.Codec);
    /// <summary>Aggregates native double inputs to nullable double, preserving empty-input NULL and explicit conversion failures.</summary>
    public static SqlExpression<double?> Average(SqlExpression<double> expression) => Aggregate(expression.Owner, 3, expression.Reference, Codecs.Nullable(Codecs.Double));
    /// <summary>Aggregates native double? inputs to nullable double, preserving empty-input NULL and explicit conversion failures.</summary>
    public static SqlExpression<double?> Average(SqlExpression<double?> expression) => Aggregate(expression.Owner, 3, expression.Reference, Codecs.Nullable(Codecs.Double));
    /// <summary>Aggregates native float inputs to nullable double, preserving empty-input NULL and explicit conversion failures.</summary>
    public static SqlExpression<double?> Average(SqlExpression<float> expression) => Aggregate(expression.Owner, 3, expression.Reference, Codecs.Nullable(Codecs.Double));
    /// <summary>Aggregates native float? inputs to nullable double, preserving empty-input NULL and explicit conversion failures.</summary>
    public static SqlExpression<double?> Average(SqlExpression<float?> expression) => Aggregate(expression.Owner, 3, expression.Reference, Codecs.Nullable(Codecs.Double));
    /// <summary>Returns the native minimum with the exact scalar codec and empty-input SQL NULL.</summary>
    public static SqlExpression<T?> Min<T>(SqlExpression<T> expression) where T : struct => Aggregate(expression.Owner, 4, expression.Reference, Codecs.Nullable(expression.Codec));
    /// <summary>Returns the native minimum with the exact scalar codec and empty-input SQL NULL.</summary>
    public static SqlExpression<T?> Min<T>(SqlExpression<T?> expression) where T : struct => Aggregate(expression.Owner, 4, expression.Reference, expression.Codec);
    /// <summary>Returns native text minimum under SQL collation with empty-input NULL.</summary>
    public static SqlExpression<string?> MinText(SqlExpression<string> expression) => Aggregate(expression.Owner, 4, expression.Reference, Codecs.NullableReference(expression.Codec));
    /// <summary>Returns native text minimum under SQL collation with empty-input NULL.</summary>
    public static SqlExpression<string?> MinNullableText(SqlExpression<string?> expression) => Aggregate(expression.Owner, 4, expression.Reference, expression.Codec);
    /// <summary>Returns the native maximum with the exact scalar codec and empty-input SQL NULL.</summary>
    public static SqlExpression<T?> Max<T>(SqlExpression<T> expression) where T : struct => Aggregate(expression.Owner, 5, expression.Reference, Codecs.Nullable(expression.Codec));
    /// <summary>Returns the native maximum with the exact scalar codec and empty-input SQL NULL.</summary>
    public static SqlExpression<T?> Max<T>(SqlExpression<T?> expression) where T : struct => Aggregate(expression.Owner, 5, expression.Reference, expression.Codec);
    /// <summary>Returns native text maximum under SQL collation with empty-input NULL.</summary>
    public static SqlExpression<string?> MaxText(SqlExpression<string> expression) => Aggregate(expression.Owner, 5, expression.Reference, Codecs.NullableReference(expression.Codec));
    /// <summary>Returns native text maximum under SQL collation with empty-input NULL.</summary>
    public static SqlExpression<string?> MaxNullableText(SqlExpression<string?> expression) => Aggregate(expression.Owner, 5, expression.Reference, expression.Codec);
    /// <summary>Snapshots a native window into an exact Int64 ROW_NUMBER expression.</summary>
    public static SqlExpression<long> RowNumber(WindowDefinition window) { ArgumentNullException.ThrowIfNull(window); return Aggregate(window.Owner, 6, null, Codecs.Int64, window: window); }
    /// <summary>Snapshots a native window into an exact Int64 RANK expression.</summary>
    public static SqlExpression<long> Rank(WindowDefinition window) { ArgumentNullException.ThrowIfNull(window); return Aggregate(window.Owner, 7, null, Codecs.Int64, window: window); }
    /// <summary>Creates a native CASE with same-query compatible scalar alternatives.</summary>
    public static SqlExpression<T> Case<T>(SqlPredicate condition, SqlExpression<T> whenTrue, SqlExpression<T> whenFalse)
    {
        var owner = condition.Owner; owner.Same(whenTrue.Owner); owner.Same(whenFalse.Owner);
        if (whenTrue.Codec.Descriptor != whenFalse.Codec.Descriptor) throw new ArgumentException("CASE alternatives require exact codec/nullability.");
        return new(owner, owner.Ref(owner.Clause(15, U(1, condition.Reference.In(owner.Binding)), U(2, whenTrue.Reference.In(owner.Binding)), U(3, whenFalse.Reference.In(owner.Binding)))), whenTrue.Codec);
    }
}
