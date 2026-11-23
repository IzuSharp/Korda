using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Korda.Interop;
using Korda.Interop.Extensibility;

if (args.Length < 3)
    throw new ArgumentException("Usage: CompatibilitySample <native-absolute-path> <accepted-sha256> <pass|major|layout|capability|hash|semantics|conflict> [--require-aot]");
if (args.Contains("--require-aot") && RuntimeFeature.IsDynamicCodeSupported)
    throw new InvalidOperationException("This row requires actual Native AOT execution.");

string path = Path.GetFullPath(args[0]);
string hash = args[1];
string scenario = args[2];
EngineCapability basic = new("compat.base.v1");
EngineCapability optional = new("compat.optional.v1");
EngineCapabilityDefinition[] definitions = [new(basic, 0)];
#if NEW_MANAGED
definitions = [new(basic, 0), new(optional, 1)];
const string generation = "new";
#else
const string generation = "old";
#endif
var manifest = new EnginePackageManifest("compat", "Korda.Compatibility.Conformance", "0.1.0",
    new AbiVersion(1, 0), "nw_compat_get_abi_v1", EngineThreadingModel.Synchronous,
    [new NativeAssetDescriptor("linux-x64", "runtimes/linux-x64/native/libnw_compat.so", hash, "x86-64-v2")], definitions);
EngineDescriptor descriptor = EnginePackage.CreateDescriptor(manifest, typeof(NativeMethods).Assembly);
int assertions = 0;
void Check(bool condition) { if (!condition) throw new InvalidOperationException("Compatibility assertion failed."); assertions++; }
EngineLoadFailure? expectedFailure = scenario switch
{
    "major" or "layout" => EngineLoadFailure.IncompatibleAbi,
    "capability" => EngineLoadFailure.MissingCapability,
    "hash" => EngineLoadFailure.AssetIntegrityMismatch,
    _ => null
};
try
{
    // Minimal consumer: an explicit path still verifies the accepted native hash.
    EngineInfo info = descriptor.Inspect(new EngineLoadOptions
    {
        NativeAssetPath = path,
        RequiredCapabilities = scenario == "capability" ? [optional] : [basic]
    });
    Check(expectedFailure is null);
    Check(info.AbiVersion.Major == 1 && info.Supports(basic));
    if (scenario == "conflict")
    {
        var conflicting = new EnginePackageManifest("compat", manifest.PackageId, "0.2.0",
            manifest.AbiVersion, manifest.BootstrapSymbol, manifest.ThreadingModel, manifest.Assets, definitions);
        try { EnginePackage.CreateDescriptor(conflicting, typeof(NativeMethods).Assembly).Inspect(new() { NativeAssetPath = path }); }
        catch (EngineLoadException error) when (error.Failure == EngineLoadFailure.ConflictingModule)
        {
            Check(NativeMethods.Calls() == 0);
            Console.WriteLine($"{{\"passed\":true,\"scenario\":\"conflict\",\"assertions\":{assertions}}}");
            return;
        }
        throw new InvalidOperationException("In-process package replacement was not rejected.");
    }
    uint result = NativeMethods.Transform(35);
    if (scenario == "semantics")
    {
        Check(result != 42 && NativeMethods.Calls() == 1);
        Console.WriteLine("Rejected changed domain behavior with identical ABI bytes.");
    }
    else
    {
        Check(result == 42 && NativeMethods.Transform(uint.MaxValue) == 6);
        // Advanced consumer: opt into an optional operation only after a capability check.
        // Old managed code deliberately knows no optional bit, even on the new binary.
        if (info.Supports(optional)) Check(NativeMethods.Optional(21) == 42);
        else
        {
            try { info.Require(optional); throw new InvalidOperationException("Missing capability was not rejected."); }
            catch (NotSupportedException) { assertions++; }
        }
        EngineLoadOptions defaults = new();
        Check(defaults.NativeAssetPath is null && defaults.RequiredCapabilities.Count == 0);
    }
}
catch (EngineLoadException error) when (error.Failure == expectedFailure)
{
    assertions++;
    // Negative fixture only: inspect its counter after A02 has rejected the
    // package, proving no domain operation ran. This is never an application fallback.
    nint module = NativeLibrary.Load(path);
    unsafe
    {
        var calls = (delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(module, "nw_compat_calls");
        Check(calls() == 0);
    }
    // The test follows A02's process-resident module policy; do not unload.
}
Console.WriteLine($"{{\"passed\":true,\"generation\":\"{generation}\",\"scenario\":\"{scenario}\",\"assertions\":{assertions},\"dynamicCode\":{RuntimeFeature.IsDynamicCodeSupported.ToString().ToLowerInvariant()}}}");

internal static partial class NativeMethods
{
    [LibraryImport("nw_compat", EntryPoint = "nw_compat_transform")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Transform(uint value);

    [LibraryImport("nw_compat", EntryPoint = "nw_compat_optional")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Optional(uint value);

    [LibraryImport("nw_compat", EntryPoint = "nw_compat_calls")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Calls();
}
