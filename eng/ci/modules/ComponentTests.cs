namespace NativeWeave.CI;

// Each suite owns its AOT assertions and its output directory. Suites share the native build.
internal sealed class ComponentTests(string id, string directory, params string[] designArgs) : Module(id)
{
    internal static readonly ComponentTests[] Suites =
    [
        new("abstraction-tests", "Data.Abstractions"), new("codec-tests", "Data.Codecs"),
        new("transaction-tests", "Data.Transactions", "--design", "artifacts/frozen-design"),
        new("streaming-tests", "Data.Streaming", "--design-root", "artifacts/frozen-design"),
        new("binding-tests", "Query.Binding", "--design", "artifacts/frozen-design"),
    ];

    public override IEnumerable<Step> Workflow()
    {
        yield return Action("test", Test, "database.native");
    }

    protected override void Test(BuildContext ctx)
        => ctx.Python($"tests/{directory}/verify.py", ["--output", $"artifacts/component-tests/{directory}",
            "--native-build", ctx.NativeDir, .. designArgs]);
}
