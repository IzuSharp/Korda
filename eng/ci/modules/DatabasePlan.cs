namespace NativeWeave.CI;

internal static class DatabasePlan
{
    internal static readonly HashSet<string> KnownChecks = [.. ComponentTests.Suites.Select(m => "db:" + m.Id), "db:deep", "db:full", "db:pack", "db:task", "db:sqlite", "db:postgresql",
        "db:public", "db:generator", "db:pack-api", "db:elf-profile"];

    public static void Select(Plan plan, HashSet<string> flags, HashSet<string> touched, bool full,
        HashSet<string> api, HashSet<string> tests)
    {
        var providers = flags.Intersect(Planner.Providers).Sorted();
        string[] components = ComponentTests.Suites.Select(m => m.Id).Where(flags.Contains).Sorted();
        bool pack = flags.Overlaps(["pack", "task", "public"]);
        plan.Deep = full || flags.Contains("deep");
        bool bootstrap = Planner.Providers.Any(p => plan.Published.TryGetValue(p, out var head) && head.Inputs is null);
        bool recover = plan.Packages.Except(touched).Any();
        plan.Scope = (plan.Deep || flags.Contains("full") || recover || (bootstrap && (plan.Packages.Length > 0 || flags.Contains("public"))) ||
            (providers.Length > 0 && pack) || (components.Length > 0 && (pack || providers.Length > 0))) ? "full" : providers.Length switch
            {
                2 => touched.Overlaps(Planner.Providers) ? "full" : "provider-tests",
                1 => providers[0],
                _ => components.Length > 0 ? "components" : pack ? "pack" : "light"
            };
        plan.RebuildTask = flags.Contains("task");
        plan.Consume = plan.Packages.Length > 0 || full || flags.Contains("public") || plan.Scope == "pack";
        plan.BuildScopes = plan.Scope switch { "provider-tests" => Planner.Providers, "light" => [], _ => [plan.Scope] };
        var checks = flags.Intersect(["generator", "elf-profile", "pack-api"]).ToHashSet();
        if (plan.Scope == "full") checks.ExceptWith(["generator", "pack-api"]);
        if (plan.Scope == "pack")
        {
            checks.Add("pack-metadata");
            if (plan.RebuildTask) checks.Add("pack-api");
        }
        if (checks.Contains("elf-profile")) checks.Remove("pack-metadata");
        plan.Checks = checks.Sorted();
        if (checks.Count > 0) plan.Actions["focused"] = plan.Checks.Select(c => "focused." + c).ToArray();
        foreach (var scope in plan.BuildScopes)
        {
            List<string> actions = [];
            if (scope == "full")
            {
                actions.AddRange(["database.dependencies", "database.api", "database.regressions", "database.consume-aot"]);
                if (plan.Deep) actions.AddRange(["database.sanitizers", "database.compatibility", "database.runtime", "database.transfer", "database.clean-build"]);
            }
            else if (scope == "components") actions.AddRange(components.Select(id => id + ".test"));
            else if (scope != "pack")
            {
                if (api.Contains(scope) || touched.Contains(scope)) actions.Add(scope + ".api");
                if (tests.Contains(scope) || touched.Contains(scope) || !api.Contains(scope)) actions.Add(scope + "-tests.test");
                if (touched.Contains(scope)) actions.Add(scope + ".dependencies");
            }
            if (plan.Consume) actions.Add(plan.Packages.Length == 0 ? "public.consume-aot" : "public.validate");
            plan.Actions[scope] = BuildContext.Order(actions, Recipes.All).Select(s => s.Name).ToArray();
        }
    }
}

internal static class Recipes
{
    internal static readonly IReadOnlyDictionary<string, Step> All =
        new Module[] { new Sqlite(), new SqliteTests(), new PostgreSql(), new PostgreSqlTests(), new Database(), new PublicPackages(), new FocusedChecks() }
        .Concat(ComponentTests.Suites).SelectMany(m => m.Workflow()).ToDictionary(step => step.Name);
}
