using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace NativeWeave.Packaging;

/// <summary>Validates a restored owned-package closure and produces SDK-selected native output records.</summary>
/// <remarks>This is an MSBuild host task, never an application runtime dependency. Each invocation is independent. It reads bounded package metadata, hashes native files and performs no network, restore, Rust compilation or native loading.</remarks>
public sealed class PackageClosure : Microsoft.Build.Utilities.Task
{
    /// <summary>Creates an unconfigured, single-invocation compiler-host task.</summary>
    public PackageClosure() { }

    /// <summary>Gets or sets the exact closure documents supplied transitively by the selected packages.</summary>
    /// <value>One through 256 bounded JSON closure files.</value>
    [Required]
    public ITaskItem[] ClosureFiles { get; set; } = [];

    /// <summary>Gets or sets the SDK's resolved project.assets.json path.</summary>
    /// <value>The existing restore output, never a guessed global package cache.</value>
    [Required]
    public string AssetsFile { get; set; } = string.Empty;

    /// <summary>Gets or sets the explicit RID; an empty value uses the current SDK host RID.</summary>
    /// <value>A supported exact RID or the empty string.</value>
    public string RuntimeIdentifier { get; set; } = string.Empty;

    /// <summary>Gets or sets validate, build, or publish mode.</summary>
    /// <value>The validation-only default or an SDK record generation phase.</value>
    public string Mode { get; set; } = "validate";

    /// <summary>Gets or sets SDK-resolved native or publish items, preserving their destination metadata.</summary>
    /// <value>Actual SDK item identities and package/destination metadata.</value>
    public ITaskItem[] ResolvedFiles { get; set; } = [];

    /// <summary>Gets or sets the record output directory for build or publish mode.</summary>
    /// <value>The SDK-owned output or intermediate publication directory.</value>
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>Gets generated records and their output-relative paths for the SDK publish pipeline.</summary>
    /// <value>Only successfully written records from this invocation; empty initially.</value>
    [Output]
    public ITaskItem[] Records { get; private set; } = [];

    /// <summary>Checks the restored graph and, when requested, emits each native record atomically.</summary>
    /// <returns>True only if all identities, exact edges, native bytes and SDK destinations match.</returns>
    /// <remarks>Expected malformed metadata, IO and compatibility failures become hard NWPKG errors even when NuGet warnings are suppressed. No partial result is returned as a successful closure.</remarks>
    public override bool Execute()
    {
        try
        {
            Require(Mode is "validate" or "build" or "publish", "NWPKG002", "Unknown package-validation mode.");
            string rid = RuntimeIdentifier.Length == 0 ? System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier : RuntimeIdentifier;
            Dictionary<string, Package> packages = ReadClosures(rid);
            using JsonDocument assets = Read(AssetsFile, 64 * 1024 * 1024);
            ValidateResolved(packages, assets.RootElement);
            foreach (Package package in packages.Values)
                foreach (Native native in package.NativeAssets.Where(x => x.Rid == rid))
                    _ = ResolveNative(package, native);
            if (Mode != "validate")
                Records = WriteRecords(packages, rid);
            return true;
        }
        catch (Exception error) when (error is PackageRejected or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            string code = error is PackageRejected rejected ? rejected.Code : "NWPKG002";
            Log.LogError(null, code, null, null, 0, 0, 0, 0, error.Message);
            return false;
        }
    }

