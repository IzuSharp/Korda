using System.Diagnostics;
using System.Text.Json;

namespace NativeWeave.CI;

internal sealed class BuildContext(string root, string version, string scope, string planFile = "artifacts/plan.json",
    string[]? unsetEnvironment = null)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string Version { get; } = version;
    public string PlanFile { get; } = Path.GetFullPath(planFile, Path.GetFullPath(root));
    public string Scope { get; } = scope;
    public string Preset => "linux-x64-" + (Scope is "full" or "components" or "selected" ? "release" : Scope);
    public string NativeDir => "artifacts/cmake/" + Preset;

    public void Run(string tool, params string[] args) => Invoke(Root, tool, args, false);
    public string Read(string tool, params string[] args) => Invoke(Root, tool, args, true);
    public void Python(string script, params string[] args) => Run("python3", [script, .. args]);

    public void RunIn(string directory, string tool, params string[] args) => Invoke(Path.Combine(Root, directory), tool, args, false);

    private string Invoke(string directory, string tool, string[] args, bool capture)
    {
        ProcessStartInfo info = new(tool) { WorkingDirectory = directory, RedirectStandardOutput = capture };
        foreach (string variable in unsetEnvironment ?? []) info.Environment.Remove(variable);
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {tool}");
        string result = capture ? process.StandardOutput.ReadToEnd() : "";
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{tool} {string.Join(' ', args)} exited with {process.ExitCode}");
        return result;
    }

    public void Execute(IReadOnlyList<string> requested, IReadOnlyDictionary<string, Step> recipes)
    {
        var steps = Order(requested, recipes);
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var timer = Stopwatch.StartNew();
            Console.WriteLine($"[{i + 1}/{steps.Count}] {step.Name} started");
            try { step.Run(this); }
            catch (Exception ex) { throw new InvalidOperationException($"{step.Name} failed after {timer.Elapsed}: {ex.Message}", ex); }
            Console.WriteLine($"[{i + 1}/{steps.Count}] {step.Name} passed in {timer.Elapsed}");
        }
    }

    public static List<Step> Order(IEnumerable<string> requested, IReadOnlyDictionary<string, Step> recipes)
    {
        List<Step> ordered = [];
        HashSet<string> complete = [], visiting = [];
        void Visit(string name)
        {
            if (complete.Contains(name)) return;
            if (!recipes.TryGetValue(name, out var step)) throw new InvalidDataException($"Unknown CI action: {name}");
            if (!visiting.Add(name)) throw new InvalidDataException($"Cyclic CI action: {name}");
            foreach (string prerequisite in step.Needs) Visit(prerequisite);
            visiting.Remove(name);
            complete.Add(name);
            ordered.Add(step);
        }
        foreach (string name in requested) Visit(name);
        return ordered;
    }
}

internal sealed record Step(string Name, Action<BuildContext> Run, params string[] Needs);

internal abstract class Module(string id)
{
    public string Id { get; } = id;
    public abstract IEnumerable<Step> Workflow();
    protected virtual void Build(BuildContext ctx) => throw Missing();
    protected virtual void Api(BuildContext ctx) => throw Missing();
    protected virtual void Test(BuildContext ctx) => throw Missing();
    protected virtual void Pack(BuildContext ctx) => throw Missing();
    protected virtual void ConsumeAot(BuildContext ctx) => throw Missing();

    private InvalidOperationException Missing([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => new($"Implement {name} before registering it on {Id}.");

    protected Step Action(string name, Action<BuildContext> run, params string[] needs) => new($"{Id}.{name}", run, needs);
}
