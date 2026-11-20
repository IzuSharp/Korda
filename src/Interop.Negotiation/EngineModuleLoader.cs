using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using NativeWeave.Interop.Abi;
using NativeWeave.Interop.Extensibility;

namespace NativeWeave.Interop;

internal static class EngineModuleLoader
{
    private static readonly ConcurrentDictionary<string, LoadSlot> Slots = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<Assembly, AssemblyResolver> Resolvers = new();

    // Domain operation tables bind only exports of the already validated resident
    // module. This does not create another load path or transfer unloading ownership.
    internal static nint ResolveExport(EngineDescriptor descriptor, string symbol)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        Inspect(descriptor, null);
        LoadSlot slot = Slots[descriptor.EngineId];
        nint handle;
        lock (slot.Gate)
            handle = slot.Module!.Handle;
        return NativeLibrary.TryGetExport(handle, symbol, out nint address) ? address : 0;
    }

    internal static EngineInfo Inspect(EngineDescriptor descriptor, EngineLoadOptions? options)
    {
        OptionsSnapshot snapshot = OptionsSnapshot.Create(options);
        string rid = RuntimeInformation.RuntimeIdentifier;
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || !BitConverter.IsLittleEndian ||
            rid is not "linux-x64" and not "win-x64")
            throw Failure(descriptor, rid, EngineLoadFailure.UnsupportedPlatform,
                "NWPKG003: this ABI-v1 loader requires a little-endian Linux GNU x64 or Windows x64 process. Use a certified matching package.");
        NativeAssetDescriptor? asset = descriptor.Manifest.Assets.FirstOrDefault(a => a.RuntimeIdentifier == rid);
        if (asset is null)
            throw Failure(descriptor, rid, EngineLoadFailure.UnsupportedPlatform,
                "NWPKG003: this package has no asset for the current portable RID. Install the matching provider package.");
        if (asset.CpuBaseline is not "x86-64" and not "x86-64-v2")
            throw Failure(descriptor, rid, EngineLoadFailure.InvalidBinary,
                "NWPKG003: the declared CPU profile is unsupported. Use the x86-64 or x86-64-v2 package profile.");
        // .NET 11's x64 process already requires x86-64-v2. The native baseline
        // cannot request an unverified stronger instruction set.
        string identity = Identity(descriptor.Manifest, asset);
        LoadSlot slot = Slots.GetOrAdd(descriptor.EngineId, static _ => new());
        PendingLoad? pending;
        LoadedModule? loaded;
        bool ownsLoad = false;
        lock (slot.Gate)
        {
            if (slot.Identity is not null && !string.Equals(slot.Identity, identity, StringComparison.Ordinal))
                throw Conflict(descriptor, rid);
            slot.Failure?.Throw();
            loaded = slot.Module;
            pending = slot.Pending;
            if (loaded is null && pending is null)
            {
                pending = new();
                slot.Identity = identity;
                slot.Pending = pending;
                ownsLoad = true;
            }
        }
        if (ownsLoad)
        {
            bool mapped = false;
            try
            {
                string path = AssetLocation.Resolve(descriptor, asset, rid, snapshot.Path);
                ValidateFile(descriptor, asset, rid, path);
                nint handle;
                try { handle = NativeLibrary.Load(path); }
                catch (BadImageFormatException)
                {
                    throw Failure(descriptor, rid, EngineLoadFailure.InvalidBinary,
                        "The OS rejected the native binary format. Reinstall the matching architecture/profile package.");
                }
                catch (DllNotFoundException)
                {
                    throw Failure(descriptor, rid, EngineLoadFailure.MissingAsset,
                        "The native asset or a required OS dependency could not be loaded. Repair the package and its documented system dependencies.");
                }
                // Once handed to the OS, this module is deliberately never freed,
                // including failed bootstrap selections. No owner can call unloaded code.
                mapped = true;
                loaded = Bootstrap(descriptor, asset, rid, path, handle);
                lock (slot.Gate)
                {
                    slot.Module = loaded;
                    slot.Pending = null;
                }
                pending!.Completion.SetResult(loaded);
            }
            catch (Exception error)
            {
                lock (slot.Gate)
                {
                    slot.Pending = null;
                    if (mapped)
                        slot.Failure = ExceptionDispatchInfo.Capture(error);
                    else
                        slot.Identity = null; // A corrected, never-mapped installation may retry.
                }
                pending!.Completion.SetException(error);
                // Observe the completion's exception also when there were no waiters.
                _ = pending.Completion.Task.Exception;
                throw;
            }
        }
        else if (loaded is null)
            loaded = pending!.Completion.Task.GetAwaiter().GetResult();

        if (!ownsLoad && snapshot.Path is not null)
            ValidateFile(descriptor, asset, rid, snapshot.Path);
        Resolvers.GetValue(descriptor.Assembly, static _ => new()).Bind(descriptor, asset, rid, loaded!);
        foreach (EngineCapability capability in snapshot.Capabilities)
            if (!loaded!.Info.Supports(capability))
                throw Failure(descriptor, rid, EngineLoadFailure.MissingCapability,
                    $"The actual native binary does not advertise '{capability.Name}'. Select a provider package implementing that capability.");
        return loaded!.Info;
    }

    private static unsafe LoadedModule Bootstrap(EngineDescriptor descriptor, NativeAssetDescriptor asset, string rid, string path, nint handle)
    {
        if (!NativeLibrary.TryGetExport(handle, descriptor.Manifest.BootstrapSymbol, out nint export))
            throw Failure(descriptor, rid, EngineLoadFailure.MissingExport,
                "The required engine-prefixed bootstrap export is absent. Restore the matching managed/native package and restart the process.");
        var bootstrap = (delegate* unmanaged[Cdecl]<uint, uint, NwAbiInfoV1*, uint>)export;
        NwAbiInfoV1 info = default;
        uint status = bootstrap(descriptor.Manifest.AbiVersion.Major, (uint)sizeof(NwAbiInfoV1), &info);
        if (status != NativeStatus.Ok)
            throw Failure(descriptor, rid, EngineLoadFailure.IncompatibleAbi,
                $"Bootstrap rejected required ABI {descriptor.Manifest.AbiVersion.Major}.{descriptor.Manifest.AbiVersion.Minor} with status {status}. Install a compatible package and restart the process.");
        if (AbiBootstrap.Validate(in info, (uint)sizeof(NwAbiInfoV1), descriptor.Manifest.AbiVersion.Major,
            descriptor.Manifest.AbiVersion.Minor, 0) != NativeStatus.Ok)
            throw Failure(descriptor, rid, EngineLoadFailure.IncompatibleAbi,
                $"Required ABI {descriptor.Manifest.AbiVersion.Major}.{descriptor.Manifest.AbiVersion.Minor}; observed {info.AbiMajor}.{info.AbiMinor} with an incompatible known record/profile. Install a compatible package and restart the process.");
        return new(handle, path, asset.Sha256, new EngineInfo(descriptor.Manifest, rid,
            new(info.AbiMajor, info.AbiMinor), info.Capabilities));
    }

    internal static void ValidateFile(EngineDescriptor descriptor, NativeAssetDescriptor asset, string rid, string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!string.Equals(hash, asset.Sha256, StringComparison.Ordinal))
                throw Failure(descriptor, rid, EngineLoadFailure.AssetIntegrityMismatch,
                    "The native bytes do not match the package SHA-256. Restore the exact provider package; hash verification cannot be disabled.");
            stream.Position = 0;
            Span<byte> header = stackalloc byte[64];
            if (stream.Length < header.Length)
                throw InvalidBinary(descriptor, rid);
            stream.ReadExactly(header);
            if (rid == "linux-x64")
            {
                if (header[0] != 0x7f || header[1] != 'E' || header[2] != 'L' || header[3] != 'F' ||
                    header[4] != 2 || header[5] != 1 || header[6] != 1 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(header[16..]) != 3 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(header[18..]) != 62)
                    throw InvalidBinary(descriptor, rid);
            }
            else
            {
                uint peOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[60..]);
                if (header[0] != 'M' || header[1] != 'Z' || peOffset > stream.Length - 26)
                    throw InvalidBinary(descriptor, rid);
                stream.Position = peOffset;
                Span<byte> pe = stackalloc byte[26];
                stream.ReadExactly(pe);
                if (!pe[..4].SequenceEqual("PE\0\0"u8) || BinaryPrimitives.ReadUInt16LittleEndian(pe[4..]) != 0x8664 ||
                    (BinaryPrimitives.ReadUInt16LittleEndian(pe[22..]) & 0x2000) == 0 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(pe[24..]) != 0x20b)
                    throw InvalidBinary(descriptor, rid);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw Failure(descriptor, rid, EngineLoadFailure.MissingAsset,
                "The selected native asset is absent or unreadable. Restore the matching provider package and check file access.");
        }
    }

    internal static EngineLoadException Failure(EngineDescriptor descriptor, string rid, EngineLoadFailure failure, string remedy) =>
        new(failure, descriptor.EngineId, rid,
            $"Engine '{descriptor.EngineId}', package '{descriptor.PackageId}' {descriptor.PackageVersion}, RID '{rid}': {remedy}");

    private static EngineLoadException Conflict(EngineDescriptor descriptor, string rid) =>
        Failure(descriptor, rid, EngineLoadFailure.ConflictingModule,
            "Another content, ABI or owned-package identity was already selected for this engine. Align package versions and restart the process.");

    private static EngineLoadException InvalidBinary(EngineDescriptor descriptor, string rid) =>
        Failure(descriptor, rid, EngineLoadFailure.InvalidBinary,
            "The selected binary has the wrong format, architecture or byte order. Restore the exact RID-specific native package.");

    private static string Identity(EnginePackageManifest manifest, NativeAssetDescriptor asset)
    {
        var value = new StringBuilder();
        foreach (string field in new[] { manifest.EngineId, manifest.PackageId, manifest.PackageVersion,
            manifest.AbiVersion.Major.ToString(System.Globalization.CultureInfo.InvariantCulture),
            manifest.AbiVersion.Minor.ToString(System.Globalization.CultureInfo.InvariantCulture), manifest.BootstrapSymbol,
            ((int)manifest.ThreadingModel).ToString(System.Globalization.CultureInfo.InvariantCulture),
            asset.RuntimeIdentifier, asset.RelativePath, asset.Sha256, asset.CpuBaseline })
            value.Append(field).Append('\0');
        foreach (EngineCapabilityDefinition definition in manifest.Capabilities.OrderBy(static c => c.BitIndex))
            value.Append(definition.BitIndex).Append(':').Append(definition.Capability.Name).Append('\0');
        return value.ToString();
    }

    private sealed class LoadSlot
    {
        internal object Gate { get; } = new();
        internal string? Identity;
        internal PendingLoad? Pending;
        internal LoadedModule? Module;
        internal ExceptionDispatchInfo? Failure;
    }

    private sealed class PendingLoad
    {
        internal TaskCompletionSource<LoadedModule> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record LoadedModule(nint Handle, string Path, string Sha256, EngineInfo Info);

    private sealed class AssemblyResolver
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, LoadedModule> _modules = new(StringComparer.Ordinal);
        private bool _installed;

        internal void Bind(EngineDescriptor descriptor, NativeAssetDescriptor asset, string rid, LoadedModule module)
        {
            string filename = Path.GetFileName(asset.RelativePath);
            string basename = rid == "linux-x64" && filename.StartsWith("lib", StringComparison.Ordinal) && filename.EndsWith(".so", StringComparison.Ordinal)
                ? filename[3..^3] : rid == "win-x64" && filename.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? filename[..^4] : filename;
            lock (_gate)
            {
                if ((_modules.TryGetValue(filename, out LoadedModule? existing) && existing.Handle != module.Handle) ||
                    (_modules.TryGetValue(basename, out existing) && existing.Handle != module.Handle))
                    throw Failure(descriptor, rid, EngineLoadFailure.InvalidManifest,
                        "Two package assets use a conflicting native import basename. Use unique engine-prefixed basenames and restart the process.");
                if (!_installed)
                {
                    try { NativeLibrary.SetDllImportResolver(descriptor.Assembly, Resolve); }
                    catch (InvalidOperationException)
                    {
                        throw Failure(descriptor, rid, EngineLoadFailure.InvalidManifest,
                            "This facade already installed an incompatible native resolver. Use its generated package-owned resolver and restart the process.");
                    }
                    _installed = true;
                }
                _modules[filename] = module;
                _modules[basename] = module;
            }
        }

        private nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            LoadedModule? module;
            lock (_gate)
                if (!_modules.TryGetValue(libraryName, out module)) return 0;
            // The validated resident reference remains owned by the loader.
            // The import resolver transfers a separate OS reference to its caller.
            nint owned = NativeLibrary.Load(module.Path);
            if (owned == module.Handle) return owned;
            NativeLibrary.Free(owned);
            throw new DllNotFoundException("The native import resolver could not reacquire the validated resident module.");
        }
    }

    private sealed record OptionsSnapshot(string? Path, EngineCapability[] Capabilities)
    {
        internal static OptionsSnapshot Create(EngineLoadOptions? options)
        {
            if (options is null)
                return new(null, []);
            if (options.NativeAssetPath is { } path)
            {
                MetadataValidation.Text(path, nameof(options.NativeAssetPath), 4096);
                if (!System.IO.Path.IsPathFullyQualified(path))
                    throw new ArgumentException("An explicit native asset path must be absolute.", nameof(options.NativeAssetPath));
            }
            ArgumentNullException.ThrowIfNull(options.RequiredCapabilities, nameof(options.RequiredCapabilities));
            var snapshot = new List<EngineCapability>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (EngineCapability capability in options.RequiredCapabilities)
            {
                if (snapshot.Count == 64 || capability.Name.Length == 0 || !names.Add(capability.Name))
                    throw new ArgumentException("Use at most 64 unique non-default required capabilities.", nameof(options.RequiredCapabilities));
                snapshot.Add(capability);
            }
            return new(options.NativeAssetPath, snapshot.ToArray());
        }
    }
}