    private Dictionary<string, Package> ReadClosures(string rid)
    {
        Require(ClosureFiles.Length is > 0 and <= 256, "NWPKG001", "Missing or excessive owned-package closure records.");
        var result = new Dictionary<string, Package>(StringComparer.OrdinalIgnoreCase);
        foreach (ITaskItem file in ClosureFiles)
        {
            using JsonDocument document = Read(file.ItemSpec, 1024 * 1024);
            JsonElement root = document.RootElement;
            Require(root.GetProperty("schemaVersion").GetInt32() == 1, "NWPKG001", "Unknown owned-package closure schema.");
            JsonElement entries = root.GetProperty("packages");
            Require(entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() is > 0 and <= 256,
                "NWPKG001", "Invalid closure package count.");
            var inDocument = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement value in entries.EnumerateArray())
            {
                string id = Text(value, "id"), version = Text(value, "version");
                Require(Regex.IsMatch(id, "^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$", RegexOptions.CultureInvariant)
                    && Regex.IsMatch(version, "^[0-9]+[.][0-9]+[.][0-9]+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant),
                    "NWPKG001", "Package ID/version must be exact normalized identities.");
                Require(inDocument.Add(id), "NWPKG001", "Duplicate owned package identity.");
                var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty edge in value.GetProperty("dependencies").EnumerateObject())
                    Require(dependencies.TryAdd(edge.Name, Bounded(edge.Value.GetString(), 256)), "NWPKG001", "Duplicate owned dependency edge.");
                var natives = new List<Native>();
                if (value.TryGetProperty("nativeAssets", out JsonElement nativeAssets))
                    foreach (JsonElement native in nativeAssets.EnumerateArray())
                    {
                        Require(natives.Count < 64, "NWPKG002", "Too many native assets.");
                        string nativeRid = Text(native, "rid");
                        Require(nativeRid == "linux-x64" || nativeRid == "win-x64", "NWPKG003", "Unknown supported native profile.");
                        string path = Relative(Text(native, "sourcePath", 4096));
                        Require(path.StartsWith($"runtimes/{nativeRid}/native/", StringComparison.Ordinal), "NWPKG002", "Native source path disagrees with its RID.");
                        string engine = Text(native, "engineId", 128);
                        Require(Regex.IsMatch(engine, "^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant), "NWPKG002", "Invalid engine identity.");
                        string digest = Text(native, "sha256");
                        Require(Regex.IsMatch(digest, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant), "NWPKG004", "Native SHA-256 must be exact lowercase hexadecimal.");
                        uint major = native.GetProperty("abiMajor").GetUInt32(), minor = native.GetProperty("abiMinor").GetUInt32();
                        Require(major > 0, "NWPKG005", "A nonzero ABI major is required.");
                        var bits = new HashSet<int>();
                        var capabilities = new SortedDictionary<string, int>(StringComparer.Ordinal);
                        foreach (JsonProperty capability in native.GetProperty("capabilities").EnumerateObject())
                        {
                            int bit = capability.Value.GetInt32();
                            Require(capabilities.Count < 64 && capability.Name.Length <= 128 && bit is >= 0 and < 64 && bits.Add(bit)
                                && Regex.IsMatch(capability.Name, "^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)
                                && capabilities.TryAdd(capability.Name, bit), "NWPKG006", "Capability names and bits must be unique and bounded.");
                        }
                        string profile = string.Join(";", capabilities.Select(x => x.Key + "=" + x.Value));
                        string cpu = Text(native, "cpuBaseline");
                        Require(cpu is "x86-64" or "x86-64-v2", "NWPKG003", "Unknown accepted x64 CPU baseline.");
                        natives.Add(new(engine, Text(native, "facadePackageId"), Text(native, "facadePackageVersion"), nativeRid, path, digest, major, minor, profile, cpu));
                    }
                var package = new Package(id, version, dependencies, natives);
                if (result.TryGetValue(id, out Package? previous))
                    Require(previous.Same(package), "NWPKG001", $"Conflicting closures select {id}; align all owned packages to one accepted catalog.");
                else
                    result.Add(id, package);
            }
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var engines = new HashSet<string>(StringComparer.Ordinal);
        foreach (Package package in result.Values)
        {
            foreach ((string target, string version) in package.Dependencies)
                Require(result.TryGetValue(target, out Package? dependency) && dependency.Version == version,
                    "NWPKG001", $"Owned edge {package.Id} -> {target} does not match the accepted exact version.");
            if (package.NativeAssets.Count > 0)
            {
                Native[] selected = package.NativeAssets.Where(x => x.Rid == rid).ToArray();
                Require(selected.Length > 0, "NWPKG003", $"{package.Id} has no validated {rid} asset. Select a supported package/profile.");
                foreach (Native native in selected)
                {
                    Require(result.TryGetValue(native.Facade, out Package? facade) && facade.Version == native.FacadeVersion,
                        "NWPKG001", "Native descriptor does not identify a selected facade version.");
                    Require(names.Add(Path.GetFileName(native.SourcePath)) && engines.Add(native.Engine), "NWPKG002", "Duplicate native basename or engine identity.");
                }
            }
        }
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string id)
        {
            Require(active.Add(id), "NWPKG001", "Owned package graph contains a cycle.");
            if (visited.Add(id))
                foreach (string dependency in result[id].Dependencies.Keys)
                    Visit(dependency);
            active.Remove(id);
        }
        foreach (string id in result.Keys)
            Visit(id);
        return result;
    }

    private static void ValidateResolved(Dictionary<string, Package> expected, JsonElement assets)
    {
        var actual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty library in assets.GetProperty("libraries").EnumerateObject())
        {
            if (library.Value.GetProperty("type").GetString() != "package")
                continue;
            int separator = library.Name.LastIndexOf('/');
            Require(separator > 0, "NWPKG001", "Malformed restored package identity.");
            string id = library.Name[..separator], version = library.Name[(separator + 1)..];
            if (!id.StartsWith("NativeWeave.", StringComparison.OrdinalIgnoreCase) && !expected.ContainsKey(id))
                continue;
            Require(actual.TryAdd(id, version), "NWPKG001", "Multiple resolved versions of an owned package.");
        }
        Require(actual.Count == expected.Count, "NWPKG001", "Missing or undeclared NativeWeave packages. Align the full owned-package closure.");
        foreach ((string id, Package package) in expected)
            Require(actual.TryGetValue(id, out string? version) && version == package.Version,
                "NWPKG001", $"{id} resolved {actual.GetValueOrDefault(id, "<missing>")}; required {package.Version}. Direct overrides and suppressed NuGet warnings cannot change this closure.");
        foreach (JsonProperty target in assets.GetProperty("targets").EnumerateObject())
            foreach (JsonProperty resolved in target.Value.EnumerateObject())
            {
                int separator = resolved.Name.LastIndexOf('/');
                if (separator <= 0 || !expected.TryGetValue(resolved.Name[..separator], out Package? package))
                    continue;
                var edges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (resolved.Value.TryGetProperty("dependencies", out JsonElement dependencies))
                    foreach (JsonProperty edge in dependencies.EnumerateObject())
                        if (expected.ContainsKey(edge.Name) || edge.Name.StartsWith("NativeWeave.", StringComparison.OrdinalIgnoreCase))
                            edges.Add(edge.Name, edge.Value.GetString()!);
                Require(edges.Count == package.Dependencies.Count && package.Dependencies.All(x => edges.GetValueOrDefault(x.Key) == "[" + x.Value + "]"),
                    "NWPKG001", $"Restored owned dependency edges of {package.Id} differ from the exact [V] closure.");
            }
    }

    private ITaskItem ResolveNative(Package package, Native native)
    {
        ITaskItem[] candidates = ResolvedFiles.Where(item =>
            string.Equals(item.GetMetadata("NuGetPackageId"), package.Id, StringComparison.OrdinalIgnoreCase)
            && (item.GetMetadata("PathInPackage") == native.SourcePath ||
                Path.GetFullPath(item.ItemSpec).Replace('\\', '/').EndsWith("/" + native.SourcePath, StringComparison.Ordinal))).ToArray();
        Require(candidates.Length == 1, "NWPKG002", $"The SDK must select exactly one {package.Id}/{native.SourcePath} native item.");
        ITaskItem selected = candidates[0];
        Require(File.Exists(selected.ItemSpec), "NWPKG002", "The SDK-selected native asset is missing.");
        using (FileStream stream = File.OpenRead(selected.ItemSpec))
            Require(Convert.ToHexStringLower(SHA256.HashData(stream)) == native.Sha256, "NWPKG004", "SDK-selected native content differs from its accepted package. Restore the exact closure.");
        return selected;
    }

    private ITaskItem[] WriteRecords(Dictionary<string, Package> packages, string rid)
    {
        Require(OutputDirectory.Length > 0, "NWPKG002", "Missing native record output directory.");
        var output = new List<ITaskItem>();
        var pending = new List<(string Path, byte[] Bytes)>();
        foreach (Package package in packages.Values)
            foreach (Native native in package.NativeAssets.Where(x => x.Rid == rid))
            {
                ITaskItem selected = ResolveNative(package, native);
                string destination = Relative(selected.GetMetadata(Mode == "publish" ? "RelativePath" : "DestinationSubPath"));
                using var bytes = new MemoryStream();
                using (var writer = new Utf8JsonWriter(bytes))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("schemaVersion", 1);
                    writer.WriteString("engineId", native.Engine);
                    writer.WriteString("packageId", native.Facade);
                    writer.WriteString("packageVersion", native.FacadeVersion);
                    writer.WriteString("rid", rid);
                    writer.WriteString("sourcePath", native.SourcePath);
                    writer.WriteString("outputPath", destination);
                    writer.WriteString("sha256", native.Sha256);
                    writer.WriteEndObject();
                }
                Require(bytes.Length <= 16384, "NWPKG002", "Native output record exceeds 16 KiB.");
                string name = $"NativeWeave.{native.Engine}.assets.json";
                string path = Path.Combine(OutputDirectory, name);
                pending.Add((path, bytes.ToArray()));
                var record = new TaskItem(path);
                record.SetMetadata("RelativePath", name);
                record.SetMetadata("CopyToPublishDirectory", "Always");
                output.Add(record);
            }
        Directory.CreateDirectory(OutputDirectory);
        foreach ((string path, byte[] bytes) in pending)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, path, true);
            }
            finally { File.Delete(temporary); }
        }
        return [.. output];
    }

    private static JsonDocument Read(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(stream.Length is >= 2 && stream.Length <= maximum, "NWPKG002", "Package metadata exceeds its bounded size.");
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        Require(stream.ReadByte() == -1, "NWPKG002", "Package metadata changed while reading.");
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 32 });
        var fields = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
                fields.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject)
                fields.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName)
                Require(fields.Peek().Add(reader.GetString()!), "NWPKG002", "Duplicate JSON property.");
        }
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
    }

    private static string Text(JsonElement value, string name, int maximum = 256) => Bounded(value.GetProperty(name).GetString(), maximum);
    private static string Bounded(string? value, int maximum)
    {
        Require(!string.IsNullOrEmpty(value) && Encoding.UTF8.GetByteCount(value) <= maximum && !value.Contains('\0'), "NWPKG002", "Missing or oversized metadata string.");
        return value!;
    }
    private static string Relative(string value)
    {
        Bounded(value, 4096);
        Require(!value.Contains('\\') && !value.Contains(':') && !Path.IsPathRooted(value)
            && value.Split('/').All(x => x is not "" and not "." and not ".."), "NWPKG002", "Native output must be a canonical relative path.");
        return value;
    }
    private static void Require(bool condition, string code, string message)
    {
        if (!condition)
            throw new PackageRejected(code, message);
    }
    private sealed class PackageRejected(string code, string message) : Exception(message)
    {
        internal string Code { get; } = code;
    }
    private sealed record Native(string Engine, string Facade, string FacadeVersion, string Rid, string SourcePath, string Sha256, uint AbiMajor, uint AbiMinor, string Capabilities, string CpuBaseline);
    private sealed record Package(string Id, string Version, Dictionary<string, string> Dependencies, List<Native> NativeAssets)
    {
        internal bool Same(Package value) => Id.Equals(value.Id, StringComparison.OrdinalIgnoreCase) && Version == value.Version
            && Dependencies.Count == value.Dependencies.Count && Dependencies.All(x => value.Dependencies.GetValueOrDefault(x.Key) == x.Value)
            && NativeAssets.SequenceEqual(value.NativeAssets);
    }
}
