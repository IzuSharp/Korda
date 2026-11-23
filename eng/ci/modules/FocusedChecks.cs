namespace NativeWeave.CI;

internal sealed class FocusedChecks() : Module("focused")
{
    private static void DotnetTool(BuildContext ctx, string project, params string[] args)
        => ctx.Run("dotnet", ["run", "--project", project, "-c", "Release", "-p:RestoreLockedMode=true", "--", .. args]);

    public override IEnumerable<Step> Workflow()
    {
        yield return Action("generator", ctx =>
        {
            DotnetTool(ctx, "tests/Query.Generator/Query.Generator.Tests.csproj");
            DotnetTool(ctx, "tests/Query.Generator/ApiCheck", ctx.Root, Path.Combine(ctx.Root, "artifacts/frozen-design"),
                Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? throw new InvalidOperationException("Set DOTNET_ROOT for API checks"));
        });
        yield return Action("pack-api", ctx => DotnetTool(ctx, "tests/Packaging.Release/ApiCheck",
            Path.Combine(ctx.Root, "artifacts/packaging-api.md")));
        yield return Action("pack-metadata", ctx => ctx.RunIn("tests/Packaging.Linux", "python3", "-m", "unittest",
            "test_profile.ProfileTests.test_pack_exact_edges_identity_and_immutability", "-v"));
        yield return Action("elf-profile", ctx => ctx.Run("python3", "-m", "unittest", "discover", "-s",
            "tests/Packaging.Linux", "-p", "test_profile.py", "-v"));
    }
}
