using System.Runtime.InteropServices;
using Korda.Interop.Abi;

namespace Korda.Query.Internal;

// Additive extension, deliberately separate from the frozen direct v1 inventory.
// All inputs are borrowed only until the synchronous call returns. The command
// contains indices into this checked owner sidecar, never native addresses.
[StructLayout(LayoutKind.Sequential)]
internal struct QueryCommandDependency
{
    internal nint Owner;
    internal uint Kind;
    internal uint Reserved;
}

internal sealed unsafe class QueryCommandCalls
{
    internal readonly delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, QueryCommandDependency*, uint, uint, nint*, NwErrorInfoV1*, uint> Compile;
    internal readonly delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, QueryCommandDependency*, uint, nint*, NwErrorInfoV1*, uint> Render;
    internal readonly delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, QueryCommandDependency*, uint, nint, uint, uint, nint*, NwErrorInfoV1*, uint> Cte;

    internal QueryCommandCalls(Func<string, nint> resolve)
    {
        Compile = (delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, QueryCommandDependency*, uint, uint, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_query_command_compile_v1");
        Render = (delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, QueryCommandDependency*, uint, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_query_command_render_v1");
        Cte = (delegate* unmanaged[Cdecl]<nint, NwBytesViewV1, QueryCommandDependency*, uint, nint, uint, uint, nint*, NwErrorInfoV1*, uint>)resolve("nw_db_query_command_cte_v1");
    }

    internal void Require()
    {
        if (Compile == null || Render == null || Cte == null)
            throw new BindingFailure(NativeStatus.NotSupported, 0, 11, 0);
    }
}

// SafeHandle.Dispose closes new admission; an already captured command must keep
// its dependencies alive independently. Each wrapper owns exactly one native
// lifetime reference, shared only by immutable snapshots and their clones.
internal sealed class RetainedQueryOwner
{
    private DatabaseOwner? _owner;
    private int _references = 1;
    internal nint Pointer { get; }
    internal DatabaseOwner Owner => _owner ?? throw new ObjectDisposedException(nameof(RetainedQueryOwner));

    internal RetainedQueryOwner(DatabaseOwner owner, DatabaseOwnerKind kind)
    {
        if (owner.Kind != kind) throw new ArgumentException("Unexpected query dependency kind.");
        using var admission = owner.Borrow(kind);
        var retained = false;
        owner.DangerousAddRef(ref retained);
        if (!retained) throw new ObjectDisposedException(nameof(DatabaseOwner));
        _owner = owner;
        Pointer = admission.Pointer;
    }

    internal RetainedQueryOwner Retain()
    {
        if (Interlocked.Increment(ref _references) <= 1) throw new ObjectDisposedException(nameof(RetainedQueryOwner));
        return this;
    }

    internal void Release()
    {
        if (Interlocked.Decrement(ref _references) != 0) return;
        Interlocked.Exchange(ref _owner, null)?.DangerousRelease();
        GC.SuppressFinalize(this);
    }

    ~RetainedQueryOwner() => Interlocked.Exchange(ref _owner, null)?.DangerousRelease();
}
