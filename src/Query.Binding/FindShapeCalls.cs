using Korda.Interop.Abi;

namespace Korda.Query.Internal;

// Private complete-key shape construction; execution uses the existing cursor pull.
internal sealed unsafe class FindShapeCalls
{
    internal const string Capability = "db.find.shape.v1";
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, uint, uint, nint*, ulong*, ulong*, NwErrorInfoV1*, uint> Create;

    internal FindShapeCalls(Func<string, nint> resolve)
    { Create = (delegate* unmanaged[Cdecl]<nint, nint, uint, uint, nint*, ulong*, ulong*, NwErrorInfoV1*, uint>)resolve("nw_db_find_plan_create_v1"); }

    internal void Require() => BindingChecks.Require((nint)Create, "nw_db_find_plan_create_v1");
}
