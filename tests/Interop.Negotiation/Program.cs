using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NativeWeave.Interop;
using NativeWeave.Interop.Extensibility;

if (args.Length < 2)
    throw new ArgumentException("Usage: Interop.Negotiation.Tests <case> <fixture-directory> [--require-aot]");
if (args.Contains("--require-aot") && RuntimeFeature.IsDynamicCodeSupported)
    throw new InvalidOperationException("The claimed Native AOT execution still supports dynamic code.");
Suite.Run(args[0], Path.GetFullPath(args[1]));
Console.WriteLine($"{{\"status\":\"passed\",\"case\":\"{args[0]}\",\"assertions\":{Suite.Assertions},\"dynamicCodeSupported\":{RuntimeFeature.IsDynamicCodeSupported.ToString().ToLowerInvariant()}}}");

internal static class Suite
{
    internal static int Assertions;
    private static readonly EngineCapability Alpha = new("codec.alpha.v1");
    private static readonly EngineCapability Beta = new("codec.beta.v1");
    private static string _fixtures = "";

    internal static void Run(string name, string fixtures)
    {
        _fixtures = fixtures;
        switch (name)
        {
            case "metadata": Metadata(); break;
            case "coexist": Coexist(); break;
            case "concurrent": Concurrent(); break;
            case "output-record": OutputRecord(); break;
            case "options": Options(); break;
            case "minor-old": Assert(Inspect("old", "minor-zero", minimumMinor: 0).AbiVersion.Minor == 0); break;
            case "future-size": Assert(Inspect("future", "future-size").AbiVersion.Minor == 3); break;
            case "missing-export": Failure(() => Inspect("missing-export", "beta", bootstrap: "nw_a02_alpha_get_abi_v1"), EngineLoadFailure.MissingExport); break;
            case "major-requirement": Failure(() => Inspect("major-requirement", "alpha", major: 2), EngineLoadFailure.IncompatibleAbi); break;
            case "minor-requirement": Failure(() => Inspect("minor-requirement", "minor-zero", minimumMinor: 1), EngineLoadFailure.IncompatibleAbi); break;
            case "missing-asset": Failure(() => Descriptor("missing", "alpha").Inspect(new() { NativeAssetPath = Path.Combine(fixtures, "missing.so") }), EngineLoadFailure.MissingAsset); break;
            case "hash-mismatch": Failure(() => Descriptor("hash", "alpha", hash: new string('0', 64)).Inspect(PathOptions("alpha")), EngineLoadFailure.AssetIntegrityMismatch); break;
            case "invalid-binary": InvalidBinary(); break;
            case "wrong-rid": Failure(() => Descriptor("wrong-rid", "alpha", rid: "win-x64").Inspect(), EngineLoadFailure.UnsupportedPlatform); break;
            case "cpu-profile": Failure(() => Descriptor("cpu", "alpha", cpu: "unverified-avx512").Inspect(PathOptions("alpha")), EngineLoadFailure.InvalidBinary); break;
            case "conflict": Conflict(); break;
            case "latched-failure": LatchedFailure(); break;
            case "preload-retry": PreloadRetry(); break;
            case "default-missing": Failure(() => Descriptor("missing-record", "alpha").Inspect(), EngineLoadFailure.MissingAsset); break;
            case "cached-override": CachedOverride(); break;
            default:
                if (!name.StartsWith("bad-", StringComparison.Ordinal)) throw new ArgumentException("Unknown case.");
                Failure(() => Inspect(name, name), EngineLoadFailure.IncompatibleAbi);
                break;
        }
    }

