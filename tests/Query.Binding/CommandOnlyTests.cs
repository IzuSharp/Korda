using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Korda.Data;
using Korda.Interop.Abi;
using Korda.Query.Internal;

// The native direct composition exports are intentionally absent from this
// resolver. Successful composition therefore proves there is no per-node native
// fallback; the three counted boundaries forward into the real C++ decoder.
internal static unsafe class CommandOnlyTests
{
    private static QueryCommandCalls _native = null!;
    private static int _compiles, _renders, _ctes, _assertions;
    [ThreadStatic] private static bool _observe;
    [ThreadStatic] private static CapturedCommand? _observed;
    private sealed record CapturedCommand(byte[] Bytes, QueryCommandDependency[] Owners);
    private static void Check(bool condition, string message)
    { _assertions++; if (!condition) throw new InvalidOperationException(message); }
    private static void Reject<T>(Action action) where T : Exception
    { _assertions++; try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static bool DirectComposition(string symbol) => symbol is "nw_db_query_create_v1" or "nw_db_expr_column_v1"
        or "nw_db_expr_value_v1" or "nw_db_expr_parameter_v1" or "nw_db_expr_binary_v1" or "nw_db_expr_unary_v1"
        or "nw_db_expr_list_v1" or "nw_db_query_import_v1" or "nw_db_query_clause_v1" or "nw_db_query_seal_v1"
        or "nw_db_query_clone_v1" or "nw_db_query_render_v1" or "nw_db_cte_create_v1";

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Compile(nint session, NwBytesViewV1 command, QueryCommandDependency* dependencies, uint count, uint terminal, nint* output, NwErrorInfoV1* error)
    { Interlocked.Increment(ref _compiles); Observe(command, dependencies, count); return _native.Compile(session, command, dependencies, count, terminal, output, error); }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Render(nint session, NwBytesViewV1 command, QueryCommandDependency* dependencies, uint count, nint* output, NwErrorInfoV1* error)
    { Interlocked.Increment(ref _renders); Observe(command, dependencies, count); return _native.Render(session, command, dependencies, count, output, error); }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Cte(nint session, NwBytesViewV1 command, QueryCommandDependency* dependencies, uint count, nint declared, uint tableId, uint recursive, nint* output, NwErrorInfoV1* error)
    { Interlocked.Increment(ref _ctes); return _native.Cte(session, command, dependencies, count, declared, tableId, recursive, output, error); }

    private static void Observe(NwBytesViewV1 command, QueryCommandDependency* dependencies, uint count)
    {
        if (_observe) _observed = new(new ReadOnlySpan<byte>(command.Data, checked((int)command.Length)).ToArray(),
            new ReadOnlySpan<QueryCommandDependency>(dependencies, checked((int)count)).ToArray());
    }

    private static CapturedCommand RenderCommand(QueryBinding query, out string sql)
    {
        _observe = true;
        try { sql = Sql(query); return _observed ?? throw new InvalidOperationException("The complete command was not observed."); }
        finally { _observe = false; _observed = null; }
    }

    private static readonly string[] Capabilities = ["db.metadata.v1", "db.query.v1", "db.scalar-codecs.v1", "db.query.command.v1",
        "db.null-safe-equality.v1", "db.relationships.explicit.v1", "db.set-operations.v1", "db.cte.v1", "db.recursive-cte.v1",
        "db.window.v1", "db.row-lock.v1", "db.prepared-shape.v1"];

    private static DatabaseOwner Source(NativeCalls calls, uint profile, QueryCommandCalls commands, int depth = 64, int nodes = 4096)
    {
        nint result = 0; var error = BindingChecks.Error(); var value = (byte)profile;
        BindingChecks.Status(calls.nw_db_source_create_v1(new() { Data = &value, Length = 1 }, &result, &error), error);
        var binding = new SourceBinding(calls, profile) { Commands = new(commands, nodes, depth, 4096, 4 * 1024 * 1024, Capabilities) };
        binding.Commands.RegisterCodec(3, Codecs.Int64.Descriptor); binding.Commands.RegisterCodec(4, Codecs.Boolean.Descriptor);
        binding.Commands.RegisterCodec(5, Codecs.String.Descriptor);
        return new(binding, DatabaseOwnerKind.Source, result);
    }

    private static DatabaseOwner Session(DatabaseOwner source, nint library)
    {
        var create = (delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint>)NativeLibrary.GetExport(library, "nw_test_session_v1");
        using var owner = source.Borrow(DatabaseOwnerKind.Source); nint result = 0; var error = BindingChecks.Error();
        BindingChecks.Status(create(owner.Pointer, &result, &error), error); return new(source.Source, DatabaseOwnerKind.Session, result);
    }

    private static void Select(QueryBinding query, Projection<Parent> projection, params ExpressionReference[] expressions) =>
        query.Clause(6, OperandEncoding.Clause(OperandEncoding.Bytes(1, OperandEncoding.Tokens(query, expressions), 8),
            OperandEncoding.Bytes(2, OperandEncoding.Projection(projection, codec => codec.Id, expressions.Select(expression => expression.Value).ToArray()))));

    private static string Sql(QueryBinding query)
    {
        var result = query.Render();
        try
        {
            NwBytesViewV1 view = default; var error = BindingChecks.Error();
            BindingChecks.Status(query.Calls.nw_db_buffer_view_v1(result, &view, &error), error);
            return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(view.Data, checked((int)view.Length)));
        }
        finally { query.Calls.nw_db_buffer_release_v1(result); }
    }

