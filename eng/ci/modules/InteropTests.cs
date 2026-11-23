namespace NativeWeave.CI;

internal sealed class InteropTests(string component) : Module("interop-" + component.ToLowerInvariant() + "-tests")
{
    internal static readonly InteropTests[] Suites =
        new[] { "Abi", "Async", "Callbacks", "Errors", "Execution", "Lifetime", "Memory", "Negotiation" }
        .Select(name => new InteropTests(name)).ToArray();

    public override IEnumerable<Step> Workflow()
    {
        yield return Action("test", Test);
    }

    protected override void Test(BuildContext ctx)
    {
        string[] options = component switch
        {
            "Negotiation" => ["--design-root", "artifacts/frozen-design", "--plan-root", "artifacts/frozen-plan", "--output", "artifacts/interop-negotiation-tests"],
            _ => []
        };
        ctx.Python($"tests/Interop.{component}/verify.py", options);
    }
}