    private static void Metadata()
    {
        Assert(default(EngineCapability).Name == "");
        Throws<ArgumentNullException>(() => new EngineCapability(null!));
        foreach (string value in new[] { "", "UPPER", "1x", ".a", "a.", "a..b", "a--b", "a.-b", "a_b", "a/b", "a b", "é", "a\0", new string('a', 129) })
            Throws<ArgumentException>(() => new EngineCapability(value));
        foreach (string value in new[] { "a", "a0", "codec.alpha.v1", "a-b.3", new string('a', 128) })
        {
            var capability = new EngineCapability(value);
            Assert(capability.Name == value && capability.ToString() == value && capability == new EngineCapability(value));
        }
        Assert(new AbiVersion(1, 2) == new AbiVersion(1, 2));
        Assert(new AbiVersion(1, 2) != new AbiVersion(1, 3));
        var asset = Asset("alpha");
        Throws<ArgumentException>(() => new NativeAssetDescriptor("linux.1-x64", asset.RelativePath, asset.Sha256, "x86-64"));
        foreach (string path in new[] { "/root.so", "../a", "runtimes/linux-x64/native/../a", "runtimes/linux-x64/native//a", "runtimes/linux-x64/native/./a", "runtimes/win-x64/native/a.dll", "runtimes\\linux-x64\\native\\a.so", "runtimes/linux-x64/native/a\0" })
            Throws<ArgumentException>(() => new NativeAssetDescriptor("linux-x64", path, asset.Sha256, "x86-64"));
        Throws<ArgumentException>(() => new NativeAssetDescriptor("linux-x64", asset.RelativePath, new string('A', 64), "x86-64"));
        Throws<ArgumentException>(() => new NativeAssetDescriptor("linux-x64", asset.RelativePath, asset.Sha256, ""));
        Throws<ArgumentException>(() => new NativeAssetDescriptor("linux-x64", asset.RelativePath, asset.Sha256, "\ud800"));
        Throws<ArgumentException>(() => Manifest("bad-id", [asset], [], major: 0));
        Throws<ArgumentException>(() => Manifest("bad-enum", [asset], [], model: (EngineThreadingModel)99));
        Throws<ArgumentException>(() => Manifest("empty", [], []));
        Throws<ArgumentException>(() => Manifest("duplicate", [asset, asset], []));
        Throws<ArgumentException>(() => Manifest("null-asset", [null!], []));
        Throws<ArgumentException>(() => Manifest("invalid-bit", [asset], [new(Alpha, 64)]));
        Throws<ArgumentException>(() => Manifest("negative-bit", [asset], [new(Alpha, -1)]));
        Throws<ArgumentException>(() => Manifest("invalid-cap", [asset], [default]));
        Throws<ArgumentException>(() => Manifest("duplicate-cap", [asset], [new(Alpha, 0), new(Alpha, 1)]));
        Throws<ArgumentException>(() => Manifest("duplicate-bit", [asset], [new(Alpha, 0), new(Beta, 0)]));
        var definitions = Enumerable.Range(0, 64).Select(i => new EngineCapabilityDefinition(new($"feature.x{i}"), i)).ToArray();
        Assert(Manifest("all-bits", [asset], definitions).Capabilities.Count == 64);
        Throws<ArgumentException>(() => Manifest("too-many", [asset], definitions.Append(new(new("feature.excess"), 0)).ToArray()));
        foreach (string version in new[] { "", "1", "1.0", "01.0.0", "1.0.0.0", "1.0.0-01", "1.0.0-", "1.0.0+", "1.0.0-β" })
            Throws<ArgumentException>(() => Manifest("version", [asset], [], version: version));
        Assert(Manifest("version", [asset], [], version: "1.0.0-preview.1+build").PackageVersion == "1.0.0-preview.1+build");
        var assets = new List<NativeAssetDescriptor> { asset };
        var caps = new List<EngineCapabilityDefinition> { new(Alpha, 0) };
        EnginePackageManifest snapshot = Manifest("snapshot", assets, caps);
        assets.Clear(); caps.Clear();
        Assert(snapshot.Assets.Count == 1 && snapshot.Capabilities.Count == 1);
        Throws<NotSupportedException>(() => ((IList<NativeAssetDescriptor>)snapshot.Assets).Clear());
        var one = EnginePackage.CreateDescriptor(snapshot, typeof(Suite).Assembly);
        var two = Descriptor("aaa", "beta");
        var descriptors = new List<EngineDescriptor> { one, two };
        var catalog = new EngineCatalog(descriptors); descriptors.Clear();
        Assert(catalog.Engines.Count == 2 && catalog.Engines[0].EngineId == "aaa");
        Assert(catalog.TryGet("snapshot", out EngineDescriptor? found) && ReferenceEquals(found, one));
        Assert(!catalog.TryGet("missing", out found) && found is null);
        Throws<KeyNotFoundException>(() => catalog.Get("missing"));
        Throws<ArgumentException>(() => catalog.Get("SNAPSHOT"));
        Throws<ArgumentNullException>(() => catalog.TryGet(null!, out _));
        Throws<ArgumentException>(() => new EngineCatalog([one, one]));
        Throws<ArgumentException>(() => new EngineCatalog([null!]));
        Throws<ArgumentNullException>(() => new EngineCatalog(null!));
        Assert(new EngineCatalog([]).Engines.Count == 0);
        var enumeration = new BoundedDescriptors(i => Descriptor($"catalog.x{i}", "alpha"));
        Throws<ArgumentException>(() => new EngineCatalog(enumeration));
        Assert(enumeration.Observed == 257 && enumeration.Disposed && enumeration.Enumerations == 1);
        Throws<ArgumentNullException>(() => EnginePackage.CreateDescriptor(null!, typeof(Suite).Assembly));
        Throws<ArgumentNullException>(() => EnginePackage.CreateDescriptor(snapshot, null!));
        // No metadata operation above needs a native output record or import.
        Assert(one.SupportedRuntimeIdentifiers.SequenceEqual(["linux-x64"]));
    }

