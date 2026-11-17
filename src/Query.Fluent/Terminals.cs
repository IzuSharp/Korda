using NativeWeave.Data;
using NativeWeave.Database;
using NativeWeave.Database.Internal;
using NativeWeave.Query.Internal;
using static NativeWeave.Query.QueryContext;

namespace NativeWeave.Query;

/// <summary>A borrowed typed view of a native query's current selection; it owns no second arena.</summary>
/// <typeparam name="T">The statically materialized owned result type.</typeparam>
public readonly struct QuerySelection<T>
{
    private readonly QueryContext? _owner;
    private readonly Projection<T>? _projection;
    internal QueryContext Owner => _owner ?? throw new InvalidOperationException("The default selection is invalid.");
    internal Projection<T> Projection => _projection ?? throw new InvalidOperationException("The default selection is invalid.");
    internal QuerySelection(QueryContext owner, Projection<T> projection) { _owner = owner; _projection = projection; }
    /// <summary>Appends a native set operation using a frozen same-source selection; the other query remains mutable.</summary>
    public QuerySelection<T> Combine(QuerySelection<T> other, SetOperation operation = SetOperation.UnionAll)
    {
        var owner = Owner; var projection = Projection; var self = this;
        return owner.Run(() => { owner.Selection(projection); var imported = owner.Import(other); owner.Clause(9, U(1, imported.In(owner.Binding)), N(2, (uint)operation)); return self; });
    }
    /// <summary>Seals once and returns a bounded owned list after native cursor retirement.</summary>
    public Task<IReadOnlyList<T>> ToListAsync(CancellationToken cancellationToken = default) => Owner.Stream(Projection, 0, cancellationToken, buffered: true).ToListAsync();
    /// <summary>Seals once and returns a bounded owned array after native cursor retirement.</summary>
    public Task<T[]> ToArrayAsync(CancellationToken cancellationToken = default) => Owner.Stream(Projection, 1, cancellationToken, buffered: true).ToArrayAsync();
    /// <summary>Reads at most one business row and retires the cursor; absence throws.</summary>
    public Task<T> FirstAsync(CancellationToken cancellationToken = default) => Owner.Stream(Projection, 2, cancellationToken).FirstAsync();
    /// <summary>Reads at most one business row, returning default(T) on absence.</summary>
    public Task<T?> FirstOrDefaultAsync(CancellationToken cancellationToken = default) => Owner.Stream(Projection, 2, cancellationToken).FirstOrDefaultAsync();
    /// <summary>Reads at most two business rows and requires exactly one, with cleanup on either cardinality failure.</summary>
    public Task<T> SingleAsync(CancellationToken cancellationToken = default) => Owner.Stream(Projection, 3, cancellationToken).SingleAsync();
    /// <summary>Requires at most one business row and returns default(T) when absent.</summary>
    public Task<T?> SingleOrDefaultAsync(CancellationToken cancellationToken = default) => Owner.Stream(Projection, 3, cancellationToken).SingleOrDefaultAsync();
    /// <summary>Seals synchronously and creates a one-shot bounded stream whose first MoveNext dispatches.</summary>
    public IAsyncEnumerable<T> AsAsyncEnumerable(CancellationToken cancellationToken = default) => Owner.Stream(Projection, 0, cancellationToken);
    /// <summary>Seals and transfers an immutable source-bound native plan for independent parameter snapshots.</summary>
    public PreparedQuery<T> Prepare()
    {
        var owner = Owner; var projection = Projection;
        return owner.Run(() =>
        { owner.Selection(projection); var id = owner.Source.RegisterProjection(projection); var bound = projection.Fields.Select(f => owner.Source.BindCodec(f.Descriptor)).ToArray(); var parameters = owner.Parameters.Values.OrderBy(p => p.Id).ToArray(); return new PreparedQuery<T>(owner.Source, owner.Seal(8), projection, id, bound, parameters); });
    }
    /// <summary>Renders a bounded native clone with placeholders and no parameter values; does not execute or seal.</summary>
    public string ToSql() => Owner.Render(Projection);
}
/// <summary>An immutable source-bound native query plan with independent execution values and cursor owners.</summary>
/// <typeparam name="T">The owned static result type.</typeparam>
/// <remarks>Concurrent execution uses separate sessions from the same source. Disposing prevents new dispatch;
/// accepted native operations retain their plan independently and complete safely.</remarks>
public sealed class PreparedQuery<T> : IDisposable
{
    private readonly DataSource _source;
    private readonly DatabaseOwner _plan;
    private readonly Projection<T> _projection;
    private readonly uint _projectionId;
    private readonly uint[] _codecs;
    private readonly ParameterDefinition[] _parameters;
    private int _disposed;
    internal PreparedQuery(DataSource source, DatabaseOwner plan, Projection<T> projection, uint projectionId, uint[] codecs, ParameterDefinition[] parameters)
    { _source = source; _plan = plan; _projection = projection; _projectionId = projectionId; _codecs = codecs; _parameters = parameters; }
    private TypedStream<T> Stream(DataSession session, QueryParameters? parameters, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(session); ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this); session.CheckOpen();
        if (!ReferenceEquals(_source, session.Source)) throw new ArgumentException("A prepared plan requires a session from its exact source.", nameof(session));
        var values = ParameterDefinition.Encode(_source, _parameters, parameters);
        return new(async (rows, cancellation) =>
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return await session.OpenPlanStreamAsync(_plan, values, rows, cancellation).ConfigureAwait(false);
        }, _projection, _source.Binding.SemanticProfile, _projectionId, _codecs, session.StreamingLimits, token);
    }
    /// <summary>Snapshots the exact declared parameter set before await and executes a bounded owned list.</summary>
    public Task<IReadOnlyList<T>> ToListAsync(DataSession session, QueryParameters? parameters = null, CancellationToken cancellationToken = default)
    {
        if (_source.Limits.MaxBufferedRows == 0 || _source.Limits.MaxBufferedBytes == 0) throw new DatabaseException("Buffered result demand is disabled.", DatabaseErrorKind.ResourceLimit);
        return Stream(session, parameters, cancellationToken).ToListAsync();
    }
    /// <summary>Snapshots exact parameter values now and creates one independent lazy native stream.</summary>
    public IAsyncEnumerable<T> AsAsyncEnumerable(DataSession session, QueryParameters? parameters = null, CancellationToken cancellationToken = default) => Stream(session, parameters, cancellationToken);
    /// <summary>Prevents new execution and releases this plan reference without cancelling accepted operations.</summary>
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _plan.Dispose(); }
}
/// <summary>A frozen native CTE with an explicit declared row shape and source identity.</summary>
/// <typeparam name="T">The declared CTE row class.</typeparam>
public sealed class CommonTable<T> : IDisposable where T : class
{
    internal DatabaseOwner Handle { get; }
    internal ParameterDefinition[] Parameters { get; }
    private CommonTable(DatabaseOwner owner, ParameterDefinition[] parameters) { Handle = owner; Parameters = parameters; }
    /// <summary>Clones a native selected body into the declared shape; recursive mode requires a validated anchor/member UNION.</summary>
    public static unsafe CommonTable<T> Create(Table<T> declaredShape, QuerySelection<T> body, bool recursive = false)
    {
        ArgumentNullException.ThrowIfNull(declaredShape); var owner = body.Owner;
        return owner.Run(() =>
        {
            owner.Selection(body.Projection); var model = owner.Source.RegisterModel(declaredShape);
            using var m = model.Borrow(DatabaseOwnerKind.Model); using var q = owner.Binding.Enter(); nint result = 0; var error = BindingChecks.Error();
            HostFailure.Check(owner.Source.Binding, owner.Binding.Calls.nw_db_cte_create_v1(m.Pointer, declaredShape.Identity.Id, q.Pointer, recursive ? 1u : 0u, &result, &error), error);
            return new CommonTable<T>(new(owner.Source.Binding, DatabaseOwnerKind.Cte, result), owner.Parameters.Values.ToArray());
        });
    }
    /// <summary>Prevents new CTE imports and releases this native snapshot reference.</summary>
    public void Dispose() => Handle.Dispose();
}
/// <summary>Finite mutable native window state; each Over/rank call snapshots its current definition.</summary>
public sealed class WindowDefinition
{
    internal QueryContext Owner { get; }
    internal ulong Token { get; }
    internal WindowDefinition(QueryContext owner, ulong token) { Owner = owner; Token = token; }
    /// <summary>Appends a same-query native partition expression.</summary>
    public WindowDefinition PartitionBy<T>(SqlExpression<T> expression)
    { Owner.Same(expression.Owner); Owner.Clause(11, N(1, 1), U(2, Token), U(3, expression.Reference.In(Owner.Binding))); return this; }
    /// <summary>Appends native ordering with explicit direction and SQL NULL placement.</summary>
    public WindowDefinition OrderBy<T>(SqlExpression<T> expression, SortDirection direction = SortDirection.Ascending, NullOrder nullOrder = NullOrder.Last)
    { Owner.Same(expression.Owner); Owner.Clause(11, N(1, 2), U(2, Token), U(3, expression.Reference.In(Owner.Binding)), N(4, (uint)direction), N(5, (uint)nullOrder)); return this; }
    /// <summary>Sets one supported ROWS frame; invalid endpoints reject before publication.</summary>
    public WindowDefinition RowsBetween(FrameBoundary start, FrameBoundary end)
    { Owner.Clause(11, N(1, 3), U(2, Token), N(6, (uint)start), N(7, (uint)end)); return this; }
}
