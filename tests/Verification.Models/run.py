#!/usr/bin/env python3
"""Record A19 regression, examples and complete model execution with raw logs."""

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import platform
import subprocess
import sys
import time


ROOT = Path(__file__).resolve().parents[2]


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def utc():
    return datetime.now(timezone.utc).isoformat()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--design-root", type=Path, required=True, help="Exact Plan-locked Design checkout")
    parser.add_argument("--output-dir", type=Path, required=True, help="New attempt directory; existing evidence is retained")
    args = parser.parse_args()
    output = args.output_dir.resolve()
    if output.exists() and any(output.iterdir()):
        parser.error("Choose an empty evidence directory; previous attempts are never overwritten")
    output.mkdir(parents=True, exist_ok=True)
    commands = [
        ("regressions", [sys.executable, "-m", "unittest", "discover", "-s", "tests/Verification.Models", "-p", "test_*.py", "-v"]),
        ("minimal", [sys.executable, "samples/api/verification-models/minimal.py"]),
        ("advanced", [sys.executable, "samples/api/verification-models/advanced.py"]),
        ("models", [sys.executable, "src/Verification.Models/verify.py", "--design-root", str(args.design_root.resolve()),
                    "--output-dir", str(output / "models")]),
    ]
    started = utc()
    records = []
    for name, argv in commands:
        beginning = utc()
        timer = time.monotonic()
        try:
            result = subprocess.run(argv, cwd=ROOT, capture_output=True, timeout=60)
            code, stdout, stderr = result.returncode, result.stdout, result.stderr
        except subprocess.TimeoutExpired as error:
            code, stdout, stderr = 124, error.stdout or b"", (error.stderr or b"") + b"\nCommand exceeded 60 second limit.\n"
        stdout_path = output / (name + ".stdout")
        stderr_path = output / (name + ".stderr")
        stdout_path.write_bytes(stdout)
        stderr_path.write_bytes(stderr)
        record = {"name": name, "argv": argv, "cwd": str(ROOT), "started_utc": beginning, "ended_utc": utc(),
                  "elapsed_seconds": round(time.monotonic() - timer, 6), "exit_code": code,
                  "stdout": stdout_path.name, "stdout_sha256": sha(stdout_path),
                  "stderr": stderr_path.name, "stderr_sha256": sha(stderr_path)}
        records.append(record)
        print(json.dumps(record), flush=True)
    passed = all(record["exit_code"] == 0 for record in records)
    artifacts = [{"path": path.relative_to(output).as_posix(), "sha256": sha(path)}
                 for path in sorted(output.rglob("*")) if path.is_file()]
    report = {"schema_version": "1.0.0", "task": "A19", "platform": platform.system(),
              "architecture": platform.machine(), "python": platform.python_version(),
              "evidence_kind": "model_check", "started_utc": started, "ended_utc": utc(),
              "status": "passed" if passed else "failed", "exit_code": 0 if passed else 1,
              "commands": records, "artifacts_excluding_this_manifest": artifacts,
              "source_identity": "models/source-manifest.json and models/summary.json",
              "scope": "Linux x64 finite models, tooling regressions and tool examples. No Native AOT or runtime claim."}
    (output / "run.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({"status": report["status"], "run": str(output / "run.json")}), flush=True)
    return report["exit_code"]


if __name__ == "__main__":
    raise SystemExit(main())
