#!/usr/bin/env python3
"""Run a frozen Dapper consumer's short correctness matrix; never initialize data."""

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import shutil
import subprocess


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def save(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--connection", required=True)
    parser.add_argument("--sqlite-native", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--repeated-operations", type=int, default=72,
                        help="Additional fixed-count operations per matrix cell; correctness, not a long benchmark")
    args = parser.parse_args()
    if args.repeated_operations < 0 or args.repeated_operations > 3600:
        raise ValueError("This preflight accepts only 0..3600 repeated operations")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    executable, manifest = args.executable.resolve(), output / "contract.json"
    shutil.copyfile(args.manifest, manifest)
    expected_count = len(json.loads(manifest.read_text())["cases"])
    inputs = {str(executable): sha(executable), str(manifest): sha(manifest)}
    if args.sqlite_native:
        inputs[str(args.sqlite_native.resolve())] = sha(args.sqlite_native)
    receipt = {"scope": "short real-database correctness only; no performance comparison, allocation attribution, or long-run qualification",
               "inputs": inputs, "executions": []}
    save(output / "execute-receipt.json", receipt)
    for concurrency in (1, 2):
        for mode in ("prepared", "dynamic"):
            for session in ("persistent", "per-operation"):
                for repeated in (False, True) if args.repeated_operations else (False,):
                    name = f"{mode}-{session}-c{concurrency}-" + ("repeat" if repeated else "verify")
                    result_path, log_path = output / (name + ".json"), output / (name + ".log")
                    command = [str(executable), "--manifest", str(manifest), "--connection", args.connection,
                               "--output", str(result_path), "--mode", mode, "--session-mode", session,
                               "--case", "mixed", "--concurrency", str(concurrency), "--warmup", "36" if repeated else "0"]
                    if args.sqlite_native:
                        command += ["--sqlite-native", str(args.sqlite_native.resolve())]
                    command += ["--operations", str(args.repeated_operations)] if repeated else ["--verify-only"]
                    entry = {"name": name, "argv": command, "cwd": str(output),
                             "started_utc": datetime.now(timezone.utc).isoformat()}
                    with log_path.open("wb") as stream:
                        try:
                            result = subprocess.run(command, cwd=output, stdout=stream, stderr=subprocess.STDOUT, timeout=60)
                            entry["exit_code"] = result.returncode
                        except subprocess.TimeoutExpired:
                            entry["exit_code"] = None
                            entry["watchdog_timeout"] = True
                    entry["finished_utc"] = datetime.now(timezone.utc).isoformat()
                    entry["log_sha256"] = sha(log_path)
                    errors = []
                    if entry["exit_code"] != 0:
                        errors.append("process did not complete normally with exit 0")
                    if result_path.is_file():
                        document = json.loads(result_path.read_text())
                        entry["result_sha256"] = sha(result_path)
                        checks = document.get("verification", [])
                        entry["verification_count"] = len(checks)
                        entry["matched"] = sum(row.get("matched", False) for row in checks)
                        entry["owner_retirement"] = document.get("owner_retirement")
                        entry["counts"] = document.get("counts")
                        if len(checks) != concurrency * expected_count or entry["matched"] != len(checks):
                            errors.append("not every worker/case matched the frozen result oracle")
                        if not document.get("run_complete") or document.get("can_qualify"):
                            errors.append("short preflight must complete without qualifying as a long run")
                        counts = document.get("counts", {})
                        if repeated and any(counts.get(key) != args.repeated_operations for key in ("planned", "started", "completed")):
                            errors.append("repeated workload count did not match the configured finite count")
                        if any(counts.get(key, 0) for key in ("failed", "not_started", "unfinished", "late_terminal", "retries")):
                            errors.append("failed/unaccounted/late/retried operations in a correctness preflight")
                        owners = document.get("owner_retirement") or {}
                        if any(owners.get(key, -1) != 0 for key in ("active_physical_connections", "active_orm_readers", "active_transactions", "disposal_failures")):
                            errors.append("adapter owners did not retire normally")
                        if owners.get("adapter_physical_pool", {}).get("active_leases", 0):
                            errors.append("physical pool retained an active lease")
                    else:
                        errors.append("result file missing")
                    entry["validation_errors"] = errors
                    receipt["executions"].append(entry)
                    save(output / "execute-receipt.json", receipt)
                    print(json.dumps({"name": name, "exit": entry["exit_code"], "matched": entry.get("matched"), "errors": errors}), flush=True)
                    if errors:
                        raise RuntimeError("Correctness preflight failed: " + name)
    receipt["status"] = "passed-short-correctness-only"
    receipt["input_hashes_stable"] = all(sha(path) == digest for path, digest in inputs.items())
    save(output / "execute-receipt.json", receipt)
    if not receipt["input_hashes_stable"]:
        raise RuntimeError("A frozen input changed during the preflight")


if __name__ == "__main__":
    main()
