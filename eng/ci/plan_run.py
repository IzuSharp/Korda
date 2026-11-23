"""GitHub event adapter for the same impact planner developers can run locally."""
import json
import os
from pathlib import Path
import subprocess
import sys

from impact import plan_tree
from promote import candidate_name


def jobs(plan, event, merged, reuse):
    pr_checks = event != "push"
    rebuild = merged and not reuse
    return {
        "build": plan["scope"] != "light" and (pr_checks or (rebuild and bool(plan["packages"]))),
        "modules": bool(plan["modules"] or plan["module_packages"]) and
                   (pr_checks or (rebuild and bool(plan["candidates"]))),
        "focused": pr_checks and bool(plan["checks"]),
    }


def main():
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text())
    kind = os.environ["GITHUB_EVENT_NAME"]
    base = (event["pull_request"]["base"]["sha"] if kind == "pull_request" else
            event["merge_group"]["base_sha"] if kind == "merge_group" else
            event.get("before") or "HEAD^")
    replay = event.get("inputs", {}).get("check_paths", "").splitlines() if kind == "workflow_dispatch" else []
    replay = [p.strip() for p in replay if p.strip()]
    full = kind == "schedule" or (kind == "workflow_dispatch" and not replay)
    result = plan_tree(base, replay=replay or None, full=full)
    output = Path("artifacts/plan.json")
    output.parent.mkdir(exist_ok=True)
    data = json.dumps(result, sort_keys=True, indent=2)
    output.write_text(data + "\n")
    print(data)
    tree = subprocess.check_output(["git", "rev-parse", "HEAD^{tree}"], text=True).strip()
    name = candidate_name(tree, result["candidates"], os.environ["GITHUB_RUN_ID"], os.environ["GITHUB_RUN_ATTEMPT"])
    workflow_plan = {k: v for k, v in result.items() if k != "reasons"}
    with open(os.environ["GITHUB_OUTPUT"], "a") as out:
        for key, value in {"scope": result["scope"], "deep": str(result["deep"]).lower(), "artifact": name,
                           "pending": str(bool(result["candidates"])).lower(),
                           "plan": json.dumps(workflow_plan, separators=(",", ":"))}.items():
            print(f"{key}={value}", file=out)
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a") as out:
        print("Linux checks:", result["scope"], "\n\nPublic candidates:", ", ".join(result["candidates"]) or "none", file=out)
        print("\nExtra checks:", ", ".join(result["checks"]) or "none", "\n\nCodeQL:", ", ".join(result["languages"]) or "none", file=out)
        print("\nModule jobs:", ", ".join(result["modules"]) or "none", file=out)
        for lane, names in result["reasons"].items():
            print(f"\n* {lane}: {len(names)} changed paths", file=out)


if __name__ == "__main__":
    if sys.argv[1:] == ["--jobs"]:
        plan = json.loads(Path("artifacts/plan.json").read_text())
        selected = jobs(plan, os.environ["GITHUB_EVENT_NAME"], os.environ["MERGED"] == "true", os.environ["REUSE"])
        with open(os.environ["GITHUB_OUTPUT"], "a") as out:
            for job, required in selected.items():
                print(f"{job}={str(required).lower()}", file=out)
    else:
        main()
