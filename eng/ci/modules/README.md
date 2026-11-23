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
    "package": "Korda.UI",
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

Run `python3 eng/ci/impact.py origin/main --output artifacts/plan.json` to see the changed languages, owning paths, actions, runner resources and selected package checks. Renames count as a deletion and an addition. Unknown paths, ambiguous ownership and dependency cycles fail before a runner installs a compiler.

**Native targets.** The fast planner uses the registered source roots and module edges. The native build requests a CMake File API reply before configure and verifies target sources, target dependencies and Ninja's observed header dependencies after compiling. New native build scripts can call `eng/ci/native_graph.py query BUILD_DIR` before configure and `verify BUILD_DIR TARGET ...` afterward. Register all supported conditional edges even when a single preset does not exercise them.

**Packaging commands.** CI runs commands from the repository root. The pack command receives `NW_PACKAGE`, `NW_VERSION`, `NW_FEED` (an empty local output directory) and `NW_INPUTS`. Produce exactly one standalone nupkg with that ID and version and include `korda/public.json` containing the `inputs` fingerprint. Pack scripts can reuse selected internal outputs under `artifacts/modules/<module>/` and must obtain unchanged components from their normal build or verified cache. The consumer restores the exact ID/version from `NW_FEED` into an isolated cache and executes a Native AOT application. The same command runs against nuget.org after publication.

PR jobs carry read-only permissions. Main checks each public package fingerprint and builds the final version for missing or changed previews. Only the trusted release job publishes those verified artifacts. Weekly and explicit full checks exercise all registered modules.

## Implement and run a workflow

A module can use a command array directly. For shared multi-step workflows implement an `Action` subclass or register existing commands with explicit prerequisites. See the two provider classes in `database.py`. A module's `checks` list selects those actions. `api_checks` can restrict an API-only edit. Each action declares the resources it uses. An independent module can request runner resources such as `"resources": ["dotnet"]`.

The Python runner executes each prerequisite once per worker, logs the command and elapsed time, and stops on the first failure. The workflow installs tools, restores caches and transfers the resulting artifacts. Keep new actions runnable in a temporary checkout with the same JSON plan.

```sh
python3 eng/ci/worker.py check-index
python3 -m unittest discover -s tests/Tooling -v
python3 eng/ci/impact.py origin/main --output artifacts/plan.json
# Pass a group listed under actions in the plan.
python3 eng/ci/worker.py run artifacts/plan.json linux
```

The historical Rust migration comparison uses its frozen archive. Run `python3 eng/build.py verify --compare-rust-reference` explicitly when that archive is available.
