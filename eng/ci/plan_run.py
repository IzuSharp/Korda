"""GitHub event adapter for the same impact planner developers can run locally."""
import json
import os
from pathlib import Path
import subprocess

from impact import inputs, plan
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
                                     "eng/ci/", "src/Packaging.", "eng/verify")) for p in paths)
    result = plan(paths, inputs("HEAD"), public_heads(), full)
    result["deep"] = deep
    output = Path("artifacts/plan.json")
    output.parent.mkdir(exist_ok=True)
    data = json.dumps(result, sort_keys=True, indent=2)
    output.write_text(data + "\n")
    print(data)
    tree = subprocess.check_output(["git", "rev-parse", "HEAD^{tree}"], text=True).strip()
    name = candidate_name(tree, result["packages"], os.environ["GITHUB_RUN_ID"], os.environ["GITHUB_RUN_ATTEMPT"])
    with open(os.environ["GITHUB_OUTPUT"], "a") as out:
        for key, value in {"scope": result["scope"], "deep": str(deep).lower(), "artifact": name,
                           "pending": str(bool(result["packages"])).lower(),
                           "plan": json.dumps(result, separators=(",", ":"))}.items():
            print(f"{key}={value}", file=out)
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a") as out:
        print("Linux checks:", result["scope"], "\n\nPublic candidates:", ", ".join(result["packages"]) or "none", file=out)
        for lane, names in result["reasons"].items():
            print(f"\n* {lane}: {len(names)} changed paths", file=out)


if __name__ == "__main__":
    main()
