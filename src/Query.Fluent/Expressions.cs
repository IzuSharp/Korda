using System.Numerics;
using NativeWeave.Data;
using NativeWeave.Query.Internal;
using static NativeWeave.Query.QueryContext;

namespace NativeWeave.Query;

/// <summary>Native SQL ordering direction.</summary>
public enum SortDirection
{
    /// <summary>Uses the native Ascending policy.</summary>
    Ascending,
    /// <summary>Uses the native Descending policy.</summary>
    Descending,
}
/// <summary>Explicit SQL NULL placement.</summary>
public enum NullOrder
{
    /// <summary>Uses the native First policy.</summary>
    First,
    /// <summary>Uses the native Last policy.</summary>
    Last,
}
/// <summary>Native SQL set operation with exact compatible projection types.</summary>
public enum SetOperation
{
    /// <summary>Uses the native Union policy.</summary>
    Union,
    /// <summary>Uses the native UnionAll policy.</summary>
    UnionAll,
    /// <summary>Uses the native Intersect policy.</summary>
    Intersect,
    /// <summary>Uses the native Except policy.</summary>
    Except,
}
/// <summary>Supported finite window-frame endpoint choices.</summary>
public enum FrameBoundary
{
    /// <summary>Uses the native UnboundedPreceding policy.</summary>
    UnboundedPreceding,
    /// <summary>Uses the native CurrentRow policy.</summary>
    CurrentRow,
    /// <summary>Uses the native UnboundedFollowing policy.</summary>
    UnboundedFollowing,
}
/// <summary>PostgreSQL row lock strength requiring an explicit transaction.</summary>
public enum RowLockMode
{
    /// <summary>Uses the native Update policy.</summary>
    Update,
    /// <summary>Uses the native Share policy.</summary>
    Share,
    /// <summary>Uses the native NoKeyUpdate policy.</summary>
    NoKeyUpdate,
    /// <summary>Uses the native KeyShare policy.</summary>
    KeyShare,
}
/// <summary>PostgreSQL row-lock conflict behavior.</summary>
public enum LockWait
{
    /// <summary>Uses the native Wait policy.</summary>
    Wait,
    /// <summary>Uses the native NoWait policy.</summary>
    NoWait,
    /// <summary>Uses the native SkipLocked policy.</summary>
    SkipLocked,
}
/// <summary>An opaque same-query native SQL condition; default predicates reject.</summary>
/// <remarks>Boolean composition uses SQL three-valued logic and performs one direct native operation.</remarks>
public readonly struct SqlPredicate
{
    private readonly QueryContext? _owner;
    internal QueryContext Owner => _owner ?? throw new InvalidOperationException("The default predicate is invalid.");
    internal ExpressionReference Reference { get; }
    internal SqlPredicate(QueryContext owner, ExpressionReference reference) { _owner = owner; Reference = reference; }
    /// <summary>Combines same-query conditions with native AND.</summary>
    public SqlPredicate And(SqlPredicate other) { var owner = Owner; owner.Same(other.Owner); return new(owner, owner.Binary(11, Reference, other.Reference)); }
    /// <summary>Combines same-query conditions with native OR.</summary>
    public SqlPredicate Or(SqlPredicate other) { var owner = Owner; owner.Same(other.Owner); return new(owner, owner.Binary(12, Reference, other.Reference)); }
    /// <summary>Negates this condition using native SQL NOT.</summary>
    public SqlPredicate Not() { var owner = Owner; return new(owner, owner.Unary(3, Reference)); }
}
/// <summary>An opaque query-owned scalar token with its exact static codec and SQL nullability.</summary>
/// <typeparam name="T">The statically known value type.</typeparam>
/// <remarks>No SQL tree or delegate is retained. Values are snapshotted immediately; default and foreign tokens reject.</remarks>
public readonly struct SqlExpression<T>
{
    private readonly QueryContext? _owner;
    private readonly ValueCodec<T>? _codec;
    internal QueryContext Owner => _owner ?? throw new InvalidOperationException("The default expression is invalid.");
    internal ValueCodec<T> Codec => _codec ?? throw new InvalidOperationException("The default expression is invalid.");
    internal ExpressionReference Reference { get; }
    internal uint? RootColumnId { get; }
    internal SqlExpression(QueryContext owner, ExpressionReference reference, ValueCodec<T> codec, uint? column = null)
    { _owner = owner; Reference = reference; _codec = codec; RootColumnId = column; }
    private SqlPredicate Compare(uint operation, SqlExpression<T> other)
    { var owner = Owner; owner.Same(other.Owner); return new(owner, owner.Binary(operation, Reference, other.Reference)); }
    private SqlPredicate CompareValue(uint operation, T value)
    { var owner = Owner; var self = this; return owner.Run(() => self.Compare(operation, owner.Value(value, self.Codec))); }
    /// <summary>Applies native Equal with exact codec/source checks and SQL NULL semantics.</summary>
    public SqlPredicate Equal(T value) => CompareValue(1, value);
    /// <summary>Applies native Equal with exact codec/source checks and SQL NULL semantics.</summary>
    public SqlPredicate Equal(SqlExpression<T> other) => Compare(1, other);
    /// <summary>Applies native NotEqual with exact codec/source checks and SQL NULL semantics.</summary>
    public SqlPredicate NotEqual(T value) => CompareValue(2, value);
    /// <summary>Applies native NotEqual with exact codec/source checks and SQL NULL semantics.</summary>
    public SqlPredicate NotEqual(SqlExpression<T> other) => Compare(2, other);
    /// <summary>Applies native GreaterThan with exact codec/source checks and SQL NULL semantics.</summary>
    public SqlPredicate GreaterThan(T value) => CompareValue(3, value);
    /// <summary>Applies native GreaterThan with exact codec/source checks and SQL NULL semantics.</summary>
    public SqlPredicate GreaterThan(SqlExpression<T> other) => Compare(3, other);
    /// <summary>Applies native GreaterThanOrEqual with exact codec/source checks and SQL NULL semantics.</summary>
    public SqlPredicate GreaterThanOrEqual(T value) => CompareValue(4, value);
    /// <summary>Applies native GreaterThanOrEqual with exact codec/source checks and SQL NULL semantics.</summary>
    public SqlPredicate GreaterThanOrEqual(SqlExpression<T> other) => Compare(4, other);
    /// <summary>Applies native LessThan with exact codec/source checks and SQL NULL semantics.</summary>
    public SqlPredicate LessThan(T value) => CompareValue(5, value);
    /// <summary>Applies native LessThan with exact codec/source checks and SQL NULL semantics.</summary>
    public SqlPredicate LessThan(SqlExpression<T> other) => Compare(5, other);
    /// <summary>Applies native LessThanOrEqual with exact codec/source checks and SQL NULL semantics.</summary>
    public SqlPredicate LessThanOrEqual(T value) => CompareValue(6, value);
    /// <summary>Applies native LessThanOrEqual with exact codec/source checks and SQL NULL semantics.</summary>
    public SqlPredicate LessThanOrEqual(SqlExpression<T> other) => Compare(6, other);
    /// <summary>Applies native NullSafeEqual with exact codec/source checks and SQL NULL semantics.</summary>
    public SqlPredicate NullSafeEqual(T value) => CompareValue(13, value);
    /// <summary>Applies native NullSafeEqual with exact codec/source checks and SQL NULL semantics.</summary>
    public SqlPredicate NullSafeEqual(SqlExpression<T> other) => Compare(13, other);
    /// <summary>Snapshots two bounds and creates native inclusive BETWEEN.</summary>
    public SqlPredicate Between(T lower, T upper) => List(3, [lower, upper]);
    /// <summary>Tests SQL NULL explicitly without substituting a sentinel.</summary>
    public SqlPredicate IsNull() { var owner = Owner; return new(owner, owner.Unary(1, Reference)); }
    /// <summary>Tests SQL non-NULL explicitly.</summary>
    public SqlPredicate IsNotNull() { var owner = Owner; return new(owner, owner.Unary(2, Reference)); }
    /// <summary>Snapshots a finite value span; an empty span yields native FALSE.</summary>
    public SqlPredicate In(ReadOnlySpan<T> values) => List(1, values);
    /// <summary>Snapshots a finite value span; an empty span yields native TRUE.</summary>
    public SqlPredicate NotIn(ReadOnlySpan<T> values) => List(2, values);
    private SqlPredicate List(uint operation, ReadOnlySpan<T> values)
    {
        var owner = Owner; var reference = Reference; var codec = Codec;
        using var admission = owner.Enter();
        if (values.Length > owner.Source.Limits.MaxParameters) throw new DatabaseException("Collection parameter limit exceeded.", DatabaseErrorKind.ResourceLimit);
        var cells = new ReadOnlyMemory<byte>?[values.Length];
        for (var i = 0; i < values.Length; i++) { var bytes = codec.Encode(values[i]); cells[i] = bytes is null ? (ReadOnlyMemory<byte>?)null : new ReadOnlyMemory<byte>(bytes); }
        var bound = owner.Source.BindCodec(codec.Descriptor);
        var wire = DbWire.Encode(new(PayloadKind.ExpressionValues, owner.Source.Binding.SemanticProfile, new byte[32], 0, 0, [new(1, codec.Descriptor, bound)]), (uint)values.Length, cells);
        return owner.Run(() => new SqlPredicate(owner, owner.Binding.List(operation, reference, wire)));
    }
    /// <summary>Snapshots a compatible same-source single-column subquery into native IN.</summary>
    public SqlPredicate In(QuerySelection<T> subquery)
    {
        var owner = Owner; var reference = Reference;
        return owner.Run(() => { var imported = owner.Import(subquery); return new SqlPredicate(owner, owner.Ref(owner.Clause(14, U(1, imported.In(owner.Binding)), N(2, 1), U(3, reference.In(owner.Binding))))); });
    }
    /// <summary>Snapshots same-query native window state onto an eligible aggregate or rank expression.</summary>
    public SqlExpression<T> Over(WindowDefinition window)
    {
        ArgumentNullException.ThrowIfNull(window); var owner = Owner; owner.Same(window.Owner);
        return new(owner, owner.Ref(owner.Clause(21, U(1, Reference.In(owner.Binding)), U(2, window.Token))), Codec);
    }
    internal SqlExpression<T> Arithmetic(uint operation, SqlExpression<T> other)
    { var owner = Owner; owner.Same(other.Owner); return new(owner, owner.Binary(operation, Reference, other.Reference), Codec); }
    internal SqlExpression<T> Arithmetic(uint operation, T value)
    { var owner = Owner; var self = this; return owner.Run(() => self.Arithmetic(operation, owner.Value(value, self.Codec))); }
}
/// <summary>Explicit one-way SQL nullability lifts; no SQL cast, coalesce or new parameter is emitted.</summary>
public static class SqlNullabilityExtensions
{
    /// <summary>Returns a fresh same-query nullable scalar token, preserving the exact value and codec.</summary>
    public static SqlExpression<T?> AsNullable<T>(this SqlExpression<T> expression) where T : struct
    { var owner = expression.Owner; if (expression.Codec.Descriptor.Nullable) throw new ArgumentException("The expression is already nullable.", nameof(expression)); return new(owner, owner.Unary(4, expression.Reference), Codecs.Nullable(expression.Codec)); }
    /// <summary>Returns a fresh same-query nullable reference token; already-nullable metadata rejects.</summary>
    public static SqlExpression<T?> AsNullableReference<T>(this SqlExpression<T> expression) where T : class
    { var owner = expression.Owner; if (expression.Codec.Descriptor.Nullable) throw new ArgumentException("The expression is already nullable.", nameof(expression)); return new(owner, owner.Unary(4, expression.Reference), Codecs.NullableReference(expression.Codec)); }
}
/// <summary>Statically numeric native SQL arithmetic; overflow, precision and division remain provider semantics.</summary>
public static class SqlArithmeticExtensions
{
    /// <summary>Performs native numeric add with explicit SQL NULL propagation.</summary>
    public static SqlExpression<T> Add<T>(this SqlExpression<T> expression, T value) where T : INumber<T> => expression.Arithmetic(7, value);
    /// <summary>Performs native numeric add with explicit SQL NULL propagation.</summary>
    public static SqlExpression<T> Add<T>(this SqlExpression<T> expression, SqlExpression<T> value) where T : INumber<T> => expression.Arithmetic(7, value);
    /// <summary>Performs native numeric add with explicit SQL NULL propagation.</summary>
    public static SqlExpression<T?> Add<T>(this SqlExpression<T?> expression, T value) where T : struct, INumber<T> => expression.Arithmetic(7, value);
    /// <summary>Performs native numeric add with explicit SQL NULL propagation.</summary>
    public static SqlExpression<T?> Add<T>(this SqlExpression<T?> expression, SqlExpression<T?> value) where T : struct, INumber<T> => expression.Arithmetic(7, value);
    /// <summary>Performs native numeric subtract with explicit SQL NULL propagation.</summary>
    public static SqlExpression<T> Subtract<T>(this SqlExpression<T> expression, T value) where T : INumber<T> => expression.Arithmetic(8, value);
    /// <summary>Performs native numeric subtract with explicit SQL NULL propagation.</summary>
    public static SqlExpression<T> Subtract<T>(this SqlExpression<T> expression, SqlExpression<T> value) where T : INumber<T> => expression.Arithmetic(8, value);
    /// <summary>Performs native numeric subtract with explicit SQL NULL propagation.</summary>
    public static SqlExpression<T?> Subtract<T>(this SqlExpression<T?> expression, T value) where T : struct, INumber<T> => expression.Arithmetic(8, value);
    /// <summary>Performs native numeric subtract with explicit SQL NULL propagation.</summary>
    public static SqlExpression<T?> Subtract<T>(this SqlExpression<T?> expression, SqlExpression<T?> value) where T : struct, INumber<T> => expression.Arithmetic(8, value);
    /// <summary>Performs native numeric multiply with explicit SQL NULL propagation.</summary>
    public static SqlExpression<T> Multiply<T>(this SqlExpression<T> expression, T value) where T : INumber<T> => expression.Arithmetic(9, value);
    /// <summary>Performs native numeric multiply with explicit SQL NULL propagation.</summary>
    public static SqlExpression<T> Multiply<T>(this SqlExpression<T> expression, SqlExpression<T> value) where T : INumber<T> => expression.Arithmetic(9, value);
    /// <summary>Performs native numeric multiply with explicit SQL NULL propagation.</summary>
    public static SqlExpression<T?> Multiply<T>(this SqlExpression<T?> expression, T value) where T : struct, INumber<T> => expression.Arithmetic(9, value);
    /// <summary>Performs native numeric multiply with explicit SQL NULL propagation.</summary>
    public static SqlExpression<T?> Multiply<T>(this SqlExpression<T?> expression, SqlExpression<T?> value) where T : struct, INumber<T> => expression.Arithmetic(9, value);
    /// <summary>Performs native numeric divide with explicit SQL NULL propagation.</summary>
    public static SqlExpression<T> Divide<T>(this SqlExpression<T> expression, T value) where T : INumber<T> => expression.Arithmetic(10, value);
    /// <summary>Performs native numeric divide with explicit SQL NULL propagation.</summary>
    public static SqlExpression<T> Divide<T>(this SqlExpression<T> expression, SqlExpression<T> value) where T : INumber<T> => expression.Arithmetic(10, value);
    /// <summary>Performs native numeric divide with explicit SQL NULL propagation.</summary>
    public static SqlExpression<T?> Divide<T>(this SqlExpression<T?> expression, T value) where T : struct, INumber<T> => expression.Arithmetic(10, value);
    /// <summary>Performs native numeric divide with explicit SQL NULL propagation.</summary>
    public static SqlExpression<T?> Divide<T>(this SqlExpression<T?> expression, SqlExpression<T?> value) where T : struct, INumber<T> => expression.Arithmetic(10, value);
}
/// <summary>Explicit SQL string pattern and allowlisted collation operations; no CLR culture comparison.</summary>
public static class SqlStringExtensions
{
    private static SqlPredicate Pattern<T>(SqlExpression<T> expression, string value, uint operation, char escape)
    {
        ArgumentNullException.ThrowIfNull(value); var owner = expression.Owner;
        return owner.Run(() =>
        {
            var bytes = OperandEncoding.Value(owner.Source.Binding.SemanticProfile, Codecs.String, owner.Source.BindCodec(Codecs.String.Descriptor), value);
            return new SqlPredicate(owner, owner.Ref(owner.Clause(16, N(1, operation), U(2, expression.Reference.In(owner.Binding)), B(3, bytes, 6), N(4, escape))));
        });
    }
    /// <summary>Escapes literal wildcard characters and searches nullable SQL text.</summary>
    public static SqlPredicate ContainsNullableLiteral(this SqlExpression<string?> expression, string value) => Pattern(expression, value, 0, '\\');
    /// <summary>Uses an explicit raw LIKE pattern against nullable SQL text.</summary>
    public static SqlPredicate LikeNullable(this SqlExpression<string?> expression, string pattern, char escape = '\\') => Pattern(expression, pattern, 3, escape);
    /// <summary>Escapes wildcard characters and binds a literal contains pattern.</summary>
    public static SqlPredicate ContainsLiteral(this SqlExpression<string> expression, string value) => Pattern(expression, value, 0, '\\');
    /// <summary>Escapes wildcard characters and binds a literal prefix pattern.</summary>
    public static SqlPredicate StartsWithLiteral(this SqlExpression<string> expression, string value) => Pattern(expression, value, 1, '\\');
    /// <summary>Escapes wildcard characters and binds a literal suffix pattern.</summary>
    public static SqlPredicate EndsWithLiteral(this SqlExpression<string> expression, string value) => Pattern(expression, value, 2, '\\');
    /// <summary>Binds the supplied raw SQL LIKE pattern with an explicit escape character.</summary>
    public static SqlPredicate Like(this SqlExpression<string> expression, string pattern, char escape = '\\') => Pattern(expression, pattern, 3, escape);
    /// <summary>Applies only a collation admitted by the provider's fixed allowlist.</summary>
    public static SqlExpression<string> Collate(this SqlExpression<string> expression, string collation)
    { ArgumentNullException.ThrowIfNull(collation); var owner = expression.Owner; return new(owner, owner.Ref(owner.Clause(16, N(1, 4), U(2, expression.Reference.In(owner.Binding)), Text(3, collation), N(4, '\\'))), expression.Codec); }
}
