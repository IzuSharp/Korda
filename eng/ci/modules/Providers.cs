namespace NativeWeave.CI;

internal abstract class Provider(string id) : Module(id)
{
    public override IEnumerable<Step> Workflow()
    {
        yield return Action("build", Build);
        yield return Action("dependencies", Dependencies, Id + ".build");
        yield return Action("api", Api, Id + ".build");
    }
    protected override void Build(BuildContext ctx)
    {
        ctx.Python("eng/ci/native_graph.py", "query", ctx.NativeDir);
        ctx.Python("eng/task07_native.py", "--output", "artifacts/provider-native", "--provider", Id);
    }
    private void Dependencies(BuildContext ctx) =>
        ctx.Python("eng/ci/native_graph.py", "verify", ctx.NativeDir, "nw_database_" + Id);
    protected abstract override void Api(BuildContext ctx);
}

internal sealed class Sqlite() : Provider("sqlite")
{
    protected override void Api(BuildContext ctx) => ctx.Python("tests/Data.Sqlite/verify.py", "--api-only",
        "--design", "artifacts/frozen-design", "--output", "artifacts/provider-api", "--native-build", ctx.NativeDir);
}

internal sealed class PostgreSql() : Provider("postgresql")
{
    protected override void Api(BuildContext ctx) => ctx.Python("tests/Data.Postgres/verify.py", "--api-only",
        "--design-root", "artifacts/frozen-design", "--output", "artifacts/provider-api", "--native-build", ctx.NativeDir);
}

internal abstract class ProviderTests(string provider) : Module(provider + "-tests")
{
    public override IEnumerable<Step> Workflow()
    {
        yield return Action("test", ctx =>
        {
            ctx.Run("ctest", "--preset", ctx.Preset, "--no-tests=error", "-R", @"^native\.selected\.abi$");
            Test(ctx);
        }, provider + ".build");
    }
    protected abstract override void Test(BuildContext ctx);
}

internal sealed class SqliteTests() : ProviderTests("sqlite")
{
    protected override void Test(BuildContext ctx) => ctx.Python("tests/Data.Sqlite/verify.py", "--test-only",
        "--output", "artifacts/provider-tests", "--design", "artifacts/frozen-design", "--native-build", ctx.NativeDir);
}

internal sealed class PostgreSqlTests() : ProviderTests("postgresql")
{
    protected override void Test(BuildContext ctx) => ctx.Python("tests/Data.Postgres/verify.py", "--test-only",
        "--output", "artifacts/provider-tests", "--design-root", "artifacts/frozen-design", "--native-build", ctx.NativeDir,
        "--postgres-bin", Environment.GetEnvironmentVariable("NW_POSTGRES_BIN") ?? "artifacts/postgres-tools/install/bin");
}
