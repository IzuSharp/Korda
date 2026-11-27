using Korda.Interop.Abi;

namespace Korda.Query.Internal;

// An admitted generation is executed or abandoned once. No borrowed pointer
// survives prepare; the native admission owns its independent retirement tickets.
internal sealed unsafe class CursorPullExecutionCalls
{
    internal const string Capability = "db.cursor.pull.execution.v1";
    internal readonly delegate* unmanaged[Cdecl]<nint, ulong, uint, ulong, NativePullCallbacks*, NwErrorInfoV1*, uint> Prepare;
    internal readonly delegate* unmanaged[Cdecl]<nint, ulong, NwErrorInfoV1*, uint> Run;
    internal readonly delegate* unmanaged[Cdecl]<nint, ulong, NwErrorInfoV1*, uint> Abandon;

    internal CursorPullExecutionCalls(Func<string, nint> resolve)
    {
        Prepare = (delegate* unmanaged[Cdecl]<nint, ulong, uint, ulong, NativePullCallbacks*, NwErrorInfoV1*, uint>)resolve("nw_db_cursor_pull_prepare_v1");
        Run = (delegate* unmanaged[Cdecl]<nint, ulong, NwErrorInfoV1*, uint>)resolve("nw_db_cursor_pull_run_v1");
        Abandon = (delegate* unmanaged[Cdecl]<nint, ulong, NwErrorInfoV1*, uint>)resolve("nw_db_cursor_pull_abandon_v1");
    }

    internal void Require()
    {
        BindingChecks.Require((nint)Prepare, "nw_db_cursor_pull_prepare_v1");
        BindingChecks.Require((nint)Run, "nw_db_cursor_pull_run_v1");
        BindingChecks.Require((nint)Abandon, "nw_db_cursor_pull_abandon_v1");
    }
}
