using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Korda.Data;
using Korda.Interop.Abi;
using Korda.Query.Internal;

// Maintainer component consumer of the actual B04 module, never a test executor.
// Query operations use the unmodified B09 vtable and async retirement protocol.
internal sealed unsafe class Host : IDisposable
{
    private readonly nint _library;
    internal NativeCalls Calls { get; }
    internal DatabaseOwner Source { get; }
    internal DatabaseOwner Session { get; }
    internal uint Profile { get; }
    internal int RetiredOperations { get; private set; }
    internal int ReleasedBuffers { get; private set; }
    internal static byte[] Parameters(uint profile) => DbWire.Encode(new(PayloadKind.Parameters, profile,
        SHA256.HashData("{\"version\":1,\"parameters\":[]}"u8), 0, 0, [])
    { ExactRows = 1 }, 1, []);
    internal Host(string path, uint profile, string connection)
    {
        Profile = profile; _library = NativeLibrary.Load(Path.GetFullPath(path));
        Calls = new("korda.database.host", symbol => NativeLibrary.TryGetExport(_library, symbol, out var p) ? p : 0);
        var limits = ControlRecord.Encode(10,
            U(1, 1), U(2, 8), U(3, 10_000_000_000, 3), U(4, 100), U(5, 1024 * 1024),
            U(6, 16), U(7, 65536), U(8, 32768), U(9, 4096), U(10, 64), U(11, 256),
            U(12, 1024 * 1024), U(13, 16), U(14, 1024 * 1024), U(15, 30_000_000_000, 3));
        var options = profile == 1
            ? ControlRecord.Encode(9, N(1, 3), N(2, 2), N(3, 1), N(4, 0), U(5, 1_000_000_000), N(6, 1))
            : ControlRecord.Encode(8, N(1, 1), T(3, "Korda.Query.Native.Tests"), U(4, 10_000_000_000, 3));
        var config = ControlRecord.Encode(1, N(1, profile == 1 ? 1u : 0u), T(2, connection), B(3, limits), B(4, options));
        nint raw = 0; var error = BindingChecks.Error();
        fixed (byte* b = config) BindingChecks.Status(Calls.nw_db_source_create_v1(V(b, config.Length), &raw, &error), error);
        Source = new(new(Calls, profile), DatabaseOwnerKind.Source, raw);
        using var source = Source.Borrow(DatabaseOwnerKind.Source); nint operation = 0;
        BindingChecks.Status(Calls.nw_db_session_open_create_v1(source.Pointer, &operation, &error), error);
        var admitted = operation; Session = Run(admitted, () => TakeOwner(admitted, 1, DatabaseOwnerKind.Session));
    }
    internal static ControlField U(ushort id, ulong value, ushort type = 2) => OperandEncoding.U64(id, value, type);
    internal static ControlField N(ushort id, uint value) => OperandEncoding.U32(id, value);
    internal static ControlField B(ushort id, byte[] value, ushort type = 5) => OperandEncoding.Bytes(id, value, type);
    internal static ControlField T(ushort id, string value) => OperandEncoding.Text(id, value);
    private static NwBytesViewV1 V(byte* p, int length) => new() { Data = p, Length = (ulong)length };
    private sealed class Completion : IDisposable
    {
        internal readonly ManualResetEventSlim Ready = new(false);
        internal uint Status;
        internal NwErrorInfoV1 Error;
        public void Dispose() => Ready.Dispose();
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Terminal(nuint cookie, uint status, NwErrorInfoV1* error)
    {
        try
        {
            var state = (Completion)GCHandle.FromIntPtr((nint)cookie).Target!;
            state.Status = status; if (error != null) state.Error = *error;
            state.Ready.Set(); return 0;
        }
        catch { return NativeStatus.ContractViolation; }
    }
    private T Run<T>(nint operation, Func<T> take)
    {
        using var completed = new Completion(); var root = GCHandle.Alloc(completed);
        var callbacks = new NwOperationCallbacksV1 { StructSize = (uint)sizeof(NwOperationCallbacksV1), Cookie = (nuint)GCHandle.ToIntPtr(root), OnTerminal = &Terminal };
        var error = BindingChecks.Error(); var started = false;
        try
        {
            BindingChecks.Status(Calls.nw_db_operation_start_v1(operation, &callbacks, &error), error); started = true;
            if (!completed.Ready.Wait(TimeSpan.FromSeconds(35)))
            {
                Calls.nw_db_operation_request_cancel_v1(operation);
                if (!completed.Ready.Wait(TimeSpan.FromSeconds(10))) Environment.FailFast("Host operation failed to retire after bounded cancellation.");
                throw new TimeoutException("Host operation deadline exceeded.");
            }
            BindingChecks.Status(completed.Status, completed.Error);
            return take();
        }
        finally
        {
            if (started)
            {
                var deadline = Stopwatch.StartNew();
                while (true)
                {
                    var receipt = Calls.nw_db_operation_try_retire_v1(operation);
                    if (receipt == 0) break;
                    if (receipt != 1 || deadline.Elapsed > TimeSpan.FromSeconds(10)) Environment.FailFast("Native retirement receipt failed; callback root cannot be released.");
                    Thread.Yield();
                }
                RetiredOperations++;
            }
            Calls.nw_db_operation_release_v1(operation); root.Free();
        }
    }
    private DatabaseOwner TakeOwner(nint operation, uint kind, DatabaseOwnerKind ownerKind)
    {
        nint value = 0; var error = BindingChecks.Error();
        BindingChecks.Status(Calls.nw_db_operation_take_owner_v1(operation, kind, &value, &error), error);
        return new(Source.Source, ownerKind, value);
    }
    private byte[] CopyBuffer(nint value)
    {
        try
        {
            NwBytesViewV1 view = default; var error = BindingChecks.Error();
            BindingChecks.Status(Calls.nw_db_buffer_view_v1(value, &view, &error), error);
            return new ReadOnlySpan<byte>(view.Data, checked((int)view.Length)).ToArray();
        }
        finally { Calls.nw_db_buffer_release_v1(value); ReleasedBuffers++; }
    }
    private byte[] TakeBuffer(nint operation)
    {
        nint value = 0; var error = BindingChecks.Error();
        BindingChecks.Status(Calls.nw_db_operation_take_buffer_v1(operation, &value, &error), error);
        return CopyBuffer(value);
    }
    internal string Render(QueryBinding query)
    {
        using var q = query.Enter(); nint buffer = 0; var error = BindingChecks.Error();
        BindingChecks.Status(Calls.nw_db_query_render_v1(q.Pointer, &buffer, &error), error);
        return Encoding.UTF8.GetString(CopyBuffer(buffer));
    }
    internal DatabaseOwner Cte(DatabaseOwner declared, uint table, QueryBinding query, bool recursive = false)
    {
        using var m = declared.Borrow(DatabaseOwnerKind.Model); using var q = query.Enter(); nint result = 0; var error = BindingChecks.Error();
        BindingChecks.Status(Calls.nw_db_cte_create_v1(m.Pointer, table, q.Pointer, recursive ? 1u : 0u, &result, &error), error);
        return new(Source.Source, DatabaseOwnerKind.Cte, result);
    }
    internal void Sql(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text); var parameters = Parameters(Profile);
        var options = ControlRecord.Encode(20, N(1, 1), N(2, 0), U(3, 30_000_000_000, 3), N(4, 0), B(5, new byte[4]), N(6, 0));
        using var session = Session.Borrow(DatabaseOwnerKind.Session); nint pointer = 0; var error = BindingChecks.Error();
        fixed (byte* s = bytes) fixed (byte* p = parameters) fixed (byte* o = options)
            BindingChecks.Status(Calls.nw_db_sql_create_v1(session.Pointer, V(s, bytes.Length), V(p, parameters.Length), V(o, options.Length), &pointer, &error), error);
        using var plan = new DatabaseOwner(Source.Source, DatabaseOwnerKind.Plan, pointer);
        Count(plan, []);
    }
    private nint CreateExecution(DatabaseOwner plan, ReadOnlySpan<byte> parameters)
    {
        using var session = Session.Borrow(DatabaseOwnerKind.Session); using var p = plan.Borrow(DatabaseOwnerKind.Plan);
        nint operation = 0; var error = BindingChecks.Error();
        fixed (byte* b = parameters) BindingChecks.Status(Calls.nw_db_execute_create_v1(session.Pointer, p.Pointer, V(b, parameters.Length), &operation, &error), error);
        return operation;
    }
    internal long Count(DatabaseOwner plan, ReadOnlySpan<byte> parameters)
    {
        var op = CreateExecution(plan, parameters);
        return Run(op, () => { long count = 0; var error = BindingChecks.Error(); BindingChecks.Status(Calls.nw_db_operation_take_count_v1(op, &count, &error), error); return count; });
    }
    internal List<T> Rows<T>(DatabaseOwner plan, Projection<T> projection, ReadOnlySpan<byte> parameters)
    {
        var projectionId = MetadataBindings.RegisterProjection(Source, projection, c => c.Id);
        var op = CreateExecution(plan, parameters);
        using var cursor = Run(op, () => TakeOwner(op, 2, DatabaseOwnerKind.Cursor));
        var result = new List<T>();
        try
        {
            while (true)
            {
                using var lease = cursor.Borrow(DatabaseOwnerKind.Cursor); nint fetch = 0; var error = BindingChecks.Error();
                BindingChecks.Status(Calls.nw_db_cursor_fetch_create_v1(lease.Pointer, 16, 65536, &fetch, &error), error);
                var admitted = fetch; var bytes = Run(admitted, () => TakeBuffer(admitted));
                var fields = projection.Fields;
                // The admitted source-bound projection ID and the complete hash /
                // ordered codec schema are verified before any DTO constructor.
                var expected = new WireExpectation(PayloadKind.Rows, Profile, Convert.FromHexString(projection.ShapeHash), 0, projectionId,
                    fields.Select((f, i) => new WireDescriptor((uint)i, f.Descriptor, f.CodecId)).ToArray());
                var batch = DbWire.Parse(bytes, expected);
                for (uint row = 0; row < batch.Rows; row++) result.Add(projection.Read(new RowLease(batch, row, fields)));
                if (batch.Rows == 0) break;
            }
            return result;
        }
        finally { Close(cursor); }
    }
    private sealed class RowLease(DbWire batch, uint row, ResultField[] fields) : IRowLease
    {
        public int Count => fields.Length;
        public string Name(int ordinal) => fields[ordinal].Name;
        public CodecDescriptor Codec(int ordinal) => fields[ordinal].Descriptor;
        public ReadOnlyMemory<byte>? Value(int ordinal) => batch.Cell(row, ordinal);
        public void AssertAlive() { }
    }
    private void Close(DatabaseOwner owner)
    {
        using var lease = owner.Borrow(owner.Kind); nint op = 0; var error = BindingChecks.Error();
        BindingChecks.Status(Calls.nw_db_close_create_v1(lease.Pointer, &op, &error), error);
        var admitted = op; Run(admitted, () => TakeBuffer(admitted));
    }
    public void Dispose()
    {
        Close(Session); Session.Dispose(); Close(Source); Source.Dispose();
        NativeLibrary.Free(_library);
    }
}
