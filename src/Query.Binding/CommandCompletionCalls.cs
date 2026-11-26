using System.Runtime.InteropServices;
using Korda.Interop.Abi;

namespace Korda.Query.Internal;

internal enum NativeCommandKind : uint
{
    Execute = 1, Mutation, BeginTransaction, FinishTransaction, BeginSavepoint, SavepointAction, CloseChild
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCommandRequest
{
    internal uint StructSize;
    internal NativeCommandKind Kind;
    internal nint Target;
    internal nint Input;
    internal NwBytesViewV1 Payload;
    internal uint Action;
    internal uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCommandResult
{
    internal uint StructSize;
    internal uint Kind;
    internal long Count;
    internal nint Owner;
    internal nint Buffer;
}

// Private typed completion records; the frozen operation ABI stays independent.
internal sealed unsafe class CommandCompletionCalls
{
    internal const string Capability = "db.command.completion.v1";
    internal readonly delegate* unmanaged[Cdecl]<NativeCommandRequest*, NativePullCallbacks*, nint*, NwErrorInfoV1*, uint> Request;
    internal readonly delegate* unmanaged[Cdecl]<nint, uint, NativeCommandResult*, NwErrorInfoV1*, uint> Take;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint> TakeError;
    internal readonly delegate* unmanaged[Cdecl]<nint, uint> Cancel;

    internal CommandCompletionCalls(Func<string, nint> resolve)
    {
        Request = (delegate* unmanaged[Cdecl]<NativeCommandRequest*, NativePullCallbacks*, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_command_request_v1");
        Take = (delegate* unmanaged[Cdecl]<nint, uint, NativeCommandResult*, NwErrorInfoV1*, uint>)resolve("nw_db_command_take_v1");
        TakeError = (delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_command_take_error_v1");
        Cancel = (delegate* unmanaged[Cdecl]<nint, uint>)resolve("nw_db_command_cancel_v1");
    }

    internal void Require()
    {
        BindingChecks.Require((nint)Request, "nw_db_command_request_v1");
        BindingChecks.Require((nint)Take, "nw_db_command_take_v1");
        BindingChecks.Require((nint)TakeError, "nw_db_command_take_error_v1");
        BindingChecks.Require((nint)Cancel, "nw_db_command_cancel_v1");
    }
}
