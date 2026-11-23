"""Execute one registered module on Linux; hand the verified candidate to release.yml."""
import argparse
import json
import os
import subprocess
import zipfile
from pathlib import Path

from catalog import ROOT, git, load_catalog

OUT = ROOT / "artifacts/public"


def command(argv, env):
    subprocess.run(argv, cwd=ROOT, env=os.environ | env, check=True)


def execute(action, name, plan, version):
    spec = load_catalog().modules[name]
    env = {"NW_MODULE": name, "NW_VERSION": version}
    if action == "public":
        if name not in plan["candidates"]:
            raise ValueError("Unexpected public consumer: " + name)
        command(spec["consume"], env | {"NW_PACKAGE": spec["package"], "NW_FEED": "https://api.nuget.org/v3/index.json"})
        return
    if action == "check":
        command(spec[plan["run_steps"][name]], env)
        return
    if name not in plan["module_packages"]:
        raise ValueError("Package not selected in the plan: " + name)

    from validate_public import compat
    identity = spec["package"]
    feed = OUT / "feed"
    feed.mkdir(parents=True)
    fingerprint = plan["inputs"][name]
    env.update(NW_PACKAGE=identity, NW_FEED=str(feed), NW_INPUTS=fingerprint)
    command(spec["pack"], env)
    path = feed / f"{identity}.{version}.nupkg"
    if list(feed.iterdir()) != [path]:
        raise ValueError(f"{name}: pack must produce exactly {path.name}")
    with zipfile.ZipFile(path) as archive:
        if json.loads(archive.read("nativeweave/public.json"))["inputs"] != fingerprint:
            raise ValueError("The candidate must embed its module input fingerprint")
    commit = git("rev-parse", "HEAD").decode().strip()
    record = dict(schema_version=1, source_tree=git("rev-parse", "HEAD^{tree}").decode().strip(),
                  source_commit=commit, source_dirty=False, inputs=plan["inputs"],
                  packages=[dict(id=identity, version=version, source_commit=commit, dependencies={},
                                 upload_archive_sha256=compat.file_hash(path), unsigned_entries=compat.package_entries(path))])
    compat.validate_closure(record, {identity: path})
    command(spec["consume"], env)
    if git("status", "--porcelain").strip() or compat.file_hash(path) != record["packages"][0]["upload_archive_sha256"]:
        raise ValueError("Module check changed the candidate or source tree")
    (OUT / "release.json").write_text(json.dumps(record, indent=2) + "\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["check", "pack", "public"])
    parser.add_argument("module")
    parser.add_argument("plan", type=Path)
    parser.add_argument("version")
    args = parser.parse_args()
    execute(args.action, args.module, json.loads(args.plan.read_text()), args.version)
