using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NativeWeave.CI;

// Read registrations and files from the same Git tree, including the preimage of a deletion.
internal sealed class GitCatalog(BuildContext ctx)
{
    private readonly Dictionary<string, string> blobs = [];
    private string Blob(string id)
    {
        if (!blobs.TryGetValue(id, out var value)) blobs[id] = value = ctx.Read("git", "cat-file", "blob", id);
        return value;
    }

    public Snapshot Read(string tree)
    {
        var entries = ctx.Read("git", "ls-tree", "-rz", tree).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(raw =>
            {
                int tab = raw.IndexOf('\t');
                return (Path: raw[(tab + 1)..], Id: raw[..tab].Split(' ')[^1], Raw: raw);
            }).ToArray();
        var specs = new Dictionary<string, Registration>();
        var documents = entries.Where(e => e.Path.StartsWith("eng/ci/modules/", StringComparison.Ordinal) &&
            e.Path.EndsWith(".json", StringComparison.Ordinal)).Select(e => Blob(e.Id)).ToArray();
        if (documents.Length == 0)
            documents = Directory.GetFiles(Path.Combine(ctx.Root, "eng/ci/modules"), "*.json").Order().Select(File.ReadAllText).ToArray();
        foreach (var document in documents)
            foreach (var (id, spec) in JsonSerializer.Deserialize<Dictionary<string, Registration>>(document, Entry.Json)!)
                specs.Add(id, spec);

        var targets = new HashSet<string>(StringComparer.Ordinal);
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, spec) in specs)
        {
            if (!Regex.IsMatch(id, @"^[a-z][a-z0-9-]*$"))
                throw new InvalidDataException($"Invalid module name: {id}");
            if (spec.Targets.Any(t => !targets.Add(t)))
                throw new InvalidDataException($"Duplicate CMake target in {id}");
            if (spec.Checks.Any(c => !DatabasePlan.KnownChecks.Contains(c)))
                throw new InvalidDataException($"Unknown check in {id}");
            if (spec.Artifacts is string path && (!path.StartsWith("artifacts/", StringComparison.Ordinal) ||
                path.Split('/').Contains("..") || spec.Run.Length == 0))
                throw new InvalidDataException($"Invalid artifact directory for {id}");
            if (spec.Package is string package && (!Regex.IsMatch(package, @"^NativeWeave\.[A-Za-z0-9.-]+$") ||
                !packages.Add(package) || (!Planner.Providers.Contains(id) && (spec.Pack.Length == 0 || spec.Consume.Length == 0))))
                throw new InvalidDataException($"Invalid public package registration: {id}");
        }
        var rules = specs.SelectMany(pair => pair.Value.Sources.Select(p => new Rule(p, pair.Key, "sources"))
            .Concat(pair.Value.Tests.Select(p => new Rule(p, pair.Key, "tests")))).OrderByDescending(r => r.Specificity).ToList();
        var owners = new Dictionary<string, string[]>();
        foreach (var entry in entries)
        {
            Rule? best = null;
            foreach (var rule in rules)
            {
                if (best != null && rule.Specificity < best.Specificity) break;
                if (!rule.Matches(entry.Path)) continue;
                if (best != null && (best.Module != rule.Module || best.Kind != rule.Kind))
                    throw new InvalidDataException($"Ambiguous ownership: {entry.Path} ({best.Module}, {rule.Module})");
                best = rule;
            }
            if (best == null) throw new InvalidDataException($"Unregistered CI path: {entry.Path}. Register the source or test directory under eng/ci/modules.");
            owners.Add(entry.Path, [best.Module, best.Kind]);
        }
        var solutions = new Dictionary<string, JsonElement>();
        foreach (var entry in entries)
        {
            if (entry.Path.EndsWith(".slnx", StringComparison.Ordinal))
            {
                var xml = XElement.Parse(Blob(entry.Id));
                var projects = new Dictionary<string, string>();
                foreach (var project in xml.Descendants("Project").ToList())
                {
                    projects[Relative(entry.Path, (string)project.Attribute("Path")!)] = project.ToString(SaveOptions.DisableFormatting);
                    project.Remove();
                }
                foreach (var folder in xml.Descendants("Folder").Where(f => !f.Elements().Any()).ToList()) folder.Remove();
                solutions[entry.Path] = JsonSerializer.SerializeToElement(new object[] { projects, xml.ToString(SaveOptions.DisableFormatting) });
            }
            if (!entry.Path.EndsWith(".csproj", StringComparison.Ordinal) || owners[entry.Path][1] != "sources") continue;
            string id = owners[entry.Path][0];
            foreach (var reference in XElement.Parse(Blob(entry.Id)).Descendants("ProjectReference"))
            {
                string? include = (string?)reference.Attribute("Include");
                if (string.IsNullOrEmpty(include) || include.Contains('$')) continue;
                string other = owners[Relative(entry.Path, include)][0];
                if (other != id && !specs[id].Depends.Contains(other))
                    specs[id] = specs[id] with { Depends = [.. specs[id].Depends, other] };
            }
        }
        Snapshot snapshot = new(owners, specs, [], solutions);
        foreach (var id in specs.Keys) _ = snapshot.Dependencies(id);
        foreach (var (id, spec) in specs.Where(p => p.Value.Package != null))
        {
            var deps = snapshot.Dependencies(id);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var entry in entries)
                if (owners[entry.Path][1] == "sources" && deps.Contains(owners[entry.Path][0]))
                    hash.AppendData(Encoding.UTF8.GetBytes(entry.Raw + "\0"));
            // Keep the published input format compatible with existing candidate manifests.
            foreach (var module in deps.Order(StringComparer.Ordinal))
                if (specs[module].Artifacts is string artifact)
                    AppendRecipe(hash, [module, artifact, specs[module].Run]);
            if (spec.Pack.Length > 0) AppendRecipe(hash, [spec.Package!, spec.Pack]);
            snapshot.Hashes[id] = Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        return snapshot;
    }

    private string Relative(string project, string include)
    {
        string file = Path.GetFullPath(Path.Combine(ctx.Root, Path.GetDirectoryName(project)!, include.Replace('\\', '/')));
        return Path.GetRelativePath(ctx.Root, file).Replace('\\', '/');
    }

    private static void AppendRecipe(IncrementalHash hash, object[] values)
    {
        // The legacy schema serialized nested arrays with a comma and a single space.
        static string Encode(object value) => value is IEnumerable<object> seq
            ? "[" + string.Join(", ", seq.Select(Encode)) + "]"
            : JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        hash.AppendData(Encoding.UTF8.GetBytes(Encode(values)));
    }

    private sealed class Rule(string pattern, string module, string kind)
    {
        public string Module => module;
        public string Kind => kind;
        public int Specificity { get; } = pattern.Split('*')[0].Length;
        private readonly Regex? glob = pattern.Contains('*') ? new("\\A" + Regex.Escape(pattern).Replace("\\*", ".*") + "\\z",
            RegexOptions.CultureInvariant | RegexOptions.Singleline) : null;
        public bool Matches(string path) => pattern.EndsWith('/') ? path.StartsWith(pattern, StringComparison.Ordinal) :
            glob?.IsMatch(path) ?? path == pattern;
    }
}
