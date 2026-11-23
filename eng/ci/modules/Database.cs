namespace NativeWeave.CI;

internal sealed class Database() : Module("database")
{
    public override IEnumerable<Step> Workflow()
    {
        yield return Action("inputs", ctx => ctx.Python("eng/build.py", "inputs"));
        yield return Action("native", ctx =>
        {
            ctx.Python("eng/ci/native_graph.py", "query", ctx.NativeDir);
            ctx.Python("eng/build.py", "native");
        }, "database.inputs");
        yield return Action("dependencies", ctx => ctx.Python("eng/ci/native_graph.py", "verify",
            ctx.NativeDir, "nw_database_sqlite", "nw_database_postgresql"), "database.native");
        yield return Action("build", Build, "database.native");
        yield return Action("api", Api, "database.build");
        yield return Action("regressions", ctx => ctx.Python("eng/build.py", "regressions"), "database.build");
        yield return Action("pack", Pack, "database.native");
        yield return Action("consume-aot", ConsumeAot, "database.pack");
        yield return Action("sanitizers", ctx => ctx.Run("bash", "eng/verify-cpp-sanitizers.sh"));
        yield return Action("compatibility", ctx => ctx.Python("tests/Verification.Compatibility/verify.py",
            "--evidence", "artifacts/cpp-compatibility", "--design-root", "artifacts/scope-design"), "database.native");
        yield return Action("interop-properties", ctx => ctx.Python("tests/Verification.Interop/verify.py",
            "--output", "artifacts/cpp-interop-properties"), "database.native");
        yield return Action("runtime", ctx => ctx.Run("bash", "eng/verify-task04-linux.sh"), "database.native");
        yield return Action("transfer", ctx => ctx.Run("bash", "eng/verify-task07-linux.sh"), "database.pack");
        yield return Action("clean-build", ctx => ctx.Run("bash", "eng/verify-cpp-clean.sh"), "database.native");
    }
    protected override void Build(BuildContext ctx) => ctx.Python("eng/build.py", "workspace");
    protected override void Api(BuildContext ctx) => ctx.Python("eng/build.py", "api");
    protected override void Pack(BuildContext ctx) => ctx.Python("eng/build.py", "pack-existing", "--version", ctx.Version);
    protected override void ConsumeAot(BuildContext ctx) => ctx.Python("eng/build.py", "consumer-probes", "--version", ctx.Version);
}

internal sealed class PublicPackages() : Module("public")
{
    public override IEnumerable<Step> Workflow()
    {
        yield return Action("pack", ctx =>
        {
            string[] source = ctx.Scope switch
            {
                "full" or "selected" => ["--components", "artifacts/db-scope/prepared/candidate"],
                "pack" => [],
                _ => ["--native", "artifacts/provider-native/manifest.json"]
            };
            ctx.Python("eng/ci/bundles.py", ["--plan", ctx.PlanFile, "--version", ctx.Version + ".2", .. source]);
        });
        yield return Action("consume-aot", ctx => ctx.Python("eng/ci/consume.py", "artifacts/public/release.json", ctx.PlanFile), "public.pack");
        yield return Action("validate", ctx => ctx.Python("eng/ci/validate_public.py", "artifacts/public", ctx.PlanFile, ctx.Version + ".2"), "public.consume-aot");
    }
}
