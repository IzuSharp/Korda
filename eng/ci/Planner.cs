using System.Text.Json;

namespace NativeWeave.CI;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
internal sealed record Registration
{
    public string[] Sources { get; init; } = [];
    public string[] Tests { get; init; } = [];
    public string[] Depends { get; init; } = [];
    public string[] Includes { get; init; } = [];
    public string[] Checks { get; init; } = [];
    public string[] Run { get; init; } = [];
    public string[] Targets { get; init; } = [];
    public string[] Consume { get; init; } = [];
    public string[] Pack { get; init; } = [];
    public string[] Integration { get; init; } = [];
    public string? Artifacts { get; init; }
    public string? Package { get; init; }
}

internal sealed record Snapshot(Dictionary<string, string[]> Owners, Dictionary<string, Registration> Modules,
    Dictionary<string, string> Hashes, Dictionary<string, JsonElement> Solutions)
{
    public HashSet<string> Dependencies(string name)
    {
        HashSet<string> result = [], visiting = [];
        void Visit(string id)
        {
            if (result.Contains(id)) return;
            if (!visiting.Add(id) || !Modules.ContainsKey(id)) throw new InvalidDataException($"Invalid CI module dependency: {id}");
            foreach (string dep in Modules[id].Depends.Concat(Modules[id].Includes)) Visit(dep);
            visiting.Remove(id);
            result.Add(id);
        }
        Visit(name);
        return result;
    }
}
internal sealed record Published(string? Version = null, string? Inputs = null);
internal sealed record Release(string Module, string Suffix);

internal sealed class Plan
{
    public string Scope { get; set; } = "light";
    public bool Deep { get; set; }
    public bool Consume { get; set; }
    public bool RebuildTask { get; set; }
    public string[] Checks { get; set; } = [];
    public string[] BuildScopes { get; set; } = [];
    public string[] Modules { get; set; } = [];
    public string[] ModulePackages { get; set; } = [];
    public string[] ChangedModules { get; init; } = [];
    public string[] Candidates { get; set; } = [];
    public string[] Packages { get; set; } = [];
    public Release[] Releases { get; set; } = [];
    public string[] Languages { get; set; } = [];
    public Dictionary<string, string> Inputs { get; init; } = [];
    public Dictionary<string, Published> Published { get; init; } = [];
    public Dictionary<string, string> RunSteps { get; } = [];
    public Dictionary<string, string> Artifacts { get; } = [];
    public Dictionary<string, string[]> ComposeFrom { get; } = [];
    public Dictionary<string, string[]> Actions { get; } = [];
}

internal static class Planner
{
    internal static readonly string[] Providers = ["postgresql", "sqlite"];
    internal static string[] Sorted(this IEnumerable<string> values) => [.. values.Distinct().Order(StringComparer.Ordinal)];

