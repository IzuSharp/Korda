#!/usr/bin/env python3
"""DB-only GC policy around the unchanged A21 cell worker, with actual receipts."""
import argparse
import gc
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
import time

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
DOMAIN = ROOT / "src/Benchmarks.Domain"
sys.path.insert(0, str(DOMAIN))
from common import digest, load, save
from supervision import identity


def policy(mode):
    if mode not in ("automatic", "deferred"):
        raise ValueError("Unknown DB scheduler GC policy")
    return {"id": "Perf-01-db-scheduler-gc-v1", "mode": mode,
            "scope": "Scheduler Python process for the complete original cell_worker.main call",
            "refcount_cleanup": "unchanged", "target_and_application_gc": "unchanged",
            "normal_exit": "restore initial automatic-GC state after worker completion",
            "forced_exit": "no restoration claim; existing supervisor cleanup, invalid cell"}


def policy_sha256(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def snapshot():
    return {"enabled": gc.isenabled(), "threshold": list(gc.get_threshold()),
            "count": list(gc.get_count()), "stats": gc.get_stats()}


def run_worker(worker, mode, receipt_path, receipt):
    """The finite worker owns all normal target drain and evidence finalization."""
    initial = snapshot()
    if not initial["enabled"]:
        raise ValueError("DB scheduler must enter with ordinary automatic GC enabled")
    receipt.update(status="running", initial_gc=initial, scope_started_ns=time.monotonic_ns())
    try:
        if mode == "deferred":
            gc.disable()
        receipt["scope_entry_gc"] = snapshot()
        save(receipt_path, receipt)
        code = worker()
        receipt.update(status="completed" if code == 0 else "failed", worker_exit_code=code)
        return code
    except BaseException as error:
        receipt.update(status="failed", error=type(error).__name__ + ": " + str(error)[:512])
        raise
    finally:
        try:
            receipt["scope_finished_ns"] = time.monotonic_ns()
            receipt["scope_exit_gc"] = snapshot()
        finally:
            # This runs after original A21 finalization on normal return. TERM
            # or KILL can bypass it; a running receipt must never imply restore.
            gc.enable()  # The entry precondition established enabled=True.
        receipt["restored_gc"] = snapshot()
        if "scope_entry_gc" in receipt and "scope_exit_gc" in receipt:
            receipt["collections_during_scope"] = [
                end["collections"] - start["collections"]
                for start, end in zip(receipt["scope_entry_gc"]["stats"], receipt["scope_exit_gc"]["stats"])]
        save(receipt_path, receipt)


def valid_receipt(value, expected, supervision, plan_sha256, sources):
    """Fail closed on missing, interrupted, changed or non-restored policy evidence."""
    try:
        enabled = expected["mode"] == "automatic"
        process = value["process"]
        owned = next(row for row in supervision["owned_processes"]
                     if row["pid"] == supervision["worker_pid"] and row["role"] == "cell-worker")
        initial, entry, end, restored = (value[name] for name in
                                        ("initial_gc", "scope_entry_gc", "scope_exit_gc", "restored_gc"))
        expected_sources = (HERE / "scheduler_gc.py", DOMAIN / "cell_worker.py",
                            DOMAIN / "common.py", ROOT / "src/Benchmarks.Harness/nativeweave_bench.py")
        deltas = [last["collections"] - first["collections"]
                  for first, last in zip(entry["stats"], end["stats"])]
        return (value["status"] == "completed" and value["worker_exit_code"] == 0 and
                value["policy"] == expected and value["policy_sha256"] == policy_sha256(expected) and
                value["plan_sha256"] == plan_sha256 and
                value["source_files"] == {str(path): sources[str(path)] for path in expected_sources} and
                process["pid"] == owned["pid"] and process["start_ticks"] == owned["start_ticks"] and
                initial["enabled"] is True and restored["enabled"] is True and
                entry["enabled"] is enabled and end["enabled"] is enabled and
                initial["threshold"] == entry["threshold"] == end["threshold"] == restored["threshold"] and
                value["scope_finished_ns"] > value["scope_started_ns"] and
                len(entry["stats"]) == len(end["stats"]) == len(deltas) == 3 and
                value["collections_during_scope"] == deltas and all(delta >= 0 for delta in deltas) and
                (enabled or deltas == [0, 0, 0]))
    except (KeyError, TypeError, ValueError, StopIteration):
        return False


def main():
    # The original worker still parses the complete unchanged argument vector.
    parser = argparse.ArgumentParser(add_help=False, allow_abbrev=False)
    parser.add_argument("--plan", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--a21", type=Path, required=True)
    args, _ = parser.parse_known_args()
    if args.a21.resolve() != ROOT / "src/Benchmarks.Harness/nativeweave_bench.py":
        raise ValueError("DB wrapper requires the same checkout's unchanged A21")
    plan = load(args.plan)
    selected = plan["settings"]["features"]["scheduler_gc_policy"]
    if selected != policy(selected["mode"]):
        raise ValueError("DB plan contains a different scheduler GC policy")
    worker_path = DOMAIN / "cell_worker.py"
    receipt_path = args.output.with_suffix(".scheduler-gc.json")
    if receipt_path.exists():
        raise ValueError("Never reuse an existing scheduler GC receipt")
    source_paths = (Path(__file__).resolve(), worker_path, DOMAIN / "common.py", args.a21.resolve())
    receipt = {"schema_version": 1, "task": "Perf-01", "policy": selected,
               "policy_sha256": policy_sha256(selected), "plan_sha256": digest(args.plan),
               "process": identity(os.getpid()), "source_files": {str(path): digest(path) for path in source_paths},
               "python_executable": str(Path(sys.executable).resolve()), "python_version": sys.version,
               "cpu_affinity": sorted(os.sched_getaffinity(0)),
               "claim": "Scheduler policy execution only; application correctness and timing require their original oracles"}
    spec = importlib.util.spec_from_file_location("perf01_original_db_cell_worker", worker_path)
    worker = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(worker)
    return run_worker(worker.main, selected["mode"], receipt_path, receipt)


if __name__ == "__main__":
    raise SystemExit(main())
