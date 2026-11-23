using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Korda.Data;
using Korda.Interop.Abi;
using Korda.Query.Internal;

internal static unsafe class Program
{
    private static int _assertions;
    private static int _callbacks;
    private static uint _terminal;
    private static nint _callbackOperation;
    private static uint _callbackRetireStatus;
    private static readonly List<string> Cases = [];
    private static NativeCalls _calls = null!;
    private static delegate* unmanaged[Cdecl]<uint, ulong> _live;
    private static delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint> _session;
    private static void Check(bool value, string message) { _assertions++; if (!value) throw new InvalidOperationException(message); }
    private static void Reject<T>(Action action) where T : Exception { _assertions++; try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void RejectStatus(uint expected, Action action) { _assertions++; try { action(); } catch (BindingFailure e) when (e.Status == expected) { return; } throw new InvalidOperationException("Expected native status " + expected); }
    private static void Test(string name, Action action) { action(); Cases.Add(name); Console.WriteLine("PASS " + name); }
    private static byte[] FieldBytes(ulong value) { var bytes = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, value); return bytes; }
    private static byte[] Key(Table<Parent> parent, Column<Parent, long> id) => DbWire.Encode(new(PayloadKind.MutationValues, 1, Convert.FromHexString(parent.MetadataHash), parent.Identity.Id, 0, [new(id.Id, Codecs.Int64.Descriptor, 3)]) { ExactRows = 1 }, 1, [Codecs.Int64.Encode(19L)]);
    private static byte[] Select(QueryBinding query, Projection<Parent> projection, params ExpressionReference[] expressions)
    {
        var tokens = expressions.Select(e => e.In(query)).ToArray();
        return OperandEncoding.Clause(OperandEncoding.Bytes(1, OperandEncoding.Tokens(query, expressions), 8), OperandEncoding.Bytes(2, OperandEncoding.Projection(projection, c => c.Id, tokens)));
    }
    private static DatabaseOwner Source()
    {
        nint pointer = 0; var error = BindingChecks.Error(); byte profile = 1;
        BindingChecks.Status(_calls.nw_db_source_create_v1(new() { Data = &profile, Length = 1 }, &pointer, &error), error);
        return new(new(_calls, 1), DatabaseOwnerKind.Source, pointer);
    }
    private static DatabaseOwner Session(DatabaseOwner source)
    {
        using var lease = source.Borrow(DatabaseOwnerKind.Source); nint result = 0; var error = BindingChecks.Error(); BindingChecks.Status(_session(lease.Pointer, &result, &error), error); return new(source.Source, DatabaseOwnerKind.Session, result);
    }
    private static byte[] Snapshot(nint owner)
    {
        nint buffer = 0; var error = BindingChecks.Error(); BindingChecks.Status(_calls.nw_db_snapshot_v1(owner, default, &buffer, &error), error);
        try { NwBytesViewV1 view = default; BindingChecks.Status(_calls.nw_db_buffer_view_v1(buffer, &view, &error), error); return new ReadOnlySpan<byte>(view.Data, checked((int)view.Length)).ToArray(); }
        finally { _calls.nw_db_buffer_release_v1(buffer); }
    }
    private static byte[] Snapshot(QueryBinding query) { using var lease = query.Enter(); return Snapshot(lease.Pointer); }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Terminal(nuint cookie, uint status, NwErrorInfoV1* error) { _callbacks++; _terminal = status; _callbackRetireStatus = _calls.nw_db_operation_try_retire_v1(_callbackOperation); return 0; }
    private static void Start(nint operation)
    {
        var callbacks = new NwOperationCallbacksV1 { StructSize = (uint)sizeof(NwOperationCallbacksV1), OnTerminal = &Terminal };
        var error = BindingChecks.Error(); var before = _callbacks; _callbackOperation = operation;
        BindingChecks.Status(_calls.nw_db_operation_start_v1(operation, &callbacks, &error), error);
        Check(_callbackRetireStatus == 1, "Native retirement stays Pending during callback.");
        Check(_callbacks == before + 1, "Callback may occur before start returns and must be observed.");
    }
    private static void Retire(nint operation) { Check(_calls.nw_db_operation_try_retire_v1(operation) == 0, "Native retirement receipt."); _calls.nw_db_operation_release_v1(operation); }
    private static void CloseCursor(nint cursor)
    {
        nint close = 0; var error = BindingChecks.Error(); BindingChecks.Status(_calls.nw_db_close_create_v1(cursor, &close, &error), error); Start(close); Retire(close); _calls.nw_db_owner_release_v1(cursor);
    }
    public static int Main(string[] args)
    {
        var parentBuilder = new TableBuilder<Parent>("Parents"); var id = parentBuilder.Column("Id", Codecs.Int64, new() { IsPrimaryKey = true }); var name = parentBuilder.Column("Name", Codecs.String);
        var parent = parentBuilder.Build(r => new(r.Get(id), r.Get(name)), (r, p) => { r.Set(id, p.Id); r.Set(name, p.Name); });
        var childBuilder = new TableBuilder<Child>("Children"); var childId = childBuilder.Column("Id", Codecs.Int64, new() { IsPrimaryKey = true }); var parentId = childBuilder.Column("ParentId", Codecs.Nullable(Codecs.Int64));
        var child = childBuilder.Build(r => new(r.Get(childId), r.Get(parentId)), (r, p) => { r.Set(childId, p.Id); r.Set(parentId, p.ParentId); });
        var relation = Relation<Parent, Child>.CreateOptional(parent, id, child, parentId);
        var key = Key(parent, id); var value = OperandEncoding.Value(1, Codecs.Int64, 3, 19L);
        if (args[0] == "--emit")
        {
            Directory.CreateDirectory(args[1]);
            var fixtures = new Dictionary<string, byte[]> { ["parent-model.json"] = parent.CanonicalBytes, ["child-model.json"] = child.CanonicalBytes, ["parent-projection.bin"] = OperandEncoding.Projection(parent.Row, c => c.Id), ["child-projection.bin"] = OperandEncoding.Projection(child.Row, c => c.Id), ["relation.bin"] = ControlRecord.Relation(relation), ["key.bin"] = key, ["value.bin"] = value, ["where.bin"] = OperandEncoding.Clause(OperandEncoding.U64(1, 0x100000001)), ["order.bin"] = OperandEncoding.Clause(OperandEncoding.U64(1, 0x100000001), OperandEncoding.U32(2, 0), OperandEncoding.U32(3, 1), OperandEncoding.U32(4, 1)), ["limit.bin"] = OperandEncoding.Clause(OperandEncoding.U64(1, 20), OperandEncoding.U64(2, 3)) };
            foreach (var (file, bytes) in fixtures) File.WriteAllBytes(Path.Combine(args[1], file), bytes);
            Console.WriteLine("PASS emitted managed golden inputs " + fixtures.Count); return 0;
        }
        var output = args[1]; Directory.CreateDirectory(output);
        if (args.Contains("--require-aot", StringComparer.Ordinal)) Check(!RuntimeFeature.IsDynamicCodeSupported, "Actual AOT execution required.");
        var library = NativeLibrary.Load(Path.GetFullPath(args[0]));
        try
        {
            _calls = new("query.binding.fixture", symbol => NativeLibrary.TryGetExport(library, symbol, out var pointer) ? pointer : 0);
            _live = (delegate* unmanaged[Cdecl]<uint, ulong>)NativeLibrary.GetExport(library, "nw_test_live_v1"); _session = (delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint>)NativeLibrary.GetExport(library, "nw_test_session_v1");
            using (var source = Source()) using (var session = Session(source)) using (var model = MetadataBindings.RegisterModel(source, parent)) using (var childModel = MetadataBindings.RegisterModel(source, child))
            {
                Test("direct_filter_projection_order_paging_seal", () =>
                {
                    using var q = QueryBinding.Create(session, model, parent.Identity.Id); var column = q.Column(id.Id); var text = q.Column(name.Id); var literal = q.Value(value); var predicate = q.Binary(1, column, literal);
                    Check(q.Clause(1, OperandEncoding.Clause(OperandEncoding.U64(1, predicate.In(q)))) == 0, "WHERE output is zero.");
                    q.Clause(3, OperandEncoding.Clause(OperandEncoding.U64(1, column.In(q)), OperandEncoding.U32(2, 0), OperandEncoding.U32(3, 1), OperandEncoding.U32(4, 1)));
                    q.Clause(5, OperandEncoding.Clause(OperandEncoding.U64(1, 20), OperandEncoding.U64(2, 3))); q.Clause(6, Select(q, parent.Row, column, text));
                    using var plan = q.Seal(0); RejectStatus(4, () => q.Column(id.Id));
                    using var planLease = plan.Borrow(DatabaseOwnerKind.Plan); Check(System.Text.Encoding.UTF8.GetString(Snapshot(planLease.Pointer)) == "{\"version\":1,\"parameters\":[]}", "Exact empty sealed parameter schema.");
                });
                Test("managed_default_wrong_owner_disposed_and_native_generation", () =>
                {
                    using var a = QueryBinding.Create(session, model, parent.Identity.Id); using var b = QueryBinding.Create(session, model, parent.Identity.Id); var left = a.Column(id.Id); var right = b.Column(id.Id);
                    Reject<ArgumentException>(() => a.Binary(1, left, default)); Reject<ArgumentException>(() => a.Binary(1, left, right));
                    using (var lease = a.Enter()) { ulong result = 777; var error = BindingChecks.Error(); var status = _calls.nw_db_expr_binary_v1(lease.Pointer, 1, left.Value, right.Value, &result, &error); Check(status == 3 && result == 0, "Native generation validation zeroes output."); Reject<InvalidOperationException>(() => a.Column(id.Id)); }
                    a.Dispose(); Reject<ObjectDisposedException>(() => a.Column(id.Id));
                });
                Test("managed_disposal_closes_admission_but_native_access_lease_survives", () =>
                {
                    var before = _live(1); var q = QueryBinding.Create(session, model, parent.Identity.Id);
                    using (var lease = q.Enter())
                    {
                        q.Dispose(); Check(_live(1) == before + 1, "SafeHandle retains the native query while an access lease is live.");
                        Reject<ObjectDisposedException>(() => q.Column(id.Id));
                        ulong result = 0; var error = BindingChecks.Error(); BindingChecks.Status(_calls.nw_db_expr_column_v1(lease.Pointer, 0, id.Id, &result, &error), error);
                        Check(result != 0, "Admitted native access remains valid until the synchronous lease ends.");
                    }
                    Check(_live(1) == before, "Final native release follows the last managed access lease.");
                });
                Test("source_logical_closing_rejects_new_lease_and_native_registration", () =>
                {
                    var before = _live(1); var closing = Source();
                    using (var held = closing.Borrow(DatabaseOwnerKind.Source))
                    {
                        closing.Dispose(); Check(closing.IsClosing, "Logical Closing is independent of physical SafeHandle lifetime.");
                        Reject<ObjectDisposedException>(() => { using var later = closing.Borrow(DatabaseOwnerKind.Source); });
                        Reject<ObjectDisposedException>(() => MetadataBindings.RegisterModel(closing, parent));
                        Check(_live(1) == before + 1, "No native model is created after logical Closing; original lease still owns source.");
                        Check(held.Pointer != 0, "The already admitted source pointer remains protected.");
                    }
                    Check(_live(1) == before, "Closing source physically releases after its last old access lease.");
                });
                Test("failure_atomic_unknown_version_opcode_fields_and_overflow", () =>
                {
                    using var q = QueryBinding.Create(session, model, parent.Identity.Id); var c = q.Column(id.Id); var predicate = q.Binary(1, c, c); var record = OperandEncoding.Clause(OperandEncoding.U64(1, predicate.Value)); var before = Snapshot(q);
                    RejectStatus(6, () => q.Clause(999, record)); record[0] = 2; RejectStatus(3, () => q.Clause(1, record)); record[0] = 1; record[16] = 99; RejectStatus(3, () => q.Clause(1, record));
                    RejectStatus(3, () => q.Clause(5, OperandEncoding.Clause(OperandEncoding.U64(1, ulong.MaxValue))));
                    Check(before.AsSpan().SequenceEqual(Snapshot(q)), "Failed calls mutated query accounting.");
                    RejectStatus(6, () => q.Binary(99, c, c)); RejectStatus(3, () => q.Unary(3, c));
                });
                Test("join_reserve_commit_abort_invalidation_and_nullable_lift", () =>
                {
                    using var q = QueryBinding.Create(session, model, parent.Identity.Id); var imported = q.Import(childModel, 1); var left = q.Column(id.Id);
                    ulong Reserve(string alias) => q.Clause(18, OperandEncoding.Clause(OperandEncoding.U64(1, imported.In(q)), OperandEncoding.U32(2, child.Identity.Id), OperandEncoding.Text(3, alias), OperandEncoding.U32(4, 1)));
                    var provisional = Reserve("right"); var right = q.Column(childId.Id, new(q, provisional)); var on = q.Binary(1, left, right); var derived = q.Unary(2, right);
                    RejectStatus(4, () => q.Clause(8, OperandEncoding.Clause()));
                    Check(q.Clause(7, OperandEncoding.Clause(OperandEncoding.U64(1, provisional), OperandEncoding.U64(2, on.In(q)))) == provisional, "JOIN promotes same alias.");
                    RejectStatus(3, () => q.Unary(1, right)); RejectStatus(3, () => q.Unary(3, derived)); var fresh = q.Column(childId.Id, new(q, provisional)); RejectStatus(3, () => q.Unary(4, fresh));
                    var marker = q.Clause(20, OperandEncoding.Clause(OperandEncoding.U64(1, provisional))); Check(marker != 0, "Left-join private marker predicate.");
                    var aborted = Reserve("abort"); var captured = q.Column(childId.Id, new(q, aborted)); q.Clause(19, OperandEncoding.Clause(OperandEncoding.U64(1, aborted))); RejectStatus(3, () => q.Unary(1, captured));
                });
                Test("clone_self_composition_preserves_mutable_source_and_owned_import", () =>
                {
                    using var q = QueryBinding.Create(session, model, parent.Identity.Id); var c = q.Column(id.Id); var t = q.Column(name.Id); q.Clause(6, Select(q, parent.Row, c, t));
                    var plan = q.SnapshotAndImport(q); q.Clause(9, OperandEncoding.Clause(OperandEncoding.U64(1, plan.In(q)), OperandEncoding.U32(2, 1))); Check(q.Column(id.Id).Value != 0, "Source remains mutable after snapshot import.");
                });
                Test("parameter_identity_and_exact_sealed_schema", () =>
                {
                    using var a = QueryBinding.Create(session, model, parent.Identity.Id); using var b = QueryBinding.Create(session, model, parent.Identity.Id);
                    a.Parameter(17, "limit_value"u8, 3, false); RejectStatus(3, () => b.Parameter(17, "different"u8, 3, false));
                    b.Parameter(18, "limit_value"u8, 3, false); var bc = b.Column(id.Id); var bt = b.Column(name.Id); b.Clause(6, Select(b, parent.Row, bc, bt)); using var imported = b.Seal(8);
                    var before = Snapshot(a); RejectStatus(3, () => a.Import(imported, 2)); Check(before.AsSpan().SequenceEqual(Snapshot(a)), "Parameter union failed atomically.");
                    var ac = a.Column(id.Id); var at = a.Column(name.Id); a.Clause(6, Select(a, parent.Row, ac, at)); using var plan = a.Seal(0); using var lease = plan.Borrow(DatabaseOwnerKind.Plan);
                    var schema = Snapshot(lease.Pointer); Check(System.Text.Encoding.UTF8.GetString(schema) == "{\"version\":1,\"parameters\":[{\"id\":17,\"name\":\"limit_value\",\"codec\":3,\"codec_config\":null,\"nullable\":false}]}", "Canonical parameter schema.");
                    var parameters = DbWire.Encode(new(PayloadKind.Parameters, 1, SHA256.HashData(schema), 0, 0, [new(17, Codecs.Int64.Descriptor, 3)]) { ExactRows = 1 }, 1, [Codecs.Int64.Encode(9)]);
                    using var sessionLease = session.Borrow(DatabaseOwnerKind.Session); nint operation = 0; var error = BindingChecks.Error();
                    fixed (byte* bytes = parameters) BindingChecks.Status(_calls.nw_db_execute_create_v1(sessionLease.Pointer, lease.Pointer, new() { Data = bytes, Length = (ulong)parameters.Length }, &operation, &error), error);
                    var saved = parameters.ToArray(); Array.Fill(parameters, (byte)0); Check(Snapshot(operation).AsSpan().SequenceEqual(saved), "Create copied execution arguments."); _calls.nw_db_operation_release_v1(operation);
                });
                Test("post_build_relation_identity_copy_kind2_fetch_close_retirement", () =>
                {
                    var parentHash = parent.MetadataHash; var childHash = child.MetadataHash; var projection = MetadataBindings.RegisterProjection(source, child.Row, c => c.Id);
                    using var registered = MetadataBindings.RegisterRelation(source, model, childModel, relation); var borrowedKey = key.ToArray();
                    var operation = MetadataBindings.CreateRelationLoad(session, registered, borrowedKey, projection); Array.Fill(borrowedKey, (byte)0);
                    Check(Snapshot(operation).AsSpan().SequenceEqual(key), "Relation create copied key bytes."); Check(parent.MetadataHash == parentHash && child.MetadataHash == childHash, "Registration mutated frozen models.");
                    Check(_calls.nw_db_operation_try_retire_v1(operation) == 1, "Created operation is not completed."); Start(operation); Check(_terminal == 0, "Terminal success."); Check(_calls.nw_db_operation_request_cancel_v1(operation) == 0, "Late cancel is only a request; successful result remains available.");
                    nint cursor = 123; var error = BindingChecks.Error(); Check(_calls.nw_db_operation_take_owner_v1(operation, 3, &cursor, &error) == 4 && cursor == 0, "Wrong owner kind does not consume result.");
                    BindingChecks.Status(_calls.nw_db_operation_take_owner_v1(operation, 2, &cursor, &error), error); nint duplicate = 0; Check(_calls.nw_db_operation_take_owner_v1(operation, 2, &duplicate, &error) == 4, "Single result transfer."); Retire(operation);
                    nint fetch = 0; BindingChecks.Status(_calls.nw_db_cursor_fetch_create_v1(cursor, 2, 4096, &fetch, &error), error); Start(fetch); nint buffer = 0; BindingChecks.Status(_calls.nw_db_operation_take_buffer_v1(fetch, &buffer, &error), error); Retire(fetch);
                    NwBytesViewV1 view = default; BindingChecks.Status(_calls.nw_db_buffer_view_v1(buffer, &view, &error), error); Check(BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(view.Data, (int)view.Length).Slice(56, 4)) == 0, "Fixture EOS uses ordinary bounded row batch."); _calls.nw_db_buffer_release_v1(buffer); CloseCursor(cursor);
                    Check(_live(2) == 0 && _live(3) == 0, "Native operations/buffers retired independently of managed owner scope.");
                });
                Test("relation_wrong_source_key_shape_and_untaken_cancel_cleanup", () =>
                {
                    var projection = MetadataBindings.RegisterProjection(source, child.Row, c => c.Id); using var registered = MetadataBindings.RegisterRelation(source, model, childModel, relation);
                    using var otherSource = Source(); using var otherSession = Session(otherSource); Reject<ArgumentException>(() => MetadataBindings.CreateRelationLoad(otherSession, registered, key, projection));
                    var invalid = key.ToArray(); invalid[24] ^= 1; RejectStatus(3, () => MetadataBindings.CreateRelationLoad(session, registered, invalid, projection));
                    var wrongProjection = MetadataBindings.RegisterProjection(source, parent.Row, c => c.Id); RejectStatus(3, () => MetadataBindings.CreateRelationLoad(session, registered, key, wrongProjection));
                    var operation = MetadataBindings.CreateRelationLoad(session, registered, key, projection); Check(_calls.nw_db_operation_request_cancel_v1(operation) == 0 && _calls.nw_db_operation_request_cancel_v1(operation) == 0, "Cancel request idempotent."); Start(operation); Check(_terminal == 2, "Fixture canceled terminal distinct from request."); Retire(operation); Check(_live(2) == 0, "Untaken cursor released with operation.");
                });
            }
            Check(_live(1) == 0 && _live(2) == 0 && _live(3) == 0, "All native owners, operations and buffers released.");
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new Report(RuntimeFeature.IsDynamicCodeSupported, _assertions, Cases.ToArray(), _callbacks, 0, 0, 0), ReportJson.Default.Report));
            Console.WriteLine($"PASS managed assertions={_assertions}, cases={Cases.Count}, dynamicCode={RuntimeFeature.IsDynamicCodeSupported}"); return 0;
        }
        finally { NativeLibrary.Free(library); }
    }
}
internal sealed record Parent(long Id, string Name);
internal sealed record Child(long Id, long? ParentId);
internal sealed record Report(bool DynamicCodeSupported, int Assertions, string[] Cases, int Callbacks, int NativeOwners, int NativeOperations, int NativeBuffers);
[JsonSerializable(typeof(Report))]
internal partial class ReportJson : JsonSerializerContext;