    public static Plan Create(Snapshot before, Snapshot after, string[] paths, bool full,
        Func<string[], Dictionary<string, Published>> registry)
    {
        var direct = new HashSet<string>();
        var selected = new HashSet<string>();
        var sourceOwners = new HashSet<string>();
        var providerApi = new HashSet<string>();
        var providerTests = new HashSet<string>();
        var touched = new HashSet<string>();
        foreach (string path in paths)
        {
            var snapshot = after.Owners.ContainsKey(path) ? after : before;
            if (!snapshot.Owners.TryGetValue(path, out var owner))
                throw new InvalidDataException($"Unregistered CI path: {path}");
            string id = owner[0];
            if (after.Modules.ContainsKey(id)) direct.Add(id);
            if (Providers.Contains(id))
            {
                if (owner[1] == "sources" || path.Contains("/ApiCheck/", StringComparison.Ordinal) || path.EndsWith("/verify.py", StringComparison.Ordinal))
                    providerApi.Add(id);
                if (!path.Contains("/ApiCheck/", StringComparison.Ordinal)) providerTests.Add(id);
            }
            if (owner[1] == "sources")
            {
                sourceOwners.Add(id);
                foreach (string consumer in snapshot.Modules.Keys.Where(n => snapshot.Dependencies(n).Contains(id)))
                {
                    if (after.Modules.ContainsKey(consumer)) selected.Add(consumer);
                    if (after.Hashes.ContainsKey(consumer)) touched.Add(consumer);
                }
            }
            if (path.EndsWith(".slnx", StringComparison.Ordinal))
                SelectSolution(path, before, after, direct);
        }
        foreach (var (id, spec) in after.Modules)
        {
            if ((spec.Run.Length != 0 || spec.Pack.Length != 0) &&
                (!before.Modules.TryGetValue(id, out var old) || JsonSerializer.Serialize(old) != JsonSerializer.Serialize(spec)))
                direct.Add(id);
            if (sourceOwners.Contains(id))
                foreach (var child in spec.Includes) selected.UnionWith(after.Dependencies(child));
        }
        touched.UnionWith(after.Hashes.Keys.Where(id => before.Hashes.GetValueOrDefault(id) != after.Hashes[id]));
        HashSet<string> db = [.. Providers.Intersect(after.Hashes.Keys)];
        string[] active = full ? [.. after.Modules.Keys] : [.. direct];
        var flags = active.SelectMany(id => after.Modules[id].Checks).Select(s => s["db:".Length..]).ToHashSet();
        var lookup = full ? after.Hashes.Keys.ToHashSet() : touched.ToHashSet();
        if (lookup.Overlaps(db) || flags.Overlaps(["public", "pack", "task"])) lookup.UnionWith(db);
        var published = after.Hashes.Keys.ToDictionary(n => n, _ => new Published());
        if (lookup.Count > 0)
            foreach (var (id, head) in registry(lookup.Sorted())) published[id] = head;
        var eligible = touched.ToHashSet();
        if (eligible.Overlaps(db)) eligible.UnionWith(db);
        var pending = eligible.Where(p => published[p].Inputs != after.Hashes[p]).ToHashSet();
        Plan plan = new()
        {
            ChangedModules = direct.Sorted(),
            Inputs = after.Hashes,
            Published = published,
            Candidates = pending.Sorted(),
            Packages = pending.Intersect(db).Sorted()
        };
        plan.Releases = [.. (plan.Packages.Length > 0 ? new[] { new Release("", "") } : []),
            .. pending.Except(db).Sorted().Select(n => new Release(n, "-" + n))];
        if (touched.Overlaps(db) && !flags.Overlaps(["full", "deep", "pack", "task", .. db]))
            flags.UnionWith(touched.Intersect(db));
        DatabasePlan.Select(plan, flags, touched, full, providerApi, providerTests);
        selected.UnionWith(active);
        foreach (string name in selected.Order(StringComparer.Ordinal))
        {
            Registration spec = after.Modules[name];
            bool primary = full || direct.Contains(name) || spec.Package is null;
            if ((primary ? spec.Run : spec.Integration).Length == 0) continue;
            plan.RunSteps[name] = primary ? "run" : "integration";
            if (spec.Artifacts is not null) plan.Artifacts[name] = spec.Artifacts;
        }
        plan.Modules = plan.RunSteps.Keys.Sorted();
        plan.ModulePackages = (full ? after.Hashes.Keys.AsEnumerable() : pending.Union(direct.Intersect(after.Hashes.Keys))).Except(db).Sorted();
        foreach (string id in plan.ModulePackages)
            plan.ComposeFrom[id] = after.Dependencies(id).Intersect(plan.Artifacts.Keys).Sorted();
        HashSet<string> languages = [];
        if (paths.Any(p => new[] { ".cs", ".csproj", ".slnx", ".props", ".targets", ".editorconfig", ".lock.json" }.Any(p.EndsWith) || p is "global.json" or "NuGet.Config")) languages.Add("csharp");
        if (paths.Any(p => p.EndsWith(".py", StringComparison.Ordinal) || p.EndsWith(".pyi", StringComparison.Ordinal))) languages.Add("python");
        if (full || paths.Contains(".github/workflows/security.yml")) languages.UnionWith(["csharp", "python"]);
        plan.Languages = languages.Sorted();
        return plan;
    }

    private static void SelectSolution(string path, Snapshot before, Snapshot after, HashSet<string> direct)
    {
        static Dictionary<string, string> Projects(JsonElement value) =>
            value.ValueKind == JsonValueKind.Array ? value[0].Deserialize<Dictionary<string, string>>()! : [];
        var oldSolution = before.Solutions.GetValueOrDefault(path);
        var newSolution = after.Solutions.GetValueOrDefault(path);
        var old = Projects(oldSolution);
        var current = Projects(newSolution);
        bool settingsChanged = oldSolution.ValueKind != newSolution.ValueKind ||
            (oldSolution.ValueKind == JsonValueKind.Array && oldSolution[1].GetString() != newSolution[1].GetString());
        foreach (string project in current.Keys.Union(old.Keys))
        {
            if (!settingsChanged && current.GetValueOrDefault(project) == old.GetValueOrDefault(project)) continue;
            var owners = after.Owners.GetValueOrDefault(project) ?? before.Owners[project];
            if (after.Modules.ContainsKey(owners[0])) direct.Add(owners[0]);
        }
    }
}
