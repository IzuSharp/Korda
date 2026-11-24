using Korda.Data;
using Korda.Database;
using Korda.Query;

internal sealed partial class Workload
{
    private readonly Dictionary<string, IReadCase> _advanced = new(StringComparer.Ordinal);
    private interface IReadCase : IDisposable
    {
        ValueTask<OperationResult> Execute(DataSession session, bool prepared);
    }
    private readonly record struct Built<T>(IDisposable Owner, QuerySelection<T> Selection, IDisposable? Other = null) : IDisposable
    {
        public void Dispose() { Owner.Dispose(); Other?.Dispose(); }
    }
    private sealed class ReadCase<T> : IReadCase
    {
        private readonly Func<DataSession, Built<T>> _build;
        private readonly PreparedQuery<T>? _plan;
        private readonly RowDigest<T> _hash;
        internal ReadCase(Func<DataSession, Built<T>> build, DataSession session, RowDigest<T> hash, bool prepared, out string sql)
        {
            _build = build; _hash = hash;
            using var query = build(session); sql = query.Selection.ToSql(); if (prepared) _plan = query.Selection.Prepare();
        }
        public ValueTask<OperationResult> Execute(DataSession session, bool prepared) => prepared
            ? Read(_plan!.AsAsyncEnumerable(session), _hash) : Dynamic(session);
        private async ValueTask<OperationResult> Dynamic(DataSession session)
        {
            using var query = _build(session);
            return await Read(query.Selection.AsAsyncEnumerable(), _hash).ConfigureAwait(false);
        }
        public void Dispose() => _plan?.Dispose();
    }

