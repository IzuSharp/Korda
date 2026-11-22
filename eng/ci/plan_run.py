"""GitHub event adapter for the same impact planner developers can run locally."""
import json
import os
from pathlib import Path
import subprocess

from impact import PROVIDERS, inputs, plan, rule
from promote import candidate_name
from registry import public_heads


def main():
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text())
    kind = os.environ["GITHUB_EVENT_NAME"]
    base = (event["pull_request"]["base"]["sha"] if kind == "pull_request" else
            event["merge_group"]["base_sha"] if kind == "merge_group" else
            event.get("before") or "HEAD^")
    diff = subprocess.check_output(["git", "diff", "--no-renames", "--name-only", "-z", base, "HEAD"])
    paths = [p.decode() for p in diff.split(b"\0") if p]
    full = kind in ("schedule", "workflow_dispatch")
    # PRs testing this build system exercise every historical gate before switching main over.
    deep = full or any(p.startswith(("native/", "CMake", ".github/workflows/build.yml", "eng/task07_native",
                                     "eng/ci/", "src/Packaging.", "eng/verify", "tests/Verification.",
                                     "tests/Packaging.", "tests/Migration.Cpp/", "tests/Interop.")) for p in paths)
    # Lightweight PRs need no registry traffic. Recovery on main consults both live heads.
    consult_registry = full or kind == "push" or any(rule(p)[1] for p in paths)
    published = public_heads() if consult_registry else {p: {} for p in PROVIDERS}
    result = plan(paths, inputs("HEAD"), published, full, recover=kind == "push")
    if full or any(p.startswith((".github/workflows/", "eng/ci/")) for p in paths):
        languages = ["csharp", "python"]
    else:
        languages = []
        if any(p.lower().endswith((".cs", ".csproj", ".slnx", ".props", ".targets", ".editorconfig", ".lock.json")) or
               p in ("global.json", "NuGet.Config") for p in paths):
            languages.append("csharp")
        if any(p.endswith((".py", ".pyi", "requirements.txt", "pyproject.toml")) for p in paths):
            languages.append("python")
    result["languages"] = languages
    result["deep"] = deep
    output = Path("artifacts/plan.json")
    output.parent.mkdir(exist_ok=True)
    data = json.dumps(result, sort_keys=True, indent=2)
    output.write_text(data + "\n")
    print(data)
    tree = subprocess.check_output(["git", "rev-parse", "HEAD^{tree}"], text=True).strip()
    name = candidate_name(tree, result["packages"], os.environ["GITHUB_RUN_ID"], os.environ["GITHUB_RUN_ATTEMPT"])
    workflow_plan = {k: v for k, v in result.items() if k != "reasons"}
    with open(os.environ["GITHUB_OUTPUT"], "a") as out:
        for key, value in {"scope": result["scope"], "deep": str(deep).lower(), "artifact": name,
                           "pending": str(bool(result["packages"])).lower(),
                           "plan": json.dumps(workflow_plan, separators=(",", ":"))}.items():
            print(f"{key}={value}", file=out)
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a") as out:
        print("Linux checks:", result["scope"], "\n\nPublic candidates:", ", ".join(result["packages"]) or "none", file=out)
        print("\nExtra checks:", ", ".join(result["checks"]) or "none", "\n\nCodeQL:", ", ".join(languages) or "none", file=out)
        for lane, names in result["reasons"].items():
            print(f"\n* {lane}: {len(names)} changed paths", file=out)


if __name__ == "__main__":
    main()
