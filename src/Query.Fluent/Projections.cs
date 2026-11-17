using NativeWeave.Data;
using NativeWeave.Database.Internal;
using NativeWeave.Query.Internal;

namespace NativeWeave.Query;

/// <summary>A stack-independent typed root-table proxy that belongs to one live native query.</summary>
/// <typeparam name="TEntity">The declared root entity class.</typeparam>
public readonly struct QueryRow<TEntity> where TEntity : class
{
    private readonly QueryContext? _owner;
    internal QueryRow(QueryContext owner) => _owner = owner;
    private QueryContext Owner => _owner ?? throw new InvalidOperationException("The default query row is invalid.");
    /// <summary>Creates an immediate native expression for an exact root-table column.</summary>
    public SqlExpression<T> Get<T>(Column<TEntity, T> column) { var owner = Owner; return owner.Column(column, owner.Table); }
    /// <summary>Snapshots a typed value and creates native SQL equality.</summary>
    public SqlPredicate Equal<T>(Column<TEntity, T> column, T value) => Get(column).Equal(value);
    /// <summary>Snapshots a typed lower bound and creates a native comparison.</summary>
    public SqlPredicate GreaterThanOrEqual<T>(Column<TEntity, T> column, T value) => Get(column).GreaterThanOrEqual(value);
    /// <summary>Tests the exact root column for SQL NULL.</summary>
    public SqlPredicate IsNull<T>(Column<TEntity, T> column) => Get(column).IsNull();
    /// <summary>Tests the exact root column for SQL non-NULL.</summary>
    public SqlPredicate IsNotNull<T>(Column<TEntity, T> column) => Get(column).IsNotNull();
}
/// <summary>A synchronous native JOIN ON proxy; both sides retain their declared nullability.</summary>
/// <typeparam name="TLeft">The root entity class.</typeparam>
/// <typeparam name="TRight">The explicitly joined entity class.</typeparam>
public readonly struct JoinPredicate<TLeft, TRight> where TLeft : class where TRight : class
{
    private readonly QueryContext? _owner;
    private readonly TableIdentity? _right;
    private readonly AliasReference _alias;
    internal JoinPredicate(QueryContext owner, TableIdentity right, AliasReference alias) { _owner = owner; _right = right; _alias = alias; }
    private QueryContext Owner => _owner ?? throw new InvalidOperationException("The default join predicate is invalid.");
    /// <summary>Reads a root-table column during the synchronous ON callback.</summary>
    public SqlExpression<T> Left<T>(Column<TLeft, T> column) { var owner = Owner; return owner.Column(column, owner.Table); }
    /// <summary>Reads a provisional joined column; the resulting ON token expires on commit or abort.</summary>
    public SqlExpression<T> Right<T>(Column<TRight, T> column) => Owner.Column(column, _right!, _alias);
}
/// <summary>An opaque inner-join alias belonging to exactly one native query and table.</summary>
/// <typeparam name="TEntity">The joined entity class.</typeparam>
public readonly struct JoinedTable<TEntity> where TEntity : class
{
    internal QueryContext? Owner { get; }
    internal TableIdentity? Table { get; }
    internal AliasReference Alias { get; }
    internal JoinedTable(QueryContext owner, TableIdentity table, AliasReference alias) { Owner = owner; Table = table; Alias = alias; }
}
/// <summary>An opaque left-join alias whose projected columns require explicit nullable reads.</summary>
/// <typeparam name="TEntity">The joined entity class.</typeparam>
public readonly struct LeftJoinedTable<TEntity> where TEntity : class
{
    internal QueryContext? Owner { get; }
    internal TableIdentity? Table { get; }
    internal AliasReference Alias { get; }
    internal LeftJoinedTable(QueryContext owner, TableIdentity table, AliasReference alias) { Owner = owner; Table = table; Alias = alias; }
}
/// <summary>A query-scoped static projection builder that freezes typed result tokens and a direct constructor.</summary>
/// <typeparam name="T">The statically known owned result type.</typeparam>
public sealed class ProjectionBuilder<T>
{
    private readonly QueryContext _owner;
    private readonly ProjectionDefinition<T> _definition;
    internal ProjectionBuilder(QueryContext owner) { _owner = owner; _definition = new(owner.Binding, owner.PublishProjection); }
    /// <summary>Adds an exact same-query scalar and unique result alias, returning its projection token.</summary>
    public ResultColumn<TValue> Add<TValue>(SqlExpression<TValue> expression, string alias)
    { _owner.Same(expression.Owner); return _owner.Run(() => _definition.Add(expression.Reference, expression.Codec, alias)); }
    /// <summary>Consumes the builder and freezes a direct AOT-visible materializer; it does not execute SQL.</summary>
    public Projection<T> Build(RowMaterializer<T> materializer) => _owner.Run(() => _definition.Build(materializer));
}
/// <summary>An immutable source-generation parameter identity, distinct from a query-arena expression.</summary>
/// <typeparam name="T">The exact statically encoded parameter type.</typeparam>
public readonly struct QueryParameter<T>
{
    internal ParameterDefinition? Definition { get; }
    internal ValueCodec<T>? Codec { get; }
    internal QueryParameter(ParameterDefinition definition, ValueCodec<T> codec) { Definition = definition; Codec = codec; }
}
/// <summary>A finite explicit parameter set; each Set copies its value and each execution takes a separate snapshot.</summary>
public sealed class QueryParameters
{
    private readonly object _gate = new();
    private readonly Dictionary<uint, ParameterSnapshot> _values = [];
    /// <summary>Creates an empty explicit parameter set.</summary>
    public QueryParameters() { }
    /// <summary>Copies or replaces one typed parameter value; default or conflicting source identities reject.</summary>
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