    private static void Coexist()
    {
        EngineDescriptor alpha = Descriptor("coexist.alpha", "alpha");
        EngineDescriptor beta = Descriptor("coexist.beta", "beta");
        EngineInfo a = alpha.Inspect(PathOptions("alpha"));
        EngineInfo b = beta.Inspect(PathOptions("beta"));
        Assert(a.EngineId == "coexist.alpha" && b.EngineId == "coexist.beta");
        Assert(a.AbiVersion == new AbiVersion(1, 3));
        Assert(a.Supports(Alpha) && !a.Supports(Beta) && b.Supports(Beta) && !b.Supports(Alpha));
        Assert(a.Capabilities.Count == 1 && b.Capabilities.Count == 1); // Unknown bit63 is ignored.
        Assert(a.ThreadingModel == EngineThreadingModel.Synchronous);
        Assert(Native.Alpha(123) == (123UL ^ 0xa102) && Native.Beta(123) == (123UL ^ 0xb203));
        Assert(Native.AlphaCalls() == 1);
        Assert(ReferenceEquals(a, alpha.Inspect()));
        a.Require(Alpha);
        Throws<NotSupportedException>(() => a.Require(Beta));
        Throws<ArgumentException>(() => a.Supports(default));
        Failure(() => alpha.Inspect(new() { RequiredCapabilities = [Beta] }), EngineLoadFailure.MissingCapability);
        Assert(ReferenceEquals(a, alpha.Inspect()) && Native.AlphaCalls() == 1);
        Assert(ReferenceEquals(a, Descriptor("coexist.alpha", "alpha").Inspect()));
        var catalog = new EngineCatalog([beta, alpha]);
        Assert(ReferenceEquals(catalog.Inspect("coexist.alpha"), a));
        // A successful hot capability test allocates no managed bytes after warm-up.
        for (int i = 0; i < 100; i++) Assert(a.Supports(Alpha));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) _ = a.Supports(Alpha);
        Assert(GC.GetAllocatedBytesForCurrentThread() == before);
    }

    private static void Concurrent()
    {
        var descriptor = Descriptor("concurrent", "alpha");
        EngineInfo?[] results = new EngineInfo[64];
        Parallel.For(0, results.Length, i => results[i] = descriptor.Inspect(PathOptions("alpha")));
        Assert(results.All(x => ReferenceEquals(x, results[0])));
        Assert(Native.AlphaCalls() == 1);
    }

    private static void Options()
    {
        EngineDescriptor descriptor = Descriptor("options", "alpha");
        Throws<ArgumentException>(() => descriptor.Inspect(new() { NativeAssetPath = "relative.so" }));
        Throws<ArgumentException>(() => descriptor.Inspect(new() { NativeAssetPath = "/" + new string('x', 4096) }));
        Throws<ArgumentNullException>(() => descriptor.Inspect(new() { RequiredCapabilities = null! }));
        Throws<ArgumentException>(() => descriptor.Inspect(new() { RequiredCapabilities = [default] }));
        Throws<ArgumentException>(() => descriptor.Inspect(new() { RequiredCapabilities = [Alpha, Alpha] }));
        Throws<ArgumentException>(() => descriptor.Inspect(new() { RequiredCapabilities = Enumerable.Range(0, 65).Select(i => new EngineCapability($"x.a{i}")).ToArray() }));
        var caps = new List<EngineCapability> { Alpha };
        var options = new EngineLoadOptions { NativeAssetPath = Fixture("alpha"), RequiredCapabilities = caps };
        EngineInfo info = descriptor.Inspect(options);
        caps[0] = Beta;
        Assert(info.Supports(Alpha) && !info.Supports(Beta));
        Failure(() => descriptor.Inspect(options), EngineLoadFailure.MissingCapability);
        Assert(ReferenceEquals(info, descriptor.Inspect()));
    }

    private static void OutputRecord()
    {
        EngineDescriptor descriptor = Descriptor("record", "alpha");
        NativeAssetDescriptor asset = descriptor.Manifest.Assets[0];
        string good = Record(descriptor, "A02-assets/libnw_a02_alpha.so");
        foreach (string bad in new[]
        {
            "{}", "[]", "null", good + "{}", good.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
            good.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
            good.Replace("\"schemaVersion\":1", "\"schemaVersion\":\"1\""),
            good.Replace("\"engineId\":\"record\"", "\"engineId\":\"different\""),
            good.Replace("\"rid\":\"linux-x64\"", "\"rid\":\"win-x64\""),
            good.Replace("\"packageVersion\":\"0.1.0-preview.1\"", "\"packageVersion\":\"0.2.0\""),
            good.Replace("A02-assets/libnw_a02_alpha.so", "../libnw_a02_alpha.so"),
            good.Replace("A02-assets/libnw_a02_alpha.so", "/tmp/libnw_a02_alpha.so"),
            good.Replace("A02-assets/libnw_a02_alpha.so", "A02-assets//libnw_a02_alpha.so"),
            good.Replace("\"outputPath\":", "\"outputPath\":null,\"outputPath\":"),
            good.Replace(asset.Sha256, new string('0', 64)), new string(' ', 16385)
        })
            Failure(() => AssetLocation.Read(Encoding.UTF8.GetBytes(bad), descriptor, asset, "linux-x64"), EngineLoadFailure.InvalidManifest);
        string optional = good[..^1] + ",\"futureOptional\":{\"nested\":[1,2]}}";
        Failure(() => AssetLocation.Read([0x7b, 0x22, 0xff, 0x22, 0x3a, 0x31, 0x7d], descriptor, asset, "linux-x64"), EngineLoadFailure.InvalidManifest);
        Assert(AssetLocation.Read(Encoding.UTF8.GetBytes(optional), descriptor, asset, "linux-x64").StartsWith(AppContext.BaseDirectory, StringComparison.Ordinal));
        string destination = Path.Combine(AppContext.BaseDirectory, "A02-assets");
        Directory.CreateDirectory(destination);
        File.Copy(Fixture("alpha"), Path.Combine(destination, "libnw_a02_alpha.so"), true);
        string recordPath = Path.Combine(AppContext.BaseDirectory, "NativeWeave.record.assets.json");
        File.WriteAllText(recordPath, "{}");
        Failure(() => descriptor.Inspect(), EngineLoadFailure.InvalidManifest);
        File.WriteAllText(recordPath, optional);
        Assert(descriptor.Inspect().Supports(Alpha));
        Assert(Native.Alpha(5) == (5UL ^ 0xa102));
    }

    private static void InvalidBinary()
    {
        byte[] bytes = File.ReadAllBytes(Fixture("alpha"));
        bytes[18] = 183; bytes[19] = 0; // Actual valid-memory wrong AArch64 ELF machine field.
        string path = Path.Combine(_fixtures, "wrong-architecture.so"); File.WriteAllBytes(path, bytes);
        Failure(() => Descriptor("wrong-arch", "alpha", hash: Digest(path)).Inspect(new() { NativeAssetPath = path }), EngineLoadFailure.InvalidBinary);
        bytes = new byte[64]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; File.WriteAllBytes(path, bytes);
        Failure(() => Descriptor("wrong-format", "alpha", hash: Digest(path)).Inspect(new() { NativeAssetPath = path }), EngineLoadFailure.InvalidBinary);
    }

    private static void Conflict()
    {
        EngineDescriptor original = Descriptor("conflict", "alpha");
        _ = original.Inspect(PathOptions("alpha"));
        Failure(() => Descriptor("conflict", "beta").Inspect(PathOptions("beta")), EngineLoadFailure.ConflictingModule);
        Failure(() => Descriptor("conflict", "alpha", major: 2).Inspect(PathOptions("alpha")), EngineLoadFailure.ConflictingModule);
        Failure(() => Descriptor("conflict", "alpha", version: "0.2.0").Inspect(PathOptions("alpha")), EngineLoadFailure.ConflictingModule);
        Assert(Native.AlphaCalls() == 1 && original.Inspect().Supports(Alpha));
    }

    private static void LatchedFailure()
    {
        EngineDescriptor broken = Descriptor("latched", "bad-major");
        EngineLoadException first = Failure(() => broken.Inspect(PathOptions("bad-major")), EngineLoadFailure.IncompatibleAbi);
        EngineLoadException second = Failure(() => broken.Inspect(PathOptions("bad-major")), EngineLoadFailure.IncompatibleAbi);
        Assert(ReferenceEquals(first, second));
        Failure(() => Descriptor("latched", "alpha").Inspect(PathOptions("alpha")), EngineLoadFailure.ConflictingModule);
    }

    private static void PreloadRetry()
    {
        EngineDescriptor descriptor = Descriptor("retry", "alpha");
        string path = Path.Combine(_fixtures, "repaired-installation.so");
        if (File.Exists(path)) File.Delete(path);
        Failure(() => descriptor.Inspect(new() { NativeAssetPath = path }), EngineLoadFailure.MissingAsset);
        File.Copy(Fixture("alpha"), path);
        Assert(descriptor.Inspect(new() { NativeAssetPath = path }).Supports(Alpha));
        Assert(Native.AlphaCalls() == 1);
    }

    private static void CachedOverride()
    {
        EngineDescriptor descriptor = Descriptor("cached", "alpha");
        EngineInfo info = descriptor.Inspect(PathOptions("alpha"));
        Failure(() => descriptor.Inspect(PathOptions("beta")), EngineLoadFailure.AssetIntegrityMismatch);
        Assert(ReferenceEquals(info, descriptor.Inspect()));
    }

    private static EngineInfo Inspect(string id, string fixture, uint major = 1, uint minimumMinor = 0, string? bootstrap = null) =>
        Descriptor(id, fixture, major, minimumMinor, bootstrap: bootstrap).Inspect(PathOptions(fixture));

    private static EngineLoadOptions PathOptions(string fixture) => new() { NativeAssetPath = Fixture(fixture) };
    private static string Fixture(string fixture) => Path.Combine(_fixtures, $"libnw_a02_{fixture.Replace('-', '_')}.so");
    private static string Digest(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }

    private static NativeAssetDescriptor Asset(string fixture, string? hash = null, string rid = "linux-x64", string cpu = "x86-64") =>
        new(rid, $"runtimes/{rid}/native/libnw_a02_{fixture.Replace('-', '_')}.so", hash ?? Digest(Fixture(fixture)), cpu);

    private static EngineDescriptor Descriptor(string id, string fixture, uint major = 1, uint minimumMinor = 0,
        string? hash = null, string rid = "linux-x64", string cpu = "x86-64", string? bootstrap = null, string version = "0.1.0-preview.1") =>
        EnginePackage.CreateDescriptor(Manifest(id, [Asset(fixture, hash, rid, cpu)],
            [new(Alpha, 0), new(Beta, 1)], major, minimumMinor, bootstrap ?? (fixture == "beta" ? "nw_a02_beta_get_abi_v1" : "nw_a02_alpha_get_abi_v1"), version: version), typeof(Suite).Assembly);

    private static EnginePackageManifest Manifest(string id, IReadOnlyList<NativeAssetDescriptor> assets,
        IReadOnlyList<EngineCapabilityDefinition> capabilities, uint major = 1, uint minor = 0,
        string bootstrap = "nw_a02_alpha_get_abi_v1", EngineThreadingModel model = EngineThreadingModel.Synchronous, string version = "0.1.0-preview.1") =>
        new(id, "NativeWeave.A02.Fixture", version, new(major, minor), bootstrap, model, assets, capabilities);

    private static string Record(EngineDescriptor descriptor, string outputPath)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("engineId", descriptor.EngineId); writer.WriteString("packageId", descriptor.PackageId);
            writer.WriteString("packageVersion", descriptor.PackageVersion); writer.WriteString("rid", "linux-x64");
            writer.WriteString("sourcePath", descriptor.Manifest.Assets[0].RelativePath);
            writer.WriteString("outputPath", outputPath); writer.WriteString("sha256", descriptor.Manifest.Assets[0].Sha256);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static EngineLoadException Failure(Action action, EngineLoadFailure expected)
    {
        EngineLoadException exception = Throws<EngineLoadException>(action);
        Assert(exception.Failure == expected);
        Assert(exception.EngineId is not null && exception.Operation == "load" && exception.RuntimeIdentifier == "linux-x64");
        Assert(exception.StatusCode is null && exception.ErrorDomain is null && exception.NativeCode is null && exception.CorrelationId is null);
        Assert(exception.Message.Length < 2048 && !exception.Message.Contains(_fixtures, StringComparison.Ordinal));
        return exception;
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T exception) { Assertions++; return exception; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Assert(bool condition)
    {
        Assertions++;
        if (!condition) throw new InvalidOperationException("Assertion failed.");
    }

    private sealed class BoundedDescriptors(Func<int, EngineDescriptor> factory) : IEnumerable<EngineDescriptor>
    {
        internal int Observed;
        internal int Enumerations;
        internal bool Disposed;
        public IEnumerator<EngineDescriptor> GetEnumerator()
        {
            Enumerations++;
            try { for (int i = 0; ; i++) { Observed++; yield return factory(i); } }
            finally { Disposed = true; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

internal static partial class Native
{
    [LibraryImport("nw_a02_alpha", EntryPoint = "nw_a02_alpha_probe")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong Alpha(ulong input);

    [LibraryImport("nw_a02_beta", EntryPoint = "nw_a02_beta_probe")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong Beta(ulong input);

    [LibraryImport("nw_a02_alpha", EntryPoint = "nw_a02_alpha_bootstrap_calls")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint AlphaCalls();
}
