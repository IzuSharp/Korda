#!/usr/bin/env python3
"""Bind rollback to retained real consumption without rerunning that consumer."""
import argparse
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--closure", type=Path, required=True)
    parser.add_argument("--consumer-receipt", type=Path, required=True)
    parser.add_argument("--feed", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    spec = importlib.util.spec_from_file_location("release", ROOT / "src/Packaging.Release/release.py")
    release = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(release)
    closure = json.loads(args.closure.read_text())
    original = json.loads(args.consumer_receipt.read_text())
    packages = {item["id"]: args.feed / f"{item['id']}.{item['version']}.nupkg" for item in closure["packages"]}
    accepted = release.rollback(closure, packages, original, 0)
    cases = [{"case": "retained-actual-consumption", "passed": accepted["complete"]}]
    for field, value in [("rid", "win-x64"), ("source_commit", "0" * 40)]:
        changed = copy.deepcopy(original)
        changed[field] = value
        try:
            release.rollback(closure, packages, changed, 0)
        except release.compat.Rejected as error:
            if "NWPKG001" not in str(error):
                raise
            cases.append({"case": "wrong-" + field, "passed": True, "diagnostic": str(error)})
        else:
            raise AssertionError("Rollback accepted a mismatching " + field)
    sha = lambda path: hashlib.sha256(Path(path).read_bytes()).hexdigest()
    receipt = {"passed": all(row["passed"] for row in cases), "cases": cases,
        "source_commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
        "source_dirty": bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip()),
        "release_tool_sha256": sha(ROOT / "src/Packaging.Release/release.py"),
        "actual_consumer_receipt_sha256": sha(args.consumer_receipt), "closure_sha256": sha(args.closure),
        "reused_package_source_commit": original["source_commit"],
        "scope": "Readback-only rollback identity regression over retained actual consumer; no old acceptance rerun"}
    (args.output / "result.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print("PASS rollback package/RID/source identity: 3 retained-evidence cases")


if __name__ == "__main__":
    main()
