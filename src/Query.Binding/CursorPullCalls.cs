using System.Runtime.InteropServices;
using Korda.Interop.Abi;

namespace Korda.Query.Internal;

// Additive cursor-pull extension, separate from the frozen operation ABI.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePullCallbacks
{
    internal uint StructSize;
    internal uint Reserved;
    internal nint Context;
    internal delegate* unmanaged[Cdecl]<nint, ulong, void> Ready;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePullBatch
{
    internal nint Owner;
    internal NwBytesViewV1 Bytes;
    internal uint KnownEnd;
    internal uint Reserved;
}

internal sealed unsafe class CursorPullCalls
{
    internal const string Capability = "db.cursor.pull.v1";
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint> Create;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, uint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint> CreateMutation;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, NwBytesViewV1, uint, nint*, NwErrorInfoV1*, uint> CreateRelation;
    internal readonly delegate* unmanaged[Cdecl]<nint, ulong, uint, ulong, NativePullCallbacks*, NwErrorInfoV1*, uint> Request;
    internal readonly delegate* unmanaged[Cdecl]<nint, ulong, NativePullBatch*, NwErrorInfoV1*, uint> Take;
    internal readonly delegate* unmanaged[Cdecl]<nint, ulong, nint*, NwErrorInfoV1*, uint> TakeError;
    internal readonly delegate* unmanaged[Cdecl]<nint, NwErrorInfoV1*, uint> CheckRead;
    internal readonly delegate* unmanaged[Cdecl]<nint, uint> Stop;

    internal CursorPullCalls(Func<string, nint> resolve)
    {
        Create = (delegate* unmanaged[Cdecl]<nint, nint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_cursor_pull_create_v1");
        CreateMutation = (delegate* unmanaged[Cdecl]<nint, nint, uint, NwBytesViewV1, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_cursor_pull_mutation_create_v1");
        CreateRelation = (delegate* unmanaged[Cdecl]<nint, nint, NwBytesViewV1, uint, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_cursor_pull_relation_create_v1");
        Request = (delegate* unmanaged[Cdecl]<nint, ulong, uint, ulong, NativePullCallbacks*, NwErrorInfoV1*, uint>)resolve("nw_db_cursor_pull_request_v1");
        Take = (delegate* unmanaged[Cdecl]<nint, ulong, NativePullBatch*, NwErrorInfoV1*, uint>)resolve("nw_db_cursor_pull_take_v1");
        TakeError = (delegate* unmanaged[Cdecl]<nint, ulong, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_cursor_pull_take_error_v1");
        CheckRead = (delegate* unmanaged[Cdecl]<nint, NwErrorInfoV1*, uint>)resolve("nw_db_cursor_pull_check_read_v1");
        Stop = (delegate* unmanaged[Cdecl]<nint, uint>)resolve("nw_db_cursor_pull_stop_v1");
    }

    internal void Require()
    {
        BindingChecks.Require((nint)Create, "nw_db_cursor_pull_create_v1");
        BindingChecks.Require((nint)CreateMutation, "nw_db_cursor_pull_mutation_create_v1");
        BindingChecks.Require((nint)CreateRelation, "nw_db_cursor_pull_relation_create_v1");
        BindingChecks.Require((nint)Request, "nw_db_cursor_pull_request_v1");
        BindingChecks.Require((nint)Take, "nw_db_cursor_pull_take_v1");
        BindingChecks.Require((nint)TakeError, "nw_db_cursor_pull_take_error_v1");
        BindingChecks.Require((nint)CheckRead, "nw_db_cursor_pull_check_read_v1");
        BindingChecks.Require((nint)Stop, "nw_db_cursor_pull_stop_v1");
    }
}
