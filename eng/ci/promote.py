"""Find an existing PR artifact for an exact merge. This command has read-only GitHub access."""

import argparse
import json
import os
from pathlib import Path
import re
import subprocess

from catalog import git, load_catalog
from facts import inputs
from impact import plan_tree


def api(endpoint):
    return json.loads(subprocess.check_output(["gh", "api", endpoint], text=True))


def matches_pr(run, pr, repo):
    if (
        run["conclusion"] != "success"
        or run["event"] != "pull_request"
        or (run.get("head_repository") or {}).get("full_name") != repo
        or run["head_sha"] != pr["head"]["sha"]
    ):
        return False
    links = run["pull_requests"]
    if links:
        return any(link["number"] == pr["number"] for link in links)
    # GitHub can drop pull_requests after merge. Its same-repository reusable workflow
    # records retain the original refs/pull/N/merge. Do not guess from a branch name.
    merge_ref = f"refs/pull/{pr['number']}/merge"
    return any(
        entry.get("ref") == merge_ref
        and entry.get("path") == f"{repo}/.github/workflows/build.yml@{entry.get('sha', '')}"
        and re.fullmatch(r"[0-9a-f]{40}", entry.get("sha") or "")
        for entry in run.get("referenced_workflows") or ()
    )


def promotion(repo, sha, plan):
    tree = git("rev-parse", sha + "^{tree}").decode().strip()
    root = "repos/" + repo
    wanted = plan["candidates"]
    prs = api(f"{root}/commits/{sha}/pulls")
    pr = next(
        (p for p in prs if p["merged_at"] and p["base"]["ref"] == "main" and p["merge_commit_sha"] == sha),
        None,
    )
    result = {"merged": "true" if pr else "false", "run": "", "artifact": "", "number": ""}
    if pr and wanted and (pr["head"]["repo"] or {}).get("full_name") == repo:
        head = pr["head"]["sha"]
        for run in api(
            f"{root}/actions/workflows/ci.yml/runs?event=pull_request&head_sha={head}&per_page=50"
        )["workflow_runs"]:
            if not matches_pr(run, pr, repo):
                continue
            version = (run["run_number"], 2)
            if any(
                version <= tuple(map(int, plan["published"][p]["version"].split(".")[-2:]))
                for p in wanted
                if plan["published"][p].get("version")
            ):
                continue
            expected = candidate_name(tree, wanted, run["id"], run["run_attempt"])
            artifacts = api(f"{root}/actions/runs/{run['id']}/artifacts?per_page=100")["artifacts"]
            available = {a["name"] for a in artifacts if not a["expired"]}
            if all(expected + item["suffix"] in available for item in plan["releases"]):
                print(f"Matched PR #{pr['number']} to CI run {run['id']} and tree {tree}.")
                result.update(run=run["id"], artifact=expected, number=run["run_number"])
                break
    return result


def retry(repo, number):
    if os.environ.get("GITHUB_REF") != "refs/heads/main" or not re.fullmatch(r"[1-9][0-9]*", number):
        raise ValueError("Select main and enter a merged PR number.")
    pr = api(f"repos/{repo}/pulls/{number}")
    sha = pr["merge_commit_sha"]
    if not (
        pr["merged_at"]
        and pr["base"]["ref"] == "main"
        and (pr["head"]["repo"] or {}).get("full_name") == repo
    ):
        raise ValueError("The PR must already be merged into main from this repository.")
    git("merge-base", "--is-ancestor", sha, "HEAD")
    plan = plan_tree(sha + "^1", sha)
    if not plan["candidates"]:
        print("No unpublished package changes remain for this PR.")
        return {"run": ""}
    if inputs("HEAD") != plan["inputs"]:
        raise ValueError("Package inputs have changed on main since this merge.")
    result = promotion(repo, sha, plan)
    if not result["run"]:
        raise ValueError("No eligible original PR artifact remains for this merge.")
    plan["retry_merge"] = sha
    result["plan"] = json.dumps({k: v for k, v in plan.items() if k != "reasons"}, separators=(",", ":"))
    return result


def candidate_name(tree, providers, run, attempt):
    if not set(providers).issubset(load_catalog().packages):
        raise ValueError("Unknown public package")
    return f"providers-{tree}-{'-'.join(sorted(providers))}-{run}-{attempt}"


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--retry-pr")
    args = parser.parse_args()
    repo = os.environ["GITHUB_REPOSITORY"]
    if args.retry_pr is not None:
        result = retry(repo, args.retry_pr)
    else:
        result = promotion(
            repo, os.environ["GITHUB_SHA"], json.loads(Path("artifacts/plan.json").read_text())
        )
    with open(os.environ["GITHUB_OUTPUT"], "a") as out:
        for key, value in result.items():
            print(f"{key}={value}", file=out)
