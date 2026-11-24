using Korda.Data;
using Korda.Interop.Abi;

namespace Korda.Query.Internal;

/// <summary>Post-Build registration keeps canonical Table/Relation identities unchanged.</summary>
/// <remarks>All registration calls borrow immutable bytes synchronously. Models, relations and admitted operations retain native copies.</remarks>
internal static unsafe class MetadataBindings
{
    internal static DatabaseOwner RegisterModel<TEntity>(DatabaseOwner source, Table<TEntity> table) where TEntity : class
    {
        var bytes = ControlRecord.Encode(2, new(1, 5, table.CanonicalBytes), new(2, 5, Convert.FromHexString(table.MetadataHash)));
        var calls = source.Source.Calls; BindingChecks.Require((nint)calls.nw_db_metadata_register_v1, "metadata_register");
        using var lease = source.Borrow(DatabaseOwnerKind.Source); nint result = 0; var error = BindingChecks.Error();
        fixed (byte* input = bytes) BindingChecks.Status(calls.nw_db_metadata_register_v1(lease.Pointer, BindingChecks.View(input, bytes.Length), &result, &error), error);
        return new(source.Source, DatabaseOwnerKind.Model, result);
    }
    internal static uint RegisterProjection<T>(DatabaseOwner source, Projection<T> projection, Func<CodecDescriptor, uint> bindCodec)
    {
        Span<byte> scratch = stackalloc byte[OperandEncoding.StackScratchBytes];
        var bytes = OperandEncoding.ProjectionInto(projection, bindCodec, scratch); var calls = source.Source.Calls; BindingChecks.Require((nint)calls.nw_db_projection_register_v1, "projection_register");
        using var lease = source.Borrow(DatabaseOwnerKind.Source); uint result = 0; var error = BindingChecks.Error();
        fixed (byte* input = bytes) BindingChecks.Status(calls.nw_db_projection_register_v1(lease.Pointer, BindingChecks.View(input, bytes.Length), &result, &error), error);
        if (result == 0) throw new BindingFailure(NativeStatus.ContractViolation, 0, 0, 0); return result;
    }
    internal static DatabaseOwner RegisterRelation<TParent, TChild>(DatabaseOwner source, DatabaseOwner parent, DatabaseOwner child, Relation<TParent, TChild> relation)
        where TParent : class where TChild : class
    {
        BindingChecks.SameSource(source.Source, parent); BindingChecks.SameSource(source.Source, child);
        var bytes = ControlRecord.Relation(relation); var calls = source.Source.Calls; BindingChecks.Require((nint)calls.nw_db_relation_register_v1, "relation_register");
        using var sourceLease = source.Borrow(DatabaseOwnerKind.Source); using var parentLease = parent.Borrow(DatabaseOwnerKind.Model); using var childLease = child.Borrow(DatabaseOwnerKind.Model);
        nint result = 0; var error = BindingChecks.Error();
        fixed (byte* input = bytes) BindingChecks.Status(calls.nw_db_relation_register_v1(sourceLease.Pointer, parentLease.Pointer, childLease.Pointer, BindingChecks.View(input, bytes.Length), &result, &error), error);
        return new(source.Source, DatabaseOwnerKind.Relation, result);
    }
    /// <summary>Returns a Created operation for immediate adoption by B04's existing operation owner; it has not executed.</summary>
    /// <remarks>The caller reserves managed operation registration before this call and transfers the returned pointer once.
    /// Start/take-owner kind2/fetch/close/retire use NativeCalls' canonical protocol; this binding does not implement another host.</remarks>
    internal static nint CreateRelationLoad(DatabaseOwner session, DatabaseOwner relation, ReadOnlySpan<byte> key, uint projectionId)
    {
        BindingChecks.SameSource(session.Source, relation); var calls = session.Source.Calls; BindingChecks.Require((nint)calls.nw_db_relation_load_create_v1, "relation_load_create");
        using var sessionLease = session.Borrow(DatabaseOwnerKind.Session); using var relationLease = relation.Borrow(DatabaseOwnerKind.Relation);
        nint result = 0; var error = BindingChecks.Error();
        fixed (byte* input = key) BindingChecks.Status(calls.nw_db_relation_load_create_v1(sessionLease.Pointer, relationLease.Pointer, BindingChecks.View(input, key.Length), projectionId, &result, &error), error);
        if (result == 0) throw new BindingFailure(NativeStatus.ContractViolation, 0, 0, 0); return result;
    }
}
