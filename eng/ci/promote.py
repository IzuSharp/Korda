"""Find a successful same-repository PR candidate for the exact merged contents."""
import json
import os
import subprocess

from impact import IDS


def api(endpoint):
    return json.loads(subprocess.check_output(["gh", "api", endpoint], text=True))


def main():
    repo = os.environ["GITHUB_REPOSITORY"]
    sha = os.environ["GITHUB_SHA"]
    root = "repos/" + repo
    tree = subprocess.check_output(["git", "rev-parse", "HEAD^{tree}"], text=True).strip()
    with open("artifacts/plan.json") as stream:
        wanted = json.load(stream)["packages"]
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
            expected = candidate_name(tree, wanted, run["id"], run["run_attempt"])
            artifacts = api(f"{root}/actions/runs/{run['id']}/artifacts")["artifacts"]
            if any(a["name"] == expected and not a["expired"] for a in artifacts):
                result.update(run=run["id"], artifact=expected, number=run["run_number"])
                break
    with open(os.environ["GITHUB_OUTPUT"], "a") as out:
        for key, value in result.items():
            print(f"{key}={value}", file=out)


def candidate_name(tree, providers, run, attempt):
    if not set(providers).issubset(IDS):
        raise ValueError("Unknown public package")
    return f"providers-{tree}-{'-'.join(sorted(providers))}-{run}-{attempt}"


if __name__ == "__main__":
    main()
