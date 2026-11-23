namespace NativeWeave.CI;

internal static class DatabasePlan
{
    internal static readonly HashSet<string> KnownChecks = [.. ComponentTests.Suites.Select(m => "db:" + m.Id), "db:deep", "db:full", "db:pack", "db:task", "db:sqlite", "db:postgresql",
        "db:public", "db:generator", "db:pack-api", "db:elf-profile", "db:abi-bootstrap", "db:interop-corpus",
        .. Recipes.All.Keys.Where(k => IsSuite(k)).Select(k => "db:" + k)];

    private static bool IsSuite(string action) => action.StartsWith("database.", StringComparison.Ordinal) ||
        InteropTests.Suites.Any(suite => action == suite.Id + ".test");

    public static void Select(Plan plan, HashSet<string> flags, HashSet<string> touched, bool full,
        HashSet<string> api, HashSet<string> tests)
    {
        var providers = flags.Intersect(Planner.Providers).Sorted();
        string[] components = ComponentTests.Suites.Select(m => m.Id).Where(flags.Contains).Sorted();
        string[] suites = flags.Where(f => IsSuite(f)).Sorted();
        if (suites.Contains("database.regressions"))
            suites = suites.Except(["interop-negotiation-tests.test", "interop-execution-tests.test"]).Sorted();
        bool pack = flags.Overlaps(["pack", "task", "public"]);
        plan.Deep = full || flags.Contains("deep");
        bool bootstrap = Planner.Providers.Any(p => plan.Published.TryGetValue(p, out var head) && head.Inputs is null);
        bool recover = plan.Packages.Except(touched).Any();
        bool needSourcePackages = bootstrap && (plan.Packages.Length > 0 || pack);
        bool combinedChecks = providers.Length > 0 && pack || components.Length > 0 && (pack || providers.Length > 0);
        if (plan.Deep || flags.Contains("full") || recover || (needSourcePackages || combinedChecks || suites.Length > 0) && plan.Packages.Length > 0)
            plan.Scope = "full";
        else if (suites.Length > 0 || needSourcePackages || combinedChecks)
            plan.Scope = "selected";
        else if (providers.Length == 2)
            plan.Scope = touched.Overlaps(Planner.Providers) ? "full" : "provider-tests";
        else if (providers.Length == 1)
            plan.Scope = providers[0];
        else if (components.Length > 0)
            plan.Scope = "components";
        else if (pack)
            plan.Scope = "pack";
        plan.RebuildTask = flags.Contains("task");
        plan.Consume = plan.Packages.Length > 0 || full || pack;
        plan.BuildScopes = plan.Scope switch { "provider-tests" => Planner.Providers, "light" => [], _ => [plan.Scope] };
        if (plan.Scope == "selected" && !suites.Contains("database.regressions"))
            plan.BuildScopes = [.. plan.BuildScopes, .. providers];
        var checks = flags.Intersect(["generator", "elf-profile", "pack-api", "abi-bootstrap", "interop-corpus"]).ToHashSet();
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
                actions.AddRange(suites);
                actions.AddRange(["database.dependencies", "database.api", "database.regressions", "database.consume-aot"]);
                if (plan.Deep) actions.AddRange(["database.sanitizers", "database.compatibility", "database.interop-properties", "database.runtime", "database.transfer", "database.clean-build"]);
            }
            else if (scope == "selected")
            {
                actions.AddRange(suites);
                // The retained regression driver already runs these real provider and component suites.
                if (!actions.Contains("database.regressions"))
                {
                    actions.AddRange(components.Select(id => id + ".test"));
                }
                if (plan.Consume) actions.Add("database.pack");
            }
            else if (scope == "components") actions.AddRange(components.Select(id => id + ".test"));
            else if (scope != "pack")
            {
                if (api.Contains(scope) || touched.Contains(scope)) actions.Add(scope + ".api");
                if (tests.Contains(scope) || touched.Contains(scope) || !api.Contains(scope)) actions.Add(scope + "-tests.test");
                if (touched.Contains(scope)) actions.Add(scope + ".dependencies");
            }
            if (plan.Consume && (scope == plan.Scope || plan.Scope == "provider-tests")) actions.Add(plan.Packages.Length == 0 ? "public.consume-aot" : "public.validate");
            plan.Actions[scope] = BuildContext.Order(actions, Recipes.All).Select(s => s.Name).ToArray();
        }
    }
}

internal static class Recipes
{
    internal static readonly IReadOnlyDictionary<string, Step> All =
        new Module[] { new Sqlite(), new SqliteTests(), new PostgreSql(), new PostgreSqlTests(), new Database(), new PublicPackages(), new FocusedChecks() }
        .Concat(ComponentTests.Suites).Concat(InteropTests.Suites).SelectMany(m => m.Workflow()).ToDictionary(step => step.Name);
}
