// Maintainer-only sample over the product ABI and a test-only engine. This does
// not publish raw ABI access as an application API or initialize a domain engine.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeWeave.Interop.Abi;

internal static unsafe partial class Program
{
    [LibraryImport("nw_abi_conformance", EntryPoint = "nw_abi_conformance_get_abi_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint GetAbi(uint requestedMajor, uint outCapacity, NwAbiInfoV1* output);

    private static int Main(string[] args)
    {
        NwAbiInfoV1 info = default;
        uint status = GetAbi(1, (uint)sizeof(NwAbiInfoV1), &info);
        if (status != NativeStatus.Ok || AbiBootstrap.Validate(info, 32, 1, 0, 1) != NativeStatus.Ok)
            throw new InvalidOperationException("The test engine does not match the required ABI.");
        Console.WriteLine($"ABI {info.AbiMajor}.{info.AbiMinor}, {info.PointerWidthBits}-bit; no domain runtime started.");
        if (args.Contains("--advanced", StringComparer.Ordinal))
        {
            uint unsupported = AbiBootstrap.Validate(info, 32, 1, 0, 2);
            if (unsupported != NativeStatus.NotSupported)
                throw new InvalidOperationException("Required capability must be rejected before any domain call.");
            Console.WriteLine("Missing capability rejected; initialized 32-byte record, no owner transferred.");
        }
        if (args.Contains("--require-aot", StringComparer.Ordinal) && RuntimeFeature.IsDynamicCodeSupported)
            throw new InvalidOperationException("Expected actual Native AOT execution.");
        return 0;
    }
}
