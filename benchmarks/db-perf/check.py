#!/usr/bin/env python3
"""Short measurement-harness correctness gate; never starts a performance campaign."""
import hashlib
import json
from pathlib import Path
import py_compile
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent


def main():
    for path in HERE.rglob("*.py"):
        if not any(part in ("bin", "obj", "packages", "__pycache__", "reproduction-inputs") for part in path.parts):
            py_compile.compile(str(path), doraise=True)
    archive = HERE / "reproduction-inputs"
    for item in json.loads((archive / "index.json").read_text())["inputs"]:
        path = (ROOT / item["path"]).resolve()
        if not path.is_relative_to(archive.resolve()):
            raise ValueError("Historical source input escaped its immutable archive")
        data = path.read_bytes()
        if len(data) != item["bytes"] or hashlib.sha256(data).hexdigest() != item["sha256"]:
            raise ValueError("Historical source input differs from its measured bytes: " + item["path"])
    for snapshot in (archive / "snapshots").glob("*.json"):
        for item in json.loads(snapshot.read_text())["inputs"]:
            if "archivePath" in item:
                path = (ROOT / item["archivePath"]).resolve()
                if not path.is_relative_to(archive.resolve()):
                    raise ValueError("Snapshot source input escaped its immutable archive")
                data = path.read_bytes()
            else:
                reference = item["reference"]
                if reference["repository"] != "IzuSharp/Korda" or not re.fullmatch(r"[0-9a-f]{40}", reference["commit"]):
                    raise ValueError("Snapshot source reference is not a fixed product commit")
                tree = subprocess.check_output(["git", "rev-parse", reference["commit"] + "^{tree}"], cwd=ROOT, text=True).strip()
                if tree != reference["tree"]:
                    raise ValueError("Snapshot source tree differs from its fixed reference")
                data = subprocess.check_output(["git", "show", reference["commit"] + ":" + reference["path"]], cwd=ROOT)
            if len(data) != item["bytes"] or hashlib.sha256(data).hexdigest() != item["sha256"]:
                raise ValueError("Snapshot input differs from its measured bytes: " + item["sourcePath"])
    for path in (HERE / "workload").glob("*.json"):
        json.loads(path.read_text())
    subprocess.run(["python3", str(HERE / "cpu_profile.py"), "validate"], check=True, cwd=ROOT, timeout=30)
    subprocess.run(["python3", str(HERE / "cpu_profile.py"), "self-test"], check=True, cwd=ROOT, timeout=30)
    subprocess.run(["python3", str(HERE / "cpu_report.py"), "validate"], check=True, cwd=ROOT, timeout=30)
    subprocess.run(["python3", str(HERE / "cpu_report.py"), "self-test"], check=True, cwd=ROOT, timeout=30)
    subprocess.run(["python3", str(HERE / "runtime_spin.py"), "validate"], check=True, cwd=ROOT, timeout=30)
    subprocess.run(["python3", str(HERE / "runtime_spin.py"), "self-test"], check=True, cwd=ROOT, timeout=30)
    subprocess.run(["python3", str(HERE / "test_evidence_archive.py")], check=True, cwd=ROOT, timeout=30)
    subprocess.run(["python3", str(HERE / "test_controller.py")], check=True, cwd=ROOT, timeout=60)
    subprocess.run(["python3", str(HERE / "test_plan_diagnostics.py")], check=True, cwd=ROOT, timeout=60)
    subprocess.run(["python3", str(HERE / "resources/test_validation.py")], check=True, cwd=ROOT, timeout=60)
    subprocess.run(["python3", str(HERE / "business-results/test_analysis.py")], check=True, cwd=ROOT, timeout=60)
    subprocess.run(["python3", str(HERE / "analysis/test_units.py")], check=True, cwd=ROOT, timeout=30)
    project = HERE / "managed/SchedulerTests/DbPerf.Scheduler.Tests.csproj"
    output = ROOT / "artifacts/db-perf/scheduler-tests"
    subprocess.run(["dotnet", "publish", str(project), "-c", "Release", "-r", "linux-x64",
                    "-p:PublishAot=true", "-p:IlcTreatWarningsAsErrors=true", "-p:RestoreLockedMode=true",
                    "-m:1", "-p:UseSharedCompilation=false", "-nr:false", "-o", str(output)], check=True, cwd=ROOT)
    subprocess.run([str(output / "DbPerf.Scheduler.Tests")], check=True, cwd=ROOT, timeout=30)


if __name__ == "__main__":
    main()
