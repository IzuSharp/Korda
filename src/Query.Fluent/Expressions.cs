using System.Numerics;
using Korda.Data;
using Korda.Query.Internal;
using static Korda.Query.QueryContext;

namespace Korda.Query;

/// <summary>Native SQL ordering direction.</summary>
/// <remarks>The native query validates each enum value before mutation. Direction is explicit; it does not change SQL NULL ordering.</remarks>
public enum SortDirection
{
    /// <summary>Uses the native Ascending policy.</summary>
    /// <inheritdoc cref="SortDirection"/>
    Ascending,
    /// <summary>Uses the native Descending policy.</summary>
    /// <inheritdoc cref="SortDirection"/>
    Descending,
}
/// <summary>Explicit SQL NULL placement.</summary>
/// <remarks>Both providers render explicit SQL NULL placement. Ordering alone supplies no uniqueness or snapshot guarantee.</remarks>
public enum NullOrder
{
    /// <summary>Uses the native First policy.</summary>
    /// <inheritdoc cref="NullOrder"/>
    First,
    /// <summary>Uses the native Last policy.</summary>
    /// <inheritdoc cref="NullOrder"/>
    Last,
}
/// <summary>Native SQL set operation with exact compatible projection types.</summary>
/// <remarks>Operands must have the same source, field order, aliases, codecs and nullability. The native engine snapshots the right operand; SQL duplicate and NULL semantics apply.</remarks>
public enum SetOperation
{
    /// <summary>Uses the native Union policy.</summary>
    /// <inheritdoc cref="SetOperation"/>
    Union,
    /// <summary>Uses the native UnionAll policy.</summary>
    /// <inheritdoc cref="SetOperation"/>
    UnionAll,
    /// <summary>Uses the native Intersect policy.</summary>
    /// <inheritdoc cref="SetOperation"/>
    Intersect,
    /// <summary>Uses the native Except policy.</summary>
    /// <inheritdoc cref="SetOperation"/>
    Except,
}
/// <summary>Supported finite window-frame endpoint choices.</summary>
/// <remarks>Only these ROWS endpoints are supported. Reversed or unsupported bounds fail before native state publication; finite numeric PRECEDING/FOLLOWING frames are not declared.</remarks>
public enum FrameBoundary
{
    /// <summary>Uses the native UnboundedPreceding policy.</summary>
    /// <inheritdoc cref="FrameBoundary"/>
    UnboundedPreceding,
    /// <summary>Uses the native CurrentRow policy.</summary>
    /// <inheritdoc cref="FrameBoundary"/>
    CurrentRow,
    /// <summary>Uses the native UnboundedFollowing policy.</summary>
    /// <inheritdoc cref="FrameBoundary"/>
    UnboundedFollowing,
}
/// <summary>PostgreSQL row lock strength requiring an explicit transaction.</summary>
/// <remarks>Available only on PostgreSQL with an explicit live transaction. SQLite rejects the operation before provider dispatch.</remarks>
public enum RowLockMode
{
    /// <summary>Uses the native Update policy.</summary>
    /// <inheritdoc cref="RowLockMode"/>
    Update,
    /// <summary>Uses the native Share policy.</summary>
    /// <inheritdoc cref="RowLockMode"/>
    Share,
    /// <summary>Uses the native NoKeyUpdate policy.</summary>
    /// <inheritdoc cref="RowLockMode"/>
    NoKeyUpdate,
    /// <summary>Uses the native KeyShare policy.</summary>
    /// <inheritdoc cref="RowLockMode"/>
    KeyShare,
}
/// <summary>PostgreSQL row-lock conflict behavior.</summary>
/// <remarks>Controls PostgreSQL row-lock conflicts within the same transaction and total query deadline; it does not enable transparent retries.</remarks>
public enum LockWait
{
    /// <summary>Uses the native Wait policy.</summary>
    /// <inheritdoc cref="LockWait"/>
    Wait,
    /// <summary>Uses the native NoWait policy.</summary>
    /// <inheritdoc cref="LockWait"/>
    NoWait,
    /// <summary>Uses the native SkipLocked policy.</summary>
    /// <inheritdoc cref="LockWait"/>
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
    /// <inheritdoc cref="SqlPredicate"/>
    /// <param name="other">The compatible same-source selection or same-query expression required by this operation.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate And(SqlPredicate other) { var owner = Owner; owner.Same(other.Owner); return new(owner, owner.Binary(11, Reference, other.Reference)); }
    /// <summary>Combines same-query conditions with native OR.</summary>
    /// <inheritdoc cref="SqlPredicate"/>
    /// <param name="other">The compatible same-source selection or same-query expression required by this operation.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate Or(SqlPredicate other) { var owner = Owner; owner.Same(other.Owner); return new(owner, owner.Binary(12, Reference, other.Reference)); }
    /// <summary>Negates this condition using native SQL NOT.</summary>
    /// <inheritdoc cref="SqlPredicate"/>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
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
    /// <inheritdoc cref="SqlExpression{T}"/>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate Equal(T value) => CompareValue(1, value);
    /// <summary>Applies native Equal with exact codec/source checks and SQL NULL semantics.</summary>
    /// <inheritdoc cref="SqlExpression{T}"/>
    /// <param name="other">The compatible same-source selection or same-query expression required by this operation.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate Equal(SqlExpression<T> other) => Compare(1, other);
    /// <summary>Applies native NotEqual with exact codec/source checks and SQL NULL semantics.</summary>
    /// <inheritdoc cref="SqlExpression{T}"/>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate NotEqual(T value) => CompareValue(2, value);
    /// <summary>Applies native NotEqual with exact codec/source checks and SQL NULL semantics.</summary>
    /// <inheritdoc cref="SqlExpression{T}"/>
    /// <param name="other">The compatible same-source selection or same-query expression required by this operation.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate NotEqual(SqlExpression<T> other) => Compare(2, other);
    /// <summary>Applies native GreaterThan with exact codec/source checks and SQL NULL semantics.</summary>
    /// <inheritdoc cref="SqlExpression{T}"/>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate GreaterThan(T value) => CompareValue(3, value);
    /// <summary>Applies native GreaterThan with exact codec/source checks and SQL NULL semantics.</summary>
    /// <inheritdoc cref="SqlExpression{T}"/>
    /// <param name="other">The compatible same-source selection or same-query expression required by this operation.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate GreaterThan(SqlExpression<T> other) => Compare(3, other);
    /// <summary>Applies native GreaterThanOrEqual with exact codec/source checks and SQL NULL semantics.</summary>
    /// <inheritdoc cref="SqlExpression{T}"/>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate GreaterThanOrEqual(T value) => CompareValue(4, value);
    /// <summary>Applies native GreaterThanOrEqual with exact codec/source checks and SQL NULL semantics.</summary>
    /// <inheritdoc cref="SqlExpression{T}"/>
    /// <param name="other">The compatible same-source selection or same-query expression required by this operation.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate GreaterThanOrEqual(SqlExpression<T> other) => Compare(4, other);
    /// <summary>Applies native LessThan with exact codec/source checks and SQL NULL semantics.</summary>
    /// <inheritdoc cref="SqlExpression{T}"/>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate LessThan(T value) => CompareValue(5, value);
    /// <summary>Applies native LessThan with exact codec/source checks and SQL NULL semantics.</summary>
    /// <inheritdoc cref="SqlExpression{T}"/>
    /// <param name="other">The compatible same-source selection or same-query expression required by this operation.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate LessThan(SqlExpression<T> other) => Compare(5, other);
    /// <summary>Applies native LessThanOrEqual with exact codec/source checks and SQL NULL semantics.</summary>
    /// <inheritdoc cref="SqlExpression{T}"/>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate LessThanOrEqual(T value) => CompareValue(6, value);
    /// <summary>Applies native LessThanOrEqual with exact codec/source checks and SQL NULL semantics.</summary>
    /// <inheritdoc cref="SqlExpression{T}"/>
    /// <param name="other">The compatible same-source selection or same-query expression required by this operation.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate LessThanOrEqual(SqlExpression<T> other) => Compare(6, other);
    /// <summary>Applies native NullSafeEqual with exact codec/source checks and SQL NULL semantics.</summary>
    /// <inheritdoc cref="SqlExpression{T}"/>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate NullSafeEqual(T value) => CompareValue(13, value);
    /// <summary>Applies native NullSafeEqual with exact codec/source checks and SQL NULL semantics.</summary>
    /// <inheritdoc cref="SqlExpression{T}"/>
    /// <param name="other">The compatible same-source selection or same-query expression required by this operation.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate NullSafeEqual(SqlExpression<T> other) => Compare(13, other);
    /// <summary>Snapshots two bounds and creates native inclusive BETWEEN.</summary>
    /// <inheritdoc cref="SqlExpression{T}"/>
    /// <param name="lower">The inclusive lower endpoint, copied before the method returns.</param>
    /// <param name="upper">The inclusive upper endpoint, copied before the method returns.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate Between(T lower, T upper) => List(3, [lower, upper]);
    /// <summary>Tests SQL NULL explicitly without substituting a sentinel.</summary>
    /// <inheritdoc cref="SqlExpression{T}"/>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate IsNull() { var owner = Owner; return new(owner, owner.Unary(1, Reference)); }
    /// <summary>Tests SQL non-NULL explicitly.</summary>
    /// <inheritdoc cref="SqlExpression{T}"/>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate IsNotNull() { var owner = Owner; return new(owner, owner.Unary(2, Reference)); }
    /// <summary>Snapshots a finite value span; an empty span yields native FALSE.</summary>
    /// <inheritdoc cref="SqlExpression{T}"/>
    /// <param name="values">The finite typed input values, copied before returning; the span is never retained.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate In(ReadOnlySpan<T> values) => List(1, values);
    /// <summary>Snapshots a finite value span; an empty span yields native TRUE.</summary>
    /// <inheritdoc cref="SqlExpression{T}"/>
    /// <param name="values">The finite typed input values, copied before returning; the span is never retained.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
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
    /// <inheritdoc cref="SqlExpression{T}"/>
    /// <param name="subquery">A current same-source selection whose native plan is cloned under admission.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate In(QuerySelection<T> subquery)
    {
        var owner = Owner; var reference = Reference;
        return owner.Run(() => { var imported = owner.Import(subquery); return new SqlPredicate(owner, owner.Ref(owner.Clause(14, U(1, imported.In(owner.Binding)), N(2, 1), U(3, reference.In(owner.Binding))))); });
    }
    /// <summary>Snapshots same-query native window state onto an eligible aggregate or rank expression.</summary>
    /// <inheritdoc cref="SqlExpression{T}"/>
    /// <param name="window">The same-query window definition to snapshot at this call.</param>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
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
/// <remarks>These forward-only native metadata lifts preserve the SQL value and codec configuration. They do not emit a sentinel, COALESCE or reverse conversion. Default/foreign owners reject, and repeating a reference lift rejects its already nullable descriptor.</remarks>
public static class SqlNullabilityExtensions
{
    /// <summary>Returns a fresh same-query nullable scalar token, preserving the exact value and codec.</summary>
    /// <inheritdoc cref="SqlNullabilityExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlExpression<T?> AsNullable<T>(this SqlExpression<T> expression) where T : struct
    { var owner = expression.Owner; if (expression.Codec.Descriptor.Nullable) throw new ArgumentException("The expression is already nullable.", nameof(expression)); return new(owner, owner.Unary(4, expression.Reference), Codecs.Nullable(expression.Codec)); }
    /// <summary>Returns a fresh same-query nullable reference token; already-nullable metadata rejects.</summary>
    /// <inheritdoc cref="SqlNullabilityExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlExpression<T?> AsNullableReference<T>(this SqlExpression<T> expression) where T : class
    { var owner = expression.Owner; if (expression.Codec.Descriptor.Nullable) throw new ArgumentException("The expression is already nullable.", nameof(expression)); return new(owner, owner.Unary(4, expression.Reference), Codecs.NullableReference(expression.Codec)); }
}
/// <summary>Statically numeric native SQL arithmetic; overflow, precision and division remain provider semantics.</summary>
/// <remarks>Operators require compatible same-query numeric codecs and preserve declared SQL NULL propagation. The native engine owns provider arithmetic and checked result conversion; unsupported codecs fail explicitly. No CLR arithmetic delegate is executed for database rows.</remarks>
public static class SqlArithmeticExtensions
{
    /// <summary>Performs native numeric add with explicit SQL NULL propagation.</summary>
    /// <inheritdoc cref="SqlArithmeticExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlExpression<T> Add<T>(this SqlExpression<T> expression, T value) where T : INumber<T> => expression.Arithmetic(7, value);
    /// <summary>Performs native numeric add with explicit SQL NULL propagation.</summary>
    /// <inheritdoc cref="SqlArithmeticExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlExpression<T> Add<T>(this SqlExpression<T> expression, SqlExpression<T> value) where T : INumber<T> => expression.Arithmetic(7, value);
    /// <summary>Performs native numeric add with explicit SQL NULL propagation.</summary>
    /// <inheritdoc cref="SqlArithmeticExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlExpression<T?> Add<T>(this SqlExpression<T?> expression, T value) where T : struct, INumber<T> => expression.Arithmetic(7, value);
    /// <summary>Performs native numeric add with explicit SQL NULL propagation.</summary>
    /// <inheritdoc cref="SqlArithmeticExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlExpression<T?> Add<T>(this SqlExpression<T?> expression, SqlExpression<T?> value) where T : struct, INumber<T> => expression.Arithmetic(7, value);
    /// <summary>Performs native numeric subtract with explicit SQL NULL propagation.</summary>
    /// <inheritdoc cref="SqlArithmeticExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlExpression<T> Subtract<T>(this SqlExpression<T> expression, T value) where T : INumber<T> => expression.Arithmetic(8, value);
    /// <summary>Performs native numeric subtract with explicit SQL NULL propagation.</summary>
    /// <inheritdoc cref="SqlArithmeticExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlExpression<T> Subtract<T>(this SqlExpression<T> expression, SqlExpression<T> value) where T : INumber<T> => expression.Arithmetic(8, value);
    /// <summary>Performs native numeric subtract with explicit SQL NULL propagation.</summary>
    /// <inheritdoc cref="SqlArithmeticExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlExpression<T?> Subtract<T>(this SqlExpression<T?> expression, T value) where T : struct, INumber<T> => expression.Arithmetic(8, value);
    /// <summary>Performs native numeric subtract with explicit SQL NULL propagation.</summary>
    /// <inheritdoc cref="SqlArithmeticExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlExpression<T?> Subtract<T>(this SqlExpression<T?> expression, SqlExpression<T?> value) where T : struct, INumber<T> => expression.Arithmetic(8, value);
    /// <summary>Performs native numeric multiply with explicit SQL NULL propagation.</summary>
    /// <inheritdoc cref="SqlArithmeticExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlExpression<T> Multiply<T>(this SqlExpression<T> expression, T value) where T : INumber<T> => expression.Arithmetic(9, value);
    /// <summary>Performs native numeric multiply with explicit SQL NULL propagation.</summary>
    /// <inheritdoc cref="SqlArithmeticExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlExpression<T> Multiply<T>(this SqlExpression<T> expression, SqlExpression<T> value) where T : INumber<T> => expression.Arithmetic(9, value);
    /// <summary>Performs native numeric multiply with explicit SQL NULL propagation.</summary>
    /// <inheritdoc cref="SqlArithmeticExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlExpression<T?> Multiply<T>(this SqlExpression<T?> expression, T value) where T : struct, INumber<T> => expression.Arithmetic(9, value);
    /// <summary>Performs native numeric multiply with explicit SQL NULL propagation.</summary>
    /// <inheritdoc cref="SqlArithmeticExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlExpression<T?> Multiply<T>(this SqlExpression<T?> expression, SqlExpression<T?> value) where T : struct, INumber<T> => expression.Arithmetic(9, value);
    /// <summary>Performs native numeric divide with explicit SQL NULL propagation.</summary>
    /// <inheritdoc cref="SqlArithmeticExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlExpression<T> Divide<T>(this SqlExpression<T> expression, T value) where T : INumber<T> => expression.Arithmetic(10, value);
    /// <summary>Performs native numeric divide with explicit SQL NULL propagation.</summary>
    /// <inheritdoc cref="SqlArithmeticExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlExpression<T> Divide<T>(this SqlExpression<T> expression, SqlExpression<T> value) where T : INumber<T> => expression.Arithmetic(10, value);
    /// <summary>Performs native numeric divide with explicit SQL NULL propagation.</summary>
    /// <inheritdoc cref="SqlArithmeticExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlExpression<T?> Divide<T>(this SqlExpression<T?> expression, T value) where T : struct, INumber<T> => expression.Arithmetic(10, value);
    /// <summary>Performs native numeric divide with explicit SQL NULL propagation.</summary>
    /// <inheritdoc cref="SqlArithmeticExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlExpression<T?> Divide<T>(this SqlExpression<T?> expression, SqlExpression<T?> value) where T : struct, INumber<T> => expression.Arithmetic(10, value);
}
/// <summary>Explicit SQL string pattern and allowlisted collation operations; no CLR culture comparison.</summary>
/// <remarks>Literal-pattern methods snapshot and escape the supplied text before native SQL rendering; LikePattern deliberately accepts SQL wildcards. Collate resolves an exact provider allowlist, never arbitrary SQL. NULL behavior follows SQL three-valued logic.</remarks>
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
    /// <inheritdoc cref="SqlStringExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlPredicate ContainsNullableLiteral(this SqlExpression<string?> expression, string value) => Pattern(expression, value, 0, '\\');
    /// <summary>Uses an explicit raw LIKE pattern against nullable SQL text.</summary>
    /// <inheritdoc cref="SqlStringExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="pattern">An explicit SQL LIKE pattern; percent and underscore retain wildcard meaning.</param>
    /// <param name="escape">The explicit single escape character for the SQL LIKE pattern.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlPredicate LikeNullable(this SqlExpression<string?> expression, string pattern, char escape = '\\') => Pattern(expression, pattern, 3, escape);
    /// <summary>Escapes wildcard characters and binds a literal contains pattern.</summary>
    /// <inheritdoc cref="SqlStringExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlPredicate ContainsLiteral(this SqlExpression<string> expression, string value) => Pattern(expression, value, 0, '\\');
    /// <summary>Escapes wildcard characters and binds a literal prefix pattern.</summary>
    /// <inheritdoc cref="SqlStringExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlPredicate StartsWithLiteral(this SqlExpression<string> expression, string value) => Pattern(expression, value, 1, '\\');
    /// <summary>Escapes wildcard characters and binds a literal suffix pattern.</summary>
    /// <inheritdoc cref="SqlStringExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlPredicate EndsWithLiteral(this SqlExpression<string> expression, string value) => Pattern(expression, value, 2, '\\');
    /// <summary>Binds the supplied raw SQL LIKE pattern with an explicit escape character.</summary>
    /// <inheritdoc cref="SqlStringExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="pattern">An explicit SQL LIKE pattern; percent and underscore retain wildcard meaning.</param>
    /// <param name="escape">The explicit single escape character for the SQL LIKE pattern.</param>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlPredicate Like(this SqlExpression<string> expression, string pattern, char escape = '\\') => Pattern(expression, pattern, 3, escape);
    /// <summary>Applies only a collation admitted by the provider's fixed allowlist.</summary>
    /// <inheritdoc cref="SqlStringExtensions"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="collation">The exact collation name in the current provider's allowlist.</param>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public static SqlExpression<string> Collate(this SqlExpression<string> expression, string collation)
    { ArgumentNullException.ThrowIfNull(collation); var owner = expression.Owner; return new(owner, owner.Ref(owner.Clause(16, N(1, 4), U(2, expression.Reference.In(owner.Binding)), Text(3, collation), N(4, '\\'))), expression.Codec); }
}
