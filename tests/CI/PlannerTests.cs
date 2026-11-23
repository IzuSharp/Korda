using System.Text.Json;
using NativeWeave.CI;
using Xunit;

namespace NativeWeave.CI.Tests;

public sealed class PlannerTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("nativeweave-ci-tests-").FullName;
    private readonly BuildContext ctx;

    public PlannerTests()
    {
        var probe = new BuildContext(root, "0.1.0-test.1", "light");
        var inheritedGit = probe.Read("git", "rev-parse", "--local-env-vars").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        ctx = new(root, "0.1.0-test.1", "light", unsetEnvironment: inheritedGit);
        ctx.Read("git", "init", "-q");
        Write("eng/ci/modules/project.json", """
            {
              "ci": {"tests":["eng/ci/","tests/CI/","NativeWeave.slnx","AGENTS.md"]},
              "layout": {"tests":["pack-tests/"],"checks":["db:pack"]},
              "codec-tests": {"tests":["tests/codec/"],"checks":["db:codec-tests"]},
              "database": {"sources":["shared/"],"checks":["db:full"]},
              "sqlite": {"sources":["sqlite/"],"tests":["tests/sqlite/"],"package":"NativeWeave.Database.Sqlite","depends":["database"],"checks":["db:sqlite"]},
              "postgresql": {"sources":["pg/"],"tests":["tests/pg/"],"package":"NativeWeave.Database.PostgreSql","depends":["database"],"checks":["db:postgresql"]},
              "compiler": {"sources":["compiler/"],"run":["compile"]},
              "gpu": {"sources":["gpu/"],"run":["check-gpu"]},
              "qt": {"sources":["qt/"],"includes":["compiler","gpu"],"package":"NativeWeave.Qt","pack":["pack"],"consume":["aot"]}
            }
            """);
        Write("NativeWeave.slnx", "<Solution><Project Path=\"sqlite/db.csproj\"/></Solution>");
        Write("sqlite/db.csproj", "<Project/>");
        Write("shared/database.cs", "// core");
        Write("tests/sqlite/test.cs", "// sqlite");
        Write("tests/pg/test.cs", "// pg");
        Write("AGENTS.md", "instructions");
        Commit();
    }

    private void Write(string path, string value)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, value);
    }
    private void Commit()
    {
        ctx.Read("git", "add", ".");
        ctx.Read("git", "-c", "user.name=CI", "-c", "user.email=ci@example.invalid", "commit", "-qm", "fixture");
    }
    private Plan Select(bool offline = false, bool full = false)
    {
        var catalog = new GitCatalog(ctx);
        var before = catalog.Read("HEAD");
        string tree = ctx.Read("git", "write-tree").Trim();
        var after = catalog.Read(tree);
        string[] paths = ctx.Read("git", "diff", "--no-renames", "--name-only", "-z", "HEAD", tree)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return Planner.Create(before, after, paths, full, ids =>
        {
            Assert.False(offline, "An isolated documentation/tooling/test change contacted the registry");
            return ids.ToDictionary(id => id, id => new Published("0.1.0-preview.1.2", before.Hashes.GetValueOrDefault(id)));
        });
    }
    private Plan Change(string path, string content = "// change", bool offline = true)
    {
        Write(path, content);
        ctx.Read("git", "add", ".");
        return Select(offline);
    }

    [Fact]
    public void AddingTheCiProjectToTheSolutionOnlyValidatesTooling()
    {
        Write("eng/ci/tool.csproj", "<Project/>");
        var plan = Change("NativeWeave.slnx", "<Solution><Project Path=\"sqlite/db.csproj\"/><Folder Name=\"/ci/\"><Project Path=\"eng/ci/tool.csproj\"/></Folder></Solution>");
        Assert.Empty(plan.BuildScopes);
        Assert.Empty(plan.Candidates);
    }

    [Theory]
    [InlineData("AGENTS.md")]
    [InlineData("eng/ci/Planner.cs")]
    [InlineData("tests/CI/Example.cs")]
    public void NonProductFilesNeverBuildOrPublishDatabase(string path)
    {
        var plan = Change(path);
        Assert.Empty(plan.Actions);
        Assert.Empty(plan.Candidates);
    }

    [Fact]
    public void SqliteRegressionNeedsItsOwnBuildOnly()
    {
        var plan = Change("tests/sqlite/test.cs");
        Assert.Equal(["sqlite.build", "sqlite-tests.test"], plan.Actions["sqlite"]);
        Assert.Equal(["sqlite"], plan.BuildScopes);
        Assert.Empty(plan.Candidates);
    }

    [Fact]
    public void ApiCheckerIsIndependent()
    {
        var plan = Change("tests/sqlite/ApiCheck/Program.cs");
        Assert.Equal(["sqlite.build", "sqlite.api"], plan.Actions["sqlite"]);
    }

    [Fact]
    public void ARegistryWithoutBundleMetadataRebuildsComponentsForTheDryRun()
    {
        Write("pack-tests/metadata.cs", "// fixture");
        ctx.Read("git", "add", ".");
        var catalog = new GitCatalog(ctx);
        var plan = Planner.Create(catalog.Read("HEAD"), catalog.Read(ctx.Read("git", "write-tree").Trim()),
            ["pack-tests/metadata.cs"], false, ids =>
            {
                Assert.Equal(["postgresql", "sqlite"], ids);
                return ids.ToDictionary(id => id, _ => new Published("0.1.0-preview.1.2"));
            });
        Assert.Equal("full", plan.Scope);
        Assert.Contains("public.consume-aot", plan.Actions["full"]);
        Assert.Empty(plan.Candidates);
    }

    [Fact]
    public void SharedSourcesAffectBothDeliveries()
    {
        var plan = Change("shared/database.cs", offline: false);
        Assert.Equal(["postgresql", "sqlite"], plan.Candidates);
        Assert.Equal(1, plan.Actions["full"].Count(s => s == "database.native"));
        Assert.Contains("public.validate", plan.Actions["full"]);
    }

    [Fact]
    public void LeafAndItsPackageDoNotRunSiblingChecks()
    {
        var plan = Change("compiler/input.cpp", offline: false);
        Assert.Equal(["compiler"], plan.Modules);
        Assert.Equal(["qt"], plan.ModulePackages);
        Assert.Empty(plan.BuildScopes);
    }

    [Fact]
    public void MisspelledRegistrationFailsBeforeScheduling()
    {
        Write("eng/ci/modules/extra.json", """{"extra":{"soruces":["new/"]}}""");
        ctx.Read("git", "add", ".");
        Assert.Throws<JsonException>(() => Select(true));
    }

    [Fact]
    public void IndependentComponentDoesNotPackOrRunSiblingRegressions()
    {
        var plan = Change("tests/codec/case.cs");
        Assert.Equal(["components"], plan.BuildScopes);
        Assert.Equal(["database.inputs", "database.native", "codec-tests.test"], plan.Actions["components"]);
        Assert.Empty(plan.Candidates);
    }

    [Fact]
    public void MainReusesCandidatesAndRebuildsOnlyTheAffectedFamily()
    {
        var plan = new Plan { ModulePackages = ["qt"], Candidates = ["qt"] };
        Assert.Equal(new(false, true, false), JobSelection.For(plan, true, true, false));
        Assert.Equal(new(false, false, false), JobSelection.For(plan, true, true, true));
        Assert.Equal(new(false, false, false), JobSelection.For(plan, true, false, false));
    }

    [Fact]
    public void BothSidesOfARenameAreRouted()
    {
        File.Delete(Path.Combine(root, "tests/sqlite/test.cs"));
        var plan = Change("tests/pg/moved.cs");
        Assert.Equal(["postgresql", "sqlite"], plan.BuildScopes);
        Assert.Empty(plan.Candidates);
    }

    [Fact]
    public void ManagedReferencesPropagateAndDuplicatesFailEarly()
    {
        Write("compiler/compiler.csproj", "<Project/>");
        Write("gpu/gpu.csproj", "<Project><ProjectReference Include=\"../compiler/compiler.csproj\"/></Project>");
        Commit();
        Assert.Equal(["compiler", "gpu"], Change("compiler/new.cpp", offline: false).Modules);
        Write("eng/ci/modules/duplicate.json", """{"another":{"sources":["gpu/"]}}""");
        ctx.Read("git", "add", ".");
        Assert.Contains("Ambiguous ownership", Assert.Throws<InvalidDataException>(() => Select(true)).Message);
    }

    [Fact]
    public void UnknownStagedPathIsReportedBeforeAnyCheckOrRegistryQuery()
    {
        var error = Assert.Throws<InvalidDataException>(() => Change("future/unowned.cpp"));
        Assert.Contains("future/unowned.cpp", error.Message);
    }

    [Fact]
    public void ExecutionReusesPrerequisitesAndStopsOnFailure()
    {
        List<string> calls = [];
        Step[] steps = [new("build", _ => calls.Add("build")), new("api", _ => calls.Add("api"), "build"),
            new("test", _ => throw new InvalidOperationException("fixture"), "build"),
            new("pack", _ => calls.Add("pack"), "test")];
        Assert.Contains("test failed", Assert.Throws<InvalidOperationException>(() =>
            ctx.Execute(["api", "pack"], steps.ToDictionary(s => s.Name))).Message);
        Assert.Equal(["build", "api"], calls);
    }

    public void Dispose() => Directory.Delete(root, true);
}