    private static string Composed(QueryBinding query, Table<Parent> table, Column<Parent, long> id, Column<Parent, string> name)
    {
        var key = query.Column(id.Id); var text = query.Column(name.Id);
        var literal = query.Value(OperandEncoding.Value(query.Source.SemanticProfile, Codecs.Int64, 3, 19L));
        var parameter = query.Parameter(71, "minimum"u8, 3, false);
        var predicate = query.Binary(12, query.Binary(1, key, literal), query.Binary(4, key, parameter));
        query.Clause(1, OperandEncoding.Clause(OperandEncoding.U64(1, predicate.Value)));
        Select(query, table.Row, key, text);
        var self = query.SnapshotAndImport(query);
        query.Clause(9, OperandEncoding.Clause(OperandEncoding.U64(1, self.Value), OperandEncoding.U32(2, 1)));
        return Sql(query);
    }

    internal static int Run(nint library, Table<Parent> table, Column<Parent, long> id, Column<Parent, string> name)
    {
        _assertions = _compiles = _renders = _ctes = 0;
        nint Resolve(string symbol) => NativeLibrary.TryGetExport(library, symbol, out var pointer) ? pointer : 0;
        _native = new(Resolve);
        var counted = new QueryCommandCalls(symbol => symbol switch
        {
            "nw_db_query_command_compile_v1" => (nint)(delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, QueryCommandDependency*, uint, uint, nint*, NwErrorInfoV1*, uint>)&Compile,
            "nw_db_query_command_render_v1" => (nint)(delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, QueryCommandDependency*, uint, nint*, NwErrorInfoV1*, uint>)&Render,
            "nw_db_query_command_cte_v1" => (nint)(delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, QueryCommandDependency*, uint, nint, uint, uint, nint*, NwErrorInfoV1*, uint>)&Cte,
            _ => 0
        });
        var calls = new NativeCalls("command.only.fixture", symbol => DirectComposition(symbol) ? 0 : Resolve(symbol));
        var direct = new NativeCalls("direct.fixture", Resolve);
        foreach (var profile in new uint[] { 1, 2 })
        {
            using var source = Source(calls, profile, counted); using var session = Session(source, library);
            using var model = MetadataBindings.RegisterModel(source, table);
            using var query = QueryBinding.CreateBuffered(session, model, table.Identity.Id);
            var compiles = _compiles; var renders = _renders;
            var actual = Composed(query, table, id, name);
            Check(_compiles == compiles && _renders == renders + 1, "Self-import and all composition reach exactly one complete render command.");
            using (var legacySource = Source(direct, profile, _native))
            using (var legacySession = Session(legacySource, library))
            using (var legacyModel = MetadataBindings.RegisterModel(legacySource, table))
            using (var legacy = QueryBinding.Create(legacySession, legacyModel, table.Identity.Id))
                Check(Composed(legacy, table, id, name) == actual, "Clone/self-import preserve exact legacy SQL and placeholder order.");
            query.Column(id.Id);
            using (var clone = query.CloneSelected())
            using (var plan = clone.Seal(8)) Check(_compiles == compiles + 1, "Clone creates no native query and Prepare sends one complete compile command.");
            Check(Sql(query) == actual, "Clone and ToSql preserve source mutability and SQL.");
            var before = _compiles;
            using (var plan = query.Seal(8)) Check(_compiles == before + 1, "Sealing submits exactly one command.");
            Reject<InvalidOperationException>(() => query.Column(id.Id));

            using var recovery = QueryBinding.CreateBuffered(session, model, table.Identity.Id);
            var key = recovery.Column(id.Id); var text = recovery.Column(name.Id);
            Reject<ArgumentException>(() => recovery.Unary(3, key));
            Reject<ArgumentException>(() => recovery.Clause(3, OperandEncoding.Clause(OperandEncoding.U64(1, key.Value), OperandEncoding.U32(2, 8), OperandEncoding.U32(3, 0), OperandEncoding.U32(4, 0))));
            recovery.Clause(4, OperandEncoding.Clause(OperandEncoding.U64(1, key.Value)));
            Select(recovery, table.Row, key, text);
            before = _compiles;
            Reject<ArgumentException>(() => recovery.Seal(8));
            Check(_compiles == before, "Recoverable grouping failure remains at Seal before command submission.");
            recovery.Clause(4, OperandEncoding.Clause(OperandEncoding.U64(1, text.Value)));
            using (var plan = recovery.Seal(8)) Check(_compiles == before + 1, "Rejected mutations and a failed Seal leave the builder usable.");

            SingleRoot(session, model, table, id, name);
            SingleRootQuota(calls, counted, library, profile, table, id, name);

            using var foreignSource = Source(calls, profile, counted); using var foreignSession = Session(foreignSource, library);
            using var foreignModel = MetadataBindings.RegisterModel(foreignSource, table);
            using var foreign = QueryBinding.CreateBuffered(foreignSession, foreignModel, table.Identity.Id);
            using var own = QueryBinding.CreateBuffered(session, model, table.Identity.Id);
            Reject<ArgumentException>(() => own.Binary(1, own.Column(id.Id), foreign.Column(id.Id)));
            Reject<ArgumentException>(() => own.Import(foreignModel, 1));

            var declaration = new TableBuilder<Parent>("CommandParents");
            var declaredId = declaration.Column("Id", Codecs.Int64); var declaredName = declaration.Column("Name", Codecs.String);
            var declared = declaration.Build(row => new(row.Get(declaredId), row.Get(declaredName)), (row, value) => { row.Set(declaredId, value.Id); row.Set(declaredName, value.Name); });
            using var declaredModel = MetadataBindings.RegisterModel(source, declared);
            using var body = QueryBinding.CreateBuffered(session, model, table.Identity.Id);
            Select(body, table.Row, body.Column(id.Id), body.Column(name.Id));
            var cteCount = _ctes;
            using var cte = body.CreateCte(declaredModel, declared.Identity.Id, false);
            Check(_ctes == cteCount + 1, "CTE creation sends its complete immutable body once.");
            body.Column(id.Id);
            using var outer = QueryBinding.CreateBuffered(session, declaredModel, declared.Identity.Id);
            var imported = outer.Import(cte, 3); cte.Dispose();
            var with = OperandEncoding.Clause(OperandEncoding.U64(1, imported.Value)); outer.Clause(10, with);
            Select(outer, declared.Row, outer.Column(declaredId.Id), outer.Column(declaredName.Id));
            var cteSql = Sql(outer);
            Check(cteSql.Contains("WITH", StringComparison.Ordinal) && cteSql.Contains("CommandParents", StringComparison.Ordinal), "An admitted CTE survives disposal of its public owner.");
            Reject<ArgumentException>(() => outer.Clause(10, with));
            Check(Sql(outer) == cteSql, "A duplicate CTE mutation fails immediately and preserves the command.");
        }
        Check(_compiles > 0 && _renders > 0 && _ctes == 2, "All three complete command boundaries executed the C++ decoder.");
        foreach (var profile in new uint[] { 1, 2 })
        {
            Check(LargeCteReuse(calls, counted, library, profile, table, id, name, true)
                == LargeCteReuse(direct, _native, library, profile, table, id, name, false),
                "A body and two separately compiled CTEs retain the legacy shared expanded-literal budget and SQL.");
            IndependentCtes(calls, counted, library, profile, table, id, name, true);
            IndependentCtes(direct, _native, library, profile, table, id, name, false);
            Check(SharedImportedSnapshot(calls, counted, library, profile, table, id, name, true)
                == SharedImportedSnapshot(direct, _native, library, profile, table, id, name, false),
                "Clones of an outer query share imported snapshot origins after the original subquery and first plan are disposed.");
        }
        MissingCapability(calls, counted, library, table);
        Console.WriteLine($"PASS complete-command-only: assertions={_assertions}, compile={_compiles}, render={_renders}, cte={_ctes}");
        return _assertions;
    }

