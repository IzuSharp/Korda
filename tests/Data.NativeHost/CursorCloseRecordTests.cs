using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Korda.Data;
using Korda.Database;
using Korda.Database.Internal;
using Korda.Interop;
using Korda.Interop.Abi;
using Korda.Query.Internal;

internal static unsafe class CursorCloseRecordTests
{
    private static nint _payload, _closeOwner, _errorOwner;
    private static int _length, _closeTakes, _errorTakes, _releases, _views, _checks;
    private static uint _closeStatus, _errorStatus;
    private static ulong _errorGeneration;
    private static nint _errorChannel;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint CloseTake(nint channel, nint* owner, NwErrorInfoV1* error)
    {
        _closeTakes++; *owner = _closeOwner;
        *error = new() { StructSize = 24, Domain = 0x4442, Code = 1, CorrelationId = 4242 };
        return _closeStatus;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint TakeError(nint channel, ulong generation, nint* owner, NwErrorInfoV1* error)
    {
        _errorTakes++; _errorChannel = channel; _errorGeneration = generation;
        *owner = _errorOwner; return _errorStatus;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint TakeBatch(nint channel, ulong generation, NativePullBatch* result, NwErrorInfoV1* error)
    {
        *result = default;
        *error = new() { StructSize = 24, Domain = 0x4442, Code = 1, CorrelationId = 4242 };
        return NativeStatus.DomainError;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint View(nint owner, NwBytesViewV1* result, NwErrorInfoV1* error)
    { _views++; *result = new() { Data = (byte*)_payload, Length = (ulong)_length }; return NativeStatus.Ok; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Release(nint owner) => _releases++;

    private static SourceBinding Binding()
    {
        var calls = new NativeCalls("close-record-fixture", symbol => symbol switch
        {
            "nw_db_buffer_view_v1" => (nint)(delegate* unmanaged[Cdecl]<nint, NwBytesViewV1*, NwErrorInfoV1*, uint>)&View,
            "nw_db_buffer_release_v1" => (nint)(delegate* unmanaged[Cdecl]<nint, void>)&Release,
            _ => 0
        });
        return new(calls, 2)
        {
            Pulls = new(symbol => symbol switch
            {
                "nw_db_cursor_pull_close_take_v1" => (nint)(delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint>)&CloseTake,
                "nw_db_cursor_pull_take_error_v1" => (nint)(delegate* unmanaged[Cdecl]<nint, ulong, nint*, NwErrorInfoV1*, uint>)&TakeError,
                "nw_db_cursor_pull_take_v1" => (nint)(delegate* unmanaged[Cdecl]<nint, ulong, NativePullBatch*, NwErrorInfoV1*, uint>)&TakeBatch,
                _ => 0
            })
        };
    }

    internal static void Verify()
    {
        var before = _checks;
        var binding = Binding();
        try
        {
            Reset(SnapshotProbe.CloseControl(CleanupStatus.Succeeded, true, ConnectionDisposition.Usable, "40001"));
            var receipt = NativeHost.TakePullClose(binding, 71);
            new Span<byte>((void*)_payload, _length).Clear();
            Check(receipt.Status == CleanupStatus.Succeeded && receipt.LocalResourcesReleased
                && receipt.ConnectionDisposition == ConnectionDisposition.Usable && receipt.CorrelationId == 123
                && receipt.ProviderCode == "40001" && !receipt.DeadlineExceeded,
                "A taken close snapshot copies its full immutable cleanup fields before native storage is released.");
            Check(_closeTakes == 1 && _views == 1 && _releases == 1 && _errorTakes == 0,
                "A successful close transfers and releases its one snapshot owner exactly once.");

            Reset([0, 0, 0]);
            var malformed = Failure<DatabaseException>(() => NativeHost.TakePullClose(binding, 71));
            Check(malformed.Kind == DatabaseErrorKind.ShapeMismatch && _closeTakes == 1 && _views == 1 && _releases == 1,
                "Malformed close payloads preserve their shape error and release the transferred owner.");

            Reset([]); _closeOwner = 0;
            Failure<InvalidOperationException>(() => NativeHost.TakePullClose(binding, 71));
            Check(_releases == 0 && _views == 0, "A null close snapshot is rejected without releasing a nonexistent owner.");

            Reset([]); _closeStatus = NativeStatus.Pending;
            Failure<NativeEngineException>(() => NativeHost.TakePullClose(binding, 71));
            Check(_errorTakes == 0 && _releases == 1,
                "A premature ready/take is a bridge error, does not consume close error state, and compensates an unexpected owner.");

            Reset([]); _closeStatus = NativeStatus.InvalidState;
            Failure<InvalidOperationException>(() => NativeHost.TakePullClose(binding, 71));
            Check(_errorTakes == 1 && _errorGeneration == 0 && _releases == 1,
                "A failed close take addresses generation0 and releases its unexpected output owner.");

            var detail = SnapshotProbe.CloseControl(CleanupStatus.Failed, true, ConnectionDisposition.Discarded, "40001");
            Reset(detail); DomainError();
            var failure = Failure<DatabaseException>(() => NativeHost.TakePullClose(binding, 71));
            Check(failure.Kind == DatabaseErrorKind.Provider && failure.ProviderCode == "40001"
                && failure.CorrelationId == 4242 && failure.IsTransient
                && failure.InnerException is DatabaseException { Kind: DatabaseErrorKind.Provider },
                "Close error mapping retains provider code, correlation, transient classification and the distinct cleanup failure.");
            Check(_closeTakes == 1 && _errorTakes == 1 && _errorChannel == 71 && _errorGeneration == 0
                && _views == 1 && _releases == 2,
                "Close enrichment consumes only the close error channel and releases both independently owned outputs once.");

            Reset(detail); DomainError();
            var marker = new InvalidDataException("after close error take");
            var injected = Failure<InvalidDataException>(() => NativeHost.TakePullClose(binding, 71, () => throw marker));
            Check(ReferenceEquals(marker, injected) && _errorTakes == 1 && _errorGeneration == 0 && _views == 0 && _releases == 2,
                "An exception after close error transfer preserves identity and releases every transferred owner.");

            Reset(detail); DomainError();
            using var batch = new ScopedStreamBatch(&Release, binding);
            var pullFailure = Failure<DatabaseException>(() => NativeHost.TakePullBatch(binding, 71, 7, batch, 65536));
            Check(pullFailure.ProviderCode == "40001" && _errorGeneration == 7 && _closeTakes == 0 && _releases == 1,
                "A positive-generation fetch error remains independent from generation0 close error consumption.");
        }
        finally
        {
            if (_payload != 0) NativeMemory.Free((void*)_payload);
            _payload = 0; _length = 0;
        }
        Console.WriteLine($"CURSOR_CLOSE_RECORD_PASS assertions={_checks - before}; owned snapshot/error transfer, rich errors and fetch/close generation separation");
    }

    private static void DomainError()
    { _closeStatus = NativeStatus.DomainError; _errorStatus = NativeStatus.Ok; _errorOwner = 19; }

    private static void Reset(byte[] bytes)
    {
        if (_payload != 0) NativeMemory.Free((void*)_payload);
        _payload = bytes.Length == 0 ? 0 : (nint)NativeMemory.Alloc((nuint)bytes.Length);
        bytes.AsSpan().CopyTo(new Span<byte>((void*)_payload, bytes.Length)); _length = bytes.Length;
        _closeOwner = 17; _errorOwner = 0;
        _closeStatus = NativeStatus.Ok; _errorStatus = NativeStatus.InvalidState;
        _closeTakes = _errorTakes = _releases = _views = 0; _errorGeneration = ulong.MaxValue; _errorChannel = 0;
    }

    private static T Failure<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T failure) { Check(true, "Observed expected " + typeof(T).Name); return failure; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static void Check(bool condition, string message)
    { _checks++; if (!condition) throw new InvalidOperationException(message); }
}
