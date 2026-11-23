using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Korda.Interop;
using Korda.Interop.Extensibility;

// The verifier supplies the hashes from the exact two compiled native fixtures.
// Real domain packages ship these as generated immutable package constants.
if (args.Length < 2)
    throw new ArgumentException("Run the A02 verifier to supply the fixture package hashes and SDK output records.");
if (args.Contains("--require-aot") && RuntimeFeature.IsDynamicCodeSupported)
    throw new InvalidOperationException("Expected actual Native AOT execution.");

var alpha = CreateFixtureDescriptor("alpha", args[0], "codec.alpha.v1", 0);
var beta = CreateFixtureDescriptor("beta", args[1], "codec.beta.v1", 1);

// Minimal explicit preflight: ordinary domain factories use their package's
// descriptor automatically, so an application need not build a global catalog.
var catalog = new EngineCatalog([alpha, beta]);
EngineInfo first = catalog.Inspect("sample.alpha");
Console.WriteLine($"{first.EngineId}: ABI {first.AbiVersion.Major}.{first.AbiVersion.Minor}, {first.RuntimeIdentifier}");

// Advanced preflight uses the same cached module and checks an exact feature
// before an application proceeds to create any dependent domain resources.
var required = new EngineCapability("codec.beta.v1");
EngineInfo second = beta.Inspect(new EngineLoadOptions { RequiredCapabilities = [required] });
second.Require(required);
if (!catalog.TryGet("sample.beta", out EngineDescriptor? found) || !ReferenceEquals(found, beta))
    throw new InvalidOperationException("The explicit catalog lost an entry.");
if (Native.Alpha(7) != (7UL ^ 0xa102) || Native.Beta(7) != (7UL ^ 0xb203))
    throw new InvalidOperationException("The package's fixed imports resolved to the wrong native module.");
try
{
    alpha.Inspect(new EngineLoadOptions { RequiredCapabilities = [required] });
    throw new InvalidOperationException("A missing capability was silently accepted.");
}
catch (EngineLoadException error) when (error.Failure == EngineLoadFailure.MissingCapability)
{
    Console.WriteLine("The missing capability was rejected before domain creation.");
}
Console.WriteLine($"{{\"status\":\"passed\",\"sample\":\"minimal-and-advanced\",\"dynamicCodeSupported\":{RuntimeFeature.IsDynamicCodeSupported.ToString().ToLowerInvariant()}}}");

static EngineDescriptor CreateFixtureDescriptor(string suffix, string hash, string capability, int bit)
{
    var manifest = new EnginePackageManifest(
        $"sample.{suffix}", "Korda.A02.Sample", "0.1.0-preview.1", new AbiVersion(1, 0),
        $"nw_a02_{suffix}_get_abi_v1", EngineThreadingModel.Synchronous,
        [new NativeAssetDescriptor("linux-x64", $"runtimes/linux-x64/native/libnw_a02_{suffix}.so", hash, "x86-64")],
        [new EngineCapabilityDefinition(new EngineCapability(capability), bit)]);
    return EnginePackage.CreateDescriptor(manifest, typeof(Native).Assembly);
}

internal static partial class Native
{
    [LibraryImport("nw_a02_alpha", EntryPoint = "nw_a02_alpha_probe")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong Alpha(ulong value);

    [LibraryImport("nw_a02_beta", EntryPoint = "nw_a02_beta_probe")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong Beta(ulong value);
}