    private static void SingleRoot(DatabaseOwner session, DatabaseOwner model, Table<Parent> table,
        Column<Parent, long> id, Column<Parent, string> name)
    {
        using var query = QueryBinding.CreateBuffered(session, model, table.Identity.Id);
        var key = query.Column(id.Id); var text = query.Column(name.Id);
        var value = query.Value(OperandEncoding.Value(query.Source.SemanticProfile, Codecs.Int64, 3, 19L));
        var parameter = query.Parameter(71, "minimum"u8, 3, false);
        var predicate = query.Binary(12, query.Binary(1, key, value), query.Binary(4, key, parameter));
        query.Clause(1, OperandEncoding.Clause(OperandEncoding.U64(1, predicate.Value)));
        Select(query, table.Row, key, text);
        var renders = _renders;
        var first = RenderCommand(query, out var expected);
        Check(_renders == renders + 1 && first.Bytes.Length <= 4096 && DbWire.R32(first.Bytes, 12) == 1
            && DbWire.R32(first.Bytes, 24) == 0 && first.Owners.Length == 1
            && first.Owners[0].Kind == 1 && first.Owners[0].Reserved == 0,
            "A private single root submits one complete bounded command with one initialized model sidecar.");
        query.Column(id.Id);
        var continued = RenderCommand(query, out var afterRender);
        Check(afterRender == expected && continued.Bytes.Length > first.Bytes.Length, "Rendering keeps the tape mutable and preserves unused records.");
        using var clone = query.CloneSelected();
        var shared = RenderCommand(clone, out var clonedSql);
        Check(clonedSql == expected && shared.Bytes.AsSpan().SequenceEqual(continued.Bytes)
            && shared.Owners.Length == 1 && shared.Owners[0].Owner == continued.Owners[0].Owner,
            "Private stack and shared graph encoders submit identical bytes, origin ordinals and model identity.");
        var compiles = _compiles;
        using var retained = clone.Seal(8);
        Check(_compiles == compiles + 1, "A shared clone still compiles in one command.");
        clone.Dispose(); retained.Dispose();
        var captured = RenderCommand(query, out var capturedSql);
        Check(capturedSql == expected && DbWire.R32(captured.Bytes, 24) == 2 && captured.Owners.Length == 2,
            "A sole remaining builder still seeds captured literal and parameter origins after public plan disposal.");
        using (var plan = query.Seal(8)) Check(_compiles == compiles + 2, "Captured origins retain the complete graph compile path.");
        query.Dispose(); Reject<ObjectDisposedException>(() => { using var admission = query.Enter(); });

        // This canonical two-column command has 666 bytes besides its string.
        // Exercise both sides of the fixed scratch boundary with the same SQL.
        foreach (var characters in new[] { 3430, 3431 })
        {
            using var large = QueryBinding.CreateBuffered(session, model, table.Identity.Id);
            _ = large.Value(OperandEncoding.Value(large.Source.SemanticProfile, Codecs.String, 5, new string('x', characters)));
            Select(large, table.Row, large.Column(id.Id), large.Column(name.Id));
            var largeFrame = RenderCommand(large, out var largeSql);
            Check(largeFrame.Bytes.Length == 666 + characters && DbWire.R32(largeFrame.Bytes, 12) == 1
                && DbWire.R32(largeFrame.Bytes, 24) == 0 && largeFrame.Owners.Length == 1,
                "Exact-size and oversized private roots preserve their unused literal at the stack/fallback boundary.");
            using var largeClone = large.CloneSelected();
            var largeShared = RenderCommand(largeClone, out var largeCloneSql);
            Check(largeCloneSql == largeSql && largeShared.Bytes.AsSpan().SequenceEqual(largeFrame.Bytes),
                "Stack-sized and oversized commands match the shared graph encoder byte for byte.");
            largeClone.Dispose();
            using var largePlan = large.Seal(8);
        }
    }

