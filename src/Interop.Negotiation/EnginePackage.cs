using System.Reflection;

namespace Korda.Interop.Extensibility;

/// <summary>A maintainer-defined immutable mapping from a capability name to an engine-local ABI bit.</summary>
/// <param name="Capability">The validated stable capability name.</param>
/// <param name="BitIndex">A unique bit from 0 through 63; the manifest validates this value.</param>
/// <remarks>Value equality has no ownership effects. Different engines may assign different names to the same bit.</remarks>
public readonly record struct EngineCapabilityDefinition(EngineCapability Capability, int BitIndex);

/// <summary>An immutable, maintainer-generated native asset descriptor; construction reads no file.</summary>
/// <remarks>This is a package consistency declaration, not authentication or a sandbox for native code.</remarks>
public sealed class NativeAssetDescriptor
{
    /// <summary>Creates one exact portable-RID native asset entry.</summary>
    /// <param name="runtimeIdentifier">The portable linux-x64 or win-x64 RID.</param>
    /// <param name="relativePath">A canonical path beneath runtimes/&lt;rid&gt;/native without traversal.</param>
    /// <param name="sha256">The exact 64-character lowercase SHA-256.</param>
    /// <param name="cpuBaseline">The nonempty published CPU profile, at most 256 UTF-8 bytes.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">An argument violates the bounded native-asset shape.</exception>
    public NativeAssetDescriptor(string runtimeIdentifier, string relativePath, string sha256, string cpuBaseline)
    {
        RuntimeIdentifier = MetadataValidation.Rid(runtimeIdentifier, nameof(runtimeIdentifier));
        RelativePath = MetadataValidation.RelativePath(relativePath, nameof(relativePath));
        string prefix = $"runtimes/{RuntimeIdentifier}/native/";
        if (!RelativePath.StartsWith(prefix, StringComparison.Ordinal) || RelativePath.Length == prefix.Length)
            throw new ArgumentException("The native asset must be under its exact RID's runtimes native directory.", nameof(relativePath));
        Sha256 = MetadataValidation.Hash(sha256, nameof(sha256));
        MetadataValidation.Text(cpuBaseline, nameof(cpuBaseline), 256);
        CpuBaseline = cpuBaseline;
    }

    /// <summary>Gets the portable RID; its presence does not assert that platform's execution passed.</summary>
    public string RuntimeIdentifier { get; }
    /// <summary>Gets the canonical package-relative native source path.</summary>
    public string RelativePath { get; }
    /// <summary>Gets the expected lowercase asset digest.</summary>
    public string Sha256 { get; }
    /// <summary>Gets the published CPU baseline, checked during preflight.</summary>
    public string CpuBaseline { get; }
}

