using Korda.Data;
using Korda.Database;
using Korda.Query;

internal sealed partial class Workload : IDisposable
{
    internal static readonly string[] Names = ["numeric", "text", "money", "json", "wide",
        "optional-filter", "range", "left-join", "inner-join", "null-notes", "literal-note",
        "customer-totals", "product-totals", "empty-sum", "cursor", "offset", "exists",
        "in-subquery", "cte", "window", "union-all", "union", "intersect", "except", "running-total", "three-join",
        "anti-association", "nullable-order", "raw-sql", "related"];
    private readonly Options _options;
    private readonly DataSession _session;
    private readonly Dictionary<string, OperationResult> _expected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sql = new(StringComparer.Ordinal);
    private PreparedQuery<long>? _numeric;
    private PreparedQuery<TextRow>? _text;
    private PreparedQuery<MoneyRow>? _money;
    private PreparedQuery<JsonRow>? _json;
    private PreparedQuery<Order>? _wide;
    private readonly QueryParameters _numericValues = new(), _textValues = new(), _moneyValues = new(), _jsonValues = new(), _wideValues = new();
    private QueryParameter<long> _numericTenant, _textTenant, _moneyTenant, _jsonTenant, _wideTenant;

    internal Workload(DataSession session, Options options)
    {
        _session = session; _options = options;
        if (options.Case != "mixed" && !Names.Contains(options.Case, StringComparer.Ordinal))
            throw new ArgumentException("Unknown case " + options.Case);
        foreach (var name in (options.Case == "mixed" || options.Verify ? Names : [options.Case]).Where(name => name is "numeric" or "text" or "money" or "json" or "wide"))
        {
            using var query = session.From(Orders.Table);
            var tenant = query.Parameter("tenant", Codecs.Int64);
            query.Where(query.Column(Orders.TenantId).Equal(query.ParameterValue(tenant))).OrderBy(Orders.Id).Take(options.Rows);
            switch (name)
            {
                case "numeric":
                    var numeric = query.Select(query.Column(Orders.Id));
                    _sql[name] = numeric.ToSql(); if (options.Mode == "prepared") _numeric = numeric.Prepare(); _numericTenant = tenant; break;
                case "text":
                    var text = query.Select(TextRows.Bind(query, Id: query.Column(Orders.Id), Note: query.Column(Orders.Note)));
                    _sql[name] = text.ToSql(); if (options.Mode == "prepared") _text = text.Prepare(); _textTenant = tenant; break;
                case "money":
                    var money = query.Select(MoneyRows.Bind(query, Id: query.Column(Orders.Id), Total: query.Column(Orders.Total)));
                    _sql[name] = money.ToSql(); if (options.Mode == "prepared") _money = money.Prepare(); _moneyTenant = tenant; break;
                case "json":
                    var json = query.Select(JsonRows.Bind(query, Id: query.Column(Orders.Id), Settings: query.Column(Orders.Settings)));
                    _sql[name] = json.ToSql(); if (options.Mode == "prepared") _json = json.Prepare(); _jsonTenant = tenant; break;
                case "wide":
                    var wide = query.SelectAll();
                    _sql[name] = wide.ToSql(); if (options.Mode == "prepared") _wide = wide.Prepare(); _wideTenant = tenant; break;
            }
            var digest = Digest.Create();
            var rows = Math.Min(options.Rows, options.OrdersPerTenant);
            for (long id = 1; id <= rows; id++)
            {
                if (name == "wide") digest.Integer(1);
                digest.Integer(id);
                if (name == "wide") { digest.Integer(Dataset.CreatedAt(id)); NullableInteger(ref digest, Dataset.CustomerId(id)); }
                if (name is "money" or "wide") digest.Integer(Dataset.Cents(1, id));
                if (name is "text" or "wide") digest.Text(Dataset.Note(id));
                if (name == "wide") digest.Integer(1);
                if (name is "json" or "wide") { digest.Text(Dataset.Channel(id)); digest.Boolean(Dataset.Gift(id)); }
                if (name == "wide") digest.Text(new string('m', id % 17 == 0 ? 2048 : 32));
            }
            _expected[name] = new(rows, digest.Value, 0);
        }
        InitializeAdvanced();
    }

