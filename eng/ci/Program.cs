using System.Text.Json;
using NativeWeave.CI;

try
{
    return Entry.Run(args);
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

namespace NativeWeave.CI
{
    internal static class Entry
    {
        internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        internal static int Run(string[] args)
        {
            var root = Directory.GetCurrentDirectory();
            var ctx = new BuildContext(root, Environment.GetEnvironmentVariable("PACKAGE_VERSION") ?? "0.1.0-local.1", "light");
            if (args is ["check-index"])
            {
                _ = new GitCatalog(ctx).Read(ctx.Read("git", "write-tree").Trim());
                return 0;
            }
            if (args is ["jobs"])
            {
                var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText("artifacts/plan.json"), Json)!;
                var selection = JobSelection.For(plan, Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") == "push",
                    Environment.GetEnvironmentVariable("MERGED") == "true", !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("REUSE")));
                using var output = File.AppendText(Environment.GetEnvironmentVariable("GITHUB_OUTPUT")!);
                output.WriteLine($"build={selection.Build.ToString().ToLowerInvariant()}");
                output.WriteLine($"modules={selection.Modules.ToString().ToLowerInvariant()}");
                output.WriteLine($"focused={selection.Focused.ToString().ToLowerInvariant()}");
                return 0;
            }
            if (args is ["module-check", var name, var filename])
            {
                using var document = JsonDocument.Parse(File.ReadAllText(filename));
                var spec = new GitCatalog(ctx).Read("HEAD").Modules[name];
                string field = document.RootElement.GetProperty("run_steps").GetProperty(name).GetString()!;
                string[] command = field switch
                {
                    "run" => spec.Run,
                    "integration" => spec.Integration,
                    _ => throw new InvalidDataException($"Unknown module check: {field}")
                };
                Environment.SetEnvironmentVariable("NW_MODULE", name);
                var step = new Step(name + "." + field, c => c.Run(command[0], command[1..]));
                ctx.Execute([step.Name], new Dictionary<string, Step> { [step.Name] = step });
                return 0;
            }
            if (args is ["action", var action])
            {
                string owner = action.Split('.')[0];
                string scope = owner switch
                {
                    "sqlite" or "sqlite-tests" => "sqlite",
                    "postgresql" or "postgresql-tests" => "postgresql",
                    "public" => "pack",
                    _ => "full"
                };
                new BuildContext(root, ctx.Version, scope).Execute([action], Recipes.All);
                return 0;
            }
            if (args is ["run", var planFile, var group])
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(planFile));
                var actions = doc.RootElement.GetProperty("actions").GetProperty(group).Deserialize<string[]>()!;
                new BuildContext(root, ctx.Version, group, planFile).Execute(actions, Recipes.All);
                return 0;
            }
            if (args is ["github"])
            {
                var kind = Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME");
                using var data = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("GITHUB_EVENT_PATH")!));
                var e = data.RootElement;
                var baseline = kind switch
                {
                    "pull_request" => e.GetProperty("pull_request").GetProperty("base").GetProperty("sha").GetString(),
                    "merge_group" => e.GetProperty("merge_group").GetProperty("base_sha").GetString(),
                    "push" => e.GetProperty("before").GetString(),
                    _ => "HEAD^"
                };
                string[] replay = kind == "workflow_dispatch" && e.TryGetProperty("inputs", out var inputs) &&
                    inputs.TryGetProperty("check_paths", out var paths) ? paths.GetString()!.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [];
                var plan = Calculate(ctx, baseline!, "HEAD", replay, kind == "schedule" || (kind == "workflow_dispatch" && replay.Length == 0));
                Directory.CreateDirectory("artifacts");
                string json = JsonSerializer.Serialize(plan, Json);
                File.WriteAllText("artifacts/plan.json", json);
                string artifact = $"providers-{ctx.Read("git", "rev-parse", "HEAD^{tree}").Trim()}-{string.Join('-', plan.Candidates)}-{Environment.GetEnvironmentVariable("GITHUB_RUN_ID")}-{Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT")}";
                File.AppendAllText(Environment.GetEnvironmentVariable("GITHUB_OUTPUT")!,
                    $"plan={json}\nartifact={artifact}\npending={(plan.Candidates.Length > 0 ? "true" : "false")}\n");
                if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is string summary)
                {
                    static string Items(IEnumerable<string> values) => values.Any() ? string.Join(", ", values.Select(v => $"`{v}`")) : "none";
                    var lines = new List<string> { "### Selected CI work", "",
                        $"Changed modules: {Items(plan.ChangedModules)}", "", $"Public candidates: {Items(plan.Candidates)}", "" };
                    foreach (var (scope, steps) in plan.Actions) lines.Add($"- **{scope}** {Items(steps)}");
                    foreach (string module in plan.Modules) lines.Add($"- **{module}** {plan.RunSteps[module]}");
                    File.AppendAllLines(summary, lines);
                }
                Console.WriteLine(json);
                return 0;
            }
            if (args.Length >= 2 && args[0] == "plan")
            {
                string? Option(string key) => Array.IndexOf(args, key) is var i && i >= 0 ? args[i + 1] : null;
                var plan = Calculate(ctx, args[1], Option("--tree") ?? "HEAD", [], args.Contains("--full"));
                string json = JsonSerializer.Serialize(plan, new JsonSerializerOptions(Json) { WriteIndented = true });
                if (Option("--output") is string output)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                    File.WriteAllText(output, json);
                }
                Console.WriteLine(json);
                return 0;
            }
            Console.Error.WriteLine("Use plan BASE [--tree TREE] [--full] [--output FILE], run PLAN GROUP, check-index, action NAME, module-check MODULE PLAN, jobs or github.");
            return 2;
        }

        private static Plan Calculate(BuildContext ctx, string baseline, string tree, string[] replay, bool full)
        {
            // Validate the complete tracked tree before contacting NuGet or starting a worker.
            var catalog = new GitCatalog(ctx);
            var after = catalog.Read(tree);
            string[] paths = replay.Length > 0 ? replay : ctx.Read("git", "diff", "--no-renames", "--name-only", "-z", baseline, tree).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            foreach (string path in replay)
                if (after.Owners.GetValueOrDefault(path)?[1] != "tests") throw new InvalidDataException($"Replay requires a tracked test or document: {path}");
            var before = catalog.Read(baseline);
            return Planner.Create(before, after, paths, full, ids =>
                JsonSerializer.Deserialize<Dictionary<string, Published>>(ctx.Read("python3", ["eng/ci/facts.py", "heads", .. ids]), Json)!);
        }
    }
}