    private void InitializeAdvanced()
    {
        foreach (var name in _options.Verify ? Names : _names)
        {
            if (name is "numeric" or "text" or "money" or "json" or "wide") continue;
            switch (name)
            {
                case "optional-filter": Add(name, BuildOptional, HashInteger, Ids().Where(id => Dataset.CustomerId(id) == 2).Take(_options.Rows)); break;
                case "range": Add(name, BuildRange, HashInteger, Ids().Where(id => Dataset.CreatedAt(id) >= 1700000100 && Dataset.CreatedAt(id) < 1700000200).Take(_options.Rows)); break;
                case "null-notes": Add(name, BuildNull, HashInteger, Ids().Where(id => Dataset.Note(id) is null).Take(_options.Rows)); break;
                case "literal-note": Add(name, BuildLiteral, HashInteger, Ids().Where(id => Dataset.Note(id)?.Contains("note-1", StringComparison.Ordinal) == true).Take(_options.Rows)); break;
                case "left-join": Add(name, BuildLeftJoin, HashJoin, Ids().Take(_options.Rows).Select(id => new JoinRow(id, Dataset.CustomerId(id) is { } customer ? Dataset.CustomerName(customer) : null))); break;
                case "inner-join": Add(name, BuildInnerJoin, HashJoin, Ids().Where(id => Dataset.CustomerId(id).HasValue).Take(_options.Rows).Select(id => new JoinRow(id, Dataset.CustomerName(Dataset.CustomerId(id)!.Value)))); break;
                case "customer-totals":
                    var groups = Ids().GroupBy(Dataset.CustomerId).Where(group => group.Count() > 3)
                        .OrderBy(group => group.Key is null).ThenBy(group => group.Key)
                        .Select(group => new CustomerTotal(group.Key, group.LongCount(), group.Sum(id => Dataset.Cents(1, id)) / 100m));
                    Add(name, BuildCustomerTotals, HashCustomerTotal, groups); break;
                case "product-totals":
                    var products = Ids().SelectMany(id => Enumerable.Range(0, Dataset.Lines(id)).Select(line => (Product: Dataset.Product(id, line), Quantity: Dataset.Quantity(id, line))))
                        .GroupBy(item => item.Product, StringComparer.Ordinal).Where(group => group.Sum(item => item.Quantity) > 10)
                        .OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => new ProductTotal(group.Key, group.Sum(item => item.Quantity)));
                    Add(name, BuildProductTotals, HashProductTotal, products); break;
                case "empty-sum": Add<decimal?>(name, BuildEmptySum, HashNullableMoney, [null]); break;
                case "cursor": Add(name, BuildCursor, HashInteger, Ids().Skip(_options.OrdersPerTenant / 2).Take(_options.Rows)); break;
                case "offset": Add(name, BuildOffset, HashInteger, Ids().Skip(_options.OrdersPerTenant * 3 / 4).Take(_options.Rows)); break;
                case "exists": Add(name, BuildExists, HashInteger, new long[] { 1 }); break;
                case "in-subquery": Add(name, BuildIn, HashInteger, Ids().Where(id => id > 128).Select(Dataset.CustomerId).Where(id => id.HasValue).Select(id => id!.Value).Distinct().Order().Take(_options.Rows)); break;
                case "cte": Add(name, BuildCte, HashInteger, Ids().Where(id => id > 16).Take(_options.Rows)); break;
                case "window":
                    var ranks = new Dictionary<long, long>();
                    Add(name, BuildWindow, HashRank, Ids().Take(_options.Rows).Select(id =>
                    {
                        var key = Dataset.CustomerId(id) ?? -1; ranks.TryGetValue(key, out var rank); ranks[key] = ++rank;
                        return new RankRow(id, rank);
                    })); break;
                case "union-all": Add(name, session => BuildSet(session, SetOperation.UnionAll), HashInteger,
                    Enumerable.Range(1, 16).Select(i => (long)i).Concat(Enumerable.Range(9, 16).Select(i => (long)i)).Order()); break;
                case "union": Add(name, session => BuildSet(session, SetOperation.Union), HashInteger, Enumerable.Range(1, 24).Select(i => (long)i)); break;
                case "intersect": Add(name, session => BuildSet(session, SetOperation.Intersect), HashInteger, Enumerable.Range(9, 8).Select(i => (long)i)); break;
                case "except": Add(name, session => BuildSet(session, SetOperation.Except), HashInteger, Enumerable.Range(1, 8).Select(i => (long)i)); break;
                case "running-total":
                    long lineId = 0, sum = 0;
                    Add(name, BuildRunning, HashRunning, Ids().SelectMany(id => Enumerable.Range(0, Dataset.Lines(id)).Select(line => new RunningRow(++lineId, sum += Dataset.Quantity(id, line)))).Take(_options.Rows)); break;
                case "three-join":
                    Add(name, BuildThreeJoin, HashDetail, Ids().Where(id => Dataset.CustomerId(id).HasValue)
                        .SelectMany(id => Enumerable.Range(0, Dataset.Lines(id)).Select(line => new DetailRow(id, Dataset.CustomerName(Dataset.CustomerId(id)!.Value), Dataset.Product(id, line), Dataset.Quantity(id, line))))
                        .Take(_options.Rows)); break;
                case "anti-association": Add(name, BuildAnti, HashInteger, Ids().Where(id => Enumerable.Range(0, Dataset.Lines(id)).All(line => Dataset.Quantity(id, line) < 5)).Take(_options.Rows)); break;
                case "nullable-order": Add(name, BuildNullableOrder, HashInteger, Ids().OrderBy(id => Dataset.CustomerId(id).HasValue).ThenBy(Dataset.CustomerId).ThenBy(id => id).Take(_options.Rows)); break;
                case "raw-sql":
                    var raw = new RawCase(_options); _advanced.Add(name, raw); _sql[name] = RawCase.Sql;
                    var textDigest = Digest.Create(); long textRows = 0;
                    foreach (var id in Ids().Take(_options.Rows)) { HashText(ref textDigest, new TextRow(id, Dataset.Note(id))); textRows++; }
                    _expected[name] = new(textRows, textDigest.Value, 0); break;
                case "related":
                    var related = new RelatedCase(_options); _advanced.Add(name, related);
                    _sql[name] = "LoadRelatedAsync: one UNION ALL SQL statement for a bounded complete composite-key batch; native-generated parent ordinal; caller sorts children by Id";
                    var relationDigest = Digest.Create(); long relationRows = 0, relationLineId = 0;
                    for (long id = 1; id <= Math.Min(Math.Min(_options.Rows, _options.BatchRows), _options.OrdersPerTenant); id++)
                    {
                        relationDigest.Integer(id); relationDigest.Integer(Dataset.Lines(id));
                        for (var line = 0; line < Dataset.Lines(id); line++)
                        {
                            HashLine(ref relationDigest, new OrderLine(++relationLineId, 1, id, Dataset.Product(id, line), Dataset.Quantity(id, line)));
                            relationRows++;
                        }
                    }
                    _expected[name] = new(relationRows, relationDigest.Value, 0); break;
            }
        }
    }

    private IEnumerable<long> Ids() => Enumerable.Range(1, _options.OrdersPerTenant).Select(i => (long)i);
    private void Add<T>(string name, Func<DataSession, Built<T>> factory, RowDigest<T> hash, IEnumerable<T> expected)
    {
        var digest = Digest.Create(); long rows = 0;
        foreach (var row in expected) { hash(ref digest, row); rows++; }
        var query = new ReadCase<T>(factory, _session, hash, _options.Mode == "prepared", out var sql);
        _advanced.Add(name, query); _sql.Add(name, sql); _expected.Add(name, new(rows, digest.Value, 0));
    }
    private Query<Order> Root(DataSession session) => session.From(Orders.Table).WhereEqual(Orders.TenantId, 1L);
    private Built<long> BuildOptional(DataSession session)
    {
        var query = Root(session);
        query.WhereIf(true, static row => row.Equal(Orders.CustomerId, (long?)2)).OrderBy(Orders.Id).Take(_options.Rows);
        return new(query, query.Select(query.Column(Orders.Id)));
    }
    private Built<long> BuildRange(DataSession session)
    {
        var query = Root(session);
        query.Where(query.Column(Orders.CreatedAt).GreaterThanOrEqual(1700000100L).And(query.Column(Orders.CreatedAt).LessThan(1700000200L))).OrderBy(Orders.Id).Take(_options.Rows);
        return new(query, query.Select(query.Column(Orders.Id)));
    }
    private Built<long> BuildNull(DataSession session)
    {
        var query = Root(session); query.Where(query.Column(Orders.Note).IsNull()).OrderBy(Orders.Id).Take(_options.Rows);
        return new(query, query.Select(query.Column(Orders.Id)));
    }
    private Built<long> BuildLiteral(DataSession session)
    {
        var query = Root(session); query.Where(query.Column(Orders.Note).ContainsNullableLiteral("note-1")).OrderBy(Orders.Id).Take(_options.Rows);
        return new(query, query.Select(query.Column(Orders.Id)));
    }
    private Built<JoinRow> BuildLeftJoin(DataSession session)
    {
        var query = Root(session);
        var customer = query.LeftJoin(Customers.Table, "customer", static on => on.Left(Orders.CustomerId).Equal(on.Right(Customers.Id).AsNullable()));
        query.OrderBy(Orders.Id).Take(_options.Rows);
        return new(query, query.Select(JoinRows.Bind(query, Id: query.Column(Orders.Id), Name: query.NullableReference(customer, Customers.Name))));
    }
    private Built<JoinRow> BuildInnerJoin(DataSession session)
    {
        var query = Root(session);
        var customer = query.InnerJoin(Customers.Table, "customer", static on => on.Left(Orders.CustomerId).Equal(on.Right(Customers.Id).AsNullable()));
        query.OrderBy(Orders.Id).Take(_options.Rows);
        return new(query, query.Select(JoinRows.Bind(query, Id: query.Column(Orders.Id), Name: query.Column(customer, Customers.Name).AsNullableReference())));
    }
    private Built<CustomerTotal> BuildCustomerTotals(DataSession session)
    {
        var query = Root(session); var customer = query.Column(Orders.CustomerId);
        query.GroupBy(customer).Having(query.CountRows().GreaterThan(3L)).OrderBy(Orders.CustomerId);
        return new(query, query.Select(CustomerTotals.Bind(query, CustomerId: customer, Count: query.CountRows(), Total: SqlFunctions.Sum(query.Column(Orders.Total)))));
    }
    private Built<ProductTotal> BuildProductTotals(DataSession session)
    {
        var query = session.From(OrderLines.Table).WhereEqual(OrderLines.TenantId, 1L);
        var product = query.Column(OrderLines.Product); var quantity = SqlFunctions.Sum(query.Column(OrderLines.Quantity));
        query.GroupBy(product).Having(quantity.GreaterThan((long?)10)).OrderBy(OrderLines.Product);
        return new(query, query.Select(ProductTotals.Bind(query, Product: product, Quantity: quantity)));
    }
    private Built<decimal?> BuildEmptySum(DataSession session)
    {
        var query = session.From(Orders.Table).WhereEqual(Orders.TenantId, -1L);
        return new(query, query.Select(SqlFunctions.Sum(query.Column(Orders.Total))));
    }
    private Built<long> BuildCursor(DataSession session)
    {
        var query = Root(session); var id = _options.OrdersPerTenant / 2;
        query.OrderBy(Orders.CreatedAt).ThenBy(Orders.TenantId).ThenBy(Orders.Id)
            .SeekAfter(QueryCursor<Order>.Create(Orders.CreatedAt, Dataset.CreatedAt(id)).Then(Orders.TenantId, 1L).Then(Orders.Id, (long)id)).Take(_options.Rows);
        return new(query, query.Select(query.Column(Orders.Id)));
    }
    private Built<long> BuildOffset(DataSession session)
    {
        var query = Root(session); query.OrderBy(Orders.CreatedAt).ThenBy(Orders.TenantId).ThenBy(Orders.Id)
            .Skip(_options.OrdersPerTenant * 3 / 4).Take(_options.Rows);
        return new(query, query.Select(query.Column(Orders.Id)));
    }
    private Built<long> BuildExists(DataSession session)
    {
        using var body = session.From(Orders.Table).WhereEqual(Orders.TenantId, 1L).WhereGreaterThanOrEqual(Orders.Id, 32L);
        var query = session.From(Customers.Table);
        query.Where(query.Exists(body.Select(body.Column(Orders.Id)))).OrderBy(Customers.Id).Take(1);
        return new(query, query.Select(query.Column(Customers.Id)));
    }
    private Built<long> BuildIn(DataSession session)
    {
        using var body = Root(session); body.Where(body.Column(Orders.Id).GreaterThan(128L));
        var query = session.From(Customers.Table);
        query.Where(query.Column(Customers.Id).AsNullable().In(body.Select(body.Column(Orders.CustomerId)))).OrderBy(Customers.Id).Take(_options.Rows);
        return new(query, query.Select(query.Column(Customers.Id)));
    }
    private Built<long> BuildCte(DataSession session)
    {
        using var body = Root(session); body.Where(body.Column(Orders.Id).GreaterThan(16L));
        var shape = body.Projection<SelectedOrderId>(); var id = shape.Add(body.Column(Orders.Id), "Id");
        var cte = CommonTable<SelectedOrderId>.Create(SelectedOrderIds.Table, body.Select(shape.Build(row => new SelectedOrderId(row.Get(id)))));
        var query = session.From(SelectedOrderIds.Table);
        query.With(cte).OrderBy(SelectedOrderIds.Id).Take(_options.Rows);
        return new(query, query.Select(query.Column(SelectedOrderIds.Id)), cte);
    }
    private Built<RankRow> BuildWindow(DataSession session)
    {
        var query = Root(session); var window = query.Window().PartitionBy(query.Column(Orders.CustomerId)).OrderBy(query.Column(Orders.CreatedAt)).OrderBy(query.Column(Orders.Id));
        query.OrderBy(Orders.Id).Take(_options.Rows);
        return new(query, query.Select(RankRows.Bind(query, Id: query.Column(Orders.Id), Rank: SqlFunctions.RowNumber(window))));
    }
    private Built<long> BuildSet(DataSession session, SetOperation operation)
    {
        using var left = Root(session); left.Where(left.Column(Orders.Id).LessThanOrEqual(16L));
        using var right = Root(session); right.Where(right.Column(Orders.Id).GreaterThanOrEqual(9L).And(right.Column(Orders.Id).LessThanOrEqual(24L)));
        var leftShape = left.Projection<SelectedOrderId>(); var leftId = leftShape.Add(left.Column(Orders.Id), "Id");
        var rightShape = right.Projection<SelectedOrderId>(); var rightId = rightShape.Add(right.Column(Orders.Id), "Id");
        var selected = left.Select(leftShape.Build(row => new SelectedOrderId(row.Get(leftId))))
            .Combine(right.Select(rightShape.Build(row => new SelectedOrderId(row.Get(rightId)))), operation);
        var cte = CommonTable<SelectedOrderId>.Create(SelectedOrderIds.Table, selected);
        var query = session.From(SelectedOrderIds.Table); query.With(cte).OrderBy(SelectedOrderIds.Id);
        return new(query, query.Select(query.Column(SelectedOrderIds.Id)), cte);
    }
    private Built<RunningRow> BuildRunning(DataSession session)
    {
        var query = session.From(OrderLines.Table).WhereEqual(OrderLines.TenantId, 1L);
        var window = query.Window().OrderBy(query.Column(OrderLines.Id)).RowsBetween(FrameBoundary.UnboundedPreceding, FrameBoundary.CurrentRow);
        query.OrderBy(OrderLines.Id).Take(_options.Rows);
        return new(query, query.Select(RunningRows.Bind(query, Id: query.Column(OrderLines.Id), Quantity: SqlFunctions.Sum(query.Column(OrderLines.Quantity)).Over(window))));
    }
    private Built<DetailRow> BuildThreeJoin(DataSession session)
    {
        var query = Root(session);
        var customer = query.InnerJoin(Customers.Table, "customer", static on => on.Left(Orders.CustomerId).Equal(on.Right(Customers.Id).AsNullable()));
        var line = query.InnerJoin(OrderLines.Table, "line", static on => on.Left(Orders.TenantId).Equal(on.Right(OrderLines.TenantId)).And(on.Left(Orders.Id).Equal(on.Right(OrderLines.OrderId))));
        query.OrderBy(Orders.Id).ThenBy(query.Column(line, OrderLines.Id)).Take(_options.Rows);
        return new(query, query.Select(DetailRows.Bind(query, OrderId: query.Column(Orders.Id), Name: query.Column(customer, Customers.Name),
            Product: query.Column(line, OrderLines.Product), Quantity: query.Column(line, OrderLines.Quantity))));
    }

    private Built<long> BuildAnti(DataSession session)
    {
        using var body = session.From(OrderLines.Table).WhereEqual(OrderLines.TenantId, 1L).WhereGreaterThanOrEqual(OrderLines.Quantity, 5L);
        var query = Root(session);
        query.Where(query.Column(Orders.Id).In(body.Select(body.Column(OrderLines.OrderId))).Not()).OrderBy(Orders.Id).Take(_options.Rows);
        return new(query, query.Select(query.Column(Orders.Id)));
    }
    private Built<long> BuildNullableOrder(DataSession session)
    {
        var query = Root(session); query.OrderBy(Orders.CustomerId, nullOrder: NullOrder.First).ThenBy(Orders.Id).Take(_options.Rows);
        return new(query, query.Select(query.Column(Orders.Id)));
    }
    private sealed class RawCase : IReadCase
    {
        internal const string Sql = "SELECT \"Id\",\"Note\" FROM orders WHERE \"TenantId\"=@tenant ORDER BY \"Id\" LIMIT @take";
        private readonly int _rows;
        private readonly SqlCommand _command;
        internal RawCase(Options options) { _rows = options.Rows; _command = Command(); }
        private SqlCommand Command() => SqlCommand.Create(Sql, new SqlParameters().Add("tenant", 1L, Codecs.Int64).Add("take", (long)_rows, Codecs.Int64));
        public ValueTask<OperationResult> Execute(DataSession session, bool prepared) => Read(session.StreamAsync(prepared ? _command : Command(), TextRows.Row), HashText);
        public void Dispose() { }
    }
    private sealed class RelatedCase : IReadCase
    {
        private readonly EntityKey<Order>[] _keys;
        private readonly Relation<Order, OrderLine> _relation;
        internal RelatedCase(Options options)
        {
            _keys = Enumerable.Range(1, Math.Min(Math.Min(options.Rows, options.BatchRows), options.OrdersPerTenant))
                .Select(id => Orders.Table.Key(Orders.TenantId, 1L).And(Orders.Id, (long)id)).ToArray();
            _relation = Relation<Order, OrderLine>.Create(Orders.Table, Orders.TenantId, OrderLines.Table, OrderLines.TenantId).And(Orders.Id, OrderLines.OrderId);
        }
        public async ValueTask<OperationResult> Execute(DataSession session, bool prepared)
        {
            // This normal public batch API has no exposed prepared-plan overload. Both modes reuse only the immutable relation/key inputs.
            var pending = session.LoadRelatedAsync(_keys, _relation);
            var suspended = pending.IsCompleted ? 0 : 1;
            var groups = await pending.ConfigureAwait(false);
            var digest = Digest.Create(); long rows = 0;
            for (var index = 0; index < groups.Count; index++)
            {
                digest.Integer(index + 1); digest.Integer(groups[index].Count);
                foreach (var row in groups[index].OrderBy(static row => row.Id)) { HashLine(ref digest, row); rows++; }
            }
            return new(rows, digest.Value, suspended);
        }
        public void Dispose() { }
    }

    private static void HashJoin(ref Digest digest, JoinRow row) { digest.Integer(row.Id); digest.Text(row.Name); }
    private static void HashNullableMoney(ref Digest digest, decimal? value) { digest.Boolean(value.HasValue); if (value.HasValue) digest.Money(value.Value); }
    private static void HashCustomerTotal(ref Digest digest, CustomerTotal row) { NullableInteger(ref digest, row.CustomerId); digest.Integer(row.Count); HashNullableMoney(ref digest, row.Total); }
    private static void HashProductTotal(ref Digest digest, ProductTotal row) { digest.Text(row.Product); NullableInteger(ref digest, row.Quantity); }
    private static void HashRank(ref Digest digest, RankRow row) { digest.Integer(row.Id); digest.Integer(row.Rank); }
    private static void HashRunning(ref Digest digest, RunningRow row) { digest.Integer(row.Id); NullableInteger(ref digest, row.Quantity); }
    private static void HashDetail(ref Digest digest, DetailRow row) { digest.Integer(row.OrderId); digest.Text(row.Name); digest.Text(row.Product); digest.Integer(row.Quantity); }
    private static void HashLine(ref Digest digest, OrderLine row) { digest.Integer(row.Id); digest.Integer(row.TenantId); digest.Integer(row.OrderId); digest.Text(row.Product); digest.Integer(row.Quantity); }
}
