using NativeWeave.Data;
using NativeWeave.Database;
using NativeWeave.Query.Internal;
using static NativeWeave.Query.QueryContext;

namespace NativeWeave.Query;

/// <summary>One mutable, source-bound native query arena with a strongly typed application facade.</summary>
/// <typeparam name="TEntity">The explicitly described root entity class.</typeparam>
/// <remarks>Composition is synchronous and fail-fast on concurrent writers. The first terminal seals the query;
/// use Clone before a second execution. Keep this owner alive across streaming and dispose after use.</remarks>
public sealed class Query<TEntity> : IDisposable where TEntity : class
{
    internal QueryContext Context { get; }
    private readonly Table<TEntity> _table;
    private readonly HashSet<uint> _orderColumns = [];
    private int _orderCount;
    internal Query(QueryContext context, Table<TEntity> table) { Context = context; _table = table; }
    /// <summary>Gets the live typed root-column proxy.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    public QueryRow<TEntity> Row => Context.Run(() => new QueryRow<TEntity>(Context));
    /// <summary>Appends a same-query native predicate with AND.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="predicate">The compatible native condition or synchronous typed callback; callbacks are invoked at this call.</param>
    /// <returns>This mutable query for further synchronous composition before its first terminal.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public Query<TEntity> Where(SqlPredicate predicate) { Context.Same(predicate.Owner); Context.Clause(1, U(1, predicate.Reference.In(Context.Binding))); return this; }
    /// <summary>Invokes the typed callback synchronously once, then attaches its complete native predicate.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="predicate">The compatible native condition or synchronous typed callback; callbacks are invoked at this call.</param>
    /// <returns>This mutable query for further synchronous composition before its first terminal.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public Query<TEntity> Where(Func<QueryRow<TEntity>, SqlPredicate> predicate)
    { ArgumentNullException.ThrowIfNull(predicate); return Context.Run(() => Where(predicate(new(Context)))); }
    /// <summary>Invokes and attaches the callback only when the condition is true; false binds no captured values.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="condition">The CLR branch condition, or same-query native CASE predicate required by this overload.</param>
    /// <param name="predicate">The compatible native condition or synchronous typed callback; callbacks are invoked at this call.</param>
    /// <returns>This mutable query for further synchronous composition before its first terminal.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public Query<TEntity> WhereIf(bool condition, Func<QueryRow<TEntity>, SqlPredicate> predicate)
    { ArgumentNullException.ThrowIfNull(predicate); return Context.Run(() => condition ? Where(predicate(new(Context))) : this); }
    /// <summary>Snapshots the value and appends root-column equality without a predicate delegate.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="column">The strongly typed column from the declared table; foreign table identities reject.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>This mutable query for further synchronous composition before its first terminal.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public Query<TEntity> WhereEqual<T>(Column<TEntity, T> column, T value) => Context.Run(() => Where(Column(column).Equal(value)));
    /// <summary>Snapshots the value and appends an inclusive native lower bound.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="column">The strongly typed column from the declared table; foreign table identities reject.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>This mutable query for further synchronous composition before its first terminal.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public Query<TEntity> WhereGreaterThanOrEqual<T>(Column<TEntity, T> column, T value) => Context.Run(() => Where(Column(column).GreaterThanOrEqual(value)));
    /// <summary>Appends an explicit root-column IS NULL predicate.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="column">The strongly typed column from the declared table; foreign table identities reject.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>This mutable query for further synchronous composition before its first terminal.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public Query<TEntity> WhereIsNull<T>(Column<TEntity, T> column) => Context.Run(() => Where(Column(column).IsNull()));
    /// <summary>Appends an explicit root-column IS NOT NULL predicate.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="column">The strongly typed column from the declared table; foreign table identities reject.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>This mutable query for further synchronous composition before its first terminal.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public Query<TEntity> WhereIsNotNull<T>(Column<TEntity, T> column) => Context.Run(() => Where(Column(column).IsNotNull()));
    /// <summary>Creates an immediate root-column native scalar token.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="column">The strongly typed column from the declared table; foreign table identities reject.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlExpression<T> Column<T>(Column<TEntity, T> column) => Context.Column(column, _table.Identity);
    /// <summary>Creates a scalar from an exact same-query inner-join alias and table.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="table">The exact static table descriptor or same-query joined alias required by this overload.</param>
    /// <param name="column">The strongly typed column from the declared table; foreign table identities reject.</param>
    /// <typeparam name="TJoin">The statically known entity class with explicit table metadata.</typeparam>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlExpression<T> Column<TJoin, T>(JoinedTable<TJoin> table, Column<TJoin, T> column) where TJoin : class
    { if (table.Owner is null) throw new ArgumentException("The default joined alias is invalid.", nameof(table)); Context.Same(table.Owner); return Context.Column(column, table.Table!, table.Alias); }
    /// <summary>Reads a non-null value column through a LEFT JOIN as an explicitly nullable scalar.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="table">The exact static table descriptor or same-query joined alias required by this overload.</param>
    /// <param name="column">The strongly typed column from the declared table; foreign table identities reject.</param>
    /// <typeparam name="TJoin">The statically known entity class with explicit table metadata.</typeparam>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlExpression<T?> NullableValue<TJoin, T>(LeftJoinedTable<TJoin> table, Column<TJoin, T> column) where TJoin : class where T : struct
    { CheckLeft(table); return Context.Run(() => { Context.CheckColumn(column, table.Table!); return new SqlExpression<T?>(Context, Context.Binding.Column(column.Id, table.Alias), Codecs.Nullable(column.Codec)); }); }
    /// <summary>Preserves an already nullable value column through a LEFT JOIN.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="table">The exact static table descriptor or same-query joined alias required by this overload.</param>
    /// <param name="column">The strongly typed column from the declared table; foreign table identities reject.</param>
    /// <typeparam name="TJoin">The statically known entity class with explicit table metadata.</typeparam>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlExpression<T?> NullableValue<TJoin, T>(LeftJoinedTable<TJoin> table, Column<TJoin, T?> column) where TJoin : class where T : struct
    { CheckLeft(table); return Context.Column(column, table.Table!, table.Alias); }
    /// <summary>Reads a reference column through a LEFT JOIN with explicit nullable result metadata.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="table">The exact static table descriptor or same-query joined alias required by this overload.</param>
    /// <param name="column">The strongly typed column from the declared table; foreign table identities reject.</param>
    /// <typeparam name="TJoin">The statically known entity class with explicit table metadata.</typeparam>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlExpression<T?> NullableReference<TJoin, T>(LeftJoinedTable<TJoin> table, Column<TJoin, T> column) where TJoin : class where T : class?
    {
        CheckLeft(table); return Context.Run(() =>
        {
            Context.CheckColumn(column, table.Table!); var codec = new ValueCodec<T?>(column.Codec.Descriptor with { Nullable = true }, v => column.Codec.Encode(v!)!, b => column.Codec.DecodeScalar(b));
            return new SqlExpression<T?>(Context, Context.Binding.Column(column.Id, table.Alias), codec);
        });
    }
    private void CheckLeft<TJoin>(LeftJoinedTable<TJoin> table) where TJoin : class
    { if (table.Owner is null) throw new ArgumentException("The default joined alias is invalid.", nameof(table)); Context.Same(table.Owner); }
    /// <summary>Tests the native explicit LEFT JOIN match marker, independently from nullable child data.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="table">The exact static table descriptor or same-query joined alias required by this overload.</param>
    /// <typeparam name="TJoin">The statically known entity class with explicit table metadata.</typeparam>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate HasMatch<TJoin>(LeftJoinedTable<TJoin> table) where TJoin : class
    { CheckLeft(table); return new(Context, Context.Ref(Context.Clause(20, U(1, table.Alias.In(Context.Binding))))); }
    /// <summary>Reserves, builds and atomically commits an explicit native INNER JOIN ON clause.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="table">The exact static table descriptor or same-query joined alias required by this overload.</param>
    /// <param name="alias">The bounded exact SQL/result alias; duplicate or invalid names reject.</param>
    /// <param name="on">The synchronous typed JOIN ON callback; it is neither stored nor invoked during row enumeration.</param>
    /// <typeparam name="TJoin">The statically known entity class with explicit table metadata.</typeparam>
    /// <returns>The typed owner or proxy described above, retaining its original source/query association.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public JoinedTable<TJoin> InnerJoin<TJoin>(Table<TJoin> table, string alias, Func<JoinPredicate<TEntity, TJoin>, SqlPredicate> on) where TJoin : class
    { var token = Join(table, alias, on, false); return new(Context, table.Identity, token); }
    /// <summary>Commits a native LEFT JOIN and returns a nullable-only projected alias.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="table">The exact static table descriptor or same-query joined alias required by this overload.</param>
    /// <param name="alias">The bounded exact SQL/result alias; duplicate or invalid names reject.</param>
    /// <param name="on">The synchronous typed JOIN ON callback; it is neither stored nor invoked during row enumeration.</param>
    /// <typeparam name="TJoin">The statically known entity class with explicit table metadata.</typeparam>
    /// <returns>The typed owner or proxy described above, retaining its original source/query association.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public LeftJoinedTable<TJoin> LeftJoin<TJoin>(Table<TJoin> table, string alias, Func<JoinPredicate<TEntity, TJoin>, SqlPredicate> on) where TJoin : class
    { var token = Join(table, alias, on, true); return new(Context, table.Identity, token); }
    private AliasReference Join<TJoin>(Table<TJoin> table, string alias, Func<JoinPredicate<TEntity, TJoin>, SqlPredicate> on, bool left) where TJoin : class
    {
        ArgumentNullException.ThrowIfNull(table); ArgumentNullException.ThrowIfNull(on); ArgumentNullException.ThrowIfNull(alias);
        return Context.Run(() =>
        {
            var model = Context.Source.RegisterModel(table); var imported = Context.Binding.Import(model, 1);
            var reserved = Context.Clause(18, U(1, imported.In(Context.Binding)), N(2, table.Identity.Id), Text(3, alias), N(4, left ? 1u : 0u));
            var scope = new JoinCallbackScope();
            try
            {
                var predicate = on(new(Context, table.Identity, new(Context.Binding, reserved), scope)); Context.Same(predicate.Owner);
                Context.Clause(7, U(1, reserved), U(2, predicate.Reference.In(Context.Binding)));
                return new AliasReference(Context.Binding, reserved);
            }
            catch { try { Context.Clause(19, U(1, reserved)); } catch (ObjectDisposedException) { } throw; }
            finally { scope.Close(); }
        });
    }
    /// <summary>Replaces native ordering with the exact root column and explicit NULL policy.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="column">The strongly typed column from the declared table; foreign table identities reject.</param>
    /// <param name="direction">Explicit ascending or descending SQL order.</param>
    /// <param name="nullOrder">Explicit placement of SQL NULL values in this ordering key.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>This mutable query for further synchronous composition before its first terminal.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public Query<TEntity> OrderBy<T>(Column<TEntity, T> column, SortDirection direction = SortDirection.Ascending, NullOrder nullOrder = NullOrder.Last) => OrderBy(Column(column), direction, nullOrder);
    /// <summary>Replaces native ordering with the same-query expression and explicit NULL policy.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="direction">Explicit ascending or descending SQL order.</param>
    /// <param name="nullOrder">Explicit placement of SQL NULL values in this ordering key.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>This mutable query for further synchronous composition before its first terminal.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public Query<TEntity> OrderBy<T>(SqlExpression<T> expression, SortDirection direction = SortDirection.Ascending, NullOrder nullOrder = NullOrder.Last) => Order(expression, direction, nullOrder, true);
    /// <summary>Appends a root-column order after an existing order.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="column">The strongly typed column from the declared table; foreign table identities reject.</param>
    /// <param name="direction">Explicit ascending or descending SQL order.</param>
    /// <param name="nullOrder">Explicit placement of SQL NULL values in this ordering key.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>This mutable query for further synchronous composition before its first terminal.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public Query<TEntity> ThenBy<T>(Column<TEntity, T> column, SortDirection direction = SortDirection.Ascending, NullOrder nullOrder = NullOrder.Last) => ThenBy(Column(column), direction, nullOrder);
    /// <summary>Appends a same-query expression after an existing order.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <param name="direction">Explicit ascending or descending SQL order.</param>
    /// <param name="nullOrder">Explicit placement of SQL NULL values in this ordering key.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>This mutable query for further synchronous composition before its first terminal.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public Query<TEntity> ThenBy<T>(SqlExpression<T> expression, SortDirection direction = SortDirection.Ascending, NullOrder nullOrder = NullOrder.Last) => Order(expression, direction, nullOrder, false);
    private Query<TEntity> Order<T>(SqlExpression<T> expression, SortDirection direction, NullOrder nullOrder, bool replace) => Context.Run(() =>
    {
        Context.Same(expression.Owner);
        if (!replace && !Context.HasOrder) throw new InvalidOperationException("ThenBy requires an existing order.");
        Context.Clause(3, U(1, expression.Reference.In(Context.Binding)), N(2, (uint)direction), N(3, (uint)nullOrder), N(4, replace ? 1u : 0u));
        if (replace) { _orderColumns.Clear(); _orderCount = 0; }
        if (expression.RootColumnId is { } id) _orderColumns.Add(id);
        _orderCount++; Context.HasOrder = true;
        Context.UniqueOrder = _table.KeyColumns.Length != 0 && _table.KeyColumns.All(c => _orderColumns.Contains(c.Definition.Id));
        Context.SingleAscendingKey = _orderCount == 1 && direction == SortDirection.Ascending && _table.KeyColumns.Length == 1 && expression.RootColumnId == _table.KeyColumns[0].Definition.Id ? expression.RootColumnId : null;
        return this;
    });
    /// <summary>Appends a native grouping key.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>This mutable query for further synchronous composition before its first terminal.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public Query<TEntity> GroupBy<T>(SqlExpression<T> expression) { Context.Same(expression.Owner); Context.Clause(4, U(1, expression.Reference.In(Context.Binding))); return this; }
    /// <summary>Appends a same-query native group predicate.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="predicate">The compatible native condition or synchronous typed callback; callbacks are invoked at this call.</param>
    /// <returns>This mutable query for further synchronous composition before its first terminal.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public Query<TEntity> Having(SqlPredicate predicate) { Context.Same(predicate.Owner); Context.Clause(2, U(1, predicate.Reference.In(Context.Binding))); return this; }
    /// <summary>Applies native SQL DISTINCT to the selected shape.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <returns>This mutable query for further synchronous composition before its first terminal.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public Query<TEntity> Distinct() { Context.Clause(8); return this; }
    /// <summary>Sets a finite nonnegative native row limit; without order the selected rows are nondeterministic.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="count">The nonnegative bounded row count.</param>
    /// <returns>This mutable query for further synchronous composition before its first terminal.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public Query<TEntity> Take(int count) { ArgumentOutOfRangeException.ThrowIfNegative(count); Context.Clause(5, U(1, (ulong)count)); return this; }
    /// <summary>Sets a finite nonnegative offset only with known unique ordering.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="count">The nonnegative bounded row count.</param>
    /// <returns>This mutable query for further synchronous composition before its first terminal.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public Query<TEntity> Skip(int count) => Context.Run(() => { ArgumentOutOfRangeException.ThrowIfNegative(count); if (!Context.UniqueOrder) throw new ArgumentException("Offset paging requires an order including the complete root primary key.", nameof(count)); Context.Clause(5, U(2, (ulong)count)); Context.HasOffset = true; return this; });
    /// <summary>Appends strict keyset comparison for one non-null unique ascending root key.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="uniqueOrderKey">The sole non-null primary key already ordered ascending, with no conflicting offset.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>This mutable query for further synchronous composition before its first terminal.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public Query<TEntity> SeekAfter<T>(Column<TEntity, T> uniqueOrderKey, T value) => Context.Run(() =>
    {
        Context.CheckColumn(uniqueOrderKey, _table.Identity);
        if (Context.HasOffset || Context.SingleAscendingKey != uniqueOrderKey.Id || uniqueOrderKey.Codec.Descriptor.Nullable) throw new ArgumentException("SeekAfter requires one unique ascending non-null order key and no offset.", nameof(uniqueOrderKey));
        return Where(Column(uniqueOrderKey).GreaterThan(value));
    });
    /// <summary>Snapshots a finite total execution/cursor timeout; zero expires before dispatch.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="timeout">The finite nonnegative total execution and cursor deadline, snapshotted before dispatch.</param>
    /// <returns>This mutable query for further synchronous composition before its first terminal.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public Query<TEntity> WithTimeout(TimeSpan timeout)
    { HostEncoding.WaitBudget(timeout); Context.Clause(22, HostEncoding.Duration(1, timeout)); return this; }
    /// <summary>Attaches a frozen same-source native CTE and its exact immutable parameter identities.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="commonTable">The immutable CTE from this exact source to retain in the query.</param>
    /// <typeparam name="TCte">The statically known entity class with explicit table metadata.</typeparam>
    /// <returns>This mutable query for further synchronous composition before its first terminal.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public Query<TEntity> With<TCte>(CommonTable<TCte> commonTable) where TCte : class
    {
        ArgumentNullException.ThrowIfNull(commonTable); return Context.Run(() =>
        { var imported = Context.Binding.Import(commonTable.Handle, 3); Context.Clause(10, U(1, imported.In(Context.Binding))); Context.MergeParameters(commonTable.Parameters); return this; });
    }
    /// <summary>Applies an admitted PostgreSQL row lock; execution requires an explicit active transaction.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="mode">The PostgreSQL row-lock strength; an explicit transaction is required.</param>
    /// <param name="wait">The PostgreSQL lock-conflict policy.</param>
    /// <returns>This mutable query for further synchronous composition before its first terminal.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public Query<TEntity> ForUpdate(RowLockMode mode = RowLockMode.Update, LockWait wait = LockWait.Wait)
    { Context.Source.Capabilities.Require("db.row-lock.v1"); Context.Clause(12, N(1, (uint)mode), N(2, (uint)wait)); return this; }
    /// <summary>Immediately snapshots a typed literal into the native arena.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <param name="codec">The statically known codec and exact nullability/configuration for the value.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlExpression<T> Value<T>(T value, ValueCodec<T> codec) => Context.Value(value, codec);
    /// <summary>Allocates one nonrecycled source-generation parameter identity and declares it in this query.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="name">The bounded exact parameter name; conflicting identities in composed plans reject.</param>
    /// <param name="codec">The statically known codec and exact nullability/configuration for the value.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>The typed owner or proxy described above, retaining its original source/query association.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public QueryParameter<T> Parameter<T>(string name, ValueCodec<T> codec) => Context.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(codec);
        var bound = Context.Source.BindCodec(codec.Descriptor); var id = Context.Source.AllocateQueryParameter();
        _ = Context.Binding.Parameter(id, HostEncoding.Utf8.GetBytes(name), bound, codec.Descriptor.Nullable);
        var definition = new ParameterDefinition(Context.Source, id, name, codec.Descriptor, bound); Context.Parameters.Add(id, definition);
        return new QueryParameter<T>(definition, codec);
    });
    /// <summary>Creates a native reference to an identity already declared or imported into this query.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="parameter">The declared source-generation parameter identity with its exact typed codec.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlExpression<T> ParameterValue<T>(QueryParameter<T> parameter) => Context.Run(() =>
    {
        var definition = parameter.Definition ?? throw new ArgumentException("The default parameter is invalid.", nameof(parameter));
        if (!Context.Parameters.TryGetValue(definition.Id, out var own) || own != definition) throw new ArgumentException("The parameter was not declared/imported into this query.", nameof(parameter));
        return new SqlExpression<T>(Context, Context.Binding.Parameter(definition.Id, HostEncoding.Utf8.GetBytes(definition.Name), definition.BoundCodec, definition.Codec.Nullable), parameter.Codec!);
    });
    /// <summary>Snapshots a same-source selection into a native EXISTS condition.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="subquery">A current same-source selection whose native plan is cloned under admission.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>A same-query borrowed native predicate token with SQL three-valued semantics.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlPredicate Exists<T>(QuerySelection<T> subquery) => Context.Run(() => { var imported = Context.Import(subquery); return new SqlPredicate(Context, Context.Ref(Context.Clause(14, U(1, imported.In(Context.Binding)), N(2, 0)))); });
    /// <summary>Creates a native COUNT(*) aggregate with exact Int64 result metadata.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <returns>A same-query borrowed native scalar token with exact typed codec and SQL nullability.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public SqlExpression<long> CountRows() => SqlFunctions.Aggregate(Context, 0, null, Codecs.Int64);
    /// <summary>Creates finite query-owned native window state.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <returns>The typed owner or proxy described above, retaining its original source/query association.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public WindowDefinition Window() => new(Context, Context.Clause(11, N(1, 0)));
    /// <summary>Starts an explicit typed DTO projection in this query.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>The typed owner or proxy described above, retaining its original source/query association.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public ProjectionBuilder<T> Projection<T>() => Context.Run(() => new ProjectionBuilder<T>(Context));
    /// <summary>Selects a projection built by this exact query, or the root table's static row descriptor.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="projection">The exact query-built projection or root table row descriptor to select.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>The borrowed current-selection view; its owning query retains the native arena.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public QuerySelection<T> Select<T>(Projection<T> projection) => Context.Run(() =>
    {
        if (ReferenceEquals(projection, _table.Row)) return Context.Select(projection, RootExpressions());
        return Context.Select(projection);
    });
    /// <summary>Selects one same-query native expression through its exact static codec.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="expression">The compatible native scalar owned by the same live query.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>The borrowed current-selection view; its owning query retains the native arena.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public QuerySelection<T> Select<T>(SqlExpression<T> expression)
    { Context.Same(expression.Owner); var projection = NativeWeave.Data.Projection<T>.Create([ResultField.Create("value", expression.Codec)], row => row.Read(0, expression.Codec)); return Context.Select(projection, [expression.Reference]); }
    /// <summary>Selects two native expressions into an AOT-visible direct DTO factory.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="first">The first same-query native expression in result order.</param>
    /// <param name="second">The second same-query native expression in result order.</param>
    /// <param name="materializer">The statically compiled direct constructor or field reader; runtime reflection is not used.</param>
    /// <typeparam name="T1">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <typeparam name="T2">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <typeparam name="TOut">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>The borrowed current-selection view; its owning query retains the native arena.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public QuerySelection<TOut> Select<T1, T2, TOut>(SqlExpression<T1> first, SqlExpression<T2> second, Func<T1, T2, TOut> materializer)
    {
        ArgumentNullException.ThrowIfNull(materializer); Context.Same(first.Owner); Context.Same(second.Owner);
        var projection = NativeWeave.Data.Projection<TOut>.Create([ResultField.Create("first", first.Codec), ResultField.Create("second", second.Codec)], row => materializer(row.Read(0, first.Codec), row.Read(1, second.Codec)));
        return Context.Select(projection, [first.Reference, second.Reference]);
    }
    private ExpressionReference[] RootExpressions() => Context.Run(() => _table.Columns.Select(c => Context.Binding.Column(c.Definition.Id)).ToArray());
    /// <summary>Selects only root-table columns in the static entity constructor order.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <returns>The borrowed current-selection view; its owning query retains the native arena.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public QuerySelection<TEntity> SelectAll() => Context.Run(() => Context.Select(_table.Row, RootExpressions()));
    /// <summary>Counts the fully composed native result without fetching DTOs into managed memory.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="cancellationToken">Cancellation requests native stop; completion still waits for cursor and operation retirement.</param>
    /// <returns>The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    /// <exception cref="OperationCanceledException">Cancellation requests termination and cleanup.</exception>
    /// <exception cref="TimeoutException">The finite total query deadline expires; cleanup still completes.</exception>
    public Task<long> CountAsync(CancellationToken cancellationToken = default) => Context.Count(4, cancellationToken);
    /// <summary>Performs native EXISTS over the fully composed query.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="cancellationToken">Cancellation requests native stop; completion still waits for cursor and operation retirement.</param>
    /// <returns>The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    /// <exception cref="OperationCanceledException">Cancellation requests termination and cleanup.</exception>
    /// <exception cref="TimeoutException">The finite total query deadline expires; cleanup still completes.</exception>
    public async Task<bool> AnyAsync(CancellationToken cancellationToken = default) => await Context.Count(5, cancellationToken).ConfigureAwait(false) != 0;
    /// <summary>Snapshots explicit typed changes and performs a filtered native set-based UPDATE.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="changes">The explicit immutable snapshot of permitted typed root-column assignments.</param>
    /// <param name="cancellationToken">Cancellation requests native stop; completion still waits for cursor and operation retirement.</param>
    /// <returns>The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    /// <exception cref="OperationCanceledException">Cancellation requests termination and cleanup.</exception>
    /// <exception cref="TimeoutException">The finite total query deadline expires; cleanup still completes.</exception>
    public Task<long> UpdateAsync(Changes<TEntity> changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes); return Context.Run(() =>
        {
            foreach (var change in changes.Snapshot(_table))
            {
                var codec = change.Column.Definition.Codec; var bound = Context.Source.BindCodec(codec);
                ReadOnlyMemory<byte>? cell = change.Bytes is null ? (ReadOnlyMemory<byte>?)null : new ReadOnlyMemory<byte>(change.Bytes);
                var wire = DbWire.Encode(new(PayloadKind.ExpressionValues, Context.Source.Binding.SemanticProfile, new byte[32], 0, 0, [new(1, codec, bound)]) { ExactRows = 1 }, 1, [cell]);
                var value = Context.Binding.Value(wire); Context.Clause(17, N(1, change.Column.Definition.Id), U(2, value.In(Context.Binding)));
            }
            return Context.Count(6, cancellationToken);
        });
    }
    /// <summary>Runs a synchronous typed setter callback, then applies complete native assignments and a filtered UPDATE.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="setters">The synchronous callback that stages permitted typed assignments before submission.</param>
    /// <param name="cancellationToken">Cancellation requests native stop; completion still waits for cursor and operation retirement.</param>
    /// <returns>The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    /// <exception cref="OperationCanceledException">Cancellation requests termination and cleanup.</exception>
    /// <exception cref="TimeoutException">The finite total query deadline expires; cleanup still completes.</exception>
    public Task<long> UpdateAsync(Action<UpdateSetters<TEntity>> setters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setters); return Context.Run(() =>
        {
            var builder = new UpdateSetters<TEntity>(Context, _table.Identity);
            try { setters(builder); builder.Apply(); return Context.Count(6, cancellationToken); }
            finally { builder.Invalidate(); }
        });
    }
    /// <summary>Executes a filtered native DELETE; missing filters and unsupported joined/grouped writes reject.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <param name="cancellationToken">Cancellation requests native stop; completion still waits for cursor and operation retirement.</param>
    /// <returns>The typed terminal result after required native operation and cursor cleanup; cardinality, mapping, provider or cancellation failures propagate through the task.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    /// <exception cref="OperationCanceledException">Cancellation requests termination and cleanup.</exception>
    /// <exception cref="TimeoutException">The finite total query deadline expires; cleanup still completes.</exception>
    public Task<long> DeleteAsync(CancellationToken cancellationToken = default) => Context.Count(7, cancellationToken);
    /// <summary>Creates an explicit deep native mutable clone; existing expression/alias proxies remain bound to this query.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    /// <returns>A new independently disposable native mutable clone; old proxies still belong to the old query.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public Query<TEntity> Clone() => Context.Run(() =>
    {
        var context = new QueryContext(Context.Session, _table.Identity, Context.Binding.CloneSelected()) { HasOrder = Context.HasOrder, UniqueOrder = Context.UniqueOrder, SingleAscendingKey = Context.SingleAscendingKey, HasOffset = Context.HasOffset };
        context.MergeParameters(Context.Parameters.Values); var clone = new Query<TEntity>(context, _table) { _orderCount = _orderCount };
        clone._orderColumns.UnionWith(_orderColumns); return clone;
    });
    /// <summary>Closes query admission, requests any active cursor stop and releases the native arena reference.</summary>
    /// <inheritdoc cref="Query{TEntity}"/>
    public void Dispose() => Context.Dispose();
}
/// <summary>A callback-scoped typed assignment collector; scalar expressions are built natively immediately.</summary>
/// <typeparam name="TEntity">The root entity class.</typeparam>
/// <remarks>The owner is valid only during the synchronous update callback. Each scalar is snapshotted immediately; duplicate, generated and primary-key assignments reject. Assignment clauses attach after callback success; no tracked entity or managed SQL expression tree is created.</remarks>
public sealed class UpdateSetters<TEntity> where TEntity : class
{
    private readonly QueryContext _owner;
    private readonly TableIdentity _table;
    private readonly List<(uint Column, ExpressionReference Value)> _values = [];
    private bool _consumed;
    internal UpdateSetters(QueryContext owner, TableIdentity table) { _owner = owner; _table = table; }
    /// <summary>Copies a typed value for a nonkey, nongenerated root-column assignment.</summary>
    /// <inheritdoc cref="UpdateSetters{TEntity}"/>
    /// <param name="column">The strongly typed column from the declared table; foreign table identities reject.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>The typed owner or proxy described above, retaining its original source/query association.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public UpdateSetters<TEntity> Set<T>(Column<TEntity, T> column, T value)
    { ArgumentNullException.ThrowIfNull(column); return Set(column, _owner.Value(value, column.Codec)); }
    /// <summary>Stages a compatible same-query expression; assignments attach only after the callback succeeds.</summary>
    /// <inheritdoc cref="UpdateSetters{TEntity}"/>
    /// <param name="column">The strongly typed column from the declared table; foreign table identities reject.</param>
    /// <param name="value">The typed value to snapshot now, or the compatible same-query native expression.</param>
    /// <typeparam name="T">The statically known CLR value or owned result type with the exact declared codec/nullability.</typeparam>
    /// <returns>The typed owner or proxy described above, retaining its original source/query association.</returns>
    /// <exception cref="ArgumentException">An argument, owner association or codec/nullability is incompatible.</exception>
    /// <exception cref="InvalidOperationException">The owner, projection or one-shot terminal is in an invalid state.</exception>
    /// <exception cref="ObjectDisposedException">The required native owner or its parent has closed admission.</exception>
    /// <exception cref="NativeWeave.Data.DatabaseException">Native capability, finite resource limits or provider execution rejects the operation.</exception>
    public UpdateSetters<TEntity> Set<T>(Column<TEntity, T> column, SqlExpression<T> value) => _owner.Run(() =>
    {
        _owner.CheckColumn(column, _table); _owner.Same(value.Owner);
        if (_consumed || column.Definition.Options.IsPrimaryKey || column.Definition.Options.IsGenerated || _values.Any(v => v.Column == column.Id)) throw new ArgumentException("Invalid, duplicate or consumed update assignment.", nameof(column));
        if (column.Codec.Descriptor != value.Codec.Descriptor) throw new ArgumentException("Assignment codec/nullability differs.", nameof(value));
        _values.Add((column.Id, value.Reference)); return this;
    });
    internal void Apply()
    { _consumed = true; foreach (var value in _values) _owner.Clause(17, N(1, value.Column), U(2, value.Value.In(_owner.Binding))); }
    internal void Invalidate() => _consumed = true;
}
