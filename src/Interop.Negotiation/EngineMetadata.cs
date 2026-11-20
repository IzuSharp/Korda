using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using NativeWeave.Interop.Extensibility;

namespace NativeWeave.Interop;

/// <summary>Identifies the incompatible major and compatible minor versions of a native C ABI.</summary>
/// <param name="Major">The representation and semantics version; manifests reject zero.</param>
/// <param name="Minor">The minimum compatible extension revision when used as a requirement.</param>
/// <remarks>An immutable, allocation-free value. Equality compares both fields; no ordering or package-version conversion is implied.</remarks>
public readonly record struct AbiVersion(uint Major, uint Minor);

/// <summary>A case-sensitive, versioned capability name, independent of loading or engine ownership.</summary>
/// <remarks>Immutable and safe for concurrent reads. The default value is invalid; comparisons use ordinal string equality.</remarks>
public readonly record struct EngineCapability
{
    private readonly string? _name;

    /// <summary>Creates a validated lower-case ASCII capability identifier.</summary>
    /// <param name="name">A dotted or hyphenated identifier of at most 128 ASCII bytes.</param>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    /// <exception cref="ArgumentException">The name is empty, oversized or malformed.</exception>
    public EngineCapability(string name) => _name = MetadataValidation.Identifier(name, nameof(name));

    /// <summary>Gets the existing identifier, or an empty string for the invalid default value.</summary>
    public string Name => _name ?? string.Empty;

    /// <summary>Returns the existing identifier without loading native code or allocating another string.</summary>
    /// <returns>The identifier, or the empty string for the invalid default value.</returns>
    public override string ToString() => Name;
}

/// <summary>The declared native execution profile; it does not grant public thread-safety guarantees.</summary>
public enum EngineThreadingModel
{
    /// <summary>Bounded work runs on the calling thread without a runtime.</summary>
    Synchronous = 0,
    /// <summary>Asynchronous work uses an instance-owned native executor.</summary>
    EngineLocalAsync = 1,
    /// <summary>Mutable native work uses a designated serialized worker.</summary>
    SerializedWorker = 2,
    /// <summary>An explicitly supplied external event loop controls progress; certification is separate.</summary>
    ExternalLoop = 3
}

/// <summary>An immutable snapshot from a module that passed actual native bootstrap validation.</summary>
/// <remarks>All members are synchronous and thread-safe. Inspection starts no domain instance or executor. The module remains resident for the process lifetime.</remarks>
public sealed class EngineInfo
{
    private readonly HashSet<string> _capabilityNames;

    internal EngineInfo(EnginePackageManifest manifest, string rid, AbiVersion actualAbi, ulong mask)
    {
        EngineId = manifest.EngineId;
        PackageId = manifest.PackageId;
        PackageVersion = manifest.PackageVersion;
        RuntimeIdentifier = rid;
        AbiVersion = actualAbi;
        ThreadingModel = manifest.ThreadingModel;
        var capabilities = manifest.Capabilities.Where(c => (mask & (1UL << c.BitIndex)) != 0)
            .Select(static c => c.Capability).OrderBy(static c => c.Name, StringComparer.Ordinal).ToArray();
        Capabilities = Array.AsReadOnly(capabilities);
        _capabilityNames = new(capabilities.Select(static c => c.Name), StringComparer.Ordinal);
    }

    /// <summary>Gets the manifest engine identity, independent of any domain instance.</summary>
    public string EngineId { get; }
    /// <summary>Gets the managed facade package identity.</summary>
    public string PackageId { get; }
    /// <summary>Gets the normalized NuGet version without a lossy System.Version conversion.</summary>
    public string PackageVersion { get; }
    /// <summary>Gets the version reported by the validated native bootstrap.</summary>
    public AbiVersion AbiVersion { get; }
    /// <summary>Gets the selected exact portable runtime identifier.</summary>
    public string RuntimeIdentifier { get; }
    /// <summary>Gets the declared execution profile; reading it starts no executor.</summary>
    public EngineThreadingModel ThreadingModel { get; }
    /// <summary>Gets an immutable ordinal snapshot of known names advertised by the actual binary.</summary>
    public IReadOnlyList<EngineCapability> Capabilities { get; }

