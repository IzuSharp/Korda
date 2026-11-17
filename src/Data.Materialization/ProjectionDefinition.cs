using NativeWeave.Data;
using NativeWeave.Query.Internal;

namespace NativeWeave.Database.Internal;

/// <summary>Builds one immutable static result shape and its same-query expression bindings.</summary>
/// <remarks>This is the shared implementation behind B13's ProjectionBuilder. It retains only
/// checked native expression references, codecs and a direct managed materializer; it is not a SQL AST.
/// Construction does not execute SQL, load an engine, compile a delegate or instantiate a runtime type.</remarks>
internal sealed class ProjectionDefinition<T>
{
    private readonly QueryBinding _query;
    private readonly Action<Projection<T>, ExpressionReference[]> _publish;
    private readonly object _tokenIdentity = new();
    private readonly List<ResultField> _fields = [];
    private readonly List<ExpressionReference> _expressions = [];
    private readonly HashSet<string> _aliases = new(StringComparer.Ordinal);
    private bool _built;

    /// <summary>Retains the creating query and the facade's immutable projection-registration callback.</summary>
    internal ProjectionDefinition(QueryBinding query, Action<Projection<T>, ExpressionReference[]> publish)
    { ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(publish); _query = query; _publish = publish; }

    /// <summary>Adds one exact static codec, alias and same-query native expression reference.</summary>
    /// <remarks>The token identity is fixed before Build, so a direct constructor can capture tokens.
    /// The eventual native SELECT independently checks actual expression codec/nullability and source identity.</remarks>
    internal ResultColumn<TValue> Add<TValue>(ExpressionReference expression, ValueCodec<TValue> codec, string alias)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _ = expression.In(_query);
        var field = ResultField.Create(alias, codec);
        using var admission = _query.Enter();
        if (_built) throw new InvalidOperationException("The projection builder has been consumed.");
        if (_fields.Count == DbWire.MaxDescriptors) throw new ArgumentException("The projection exceeds its finite field limit.");
        if (_aliases.Contains(alias)) throw new ArgumentException("Projection aliases must be unique.", nameof(alias));
        var token = new ResultColumn<TValue>(_tokenIdentity, _fields.Count, alias, codec);
        _fields.Add(field); _expressions.Add(expression); _aliases.Add(alias);
        return token;
    }

    /// <summary>Freezes ordered fields and native references and retains the caller's direct materializer.</summary>
    /// <remarks>Complete row shape validation remains B01's Projection.Read; B06 owns batch and cursor cleanup
    /// on every constructor, JSON decoder or setter exception. A failed Build publishes no partial projection.
    /// The publication callback retains immutable metadata and must not re-enter query admission or FFI.</remarks>
    internal Projection<T> Build(RowMaterializer<T> materializer)
    {
        ArgumentNullException.ThrowIfNull(materializer);
        using var admission = _query.Enter();
        if (_built) throw new InvalidOperationException("The projection builder has been consumed.");
        if (_fields.Count == 0) throw new InvalidOperationException("A projection requires at least one result field.");
        var projection = new Projection<T>([.. _fields], materializer, tokenIdentity: _tokenIdentity);
        var expressions = _expressions.ToArray();
        _publish(projection, expressions);
        _built = true;
        return projection;
    }
}
