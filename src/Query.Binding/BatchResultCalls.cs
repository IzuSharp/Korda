using System.Runtime.InteropServices;
using Korda.Interop.Abi;

namespace Korda.Query.Internal;

// Additive result extension; deliberately absent from the frozen v1 inventory.
[StructLayout(LayoutKind.Sequential)]
internal struct NativeBatchResult
{
    internal nint Owner;
    internal NwBytesViewV1 Bytes;
    internal uint KnownEnd;
    internal uint Reserved;
}

internal sealed unsafe class BatchResultCalls
{
    internal const string Capability = "db.result.batch.v1";
    internal readonly delegate* unmanaged[Cdecl]<nint, NativeBatchResult*, NwErrorInfoV1*, uint> Take;
    internal readonly delegate* unmanaged[Cdecl]<nint, NwErrorInfoV1*, uint> CheckReadAdmission;

    internal BatchResultCalls(Func<string, nint> resolve)
    {
        Take = (delegate* unmanaged[Cdecl]<nint, NativeBatchResult*, NwErrorInfoV1*, uint>)resolve("nw_db_operation_take_batch_v1");
        CheckReadAdmission = (delegate* unmanaged[Cdecl]<nint, NwErrorInfoV1*, uint>)resolve("nw_db_cursor_check_read_admission_v1");
    }

    internal void Require()
    {
        BindingChecks.Require((nint)Take, "nw_db_operation_take_batch_v1");
        BindingChecks.Require((nint)CheckReadAdmission, "nw_db_cursor_check_read_admission_v1");
    }
}
