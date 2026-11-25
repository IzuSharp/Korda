using System.Runtime.InteropServices;
using Korda.Interop.Abi;

namespace Korda.Query.Internal;

/// <summary>Distinct native owner kinds; these values are managed tags, except the documented take-owner kinds.</summary>
internal enum DatabaseOwnerKind { Source, Model, Session, Query, Plan, Relation, Cte, Cursor }

/// <summary>A source-generation identity retained by every related managed wrapper.</summary>
/// <remarks>Created only by the source adapter after A02 negotiation. Identity uses reference equality; it is never serialized.</remarks>
internal sealed class SourceBinding(NativeCalls calls, uint semanticProfile)
{
    internal NativeCalls Calls { get; } = calls;
    internal uint SemanticProfile { get; } = semanticProfile is 1 or 2 ? semanticProfile : throw new ArgumentOutOfRangeException(nameof(semanticProfile));
    internal QueryCommandEnvironment? Commands { get; set; }
    internal BatchResultCalls? Batches { get; set; }
}

/// <summary>One creating-module owner reference. Native close/retirement remains B04's obligation before final release.</summary>
/// <remarks>SafeHandle protects pointer access; a separate query admission gate protects semantics. Final release is void and nonblocking.</remarks>
internal sealed unsafe class DatabaseOwner : SafeHandle
{
    private int _closing;
    internal bool IsClosing => Volatile.Read(ref _closing) != 0;
    protected override void Dispose(bool disposing) { Interlocked.Exchange(ref _closing, 1); base.Dispose(disposing); }
    internal DatabaseOwner(SourceBinding source, DatabaseOwnerKind kind, nint value) : base(0, true)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (value == 0) throw new ArgumentException("An owned native reference cannot be zero.", nameof(value));
        BindingChecks.Require((nint)source.Calls.nw_db_owner_release_v1, "owner_release");
        Source = source; Kind = kind; SetHandle(value);
    }
    internal SourceBinding Source { get; }
    internal DatabaseOwnerKind Kind { get; }
    internal QueryModelMetadata? QueryModel { get; init; }
    internal QueryPlanMetadata? QueryPlan { get; init; }
    internal QueryCteMetadata? QueryCte { get; init; }
    public override bool IsInvalid => handle == 0;
    protected override bool ReleaseHandle() { Source.Calls.nw_db_owner_release_v1(handle); return true; }
    internal OwnerLease Borrow(DatabaseOwnerKind expected)
    {
        if (Kind != expected) throw new ArgumentException("The native reference has another owner kind.");
        return new(this);
    }
}

/// <summary>A synchronous native access lease; no view or pointer may escape this scope.</summary>
internal ref struct OwnerLease
{
    private DatabaseOwner? _owner;
    internal OwnerLease(DatabaseOwner owner)
    {
        if (owner.IsClosing || owner.IsClosed || owner.IsInvalid) throw new ObjectDisposedException(nameof(DatabaseOwner));
        var retained = false;
        owner.DangerousAddRef(ref retained);
        if (!retained) throw new ObjectDisposedException(nameof(DatabaseOwner));
        _owner = owner;
        if (owner.IsClosing || owner.IsClosed) { Dispose(); throw new ObjectDisposedException(nameof(DatabaseOwner)); }
        Pointer = owner.DangerousGetHandle();
    }
    internal nint Pointer { get; }
    public void Dispose() { var owner = _owner; _owner = null; owner?.DangerousRelease(); }
}

/// <summary>Bounded cold-path failure preserved for the downstream domain mapper; no new public exception surface.</summary>
internal sealed class BindingFailure(uint status, uint domain, int code, ulong correlation) : Exception("Native query binding rejected the operation.")
{
    internal uint Status { get; } = status;
    internal uint Domain { get; } = domain;
    internal int Code { get; } = code;
    internal ulong Correlation { get; } = correlation;
}

/// <summary>Local pointer/record checks do not replace the native owner/type/size checks.</summary>
internal static unsafe class BindingChecks
{
    internal static NwErrorInfoV1 Error() => new() { StructSize = (uint)sizeof(NwErrorInfoV1) };
    internal static void Require(nint address, string operation) { if (address == 0) throw new NotSupportedException("The creating engine does not export " + operation + "."); }
    internal static void Status(uint status, NwErrorInfoV1 error)
    {
        if (status == NativeStatus.Ok) return;
        // Synchronous direct operations must not return Pending. Preserve unknown codes.
        if (status == NativeStatus.Pending) throw new BindingFailure(NativeStatus.ContractViolation, error.Domain, error.Code, error.CorrelationId);
        throw new BindingFailure(status, error.Domain, error.Code, error.CorrelationId);
    }
    internal static void SameSource(SourceBinding expected, DatabaseOwner owner)
    { if (!ReferenceEquals(expected, owner.Source)) throw new ArgumentException("References belong to different source generations."); }
    internal static NwBytesViewV1 View(byte* pointer, int length) => new() { Data = pointer, Length = checked((ulong)length) };
}