/// <summary>Immutable loader inputs from a complete maintainer release manifest.</summary>
/// <remarks>Construction validates and snapshots at most 64 assets and 64 capability definitions, with a combined 65,536-byte string budget. It loads no native code and owns no domain resources.</remarks>
public sealed class EnginePackageManifest
{
    /// <summary>Validates and snapshots the bounded loader subset of a release manifest.</summary>
    /// <param name="engineId">A case-sensitive lower-case ASCII engine identity of at most 128 bytes.</param>
    /// <param name="packageId">The managed facade NuGet identity, at most 256 UTF-8 bytes.</param>
    /// <param name="packageVersion">The normalized NuGet version, at most 256 UTF-8 bytes.</param>
    /// <param name="abiVersion">The required nonzero ABI major and minimum compatible minor.</param>
    /// <param name="bootstrapSymbol">The exact engine-specific C bootstrap export, at most 128 ASCII bytes.</param>
    /// <param name="threadingModel">The declared execution profile; construction starts no executor.</param>
    /// <param name="assets">One through 64 immutable entries with unique RIDs and paths.</param>
    /// <param name="capabilities">At most 64 unique non-default capability names and bit indices.</param>
    /// <exception cref="ArgumentNullException">A required argument or collection is null.</exception>
    /// <exception cref="ArgumentException">Metadata is malformed, duplicated or exceeds a fixed bound.</exception>
    /// <remarks>Collections are enumerated once with bounds checked as entries arrive. Callers must not mutate them during construction; later mutations cannot change the manifest.</remarks>
    public EnginePackageManifest(string engineId, string packageId, string packageVersion,
        AbiVersion abiVersion, string bootstrapSymbol, EngineThreadingModel threadingModel,
        IReadOnlyList<NativeAssetDescriptor> assets, IReadOnlyList<EngineCapabilityDefinition> capabilities)
    {
        EngineId = MetadataValidation.Identifier(engineId, nameof(engineId));
        MetadataValidation.Package(packageId, packageVersion);
        MetadataValidation.Bootstrap(bootstrapSymbol);
        if (abiVersion.Major == 0)
            throw new ArgumentException("An ABI major requirement cannot be zero.", nameof(abiVersion));
        if (!Enum.IsDefined(threadingModel))
            throw new ArgumentException("The threading model is not defined.", nameof(threadingModel));
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(capabilities);
        PackageId = packageId;
        PackageVersion = packageVersion;
        AbiVersion = abiVersion;
        BootstrapSymbol = bootstrapSymbol;
        ThreadingModel = threadingModel;
        int totalBytes = 0;
        foreach (string value in new[] { engineId, packageId, packageVersion, bootstrapSymbol })
            MetadataValidation.AddBytes(ref totalBytes, value, nameof(engineId));
        var assetSnapshot = new List<NativeAssetDescriptor>();
        var rids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (NativeAssetDescriptor? asset in assets)
        {
            if (assetSnapshot.Count == 64 || asset is null)
                throw new ArgumentException("Provide 1–64 non-null native asset entries.", nameof(assets));
            if (!rids.Add(asset.RuntimeIdentifier) || !paths.Add(asset.RelativePath))
                throw new ArgumentException("Native asset RIDs and paths must be unique.", nameof(assets));
            foreach (string value in new[] { asset.RuntimeIdentifier, asset.RelativePath, asset.Sha256, asset.CpuBaseline })
                MetadataValidation.AddBytes(ref totalBytes, value, nameof(assets));
            assetSnapshot.Add(asset);
        }
        if (assetSnapshot.Count == 0)
            throw new ArgumentException("At least one native asset is required.", nameof(assets));
        var capabilitySnapshot = new List<EngineCapabilityDefinition>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        ulong occupiedBits = 0;
        foreach (EngineCapabilityDefinition definition in capabilities)
        {
            if (capabilitySnapshot.Count == 64 || definition.Capability.Name.Length == 0 || definition.BitIndex is < 0 or > 63)
                throw new ArgumentException("Provide at most 64 non-default capability names with bit indices 0–63.", nameof(capabilities));
            ulong bit = 1UL << definition.BitIndex;
            if (!names.Add(definition.Capability.Name) || (occupiedBits & bit) != 0)
                throw new ArgumentException("Capability names and bit indices must be unique.", nameof(capabilities));
            occupiedBits |= bit;
            MetadataValidation.AddBytes(ref totalBytes, definition.Capability.Name, nameof(capabilities));
            capabilitySnapshot.Add(definition);
        }
        Assets = Array.AsReadOnly(assetSnapshot.ToArray());
        Capabilities = Array.AsReadOnly(capabilitySnapshot.ToArray());
    }

    /// <summary>Gets the stable case-sensitive engine identity.</summary>
    public string EngineId { get; }
    /// <summary>Gets the facade package identity.</summary>
    public string PackageId { get; }
    /// <summary>Gets the normalized package version.</summary>
    public string PackageVersion { get; }
    /// <summary>Gets the required major and minimum compatible minor ABI versions.</summary>
    public AbiVersion AbiVersion { get; }
    /// <summary>Gets the exact bootstrap export identifier.</summary>
    public string BootstrapSymbol { get; }
    /// <summary>Gets the declared execution profile without starting it.</summary>
    public EngineThreadingModel ThreadingModel { get; }
    /// <summary>Gets the immutable native asset snapshot.</summary>
    public IReadOnlyList<NativeAssetDescriptor> Assets { get; }
    /// <summary>Gets the immutable engine-local capability map.</summary>
    public IReadOnlyList<EngineCapabilityDefinition> Capabilities { get; }
}

/// <summary>The explicit AOT-safe maintainer entry point for statically compiled engine packages.</summary>
/// <remarks>Ordinary applications obtain descriptors through domain factories. No assembly discovery or managed plugin loading occurs.</remarks>
public static class EnginePackage
{
    /// <summary>Creates a descriptor without scanning assemblies, reading assets or loading native code.</summary>
    /// <param name="manifest">An already validated immutable loader manifest.</param>
    /// <param name="assembly">The statically rooted facade assembly that owns generated native imports.</param>
    /// <returns>An immutable descriptor for explicit catalog or ordinary domain use.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static EngineDescriptor CreateDescriptor(EnginePackageManifest manifest, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(assembly);
        return new(manifest, assembly);
    }
}