    private static void SingleRootQuota(NativeCalls calls, QueryCommandCalls commands, nint library, uint profile,
        Table<Parent> table, Column<Parent, long> id, Column<Parent, string> name)
    {
        using var source = Source(calls, profile, commands, nodes: 6); using var session = Session(source, library);
        using var model = MetadataBindings.RegisterModel(source, table);
        using var query = QueryBinding.CreateBuffered(session, model, table.Identity.Id);
        Select(query, table.Row, query.Column(id.Id), query.Column(name.Id));
        query.Column(id.Id);
        var before = _compiles;
        Reject<BindingFailure>(() => query.Value(OperandEncoding.Value(profile, Codecs.Int64, 3, 42L)));
        var frame = RenderCommand(query, out _);
        Check(DbWire.R32(frame.Bytes, 20) == 4 && _compiles == before, "Rejected capacity mutations do not add records or submit a command.");
        using var plan = query.Seal(8);
        Check(_compiles == before + 1, "A private root at its finite node quota can still be sealed after rejection.");
    }

    private static Table<Parent> Declaration(string name)
    {
        var builder = new TableBuilder<Parent>(name); var id = builder.Column("Id", Codecs.Int64); var text = builder.Column("Name", Codecs.String);
        return builder.Build(row => new(row.Get(id), row.Get(text)), (row, value) => { row.Set(id, value.Id); row.Set(text, value.Name); });
    }
    private static QueryBinding Query(DatabaseOwner session, DatabaseOwner model, uint table, bool buffered) =>
        buffered ? QueryBinding.CreateBuffered(session, model, table) : QueryBinding.Create(session, model, table);
    private static void LargeUnusedLiteral(QueryBinding body, Table<Parent> table, Column<Parent, long> id, Column<Parent, string> name)
    {
        // The escaped pattern is 2,200,002 bytes. Two raw command bodies fit the
        // 4 MiB owned-byte quota; two independent expanded slots do not. Keep the
        // expression unused so final SQL parameter-frame size is not the limit.
        var text = body.Column(name.Id);
        body.Clause(16, OperandEncoding.Clause(OperandEncoding.U32(1, 0), OperandEncoding.U64(2, text.Value),
            OperandEncoding.Bytes(3, OperandEncoding.Value(body.Source.SemanticProfile, Codecs.String, 5, new string('%', 1_100_000)), 6), OperandEncoding.U32(4, '\\')));
        Select(body, table.Row, body.Column(id.Id), text);
    }
    private static string LargeCteReuse(NativeCalls calls, QueryCommandCalls commands, nint library, uint profile,
        Table<Parent> table, Column<Parent, long> id, Column<Parent, string> name, bool buffered)
    {
        using var source = Source(calls, profile, commands); using var session = Session(source, library);
        using var model = MetadataBindings.RegisterModel(source, table);
        var a = Declaration("LargeOriginA"); var b = Declaration("LargeOriginB");
        using var ma = MetadataBindings.RegisterModel(source, a); using var mb = MetadataBindings.RegisterModel(source, b);
        using var body = Query(session, model, table.Identity.Id, buffered); LargeUnusedLiteral(body, table, id, name);
        using var first = body.CloneSelected(); using var second = body.CloneSelected();
        DatabaseOwner ca, cb;
        if (buffered)
        {
            // These builders share origins, but have independent admission. The
            // cold origin gate must publish one identity even on concurrent CTEs.
            var left = Task.Run(() => first.CreateCte(ma, a.Identity.Id, false));
            var right = Task.Run(() => second.CreateCte(mb, b.Identity.Id, false));
            Task.WhenAll(left, right).GetAwaiter().GetResult(); ca = left.Result; cb = right.Result;
        }
        else { ca = first.CreateCte(ma, a.Identity.Id, false); cb = second.CreateCte(mb, b.Identity.Id, false); }
        using (ca) using (cb)
        {
            using (var copy = body.CloneSelected())
            {
                var dependency = copy.Import(ca, 3); copy.Clause(10, OperandEncoding.Clause(OperandEncoding.U64(1, dependency.Value)));
                Check(Sql(copy).Contains("WITH", StringComparison.Ordinal), "A compiled CTE and its source clone share one expanded literal allocation.");
            }
            body.Dispose(); first.Dispose(); second.Dispose();
            using var outer = Query(session, model, table.Identity.Id, buffered);
            var ia = outer.Import(ca, 3); var ib = outer.Import(cb, 3); ca.Dispose(); cb.Dispose();
            outer.Clause(10, OperandEncoding.Clause(OperandEncoding.U64(1, ia.Value))); outer.Clause(10, OperandEncoding.Clause(OperandEncoding.U64(1, ib.Value)));
            Select(outer, table.Row, outer.Column(id.Id), outer.Column(name.Id));
            return Sql(outer);
        }
    }
    private static void IndependentCtes(NativeCalls calls, QueryCommandCalls commands, nint library, uint profile,
        Table<Parent> table, Column<Parent, long> id, Column<Parent, string> name, bool buffered)
    {
        using var source = Source(calls, profile, commands); using var session = Session(source, library);
        using var model = MetadataBindings.RegisterModel(source, table);
        var a = Declaration("DistinctOriginA"); var b = Declaration("DistinctOriginB");
        using var ma = MetadataBindings.RegisterModel(source, a); using var mb = MetadataBindings.RegisterModel(source, b);
        using var first = Query(session, model, table.Identity.Id, buffered); using var second = Query(session, model, table.Identity.Id, buffered);
        LargeUnusedLiteral(first, table, id, name); LargeUnusedLiteral(second, table, id, name);
        using var ca = first.CreateCte(ma, a.Identity.Id, false); using var cb = second.CreateCte(mb, b.Identity.Id, false);
        using var outer = Query(session, model, table.Identity.Id, buffered); var imported = outer.Import(ca, 3);
        outer.Clause(10, OperandEncoding.Clause(OperandEncoding.U64(1, imported.Value)));
        var failed = false;
        try { outer.Import(cb, 3); }
        catch (BindingFailure error) when (error.Status == NativeStatus.DomainError) { failed = true; }
        Check(failed, "Independent equal-value origins remain distinct and reject over-budget import immediately.");
        Select(outer, table.Row, outer.Column(id.Id), outer.Column(name.Id));
        Check(Sql(outer).Contains("DistinctOriginA", StringComparison.Ordinal), "An over-budget import leaves the prior query usable.");
    }

