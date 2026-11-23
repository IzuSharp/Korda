using Korda.Data;
using Korda.Database.Internal;
using Korda.Query.Internal;

namespace Korda.Query;

/// <summary>A stack-independent typed root-table proxy that belongs to one live native query.</summary>
/// <typeparam name="TEntity">The declared root entity class.</typeparam>
/// <remarks>This borrowed proxy retains its query association. Default, foreign, closed or sealed owners reject. Each operation forwards immediately to a finite native arena; no managed SQL tree or captured callback is retained.</remarks>
public readonly struct QueryRow<TEntity> where TEntity : class
{
    private readonly QueryContext? _owner;
    internal QueryRow(QueryContext owner) => _owner = owner;
    private QueryContext Owner => _owner ?? throw new InvalidOperationException("The default query row is invalid.");
    /// <summary>Creates an immediate native expression for an exact root-table column.</summary>
    /// <inheritdoc cref="QueryRow{TEntity}"/>
    /// <param name="column">The strongly typed column from the declared table; foreign table identities reject.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlExpression<T> Get<T>(Column<TEntity, T> column) { var owner = Owner; return owner.Column(column, owner.Table); }
    /// <summary>Snapshots a typed value and creates native SQL equality.</summary>
    /// <inheritdoc cref="QueryRow{TEntity}"/>
    /// <param name="column">The strongly typed column from the declared table; foreign table identities reject.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate Equal<T>(Column<TEntity, T> column, T value) => Get(column).Equal(value);
    /// <summary>Snapshots a typed lower bound and creates a native comparison.</summary>
    /// <inheritdoc cref="QueryRow{TEntity}"/>
    /// <param name="column">The strongly typed column from the declared table; foreign table identities reject.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate GreaterThanOrEqual<T>(Column<TEntity, T> column, T value) => Get(column).GreaterThanOrEqual(value);
    /// <summary>Tests the exact root column for SQL NULL.</summary>
    /// <inheritdoc cref="QueryRow{TEntity}"/>
    /// <param name="column">The strongly typed column from the declared table; foreign table identities reject.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate IsNull<T>(Column<TEntity, T> column) => Get(column).IsNull();
    /// <summary>Tests the exact root column for SQL non-NULL.</summary>
    /// <inheritdoc cref="QueryRow{TEntity}"/>
    /// <param name="column">The strongly typed column from the declared table; foreign table identities reject.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate IsNotNull<T>(Column<TEntity, T> column) => Get(column).IsNotNull();
}
/// <summary>A synchronous native JOIN ON proxy; both sides retain their declared nullability.</summary>
/// <typeparam name="TLeft">The root entity class.</typeparam>
/// <typeparam name="TRight">The explicitly joined entity class.</typeparam>
/// <remarks>Use only inside the synchronous JOIN callback. Provisional right-side expressions are invalidated on commit or abort. Values and nullability stay exact; explicitly lift a non-null parent key to compare a nullable foreign key.</remarks>
public readonly struct JoinPredicate<TLeft, TRight> where TLeft : class where TRight : class
{
    private readonly QueryContext? _owner;
    private readonly TableIdentity? _right;
    private readonly AliasReference _alias;
    private readonly JoinCallbackScope? _scope;
    internal JoinPredicate(QueryContext owner, TableIdentity right, AliasReference alias, JoinCallbackScope scope) { _owner = owner; _right = right; _alias = alias; _scope = scope; }
    private QueryContext Owner
    {
        get
        {
            var owner = _owner ?? throw new InvalidOperationException("The default join predicate is invalid.");
            _scope!.Check(); return owner;
        }
    }
    /// <summary>Reads a root-table column during the synchronous ON callback.</summary>
    /// <inheritdoc cref="JoinPredicate{TLeft,TRight}"/>
    /// <param name="column">The strongly typed column from the declared table; foreign table identities reject.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlExpression<T> Left<T>(Column<TLeft, T> column) { var owner = Owner; return owner.Column(column, owner.Table); }
    /// <summary>Reads a provisional joined column; the resulting ON token expires on commit or abort.</summary>
    /// <inheritdoc cref="JoinPredicate{TLeft,TRight}"/>
    /// <param name="column">The strongly typed column from the declared table; foreign table identities reject.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlExpression<T> Right<T>(Column<TRight, T> column) => Owner.Column(column, _right!, _alias);
}
internal sealed class JoinCallbackScope
{
    private int _closed;
    internal void Check() { if (Volatile.Read(ref _closed) != 0) throw new InvalidOperationException("The JOIN callback has completed."); }
    internal void Close() => Volatile.Write(ref _closed, 1);
}
/// <summary>An opaque inner-join alias belonging to exactly one native query and table.</summary>
/// <typeparam name="TEntity">The joined entity class.</typeparam>
/// <remarks>The alias is a borrowed token, not an independently disposable query. Default aliases and aliases from another query reject, even if the CLR entity type matches.</remarks>
public readonly struct JoinedTable<TEntity> where TEntity : class
{
    internal QueryContext? Owner { get; }
    internal TableIdentity? Table { get; }
    internal AliasReference Alias { get; }
    internal JoinedTable(QueryContext owner, TableIdentity table, AliasReference alias) { Owner = owner; Table = table; Alias = alias; }
}
/// <summary>An opaque left-join alias whose projected columns require explicit nullable reads.</summary>
/// <typeparam name="TEntity">The joined entity class.</typeparam>
/// <remarks>Use NullableValue or NullableReference to project its columns and HasMatch to distinguish absence. No default scalar sentinel or implicit object graph is introduced. Its query owns the alias lifetime.</remarks>
public readonly struct LeftJoinedTable<TEntity> where TEntity : class
{
    internal QueryContext? Owner { get; }
    internal TableIdentity? Table { get; }
    internal AliasReference Alias { get; }
    internal LeftJoinedTable(QueryContext owner, TableIdentity table, AliasReference alias) { Owner = owner; Table = table; Alias = alias; }
}
/// <summary>A query-scoped static projection builder that freezes typed result tokens and a direct constructor.</summary>
/// <typeparam name="T">The statically known owned result type.</typeparam>
/// <remarks>Fields must come from its exact live query. Build freezes field order, unique names, codec/nullability and the direct materializer; later Add or Build rejects. RowReader validates the full shape before the callback, and escaping DTO values own their data.</remarks>
public sealed class ProjectionBuilder<T>
{
    private readonly QueryContext _owner;
    private readonly ProjectionDefinition<T> _definition;
    internal ProjectionBuilder(QueryContext owner) { _owner = owner; _definition = new(owner.Binding, owner.PublishProjection); }
    /// <summary>Adds an exact same-query scalar and unique result alias, returning its projection token.</summary>
    /// <inheritdoc cref="ProjectionBuilder{T}"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="alias">The bounded exact SQL/result alias; duplicate or invalid names reject.</param>
    /// <typeparam name="TValue">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>The typed result token associated with this exact projection builder.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public ResultColumn<TValue> Add<TValue>(SqlExpression<TValue> expression, string alias)
    { _owner.Same(expression.Owner); return _owner.Run(() => _definition.Add(expression.Reference, expression.Codec, alias)); }
    /// <summary>Adds a same-query scalar after checking its exact declared DTO codec policy.</summary>
    /// <inheritdoc cref="ProjectionBuilder{T}"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="alias">The unique exact SQL/result alias.</param>
    /// <param name="codec">The expected logical codec, configuration and SQL nullability used by the DTO reader.</param>
    /// <typeparam name="TValue">The statically known property type.</typeparam>
    /// <returns>The result token associated with this projection builder and explicit reader policy.</returns>
    /// <exception cref="ArgumentNullException">The expected codec is null.</exception>
    /// <exception cref="ArgumentException">The expression belongs elsewhere or differs in codec, configuration or nullability.</exception>
    /// <exception cref="InvalidOperationException">The owner or builder is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The native query owner has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability or finite resource limits reject the operation.</exception>
    public ResultColumn<TValue> Add<TValue>(SqlExpression<TValue> expression, string alias, ValueCodec<TValue> codec)
    {
        ArgumentNullException.ThrowIfNull(codec); _owner.Same(expression.Owner);
        if (expression.Codec.Descriptor != codec.Descriptor) throw new ArgumentException("The expression differs from the projection's declared codec policy.", nameof(expression));
        return _owner.Run(() => _definition.Add(expression.Reference, codec, alias));
    }
    /// <summary>Consumes the builder and freezes a direct AOT-visible materializer; it does not execute SQL.</summary>
    /// <inheritdoc cref="ProjectionBuilder{T}"/>
    /// <param name="materializer">The statically compiled direct constructor or field reader; runtime reflection is not used.</param>
    /// <returns>An immutable projection with typed field tokens and the static direct materializer.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public Projection<T> Build(RowMaterializer<T> materializer) => _owner.Run(() => _definition.Build(materializer));
}
/// <summary>An immutable source-generation parameter identity, distinct from a query-arena expression.</summary>
/// <typeparam name="T">The exact statically encoded parameter type.</typeparam>
/// <remarks>Parameter IDs are monotonically allocated within a source generation, independently of query expression tokens. A copied parameter remains an identity; it owns no native arena or value, and only declared/imported plans may execute it.</remarks>
public readonly struct QueryParameter<T>
{
    internal ParameterDefinition? Definition { get; }
    internal ValueCodec<T>? Codec { get; }
    internal QueryParameter(ParameterDefinition definition, ValueCodec<T> codec) { Definition = definition; Codec = codec; }
}
/// <summary>A finite explicit parameter set; each Set copies its value and each execution takes a separate snapshot.</summary>
/// <remarks>Set snapshots each encoded value before returning. A terminal copies the complete finite set before its first await; later Set calls do not change admitted execution. Missing, extra, foreign or codec-incompatible identities reject before native dispatch. No global parameter dictionary exists.</remarks>
public sealed class QueryParameters
{
    private readonly object _gate = new();
    private readonly Dictionary<uint, ParameterSnapshot> _values = [];
    /// <summary>Creates an empty explicit parameter set.</summary>
    /// <inheritdoc cref="QueryParameters"/>
    public QueryParameters() { }
    /// <summary>Copies or replaces one typed parameter value; default or conflicting source identities reject.</summary>
    /// <inheritdoc cref="QueryParameters"/>
    /// <param name="parameter">The declared source-generation parameter identity with its exact typed codec.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>The typed owner or proxy described above, retaining its original source/query association.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public QueryParameters Set<T>(QueryParameter<T> parameter, T value)
    {
        var definition = parameter.Definition ?? throw new ArgumentException("The default parameter is invalid.", nameof(parameter));
        var bytes = parameter.Codec!.Encode(value);
        lock (_gate)
        {
            if (_values.Count != 0 && !ReferenceEquals(_values.First().Value.Parameter.Source, definition.Source)) throw new ArgumentException("Parameters belong to different sources.", nameof(parameter));
            if (!_values.ContainsKey(definition.Id) && _values.Count == definition.Source.Limits.MaxParameters) throw new DatabaseException("Parameter count limit exceeded.", DatabaseErrorKind.ResourceLimit);
            _values[definition.Id] = new(definition, bytes);
        }
        return this;
    }
    internal ParameterSnapshot[] Snapshot() { lock (_gate) return _values.Values.OrderBy(v => v.Parameter.Id).ToArray(); }
}
