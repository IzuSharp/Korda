using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

if (args.Length == 0) throw new ArgumentException("Native library path required.");
NativeLibrary.SetDllImportResolver(typeof(Imports).Assembly, (name, _, _) => name == "nativeweave_exports_conformance" ? NativeLibrary.Load(Path.GetFullPath(args[0])) : 0);
var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks++; }
unsafe
{
    Check(sizeof(AbiInfo) == 32, "C# size");
    AbiInfo info = default;
    Check((byte*)&info.Capabilities - (byte*)&info == 16, "C# field offset");
    Check(Imports.GetAbi(1, 32, &info) == 0 && info.StructSize == 32 && info.AbiMajor == 1 && info.PointerWidth == 64 && info.Capabilities == 1 && info.Reserved == 0, "actual A01 bootstrap");
    for (uint capacity = 0; capacity < 32; capacity++)
    {
        info = new() { StructSize = 0xDEAD };
        Check(Imports.GetAbi(1, capacity, &info) == 7 && info.StructSize == 0xDEAD, "failure atomic bootstrap");
    }
    Check(Imports.GetAbi(2, 32, &info) == 6, "ABI major negative");
    nint owner = 42;
    Check(Imports.Create(0, &owner) == 3 && owner == 0, "failure atomic owner output");
    Check(Imports.Create(37, &owner) == 0 && owner != 0, "native ownership transfer");
    ulong value = 0;
    try { Check(Imports.Read(owner, &value) == 0 && value == 37, "native value"); }
    finally { Imports.Release(owner); }
    Check(Imports.Create(13, &owner) == 0, "failure owner create");
    value = 51;
    try { Check(Imports.Read(owner, &value) == 13 && value == 51, "ordinary error retains output"); }
    finally { Imports.Release(owner); }
    nuint live = 0, released = 0;
    Check(Imports.Counters(&live, &released) == 0 && live == 0 && released == 2, "creator release balance");
}
if (args.Contains("--require-aot", StringComparer.Ordinal)) Check(!RuntimeFeature.IsDynamicCodeSupported, "actual Native AOT");
Console.WriteLine($"PASS A11 actual C# native consumer: {checks} assertions; public symbols=0; dynamicCode={RuntimeFeature.IsDynamicCodeSupported}.");

[StructLayout(LayoutKind.Sequential)]
internal struct AbiInfo
{
    public uint StructSize, AbiMajor, AbiMinor, PointerWidth;
    public ulong Capabilities, Reserved;
}
internal static unsafe partial class Imports
{
    [LibraryImport("nativeweave_exports_conformance", EntryPoint = "nw_exports_get_abi_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetAbi(uint major, uint capacity, AbiInfo* output);
    [LibraryImport("nativeweave_exports_conformance", EntryPoint = "nw_exports_create_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Create(ulong value, nint* output);
    [LibraryImport("nativeweave_exports_conformance", EntryPoint = "nw_exports_read_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Read(nint owner, ulong* output);
    [LibraryImport("nativeweave_exports_conformance", EntryPoint = "nw_exports_release_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Release(nint owner);
    [LibraryImport("nativeweave_exports_conformance", EntryPoint = "nw_exports_counters_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Counters(nuint* live, nuint* released);
}
