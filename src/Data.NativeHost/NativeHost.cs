using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Korda.Data;
using Korda.Database.Internal;
using Korda.Interop;
using Korda.Interop.Abi;
using Korda.Interop.Async;
using Korda.Interop.Callbacks;
using Korda.Interop.Lifetime;
using Korda.Query.Internal;

namespace Korda.Database;

internal sealed class OperationHandle : SafeHandle
{
    private readonly SourceBinding _binding;
    internal OperationHandle(SourceBinding binding, nint value) : base(0, true)
    { if (value == 0) throw new InvalidOperationException("The native operation owner is zero."); _binding = binding; SetHandle(value); }
    public override bool IsInvalid => handle == 0;
    protected override unsafe bool ReleaseHandle() { _binding.Calls.nw_db_operation_release_v1(handle); return true; }
}
internal sealed class SourceResources : SafeHandle
{
    private readonly DatabaseOwner _source;
    internal CallbackCoordinator Coordinator { get; }
    internal SourceResources(DatabaseOwner source, int capacity) : base(0, true)
    { _source = source; Coordinator = new(capacity); SetHandle(1); }
    public override bool IsInvalid => handle == 0;
    protected override bool ReleaseHandle() { _source.Dispose(); Coordinator.Dispose(); return true; }
}
internal sealed class NativeBuffer : SafeHandle
{
    private readonly SourceBinding _binding;
    internal NativeBuffer(SourceBinding binding, nint pointer) : base(0, true)
    { if (pointer == 0) throw new InvalidOperationException("Native buffer owner is zero."); _binding = binding; SetHandle(pointer); }
    public override bool IsInvalid => handle == 0;
    protected override unsafe bool ReleaseHandle() { _binding.Calls.nw_db_buffer_release_v1(handle); return true; }
    // Only callers holding a synchronous DangerousAddRef reference may consume this view.
    // The buffer's immutable native storage remains owned by this SafeHandle.
    internal unsafe void ViewWhileRetained(int maximum, out nint address, out int length)
    {
        var view = new NwBytesViewV1(); var error = BindingChecks.Error();
        var status = _binding.Calls.nw_db_buffer_view_v1(DangerousGetHandle(), &view, &error); HostFailure.Check(_binding, status, error);
        CheckView(view, maximum, out address, out length);
    }
    internal static unsafe void CheckView(NwBytesViewV1 view, int maximum, out nint address, out int length)
    {
        if (view.Length > (ulong)maximum || view.Length != 0 && view.Data is null)
            throw new DatabaseException("Native buffer exceeds the admitted range.", DatabaseErrorKind.ResourceLimit);
        address = (nint)view.Data; length = checked((int)view.Length);
    }
    internal unsafe byte[] Copy(int maximum = DbWire.MaxEncodedBytes)
    {
        var retained = false;
        try
        {
            DangerousAddRef(ref retained); var view = new NwBytesViewV1(); var error = BindingChecks.Error();
            var status = _binding.Calls.nw_db_buffer_view_v1(DangerousGetHandle(), &view, &error); HostFailure.Check(_binding, status, error);
            if (view.Length > (ulong)maximum || view.Length != 0 && view.Data is null) throw new DatabaseException("Native buffer exceeds the admitted range.", DatabaseErrorKind.ResourceLimit);
            return new ReadOnlySpan<byte>(view.Data, checked((int)view.Length)).ToArray();
        }
        finally { if (retained) DangerousRelease(); }
    }
    internal unsafe TransactionSnapshot ReadSnapshot(int maximum = DbWire.MaxEncodedBytes)
    {
        var retained = false;
        try
        {
            DangerousAddRef(ref retained); var view = new NwBytesViewV1(); var error = BindingChecks.Error();
            var status = _binding.Calls.nw_db_buffer_view_v1(DangerousGetHandle(), &view, &error); HostFailure.Check(_binding, status, error);
            if (view.Length > (ulong)maximum || view.Length != 0 && view.Data is null) throw new DatabaseException("Native buffer exceeds the admitted range.", DatabaseErrorKind.ResourceLimit);
            // Snapshot materializes owned values synchronously; no borrowed bytes
            // can escape the SafeHandle reference protecting this native view.
            return HostEncoding.Snapshot(new ReadOnlySpan<byte>(view.Data, checked((int)view.Length)));
        }
        finally { if (retained) DangerousRelease(); }
    }
}
internal sealed unsafe class HostAsyncCalls(DataSource source) : AsyncNativeCalls
{
    internal override uint Start(nint operation, in NwOperationCallbacksV1 callbacks)
    { var error = BindingChecks.Error(); fixed (NwOperationCallbacksV1* pointer = &callbacks) return source.Binding.Calls.nw_db_operation_start_v1(operation, pointer, &error); }
    internal override uint RequestCancel(nint operation) => source.Binding.Calls.nw_db_operation_request_cancel_v1(operation);
    internal override uint TryRetire(nint operation) => source.Binding.Calls.nw_db_operation_try_retire_v1(operation);
    internal override void Poison() => source.Poison();
    internal override void ObserveRetirement(AsyncOperation operation) => source.TestRetirement?.Invoke(operation.Snapshot);
    internal override Exception MapFailure(nint operation, uint status, in NwErrorInfoV1 error, CancellationToken token)
    {
        string? provider = null; bool cleanupFailed = false; BulkProgress? nativeProgress = null;
        if (source.Binding.Calls.nw_db_operation_take_error_v1 != null)
        {
            nint pointer = 0; var detail = BindingChecks.Error(); var taken = source.Binding.Calls.nw_db_operation_take_error_v1(operation, &pointer, &detail);
            if (taken == NativeStatus.Ok)
            {
                using var owner = new NativeBuffer(source.Binding, pointer);
                source.TestAfterErrorTake?.Invoke();
                var fields = HostEncoding.ReadControl(BulkErrorPayload.Read(owner.Copy(), out nativeProgress), 7);
                if (fields.TryGetValue(9, out var code)) provider = HostEncoding.Utf8.GetString(code);
                cleanupFailed = fields.TryGetValue(4, out var cleanup) && BinaryPrimitives.ReadUInt32LittleEndian(cleanup) == (uint)CleanupStatus.Failed;
            }
        }
        var failure = HostFailure.Create(source.Binding, status, error, provider, token);
        if (!cleanupFailed) return BulkErrorPayload.AttachNative(failure, nativeProgress);
        var cleanupFailure = new DatabaseException("Administrative rollback or connection closure also failed; the dedicated connection was discarded.", DatabaseErrorKind.Provider);
        Exception enriched = failure switch
        {
            DatabaseException database => new DatabaseException(database.Message, database.Kind, database.ProviderCode, database.CorrelationId, database.IsTransient, cleanupFailure),
            OperationCanceledException cancellation => new OperationCanceledException(cancellation.Message, cleanupFailure, cancellation.CancellationToken),
            TimeoutException timeout => new TimeoutException(timeout.Message, cleanupFailure),
            _ => new AggregateException(failure, cleanupFailure)
        };
        return BulkErrorPayload.AttachNative(enriched, nativeProgress);
    }
}
internal static class HostFailure
{
    internal static void Check(SourceBinding binding, uint status, in NwErrorInfoV1 error)
    { if (status != NativeStatus.Ok) throw Create(binding, status, error); }
    internal static Exception Create(SourceBinding binding, uint status, in NwErrorInfoV1 error, string? provider = null, CancellationToken token = default)
    {
        if (status != NativeStatus.DomainError || error.Domain != 0x4442) return NativeError.Create(binding.Calls.EngineId, "database", status, in error, token);
        var kind = error.Code switch { 1 or 10 => DatabaseErrorKind.Provider, 2 or 16 => DatabaseErrorKind.Constraint, 3 => DatabaseErrorKind.Conversion, 4 => DatabaseErrorKind.ShapeMismatch, 5 => DatabaseErrorKind.UnsupportedCapability, 6 or 14 => DatabaseErrorKind.ResourceLimit, 7 => DatabaseErrorKind.ConcurrentUse, 9 => DatabaseErrorKind.UnknownOutcome, _ => DatabaseErrorKind.InvalidState };
        if (error.Code == 16) return new ConcurrencyConflictException("The native write did not match its expected key and concurrency tokens.", 1, 0);
        if (error.Code == 9) return new CommitOutcomeUnknownException("Native commit outcome is unknown; do not retry implicitly.", null, provider, error.CorrelationId);
        var transient = kind == DatabaseErrorKind.Provider && provider is "40001" or "40P01" or "55P03" or "5" or "6";
        return new DatabaseException(error.Code == 14 ? "LargeValueRequiresStreaming: an inline field exceeds the admitted byte budget." : "The native database operation failed.", kind, provider, error.CorrelationId, transient);
    }
    internal static Exception Binding(SourceBinding source, BindingFailure failure) => Create(source, failure.Status, new NwErrorInfoV1 { StructSize = 24, Domain = failure.Domain, Code = failure.Code, CorrelationId = failure.Correlation });
}
internal static class NativeHost
{
    internal static unsafe nint Open(DataSource source)
    {
        using var lease = source.Owner.Borrow(DatabaseOwnerKind.Source); nint result = 0; var error = BindingChecks.Error();
        HostFailure.Check(source.Binding, source.Binding.Calls.nw_db_session_open_create_v1(lease.Pointer, &result, &error), error); return result;
    }
    internal static unsafe nint Close(DatabaseOwner owner)
    {
        using var lease = owner.Borrow(owner.Kind); nint result = 0; var error = BindingChecks.Error();
        HostFailure.Check(owner.Source, owner.Source.Calls.nw_db_close_create_v1(lease.Pointer, &result, &error), error); return result;
    }
    internal static unsafe DatabaseOwner TakeOwner(SourceBinding binding, nint operation, uint kind, DatabaseOwnerKind tag)
    {
        nint result = 0; var error = BindingChecks.Error(); HostFailure.Check(binding, binding.Calls.nw_db_operation_take_owner_v1(operation, kind, &result, &error), error);
        try { return new(binding, tag, result); } catch { binding.Calls.nw_db_owner_release_v1(result); throw; }
    }
    internal static unsafe long TakeCount(SourceBinding binding, nint operation)
    { long result = 0; var error = BindingChecks.Error(); HostFailure.Check(binding, binding.Calls.nw_db_operation_take_count_v1(operation, &result, &error), error); return result; }
    internal static unsafe NativeBuffer TakeBuffer(SourceBinding binding, nint operation)
    { nint result = 0; var error = BindingChecks.Error(); HostFailure.Check(binding, binding.Calls.nw_db_operation_take_buffer_v1(operation, &result, &error), error); try { return new(binding, result); } catch { binding.Calls.nw_db_buffer_release_v1(result); throw; } }
    internal static unsafe void TakePullBatch(SourceBinding binding, nint channel, ulong generation,
        ScopedStreamBatch batch, int maximum, CancellationToken token = default, Action? afterErrorTake = null)
    {
        var calls = Pulls(binding);
        NativePullBatch result = default; var error = BindingChecks.Error();
        try
        {
            var status = calls.Take(channel, generation, &result, &error);
            if (status != NativeStatus.Ok)
                throw PullFailure(binding, channel, generation, status, error, token, afterErrorTake);
            if (result.Owner == 0) throw new InvalidOperationException("Native pull returned a zero batch owner.");
            if (result.KnownEnd > 1 || result.Reserved != 0)
                throw new DatabaseException("Native pull batch flags are invalid.", DatabaseErrorKind.ShapeMismatch);
            NativeBuffer.CheckView(result.Bytes, maximum, out var address, out var length);
            if (length < 96)
                throw new DatabaseException("Native pull batch header is incomplete.", DatabaseErrorKind.ShapeMismatch);
            batch.Attach(result.Owner, address, length, result.KnownEnd != 0);
            result.Owner = 0; // Attach accepted this exact owner; no wrapper allocation.
        }
        finally { if (result.Owner != 0) binding.Calls.nw_db_buffer_release_v1(result.Owner); }
    }
    internal static unsafe DatabaseCloseResult TakePullClose(SourceBinding binding, nint channel,
        Action? afterErrorTake = null)
    {
        nint pointer = 0; var error = BindingChecks.Error();
        NativeBuffer? owner = null;
        try
        {
            var status = Pulls(binding).CloseTake(channel, &pointer, &error);
            if (status != NativeStatus.Ok)
                throw PullFailure(binding, channel, 0, status, error, default, afterErrorTake);
            owner = new(binding, pointer); pointer = 0;
            return owner.ReadSnapshot().Cleanup;
        }
        finally
        {
            owner?.Dispose();
            if (pointer != 0) binding.Calls.nw_db_buffer_release_v1(pointer);
        }
    }
    private static unsafe Exception PullFailure(SourceBinding binding, nint channel, ulong generation,
        uint status, in NwErrorInfoV1 error, CancellationToken token, Action? afterErrorTake)
    {
        if (status == NativeStatus.Pending)
            return new NativeEngineException("The pull ready notification preceded a transferable result.");
        nint pointer = 0; var detail = BindingChecks.Error();
        var taken = Pulls(binding).TakeError(channel, generation, &pointer, &detail);
        NativeBuffer? owner = null;
        try
        {
            if (taken != NativeStatus.Ok) return HostFailure.Create(binding, status, error, token: token);
            owner = new(binding, pointer); pointer = 0;
            afterErrorTake?.Invoke();
            var fields = HostEncoding.ReadControl(BulkErrorPayload.Read(owner.Copy(), out var progress), 7);
            var provider = fields.TryGetValue(9, out var code) ? HostEncoding.Utf8.GetString(code) : null;
            var cleanupFailed = fields.TryGetValue(4, out var cleanup)
                && BinaryPrimitives.ReadUInt32LittleEndian(cleanup) == (uint)CleanupStatus.Failed;
            var failure = HostFailure.Create(binding, status, error, provider, token);
            if (!cleanupFailed) return BulkErrorPayload.AttachNative(failure, progress);
            var cleanupFailure = new DatabaseException("Native cursor cleanup also failed; the connection cannot be reused.", DatabaseErrorKind.Provider);
            Exception enriched = failure switch
            {
                DatabaseException database => new DatabaseException(database.Message, database.Kind, database.ProviderCode, database.CorrelationId, database.IsTransient, cleanupFailure),
                OperationCanceledException cancellation => new OperationCanceledException(cancellation.Message, cleanupFailure, cancellation.CancellationToken),
                TimeoutException timeout => new TimeoutException(timeout.Message, cleanupFailure),
                _ => new AggregateException(failure, cleanupFailure)
            };
            return BulkErrorPayload.AttachNative(enriched, progress);
        }
        finally
        {
            owner?.Dispose();
            if (pointer != 0) binding.Calls.nw_db_buffer_release_v1(pointer);
        }
    }
    internal static CursorPullCalls Pulls(SourceBinding binding) => binding.Pulls
        ?? throw new NotSupportedException("The native cursor pull extension is unavailable.");
    internal static unsafe void CheckPullRead(DatabaseOwner channel)
    {
        using var lease = channel.Borrow(DatabaseOwnerKind.Cursor); var error = BindingChecks.Error();
        HostFailure.Check(channel.Source, Pulls(channel.Source).CheckRead(lease.Pointer, &error), error);
    }
    internal static unsafe DatabaseOwner CreatePull(DataSession session, DatabaseOwner plan, ReadOnlyMemory<byte> arguments)
    {
        var binding = session.Source.Binding;
        BindingChecks.SameSource(binding, plan);
        using var sessionLease = session.Owner.Borrow(DatabaseOwnerKind.Session);
        using var planLease = plan.Borrow(DatabaseOwnerKind.Plan);
        nint channel = 0; var error = BindingChecks.Error();
        fixed (byte* bytes = arguments.Span)
            HostFailure.Check(binding, Pulls(binding).Create(sessionLease.Pointer, planLease.Pointer,
                BindingChecks.View(bytes, arguments.Length), &channel, &error), error);
        return AdoptPull(binding, channel);
    }
    internal static unsafe DatabaseOwner CreateMutationPull(DataSession session, DatabaseOwner model, uint kind, byte[] payload)
    {
        var binding = session.Source.Binding;
        BindingChecks.SameSource(binding, model);
        using var sessionLease = session.Owner.Borrow(DatabaseOwnerKind.Session);
        using var modelLease = model.Borrow(DatabaseOwnerKind.Model);
        nint channel = 0; var error = BindingChecks.Error();
        fixed (byte* bytes = payload)
            HostFailure.Check(binding, Pulls(binding).CreateMutation(sessionLease.Pointer, modelLease.Pointer,
                kind, BindingChecks.View(bytes, payload.Length), &channel, &error), error);
        return AdoptPull(binding, channel);
    }
    internal static unsafe DatabaseOwner CreateRelationPull(DataSession session, DatabaseOwner relation, byte[] key, uint projection)
    {
        var binding = session.Source.Binding;
        BindingChecks.SameSource(binding, relation);
        using var sessionLease = session.Owner.Borrow(DatabaseOwnerKind.Session);
        using var relationLease = relation.Borrow(DatabaseOwnerKind.Relation);
        nint channel = 0; var error = BindingChecks.Error();
        fixed (byte* bytes = key)
            HostFailure.Check(binding, Pulls(binding).CreateRelation(sessionLease.Pointer, relationLease.Pointer,
                BindingChecks.View(bytes, key.Length), projection, &channel, &error), error);
        return AdoptPull(binding, channel);
    }
    private static unsafe DatabaseOwner AdoptPull(SourceBinding binding, nint pointer)
    {
        // Cursor is a managed lifetime tag; native close/snapshot distinguish
        // the channel variant without changing frozen take-owner kinds.
        try { return new(binding, DatabaseOwnerKind.Cursor, pointer); }
        catch { if (pointer != 0) binding.Calls.nw_db_owner_release_v1(pointer); throw; }
    }
    internal static unsafe TransactionSnapshot Snapshot(DatabaseOwner owner)
    {
        using var lease = owner.Borrow(owner.Kind); nint result = 0; var error = BindingChecks.Error();
        HostFailure.Check(owner.Source, owner.Source.Calls.nw_db_snapshot_v1(lease.Pointer, default, &result, &error), error);
        using var buffer = new NativeBuffer(owner.Source, result); return buffer.ReadSnapshot();
    }
    internal static unsafe nint Execute(DatabaseOwner session, DatabaseOwner plan, ReadOnlyMemory<byte> parameters)
    {
        BindingChecks.SameSource(session.Source, plan); using var sessionLease = session.Borrow(DatabaseOwnerKind.Session); using var planLease = plan.Borrow(DatabaseOwnerKind.Plan);
        nint result = 0; var error = BindingChecks.Error(); fixed (byte* bytes = parameters.Span)
            HostFailure.Check(session.Source, session.Source.Calls.nw_db_execute_create_v1(sessionLease.Pointer, planLease.Pointer, BindingChecks.View(bytes, parameters.Length), &result, &error), error);
        return result;
    }
}
