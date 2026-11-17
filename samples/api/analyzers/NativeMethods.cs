using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NativeWeave.Analyzers.Example;

// This maintainer-facing example compiles against the existing prebuilt Rust
// bootstrap asset. It does not define a new engine or a new application API.
internal static unsafe partial class NativeMethods
{
    [LibraryImport("nativeweave_hello", EntryPoint = "nw_hello_abi_version")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial uint Version();

    [LibraryImport("nativeweave_hello", EntryPoint = "nw_hello_greet")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial uint Greet(byte* name, nuint nameLength, byte* output, nuint capacity, nuint* written);
}
