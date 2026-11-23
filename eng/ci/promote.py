"""Find a successful same-repository PR candidate for the exact merged contents."""
import json
import os
import subprocess

from catalog import load_catalog


def api(endpoint):
    return json.loads(subprocess.check_output(["gh", "api", endpoint], text=True))


def main():
    repo = os.environ["GITHUB_REPOSITORY"]
    sha = os.environ["GITHUB_SHA"]
    root = "repos/" + repo
    tree = subprocess.check_output(["git", "rev-parse", "HEAD^{tree}"], text=True).strip()
    with open("artifacts/plan.json") as stream:
        plan = json.load(stream)
    wanted = plan["candidates"]
    prs = api(f"{root}/commits/{sha}/pulls")
    pr = next((p for p in prs if p["merged_at"] and p["base"]["ref"] == "main" and p["merge_commit_sha"] == sha), None)
    result = {"merged": "true" if pr else "false", "run": "", "artifact": "", "number": ""}
    if pr and wanted and (pr["head"]["repo"] or {}).get("full_name") == repo:
        head = pr["head"]["sha"]
        for run in api(f"{root}/actions/workflows/ci.yml/runs?event=pull_request&head_sha={head}&per_page=50")["workflow_runs"]:
            if run["conclusion"] != "success" or run["head_repository"]["full_name"] != repo:
                continue
            if not any(p["number"] == pr["number"] for p in run["pull_requests"]):
                continue
            # A long-lived PR can have an older CI sequence than a later public release.
            version = (run["run_number"], 2)
            if any(version <= tuple(map(int, plan["published"][p]["version"].split(".")[-2:])) for p in wanted if plan["published"][p].get("version")):
                continue
            expected = candidate_name(tree, wanted, run["id"], run["run_attempt"])
            artifacts = api(f"{root}/actions/runs/{run['id']}/artifacts")["artifacts"]
            available = {a["name"] for a in artifacts if not a["expired"]}
            if all(expected + item["suffix"] in available for item in plan["releases"]):
                result.update(run=run["id"], artifact=expected, number=run["run_number"])
                break
    with open(os.environ["GITHUB_OUTPUT"], "a") as out:
        for key, value in result.items():
            print(f"{key}={value}", file=out)


def candidate_name(tree, providers, run, attempt):
    if not set(providers).issubset(load_catalog().packages):
        raise ValueError("Unknown public package")
    return f"providers-{tree}-{'-'.join(sorted(providers))}-{run}-{attempt}"


if __name__ == "__main__":
    main()