    internal IReadOnlyDictionary<string, string> Sql => _sql;
    internal IReadOnlyDictionary<string, OperationResult> Expectations => _expected;
    internal OperationResult Expected(string name) => _expected[name];
    internal string Name(long iteration) => _options.Case == "mixed" ? Names[(int)(iteration % Names.Length)] : _options.Case;
    internal ValueTask<OperationResult> Execute(string name, DataSession? session = null)
    {
        session ??= _session;
        if (_options.EmptyDriver) return ValueTask.FromResult(_expected[name]);
        if (_advanced.TryGetValue(name, out var advanced)) return advanced.Execute(session, _options.Mode == "prepared");
        return _options.Mode == "prepared" ? Prepared(name, session) : Dynamic(name, session);
    }

    private ValueTask<OperationResult> Prepared(string name, DataSession session) => name switch
    {
        "numeric" => Read(_numeric!.AsAsyncEnumerable(session, _numericValues.Set(_numericTenant, 1L)), HashInteger),
        "text" => Read(_text!.AsAsyncEnumerable(session, _textValues.Set(_textTenant, 1L)), HashText),
        "money" => Read(_money!.AsAsyncEnumerable(session, _moneyValues.Set(_moneyTenant, 1L)), HashMoney),
        "json" => Read(_json!.AsAsyncEnumerable(session, _jsonValues.Set(_jsonTenant, 1L)), HashJson),
        "wide" => Read(_wide!.AsAsyncEnumerable(session, _wideValues.Set(_wideTenant, 1L)), HashOrder),
        _ => throw new ArgumentException("Unknown prepared case."),
    };

    private async ValueTask<OperationResult> Dynamic(string name, DataSession session)
    {
        using var query = session.From(Orders.Table);
        query.WhereEqual(Orders.TenantId, 1L).OrderBy(Orders.Id).Take(_options.Rows);
        return name switch
        {
            "numeric" => await Read(query.Select(query.Column(Orders.Id)).AsAsyncEnumerable(), HashInteger),
            "text" => await Read(query.Select(TextRows.Bind(query, Id: query.Column(Orders.Id), Note: query.Column(Orders.Note))).AsAsyncEnumerable(), HashText),
            "money" => await Read(query.Select(MoneyRows.Bind(query, Id: query.Column(Orders.Id), Total: query.Column(Orders.Total))).AsAsyncEnumerable(), HashMoney),
            "json" => await Read(query.Select(JsonRows.Bind(query, Id: query.Column(Orders.Id), Settings: query.Column(Orders.Settings))).AsAsyncEnumerable(), HashJson),
            "wide" => await Read(query.SelectAll().AsAsyncEnumerable(), HashOrder),
            _ => throw new ArgumentException("Unknown dynamic case."),
        };
    }

    internal static async ValueTask<OperationResult> Read<T>(IAsyncEnumerable<T> stream, RowDigest<T> hash, int stopAfter = int.MaxValue)
    {
        var digest = Digest.Create(); long rows = 0, suspensions = 0;
        var reader = stream.GetAsyncEnumerator();
        try
        {
            while (rows < stopAfter)
            {
                var move = reader.MoveNextAsync();
                if (!move.IsCompleted) suspensions++;
                if (!await move.ConfigureAwait(false)) break;
                hash(ref digest, reader.Current); rows++;
            }
        }
        finally
        {
            var close = reader.DisposeAsync();
            if (!close.IsCompleted) suspensions++;
            await close.ConfigureAwait(false);
        }
        return new(rows, digest.Value, suspensions);
    }

    private static void HashInteger(ref Digest digest, long value) => digest.Integer(value);
    private static void HashText(ref Digest digest, TextRow value) { digest.Integer(value.Id); digest.Text(value.Note); }
    private static void HashMoney(ref Digest digest, MoneyRow value) { digest.Integer(value.Id); digest.Money(value.Total); }
    private static void HashJson(ref Digest digest, JsonRow value) { digest.Integer(value.Id); digest.Text(value.Settings.Channel); digest.Boolean(value.Settings.Gift); }
    private static void NullableInteger(ref Digest digest, long? value) { digest.Boolean(value.HasValue); if (value.HasValue) digest.Integer(value.Value); }
    private static void HashOrder(ref Digest digest, Order value)
    {
        digest.Integer(value.TenantId); digest.Integer(value.Id); digest.Integer(value.CreatedAt); NullableInteger(ref digest, value.CustomerId);
        digest.Money(value.Total); digest.Text(value.Note); digest.Integer(value.Version); digest.Text(value.Settings.Channel);
        digest.Boolean(value.Settings.Gift); digest.Text(value.InternalMemo);
    }

    public void Dispose()
    {
        _numeric?.Dispose(); _text?.Dispose(); _money?.Dispose(); _json?.Dispose(); _wide?.Dispose();
        foreach (var value in _advanced.Values) value.Dispose();
    }
}
