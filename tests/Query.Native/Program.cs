using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NativeWeave.Data;
using NativeWeave.Interop.Abi;
using NativeWeave.Query.Internal;

internal static class Program
{
    private static int _assertions;
    private static int _cases;
    private static readonly List<string> Completed = [];
    private static void Check(bool value, string reason) { _assertions++; if (!value) throw new InvalidOperationException(reason); }
    private static void Reject(uint status, Action action)
    {
        _assertions++; try { action(); } catch (BindingFailure e) when (e.Status == status) { return; }
        throw new InvalidOperationException("Expected native status " + status);
    }
    private static byte[] Clause(params ControlField[] fields) => OperandEncoding.Clause(fields);
    private static ControlField U(ushort id, ulong value) => Host.U(id, value);
    private static ControlField N(ushort id, uint value) => Host.N(id, value);
    private static ExpressionReference Expr(QueryBinding q, ulong id) => new(q, id);
    private static void Where(QueryBinding q, ExpressionReference p) => q.Clause(1, Clause(U(1, p.In(q))));
    private static void Order(QueryBinding q, ExpressionReference p) => q.Clause(3, Clause(U(1, p.In(q)), N(2, 0), N(3, 1), N(4, 1)));
    private static ExpressionReference Value(Host h, QueryBinding q, long value) => q.Value(OperandEncoding.Value(h.Profile, Codecs.Int64, 3, value));
    private readonly record struct Field(ExpressionReference Expression, string Name, uint Codec, bool Nullable = false);
    private static CodecDescriptor Codec(Field field) => (field.Codec switch
    {
        3 => Codecs.Int64.Descriptor, 4 => Codecs.Boolean.Descriptor, 5 => Codecs.String.Descriptor,
        11 => Codecs.Double.Descriptor, _ => throw new InvalidOperationException("Test field codec")
    }) with { Nullable = field.Nullable };
    private static Projection<string> Select(QueryBinding q, params Field[] fields)
    {
        var projection = Projection<string>.Create(fields.Select(f => new ResultField(f.Name, Codec(f))).ToArray(), row =>
        {
            var values = new string[fields.Length];
            for (var i = 0; i < fields.Length; i++)
            {
                if (row.IsNull(i)) { values[i] = "null"; continue; }
                var bytes = fields[i].Codec switch
                {
                    3 => Codecs.Int64.Encode(row.Read(i, Codecs.Int64)),
                    4 => Codecs.Boolean.Encode(row.Read(i, Codecs.Boolean)),
                    5 => Codecs.String.Encode(row.Read(i, Codecs.String)),
                    11 => Codecs.Double.Encode(row.Read(i, Codecs.Double)),
                    _ => throw new InvalidOperationException("Codec")
                };
                values[i] = Convert.ToHexStringLower(bytes!);
            }
            return string.Join('|', values);
        });
        var expressions = fields.Select(f => f.Expression).ToArray();
        q.Clause(6, Clause(Host.B(1, OperandEncoding.Tokens(q, expressions), 8),
            Host.B(2, OperandEncoding.Projection(projection, c => c.Id, expressions.Select(e => e.In(q)).ToArray()))));
        return projection;
    }
    private static void Verify(Host host, QueryBinding q, Projection<string> projection, string label, JsonElement oracle)
    {
        Check(oracle.GetProperty("label").GetString() == label, "Native oracle order " + label);
        var sql = host.Render(q);
        Check(sql == oracle.GetProperty("sql").GetString(), "Direct Rust / C# ABI SQL parity: " + label + "\n" + sql);
        using var plan = q.Seal(0);
        var rows = host.Rows(plan, projection, Host.Parameters(host.Profile));
        var expected = oracle.GetProperty("rows").EnumerateArray().Select(e => e.GetString()!).ToList();
        if (label == "set-operation") { rows.Sort(StringComparer.Ordinal); expected.Sort(StringComparer.Ordinal); }
        Check(rows.SequenceEqual(expected), "Actual provider rows: " + label);
        Completed.Add((host.Profile == 1 ? "sqlite/" : "postgres/") + label); _cases++;
    }
    private sealed record Parent(long Id, string Name);
    private sealed record Child(long Id, long? ParentId);
    private sealed record CteRow(long Id);
    private sealed record ChangedEntity(long Id, string Name);
    private static (Table<CteRow> Table, Column<CteRow, long> Id) CteShape(string name)
    {
        var builder = new TableBuilder<CteRow>(name); var id = builder.Column("Id", Codecs.Int64);
        return (builder.Build(r => new(r.Get(id)), (r, v) => r.Set(id, v.Id)), id);
    }
    private static void Scenarios(Host host, string oraclePath)
    {
        using var oracle = JsonDocument.Parse(File.ReadAllBytes(oraclePath)); var expected = oracle.RootElement.EnumerateArray().ToArray(); var index = 0;
        var pb = new TableBuilder<Parent>("Parents"); var pid = pb.Column("Id", Codecs.Int64, new() { IsPrimaryKey = true }); var name = pb.Column("Name", Codecs.String);
        var parent = pb.Build(r => new(r.Get(pid), r.Get(name)), (r, v) => { r.Set(pid, v.Id); r.Set(name, v.Name); });
        var cb = new TableBuilder<Child>("Children"); var cid = cb.Column("Id", Codecs.Int64, new() { IsPrimaryKey = true }); var fk = cb.Column("ParentId", Codecs.Nullable(Codecs.Int64));
        var child = cb.Build(r => new(r.Get(cid), r.Get(fk)), (r, v) => { r.Set(cid, v.Id); r.Set(fk, v.ParentId); });
        using var model = MetadataBindings.RegisterModel(host.Source, parent); using var children = MetadataBindings.RegisterModel(host.Source, child);
        QueryBinding Query() => QueryBinding.Create(host.Session, model, parent.Identity.Id);
        void Case(QueryBinding q, Projection<string> projection, string label) => Verify(host, q, projection, label, expected[index++]);
        (QueryBinding Query, Projection<string> Projection) Ids(uint op, long threshold)
        {
            var q = Query(); var id = q.Column(pid.Id); var v = Value(host, q, threshold); Where(q, q.Binary(op, id, v));
            return (q, Select(q, new Field(id, "Id", 3)));
        }
        host.Sql("CREATE TEMP TABLE \"Parents\" (\"Id\" BIGINT PRIMARY KEY, \"Name\" TEXT NOT NULL)");
        host.Sql("CREATE TEMP TABLE \"Children\" (\"Id\" BIGINT PRIMARY KEY, \"ParentId\" BIGINT NULL)");
        host.Sql("INSERT INTO \"Parents\" VALUES (0,'Zero'),(1,'Alice'),(2,'O''Reilly 🦀')");
        host.Sql("INSERT INTO \"Children\" VALUES (10,1),(11,NULL)");
        using (var q = Query())
        {
            var id = q.Column(pid.Id); var one = Value(host, q, 1); Where(q, q.Binary(4, id, one)); Order(q, id);
            Case(q, Select(q, new Field(id, "Id", 3), new(q.Column(name.Id), "Name", 5)), "filter-order");
        }
        foreach (var (op, values) in new (uint, long[])[] { (1, [1, 2]), (2, []), (3, [1, 2]) })
        {
            using var q = Query(); var id = q.Column(pid.Id);
            var bytes = DbWire.Encode(new(PayloadKind.ExpressionValues, host.Profile, new byte[32], 0, 0, [new(1, Codecs.Int64.Descriptor, 3)]), (uint)values.Length,
                values.Select(v => (ReadOnlyMemory<byte>?)Codecs.Int64.Encode(v)).ToArray());
            Where(q, q.List(op, id, bytes)); Order(q, id); Case(q, Select(q, new Field(id, "Id", 3)), "membership");
        }
        using (var q = Query())
        {
            var left = q.Column(pid.Id); var imported = q.Import(children, 1);
            var reserved = q.Clause(18, Clause(U(1, imported.In(q)), N(2, child.Identity.Id), Host.T(3, "c"), N(4, 1)));
            var right = q.Column(fk.Id, new(q, reserved)); var on = q.Binary(1, q.Unary(4, left), right);
            q.Clause(7, Clause(U(1, reserved), U(2, on.In(q)))); Reject(3, () => q.Unary(1, right));
            var joined = q.Column(cid.Id, new(q, reserved)); var matched = Expr(q, q.Clause(20, Clause(U(1, reserved)))); Order(q, left);
            Case(q, Select(q, new Field(left, "Id", 3), new(joined, "child", 3, true), new(matched, "matched", 4)), "nullable-left-join");
        }
        using (var q = Query())
        {
            var count = Expr(q, q.Clause(13, Clause(N(1, 0), N(3, 0), N(5, 3)))); var id = q.Column(pid.Id);
            var sum = Expr(q, q.Clause(13, Clause(N(1, 2), U(2, id.In(q)), N(3, 0), N(5, 3))));
            Case(q, Select(q, new Field(count, "count", 3), new(sum, "sum", 3, true)), "aggregate");
        }
        using (var q = Query())
        {
            var id = q.Column(pid.Id); var window = q.Clause(11, Clause(N(1, 0)));
            q.Clause(11, Clause(N(1, 2), U(2, window), U(3, id.In(q)), N(4, 0), N(5, 1)));
            var rank = q.Clause(13, Clause(N(1, 6), N(3, 0), N(5, 3))); var over = Expr(q, q.Clause(21, Clause(U(1, rank), U(2, window))));
            q.Clause(11, Clause(N(1, 1), U(2, window), U(3, id.In(q)))); Order(q, id);
            Case(q, Select(q, new Field(id, "Id", 3), new(over, "rank", 3)), "window-snapshot");
        }
        for (uint op = 0; op < 4; op++)
        {
            var a = Ids(4, 1); using var left = a.Query; var b = Ids(1, 0); using var right = b.Query;
            var imported = left.SnapshotAndImport(right); left.Clause(9, Clause(U(1, imported.In(left)), N(2, op)));
            Case(left, a.Projection, "set-operation");
        }
        for (uint op = 0; op < 2; op++)
        {
            var source = Ids(1, 1); using var body = source.Query; using var q = Query(); var id = q.Column(pid.Id);
            var imported = q.SnapshotAndImport(body); Value(host, body, 2);
            var f = new List<ControlField> { U(1, imported.In(q)), N(2, op) }; if (op == 1) f.Add(U(3, id.In(q)));
            Where(q, Expr(q, q.Clause(14, Clause([.. f])))); Order(q, id); Case(q, Select(q, new Field(id, "Id", 3)), "subquery");
        }
        {
            var source = Ids(4, 1); using var body = source.Query; var declared = CteShape("SelectedParents");
            using var cm = MetadataBindings.RegisterModel(host.Source, declared.Table); using var cte = host.Cte(cm, declared.Table.Identity.Id, body);
            var changed = body.Binary(1, body.Column(pid.Id), Value(host, body, 0)); Where(body, changed);
            using var q = QueryBinding.Create(host.Session, cm, declared.Table.Identity.Id); var imported = q.Import(cte, 3); q.Clause(10, Clause(U(1, imported.In(q))));
            var id = q.Column(declared.Id.Id); Order(q, id); Case(q, Select(q, new Field(id, "Id", 3)), "cte-snapshot");
        }
        {
            var declared = CteShape("Numbers"); using var cm = MetadataBindings.RegisterModel(host.Source, declared.Table); var a = Ids(1, 0); using var anchor = a.Query;
            Reject(3, () => { using var invalid = host.Cte(cm, declared.Table.Identity.Id, anchor, true); });
            using var member = QueryBinding.Create(host.Session, cm, declared.Table.Identity.Id); var id = member.Column(declared.Id.Id);
            var one = Value(host, member, 1); var three = Value(host, member, 3); var next = member.Binary(7, id, one); Where(member, member.Binary(5, id, three));
            Select(member, new Field(next, "Id", 3)); var import = anchor.SnapshotAndImport(member); anchor.Clause(9, Clause(U(1, import.In(anchor)), N(2, 1)));
            using var cte = host.Cte(cm, declared.Table.Identity.Id, anchor, true); using var q = QueryBinding.Create(host.Session, cm, declared.Table.Identity.Id);
            var imported = q.Import(cte, 3); q.Clause(10, Clause(U(1, imported.In(q)))); var col = q.Column(declared.Id.Id); Order(q, col);
            Case(q, Select(q, new Field(col, "Id", 3)), "recursive-cte");
        }
        foreach (var direct in new[] { false, true })
        {
            using var q = Query(); var id = q.Column(pid.Id); var window = q.Clause(11, Clause(N(1, 0)));
            q.Clause(11, Clause(N(1, 2), U(2, window), U(3, id.In(q)), N(4, 0), N(5, 1)));
            q.Clause(11, Clause(N(1, 3), U(2, window), N(6, 0), N(7, 1)));
            var f = new List<ControlField> { N(1, 2), U(2, id.In(q)), N(3, 0) }; if (direct) f.Add(U(4, window)); f.Add(N(5, 3));
            var sum = q.Clause(13, Clause([.. f])); if (!direct) sum = q.Clause(21, Clause(U(1, sum), U(2, window))); Order(q, id);
            Case(q, Select(q, new Field(Expr(q, sum), "sum", 3, true)), "window-aggregate-cast");
        }
        foreach (var (operation, text) in new (uint, string)[] { (0, "Reilly"), (1, "Ali"), (2, "🦀"), (3, "O%"), (0, "%"), (0, "_"), (0, "';DROP") })
        {
            using var q = Query(); var id = q.Column(pid.Id); var value = q.Column(name.Id);
            var bytes = OperandEncoding.Value(host.Profile, Codecs.String, 5, text);
            var predicate = Expr(q, q.Clause(16, Clause(N(1, operation), U(2, value.In(q)), Host.B(3, bytes, 6), N(4, 92))));
            Where(q, predicate); Order(q, id); Case(q, Select(q, new Field(id, "Id", 3)), "text-escaped-values");
        }
        using (var q = Query())
        {
            var id = q.Column(pid.Id); var one = Value(host, q, 1); var zero = Value(host, q, 0); var condition = q.Binary(3, id, one);
            var result = Expr(q, q.Clause(15, Clause(U(1, condition.In(q)), U(2, one.In(q)), U(3, zero.In(q))))); Order(q, id);
            Case(q, Select(q, new Field(result, "case", 3)), "case");
        }
        using (var q = Query())
        {
            var id = q.Column(pid.Id); q.Clause(4, Clause(U(1, id.In(q)))); var count = Expr(q, q.Clause(13, Clause(N(1, 0), N(3, 0), N(5, 3))));
            var having = q.Binary(3, count, Value(host, q, 0)); q.Clause(2, Clause(U(1, having.In(q)))); Order(q, id);
            Case(q, Select(q, new Field(id, "Id", 3)), "group-having");
        }
        Check(index == expected.Length, "Every native SQL/result oracle was replayed through C# ABI.");
        using (var q = Query())
        {
            var id = q.Column(pid.Id); var parameter = q.Parameter(17, "minimum"u8, 3, false); Where(q, q.Binary(4, id, parameter)); Order(q, id);
            var projection = Select(q, new Field(id, "Id", 3)); using var plan = q.Seal(8);
            var schema = "{\"version\":1,\"parameters\":[{\"id\":17,\"name\":\"minimum\",\"codec\":3,\"codec_config\":null,\"nullable\":false}]}"u8.ToArray();
            byte[] Args(long v) => DbWire.Encode(new(PayloadKind.Parameters, host.Profile, SHA256.HashData(schema), 0, 0, [new(17, Codecs.Int64.Descriptor, 3)]) { ExactRows = 1 }, 1, [Codecs.Int64.Encode(v)]);
            Check(host.Rows(plan, projection, Args(1)).Count == 2, "Prepared execution1");
            Check(host.Rows(plan, projection, Args(2)).Single() == Convert.ToHexStringLower(Codecs.Int64.Encode(2L)!), "Independent prepared values");
            Reject(3, () => host.Rows(plan, projection, Host.Parameters(host.Profile)));
        }
        using (var q = Query())
        {
            var id = q.Column(pid.Id); var original = OperandEncoding.Value(host.Profile, Codecs.Int64, 3, 2L); var value = q.Value(original); Array.Fill(original, (byte)0);
            Where(q, q.Binary(1, id, value)); var projection = Select(q, new Field(id, "Id", 3)); var before = host.Render(q);
            Reject(6, () => q.Clause(999, Clause())); Reject(3, () => q.Clause(5, Clause(U(1, ulong.MaxValue))));
            if (host.Profile == 1) Reject(6, () => q.Clause(12, Clause(N(1, 0), N(2, 0))));
            Check(host.Render(q) == before, "Failed mutation is atomic"); using var plan = q.Seal(0);
            Check(host.Rows(plan, projection, Host.Parameters(host.Profile)).Count == 1, "Borrowed literal snapshot survived caller mutation");
        }
        // A changed ordinary C# entity uses the same loaded native image and table,
        // with a distinct canonical metadata identity and statically direct constructor.
        var changedBuilder = new TableBuilder<ChangedEntity>("Parents"); var changedId = changedBuilder.Column("Id", Codecs.Int64); var changedName = changedBuilder.Column("Name", Codecs.String);
        var changedTable = changedBuilder.Build(r => new(r.Get(changedId), r.Get(changedName)), (r, v) => { r.Set(changedId, v.Id); r.Set(changedName, v.Name); });
        using var changedModel = MetadataBindings.RegisterModel(host.Source, changedTable);
        using (var q = QueryBinding.Create(host.Session, changedModel, changedTable.Identity.Id))
        {
            var id = q.Column(changedId.Id); var text = q.Column(changedName.Id); var tokens = new[] { id, text };
            q.Clause(6, Clause(Host.B(1, OperandEncoding.Tokens(q, tokens), 8), Host.B(2, OperandEncoding.Projection(changedTable.Row, c => c.Id, tokens.Select(e => e.In(q)).ToArray()))));
            using var plan = q.Seal(0); var rows = host.Rows(plan, changedTable.Row, Host.Parameters(host.Profile));
            Check(rows.Count == 3 && rows.Any(r => r.Name == "O'Reilly 🦀"), "New C# entity actual DTO materialization");
        }
    }
    public static int Main(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("native-module oracle-directory result-directory [--require-aot]");
        if (args.Contains("--require-aot", StringComparer.Ordinal)) Check(!RuntimeFeature.IsDynamicCodeSupported, "Actual Native AOT execution");
        var before = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(args[0])));
        var url = Environment.GetEnvironmentVariable("NATIVEWEAVE_TEST_POSTGRES_URL") ?? throw new InvalidOperationException("Real PostgreSQL test endpoint is required.");
        var retired = 0; var buffers = 0;
        foreach (var profile in new uint[] { 1, 2 })
        {
            using var host = new Host(args[0], profile, profile == 1 ? ":memory:" : url);
            Scenarios(host, Path.Combine(args[1], profile == 1 ? "sqlite.json" : "postgres.json"));
            NativeQueryExamples.Run(host);
            retired += host.RetiredOperations; buffers += host.ReleasedBuffers;
        }
        var after = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(args[0]))); Check(before == after, "Same native hash after ordinary entity changes");
        var result = new TestResult(!RuntimeFeature.IsDynamicCodeSupported, _cases, _assertions, retired, buffers, before, after, Completed);
        Directory.CreateDirectory(args[2]); File.WriteAllText(Path.Combine(args[2], "result.json"), JsonSerializer.Serialize(result, TestJson.Default.TestResult));
        Console.WriteLine($"PASS B11 actual C# ABI: {_cases} SQL/result cases; {_assertions} assertions; {retired} retired operations; {buffers} released buffers; AOT={result.NativeAot}");
        return 0;
    }
}
internal sealed record TestResult(bool NativeAot, int Cases, int Assertions, int NativeRetirementReceipts, int NativeBuffersReleased, string NativeSha256Before, string NativeSha256After, List<string> Completed);
[JsonSerializable(typeof(TestResult))]
internal partial class TestJson : JsonSerializerContext { }
