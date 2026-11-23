# Register a CI module

Add an entry to a JSON file in this directory. Entries can be grouped by product. The key is a stable module ID. A new file under a registered directory needs no CI edit.

```json
{
  "compiler": {
    "sources": ["native/compiler/"],
    "tests": ["tests/compiler/"],
    "targets": ["compiler"],
    "run": ["bash", "tests/compiler/check.sh"],
    "artifacts": "artifacts/compiler"
  },
  "ui": {
    "sources": ["src/UI/", "eng/ui-package/"],
    "tests": ["tests/UI/"],
    "includes": ["compiler"],
    "run": ["bash", "tests/UI/check.sh"],
    "package": "NativeWeave.UI",
    "pack": ["python3", "eng/ui-package/pack.py"],
    "consume": ["bash", "tests/UI/consume-aot.sh"]
  }
}
```

| Register | Meaning |
| --- | --- |
| `sources`, `tests` | Repository-relative files or directory prefixes ending in `/`. A more specific rule wins. Changes to tests select their own module check. Changes to sources also select consumers. |
| `targets`, `depends` | Native CMake target names and additional module dependencies, including generators, linked sources and native libraries loaded at runtime. Literal managed project references between source modules are read from Git. Register conditional or computed dependencies that are invisible there. |
| `includes` | Modules bundled into an aggregate. Editing the aggregate's own source also exercises its included components. A leaf edit affects consumers and package acceptance without pulling in independent siblings. |
| `run`, `artifacts` | Linux check command as an argument array, with optional internal build output under `artifacts/`. A module can be test-only. Register a multi-step check as one script. |
| `package`, `pack`, `consume` | Optional top-level NuGet ID and its producer and AOT consumer commands. Set `integration` to an additional command if a changed dependency needs an integration check before packaging. |

Use `dotnet run --project eng/ci -- plan origin/main` on a committed change to see the checks, candidates and file ownership. Unknown paths, conflicting owners and dependency cycles fail planning. CI orchestration and repository documentation have their own registrations. The database entries use named `db:` checks to reuse their real provider services, locked tools and existing verification suites.

**Native targets.** The fast planner uses the registered source roots and module edges. The native build requests a CMake File API reply before configure and verifies target sources, target dependencies and Ninja's observed header dependencies after compiling. New native build scripts can call `eng/ci/native_graph.py query BUILD_DIR` before configure and `verify BUILD_DIR TARGET ...` afterward. Register all supported conditional edges even when a single preset does not exercise them.

**Packaging commands.** CI runs commands from the repository root. The pack command receives `NW_PACKAGE`, `NW_VERSION`, `NW_FEED` (an empty local output directory) and `NW_INPUTS`. Produce exactly one standalone nupkg with that ID and version and include `nativeweave/public.json` containing the `inputs` fingerprint. Pack scripts can reuse selected internal outputs under `artifacts/modules/<module>/` and must obtain unchanged components from their normal build or verified cache. The consumer restores the exact ID/version from `NW_FEED` into an isolated cache and executes a Native AOT application. The same command runs against nuget.org after publication.

PR jobs carry read-only permissions. Only the trusted merge workflow can publish a verified candidate. Weekly and explicit full checks exercise all registered modules.


## Implement and run a workflow

The SDK builds the CI executable and its tests in the main solution. Product consumers run as Native AOT executables. GitHub Actions prepares tools and caches, passes the plan JSON to the selected worker and retains its outputs. A cache miss uses the normal build.

For a new C# workflow copy a provider or test class from this directory. Override the methods the module supports (`Build`, `Api`, `Test`, `Pack`, `ConsumeAot`). Its `Workflow()` registers each action and the action names it needs. Add that class to `Recipes.All`. A registration can invoke the class with `"run": ["dotnet", "run", "--project", "eng/ci", "--", "action", "compiler.test"]`. Give product and test directories explicit owners. Declare implicit native or bundle dependencies and expose only the top-level package. The executor orders prerequisites once, prints each action and elapsed time, and stops on failure.

From the repository root:

```sh
dotnet test tests/CI -c Release
dotnet run --project eng/ci -- check-index
dotnet run --project eng/ci -- plan origin/main --output artifacts/plan.json
# On a prepared Linux host, use a group listed under actions in this plan.
dotnet run --project eng/ci -- run artifacts/plan.json sqlite
```

Enable local hooks with `pre-commit install`. On commit they format staged CI C# files, check staged ownership and run the CI project's tests. To request local native analysis, configure a CMake compilation database, set `NW_CMAKE_BUILD_DIR` to its directory, then run `pre-commit run --hook-stage manual clang-tidy`. Full clang-tidy and C/C++ CodeQL run weekly and on manual dispatch.