    /// <summary>Checks a capability with no native call or per-call allocation.</summary>
    /// <param name="capability">A non-default capability name.</param>
    /// <returns>True when the actual binary advertises the known capability; otherwise false.</returns>
    /// <exception cref="ArgumentException">The capability is the invalid default value.</exception>
    public bool Supports(EngineCapability capability)
    {
        if (capability.Name.Length == 0)
            throw new ArgumentException("A non-default capability is required.", nameof(capability));
        return _capabilityNames.Contains(capability.Name);
    }

    /// <summary>Requires a capability before creating dependent domain resources.</summary>
    /// <param name="capability">The capability required by the caller.</param>
    /// <exception cref="ArgumentException">The capability is invalid.</exception>
    /// <exception cref="NotSupportedException">The actual binary does not advertise the capability.</exception>
    public void Require(EngineCapability capability)
    {
        if (!Supports(capability))
            throw new NotSupportedException($"Engine '{EngineId}' does not advertise '{capability.Name}'. Select a package that implements this capability.");
    }
}

/// <summary>A statically compiled immutable package descriptor, normally supplied by a domain factory.</summary>
/// <remarks>Metadata access performs no native load. Inspect validates local assets and caches one process-resident module; there is no disposal or hot-unload operation.</remarks>
public sealed class EngineDescriptor
{
    private EngineInfo? _defaultInfo;

    internal EngineDescriptor(EnginePackageManifest manifest, Assembly assembly)
    {
        Manifest = manifest;
        Assembly = assembly;
        SupportedRuntimeIdentifiers = Array.AsReadOnly(manifest.Assets.Select(static a => a.RuntimeIdentifier)
            .Order(StringComparer.Ordinal).ToArray());
    }

    internal EnginePackageManifest Manifest { get; }
    internal Assembly Assembly { get; }
    /// <summary>Gets the stable case-sensitive engine identity without loading native code.</summary>
    public string EngineId => Manifest.EngineId;
    /// <summary>Gets the facade package identity.</summary>
    public string PackageId => Manifest.PackageId;
    /// <summary>Gets the normalized package version.</summary>
    public string PackageVersion => Manifest.PackageVersion;
    /// <summary>Gets the exact immutable packaged RID list, without applying a fallback graph.</summary>
    public IReadOnlyList<string> SupportedRuntimeIdentifiers { get; }

    /// <summary>Loads and validates this module without creating a domain instance or executor.</summary>
    /// <param name="options">Additional immutable-at-use load policy; null uses package defaults.</param>
    /// <returns>Cached, validated metadata for the process-resident module.</returns>
    /// <exception cref="ArgumentException">An option has an invalid path, capability or metadata bound.</exception>
    /// <exception cref="EngineLoadException">Platform, asset, identity, bootstrap or required capability validation fails.</exception>
    /// <remarks>First use reads and hashes the asset, loads it and calls bootstrap once. Compatible subsequent calls reuse metadata; explicit path overrides are verified. Options are snapshotted before file access. Calls are thread-safe and synchronous.</remarks>
    public EngineInfo Inspect(EngineLoadOptions? options = null)
    {
        if (options is not null) return EngineModuleLoader.Inspect(this, options);
        EngineInfo? cached = Volatile.Read(ref _defaultInfo);
        if (cached is not null) return cached;
        EngineInfo inspected = EngineModuleLoader.Inspect(this, null);
        return Interlocked.CompareExchange(ref _defaultInfo, inspected, null) ?? inspected;
    }
}

