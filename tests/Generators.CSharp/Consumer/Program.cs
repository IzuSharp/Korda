using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeWeave.Generated.Conformance;

if (args.Length == 0) throw new ArgumentException("Native conformance library path is required.");
var library = NativeLibrary.Load(Path.GetFullPath(args[0]));
NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, (name, _, _) => name == "nativeweave_exports_conformance" ? library : 0);
var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks++; }
unsafe
{
    NwAbiInfoV1 info = default;
    Check(sizeof(NwAbiInfoV1) == 32, "generated record size");
    Check(NativeMethods.nw_exports_get_abi_v1(1, 32, &info) == 0 && info.abi_major == 1, "generated LibraryImport");
    Check(Handwritten.GetAbi(1, 32, &info) == 0 && info.pointer_width_bits == 64, "handcrafted LibraryImport parity");
    var direct = (delegate* unmanaged[Cdecl]<uint, uint, NwAbiInfoV1*, uint>)NativeLibrary.GetExport(library, "nw_exports_get_abi_v1");
    Check(direct(1, 32, &info) == 0 && info.capabilities == 1, "direct native parity");
    for (uint capacity = 0; capacity < 32; capacity++)
        Check(NativeMethods.nw_exports_get_abi_v1(1, capacity, &info) == Handwritten.GetAbi(1, capacity, &info), "error parity");
    Check(OwnedCalls.nw_exports_create_v1(0, out var failed) == 3 && failed == null, "failure transfers no owner");
    Check(OwnedCalls.nw_exports_create_v1(37, out var owner) == 0 && owner != null, "typed owner");
    ulong value = 0;
    Check(OwnedCalls.nw_exports_read_v1(owner!, &value) == 0 && value == 37, "A03 admitted generated call");
    owner!.Dispose(); owner.Dispose();
    try { OwnedCalls.nw_exports_read_v1(owner, &value); throw new Exception("Late admission accepted"); }
    catch (ObjectDisposedException) { checks++; }
    Check(owner.Resource.Retirement.IsCompletedSuccessfully, "true retirement");
    Check(OwnedCalls.nw_exports_create_v1(13, out var errorOwner) == 0, "error fixture owner");
    try { Check(OwnedCalls.nw_exports_read_v1(errorOwner!, &value) == 13, "ordinary native error preserved"); }
    finally { errorOwner!.Dispose(); }

    // The callback never runs user code. The fixed coordinator only records a terminal
    // notification, retains the A03 setup lease, and later performs synchronous fixture retirement.
    foreach (var mode in new[] { 0, 1, 2 })
    {
        Check(OwnedCalls.nw_exports_create_v1(73, out var operationOwner) == 0, "operation fixture create");
        var context = new CallbackContext { ThrowDispatch = mode == 1, ThrowWake = mode == 2, OriginalNativeStatus = 2 };
        var cookie = GCHandle.Alloc(context);
        try
        {
            var callback = (nuint)(delegate* unmanaged[Cdecl]<nuint, uint, uint>)&ReverseEntries.nw_exports_notification_v1;
            var result = Operation_fixture.Start(operationOwner!, callback, (nuint)GCHandle.ToIntPtr(cookie), 2);
            Check(result == (mode == 0 ? 0u : 8u), "generated callback containment");
            var setup = context.Setup!;
            Check(context.CompletedBeforeStartReturn && setup.StartReturned && setup.Owner.Resource.ActiveLeases == 1, "setup retained beyond early completion");
            Check(context.OriginalNativeStatus == 2 && context.CleanupSignals == 1, "native outcome and cleanup signal remain distinct");
            Check(context.Faults == (mode == 0 ? 0 : 1), "bridge fault latch");
            operationOwner!.Dispose();
            nuint live = 0, released = 0;
            Check(NativeMethods.nw_exports_counters_v1(&live, &released) == 0 && live == 1, "logical close retains native setup owner");
            Check(OwnedCalls.nw_exports_create_v1(91, out var unpublished) == 0, "owned result awaiting mapping");
            setup.Unpublished = unpublished;
            if (mode == 1)
            {
                try { Operation_fixture.MapResult<int>(setup, static () => throw new FormatException("injected mapping")); throw new Exception("Mapper did not throw"); }
                catch (FormatException) { checks++; }
                Check(setup.MapperFailures == 1, "mapping rollback hook");
            }
            else Check(Operation_fixture.MapResult(setup, static () => 42) == 42, "closed generic mapper");
            Check(setup.Retired && setup.RetirementRequests == 1 && NativeMethods.nw_exports_counters_v1(&live, &released) == 0 && live == 0, "creator releases after actual fixture drain");
        }
        finally { cookie.Free(); }
    }
}
Check(BindingMetadata_M_55ef77824d13a8b4123ce20319df47b1ec58874d6767553213184b9f5d986af8.ExportCount == 10, "incremental generated metadata executes");
if (args.Contains("--require-aot", StringComparer.Ordinal)) Check(!RuntimeFeature.IsDynamicCodeSupported, "actual Native AOT");
Console.WriteLine($"PASS A10 generated/native/manual consumers: {checks} assertions; A03 admission; static UCO; fixed D06 coordinator; dynamicCode={RuntimeFeature.IsDynamicCodeSupported}.");

internal static unsafe partial class Handwritten
{
    [LibraryImport("nativeweave_exports_conformance", EntryPoint = "nw_exports_get_abi_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetAbi(uint major, uint capacity, NwAbiInfoV1* info);
}