    private static string SharedImportedSnapshot(NativeCalls calls, QueryCommandCalls commands, nint library, uint profile,
        Table<Parent> table, Column<Parent, long> id, Column<Parent, string> name, bool buffered)
    {
        using var source = Source(calls, profile, commands); using var session = Session(source, library);
        using var model = MetadataBindings.RegisterModel(source, table);
        using var subquery = Query(session, model, table.Identity.Id, buffered); LargeUnusedLiteral(subquery, table, id, name);
        using var first = Query(session, model, table.Identity.Id, buffered);
        var imported = first.SnapshotAndImport(subquery); subquery.Dispose();
        var exists = first.Clause(14, OperandEncoding.Clause(OperandEncoding.U64(1, imported.Value), OperandEncoding.U32(2, 0)));
        first.Clause(1, OperandEncoding.Clause(OperandEncoding.U64(1, exists))); Select(first, table.Row, first.Column(id.Id), first.Column(name.Id));
        using var second = first.CloneSelected(); using var third = first.CloneSelected();
        using (var initialPlan = first.Seal(8)) { }
        first.Dispose();
        using var secondPlan = second.Seal(8); using var thirdPlan = third.Seal(8);
        using var outer = Query(session, model, table.Identity.Id, buffered); Select(outer, table.Row, outer.Column(id.Id), outer.Column(name.Id));
        foreach (var plan in new[] { secondPlan, thirdPlan })
        {
            var dependency = outer.Import(plan, 2);
            outer.Clause(9, OperandEncoding.Clause(OperandEncoding.U64(1, dependency.Value), OperandEncoding.U32(2, 1)));
        }
        return Sql(outer);
    }

    private static void MissingCapability(NativeCalls calls, QueryCommandCalls commands, nint library, Table<Parent> table)
    {
        using var source = Source(calls, 1, commands); using var session = Session(source, library); using var model = MetadataBindings.RegisterModel(source, table);
        source.Source.Commands = new(commands, 4096, 64, 4096, 4 * 1024 * 1024, []);
        Reject<BindingFailure>(() => QueryBinding.CreateBuffered(session, model, table.Identity.Id));
        source.Source.Commands = new(new(_ => 0), 4096, 64, 4096, 4 * 1024 * 1024, Capabilities);
        Reject<BindingFailure>(() => QueryBinding.CreateBuffered(session, model, table.Identity.Id));
    }
}