/// <summary>Advanced first-use loading options; domain builders and Inspect snapshot the values before use.</summary>
/// <remarks>Do not mutate a supplied collection during snapshot. There is no hash-bypass or native replacement option.</remarks>
public sealed class EngineLoadOptions
{
    /// <summary>Creates options using the package asset and no additional capability requirements.</summary>
    public EngineLoadOptions() { }
    /// <summary>Gets or initializes an absolute asset path, still subject to exact RID, format and hash validation.</summary>
    public string? NativeAssetPath { get; init; }
    /// <summary>Gets or initializes at most 64 unique non-default capabilities required before use.</summary>
    public IReadOnlyList<EngineCapability> RequiredCapabilities { get; init; } = Array.Empty<EngineCapability>();
}

/// <summary>An immutable, explicit index of at most 256 descriptors; it never scans assemblies or directories.</summary>
/// <remarks>Construction enumerates once and loads no native code. All subsequent reads are safe concurrently.</remarks>
public sealed class EngineCatalog
{
    private readonly Dictionary<string, EngineDescriptor> _byId;

    /// <summary>Snapshots descriptors and rejects invalid or duplicate identities before publishing the catalog.</summary>
    /// <param name="descriptors">Explicitly selected, already compiled package descriptors.</param>
    /// <exception cref="ArgumentNullException">The collection is null.</exception>
    /// <exception cref="ArgumentException">An entry is null, an identity is repeated, or more than 256 entries are observed.</exception>
    public EngineCatalog(IEnumerable<EngineDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        _byId = new(StringComparer.Ordinal);
        foreach (EngineDescriptor? descriptor in descriptors)
        {
            if (_byId.Count == 256)
                throw new ArgumentException("A catalog supports at most 256 descriptors.", nameof(descriptors));
            if (descriptor is null || !_byId.TryAdd(descriptor.EngineId, descriptor))
                throw new ArgumentException("Catalog entries must be non-null with unique engine identities.", nameof(descriptors));
        }
        Engines = new ReadOnlyCollection<EngineDescriptor>(_byId.Values.OrderBy(static d => d.EngineId, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Gets immutable descriptors in ordinal engine-identity order without loading modules.</summary>
    public IReadOnlyList<EngineDescriptor> Engines { get; }

    /// <summary>Finds a descriptor without loading a module.</summary>
    /// <param name="engineId">A valid case-sensitive manifest identity.</param>
    /// <param name="descriptor">The matching descriptor, or null when absent.</param>
    /// <returns>True when this explicit catalog contains the identity.</returns>
    /// <exception cref="ArgumentException">The identity is malformed.</exception>
    /// <exception cref="ArgumentNullException">The identity is null.</exception>
    public bool TryGet(string engineId, [NotNullWhen(true)] out EngineDescriptor? descriptor) =>
        _byId.TryGetValue(MetadataValidation.Identifier(engineId, nameof(engineId)), out descriptor);

    /// <summary>Gets a descriptor without loading a module.</summary>
    /// <param name="engineId">A valid case-sensitive manifest identity.</param>
    /// <returns>The matching compiled descriptor.</returns>
    /// <exception cref="KeyNotFoundException">The identity is absent.</exception>
    /// <exception cref="ArgumentException">The identity is malformed.</exception>
    /// <exception cref="ArgumentNullException">The identity is null.</exception>
    public EngineDescriptor Get(string engineId) => TryGet(engineId, out EngineDescriptor? value)
        ? value : throw new KeyNotFoundException($"Engine '{engineId}' is not in this explicit catalog.");

    /// <summary>Performs the same preflight as the selected descriptor.</summary>
    /// <param name="engineId">An identity contained in this catalog.</param>
    /// <param name="options">Optional first-use load policy.</param>
    /// <returns>Validated metadata without starting a domain runtime.</returns>
    /// <exception cref="KeyNotFoundException">The identity is absent.</exception>
    /// <exception cref="ArgumentException">The identity or options are invalid.</exception>
    /// <exception cref="EngineLoadException">Preflight fails.</exception>
    public EngineInfo Inspect(string engineId, EngineLoadOptions? options = null) => Get(engineId).Inspect(options);
}
