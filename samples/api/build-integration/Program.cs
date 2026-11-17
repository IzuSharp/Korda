using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Only the maintainer build creates this fixture asset. A normal managed build
// reads precompiled bytes; it has no Cargo target or source-generator side effect.
if (Native.Version() != 1 || Native.Value(5) != 42 || Native.Value(ulong.MaxValue) != 36)
    throw new InvalidOperationException("The staged native build-conformance asset differs.");
Console.WriteLine(RuntimeFeature.IsDynamicCodeSupported ? "PASS A13 JIT: 3 native assertions" : "PASS A13 AOT: 3 native assertions");

internal static partial class Native
{
    [LibraryImport("nativeweave_build_conformance", EntryPoint = "nw_build_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Version();

    [LibraryImport("nativeweave_build_conformance", EntryPoint = "nw_build_value")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong Value(ulong value);
}
