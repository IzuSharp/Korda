using Korda.Data;
using Korda.Database;
using Korda.Database.Internal;
using Korda.Query.Internal;
using static Korda.Query.QueryContext;

namespace Korda.Query;

/// <summary>A borrowed typed view of a native query's current selection; it owns no second arena.</summary>
/// <typeparam name="T">The statically materialized owned result type.</typeparam>
/// <remarks>This borrowed view is valid only while its projection remains current in the live query. The first execution or Prepare seals the owner synchronously. Dispose the owning query after enumeration; early exit, failure and cancellation await actual native retirement.</remarks>
public readonly struct QuerySelection<T>
{
    private readonly QueryContext? _owner;
    private readonly Projection<T>? _projection;
    internal QueryContext Owner => _owner ?? throw new InvalidOperationException("The default selection is invalid.");
    internal Projection<T> Projection => _projection ?? throw new InvalidOperationException("The default selection is invalid.");
    internal QuerySelection(QueryContext owner, Projection<T> projection) { _owner = owner; _projection = projection; }
    /// <summary>Appends a native set operation using a frozen same-source selection; the other query remains mutable.</summary>
    /// <inheritdoc cref="QuerySelection{T}"/>
    /// <param name="other">The compatible same-source selection or same-query expression required by this operation.</param>
    /// <param name="operation">The native SQL set operation with exact compatible row shapes.</param>
    /// <returns>The borrowed current-selection view; its owning query retains the native arena.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public QuerySelection<T> Combine(QuerySelection<T> other, SetOperation operation = SetOperation.UnionAll)
    {
        var owner = Owner; var projection = Projection; var self = this;
        return owner.Run(() => { owner.Selection(projection); var imported = owner.Import(other); owner.Clause(9, U(1, imported.In(owner.Binding)), N(2, (uint)operation)); return self; });
    }
    /// <summary>Seals once and returns a bounded owned list after native cursor retirement.</summary>
    /// <inheritdoc cref="QuerySelection{T}"/>
    /// <param name="cancellationToken">Cancellation requests native stop; completion still waits for cursor and operation retirement.</param>
    /// <returns>The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    /// <exception cref="OperationCanceledException">Cancellation requests termination and cleanup.</exception>
    /// <exception cref="TimeoutException">The finite total query deadline expires; cleanup still completes.</exception>
    public Task<IReadOnlyList<T>> ToListAsync(CancellationToken cancellationToken = default) => Owner.Stream(Projection, 0, cancellationToken, buffered: true).ToListAsync();
    /// <summary>Seals once and returns a bounded owned array after native cursor retirement.</summary>
    /// <inheritdoc cref="QuerySelection{T}"/>
    /// <param name="cancellationToken">Cancellation requests native stop; completion still waits for cursor and operation retirement.</param>
    /// <returns>The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    /// <exception cref="OperationCanceledException">Cancellation requests termination and cleanup.</exception>
    /// <exception cref="TimeoutException">The finite total query deadline expires; cleanup still completes.</exception>
    public Task<T[]> ToArrayAsync(CancellationToken cancellationToken = default) => Owner.Stream(Projection, 1, cancellationToken, buffered: true).ToArrayAsync();
    /// <summary>Reads at most one business row and retires the cursor; absence throws.</summary>
    /// <inheritdoc cref="QuerySelection{T}"/>
    /// <param name="cancellationToken">Cancellation requests native stop; completion still waits for cursor and operation retirement.</param>
    /// <returns>The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    /// <exception cref="OperationCanceledException">Cancellation requests termination and cleanup.</exception>
    /// <exception cref="TimeoutException">The finite total query deadline expires; cleanup still completes.</exception>
    public Task<T> FirstAsync(CancellationToken cancellationToken = default) => Owner.Stream(Projection, 2, cancellationToken).FirstAsync();
    /// <summary>Reads at most one business row, returning default(T) on absence.</summary>
    /// <inheritdoc cref="QuerySelection{T}"/>
    /// <param name="cancellationToken">Cancellation requests native stop; completion still waits for cursor and operation retirement.</param>
    /// <returns>The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    /// <exception cref="OperationCanceledException">Cancellation requests termination and cleanup.</exception>
    /// <exception cref="TimeoutException">The finite total query deadline expires; cleanup still completes.</exception>
    public Task<T?> FirstOrDefaultAsync(CancellationToken cancellationToken = default) => Owner.Stream(Projection, 2, cancellationToken).FirstOrDefaultAsync();
    /// <summary>Reads at most two business rows and requires exactly one, with cleanup on either cardinality failure.</summary>
    /// <inheritdoc cref="QuerySelection{T}"/>
    /// <param name="cancellationToken">Cancellation requests native stop; completion still waits for cursor and operation retirement.</param>
    /// <returns>The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    /// <exception cref="OperationCanceledException">Cancellation requests termination and cleanup.</exception>
    /// <exception cref="TimeoutException">The finite total query deadline expires; cleanup still completes.</exception>
    public Task<T> SingleAsync(CancellationToken cancellationToken = default) => Owner.Stream(Projection, 3, cancellationToken).SingleAsync();
    /// <summary>Requires at most one business row and returns default(T) when absent.</summary>
    /// <inheritdoc cref="QuerySelection{T}"/>
    /// <param name="cancellationToken">Cancellation requests native stop; completion still waits for cursor and operation retirement.</param>
    /// <returns>The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    /// <exception cref="OperationCanceledException">Cancellation requests termination and cleanup.</exception>
    /// <exception cref="TimeoutException">The finite total query deadline expires; cleanup still completes.</exception>
    public Task<T?> SingleOrDefaultAsync(CancellationToken cancellationToken = default) => Owner.Stream(Projection, 3, cancellationToken).SingleOrDefaultAsync();
    /// <summary>Seals synchronously and creates a one-shot bounded stream whose first MoveNext dispatches.</summary>
    /// <inheritdoc cref="QuerySelection{T}"/>
    /// <param name="cancellationToken">Cancellation requests native stop; completion still waits for cursor and operation retirement.</param>
    /// <returns>A one-shot lazy stream of owned results. Dispose its enumerator to request stop and await retirement on early exit.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    /// <exception cref="OperationCanceledException">Cancellation requests termination and cleanup.</exception>
    /// <exception cref="TimeoutException">The finite total query deadline expires; cleanup still completes.</exception>
    public IAsyncEnumerable<T> AsAsyncEnumerable(CancellationToken cancellationToken = default) => Owner.Stream(Projection, 0, cancellationToken);
    /// <summary>Seals and transfers an immutable source-bound native plan for independent parameter snapshots.</summary>
    /// <inheritdoc cref="QuerySelection{T}"/>
    /// <returns>An independently disposable immutable native plan bound to this source.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public PreparedQuery<T> Prepare()
    {
        var owner = Owner; var projection = Projection;
        return owner.Run(() =>
        {
            owner.Selection(projection); var id = owner.Source.RegisterProjection(projection);
            var bound = projection.Fields.Select(f => owner.Source.BindCodec(f.Descriptor)).ToArray();
            // Finish managed layout construction before acquiring the native plan.
            var parameters = new PreparedParameterLayout(owner.Source, owner.Parameters.Values);
            return new PreparedQuery<T>(owner.Source, owner.Seal(8), projection, id, bound, parameters);
        });
    }
    /// <summary>Renders a bounded native clone with placeholders and no parameter values; does not execute or seal.</summary>
    /// <inheritdoc cref="QuerySelection{T}"/>
    /// <returns>A bounded native SQL rendering with placeholders and no bound values.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
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
    private PreparedParameterLayout? _parameters;
    private int _disposed;
    internal PreparedQuery(DataSource source, DatabaseOwner plan, Projection<T> projection, uint projectionId, uint[] codecs, PreparedParameterLayout parameters)
    { _source = source; _plan = plan; _projection = projection; _projectionId = projectionId; _codecs = codecs; _parameters = parameters; }
    private TypedStream<T> Stream(DataSession session, QueryParameters? parameters, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(session); ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this); session.CheckOpen();
        if (!ReferenceEquals(_source, session.Source)) throw new ArgumentException("A prepared plan requires a session from its exact source.", nameof(session));
        var layout = Volatile.Read(ref _parameters);
        ObjectDisposedException.ThrowIf(layout is null, this);
        var values = layout.Encode(_source, parameters);
        return new(async (rows, cancellation) =>
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return await session.OpenPlanStreamAsync(_plan, values, rows, cancellation).ConfigureAwait(false);
        }, _projection, _source.Binding.SemanticProfile, _projectionId, _codecs, session.StreamingLimits, token);
    }
    /// <summary>Snapshots the exact declared parameter set before await and executes a bounded owned list.</summary>
    /// <inheritdoc cref="PreparedQuery{T}"/>
    /// <param name="session">An open execution session belonging to this plan's exact source.</param>
    /// <param name="parameters">The complete declared parameter set; null denotes an empty set and is valid only for a parameterless plan.</param>
    /// <param name="cancellationToken">Cancellation requests native stop; completion still waits for cursor and operation retirement.</param>
    /// <returns>The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    /// <exception cref="OperationCanceledException">Cancellation requests termination and cleanup.</exception>
    /// <exception cref="TimeoutException">The finite total query deadline expires; cleanup still completes.</exception>
    public Task<IReadOnlyList<T>> ToListAsync(DataSession session, QueryParameters? parameters = null, CancellationToken cancellationToken = default)
    {
        if (_source.Limits.MaxBufferedRows == 0 || _source.Limits.MaxBufferedBytes == 0) throw new DatabaseException("Buffered result demand is disabled.", DatabaseErrorKind.ResourceLimit);
        return Stream(session, parameters, cancellationToken).ToListAsync();
    }
    /// <summary>Snapshots exact parameter values now and creates one independent lazy native stream.</summary>
    /// <inheritdoc cref="PreparedQuery{T}"/>
    /// <param name="session">An open execution session belonging to this plan's exact source.</param>
    /// <param name="parameters">The complete declared parameter set; null denotes an empty set and is valid only for a parameterless plan.</param>
    /// <param name="cancellationToken">Cancellation requests native stop; completion still waits for cursor and operation retirement.</param>
    /// <returns>A one-shot lazy stream of owned results. Dispose its enumerator to request stop and await retirement on early exit.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    /// <exception cref="OperationCanceledException">Cancellation requests termination and cleanup.</exception>
    /// <exception cref="TimeoutException">The finite total query deadline expires; cleanup still completes.</exception>
    public IAsyncEnumerable<T> AsAsyncEnumerable(DataSession session, QueryParameters? parameters = null, CancellationToken cancellationToken = default) => Stream(session, parameters, cancellationToken);
    /// <summary>Prevents new execution and releases this plan reference without cancelling accepted operations.</summary>
    /// <inheritdoc cref="PreparedQuery{T}"/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // In-flight construction holds its own immutable layout reference. New
        // dispatch is already closed, and the disposed wrapper retains no cache.
        Interlocked.Exchange(ref _parameters, null);
        _plan.Dispose();
    }
}
/// <summary>A frozen native CTE with an explicit declared row shape and source identity.</summary>
/// <typeparam name="T">The declared CTE row class.</typeparam>
/// <remarks>The snapshot is bound to one source and a declared static row shape. With imports a native retained reference. Disposal prevents new imports and does not mutate previously imported snapshots. Recursive creation requires one validated anchor/member self-reference.</remarks>
public sealed class CommonTable<T> : IDisposable where T : class
{
    internal DatabaseOwner Handle { get; }
    internal ParameterDefinition[] Parameters { get; }
    private CommonTable(DatabaseOwner owner, ParameterDefinition[] parameters) { Handle = owner; Parameters = parameters; }
    /// <summary>Clones a native selected body into the declared shape; recursive mode requires a validated anchor/member UNION.</summary>
    /// <inheritdoc cref="CommonTable{T}"/>
    /// <param name="declaredShape">The static CTE row descriptor with an explicit logical table name and compatible fields.</param>
    /// <param name="body">The selected native CTE body to freeze; later body mutations do not affect this snapshot.</param>
    /// <param name="recursive">Whether the validated body is a recursive anchor/member UNION with one declared self-reference.</param>
    /// <returns>The typed owner or proxy described above, retaining its original source/query association.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
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
    /// <inheritdoc cref="CommonTable{T}"/>
    public void Dispose() => Handle.Dispose();
}
/// <summary>Finite mutable native window state; each Over/rank call snapshots its current definition.</summary>
/// <remarks>The owning query bounds lifetime and concurrent access. Rank, RowNumber and Over snapshot native window state immediately; subsequent mutation cannot change existing expressions. Defaults and limits follow the provider capability contract.</remarks>
public sealed class WindowDefinition
{
    internal QueryContext Owner { get; }
    internal ulong Token { get; }
    internal WindowDefinition(QueryContext owner, ulong token) { Owner = owner; Token = token; }
    /// <summary>Appends a same-query native partition expression.</summary>
    /// <inheritdoc cref="WindowDefinition"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>The typed owner or proxy described above, retaining its original source/query association.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public WindowDefinition PartitionBy<T>(SqlExpression<T> expression)
    { Owner.Same(expression.Owner); Owner.Clause(11, N(1, 1), U(2, Token), U(3, expression.Reference.In(Owner.Binding))); return this; }
    /// <summary>Appends native ordering with explicit direction and SQL NULL placement.</summary>
    /// <inheritdoc cref="WindowDefinition"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="direction">Explicit ascending or descending SQL order.</param>
    /// <param name="nullOrder">Explicit placement of SQL NULL values in this ordering key.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>The typed owner or proxy described above, retaining its original source/query association.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public WindowDefinition OrderBy<T>(SqlExpression<T> expression, SortDirection direction = SortDirection.Ascending, NullOrder nullOrder = NullOrder.Last)
    { Owner.Same(expression.Owner); Owner.Clause(11, N(1, 2), U(2, Token), U(3, expression.Reference.In(Owner.Binding)), N(4, (uint)direction), N(5, (uint)nullOrder)); return this; }
    /// <summary>Sets one supported ROWS frame; invalid endpoints reject before publication.</summary>
    /// <inheritdoc cref="WindowDefinition"/>
    /// <param name="start">The supported starting ROWS frame endpoint.</param>
    /// <param name="end">The supported ending ROWS frame endpoint.</param>
    /// <returns>The typed owner or proxy described above, retaining its original source/query association.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="Korda.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public WindowDefinition RowsBetween(FrameBoundary start, FrameBoundary end)
    { Owner.Clause(11, N(1, 3), U(2, Token), N(6, (uint)start), N(7, (uint)end)); return this; }
}
