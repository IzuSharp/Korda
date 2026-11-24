#!/usr/bin/env python3
"""Run the buffered-result diagnostic serially against a fresh database per process."""
import argparse
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import types

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
OBSERVER_PATH = "benchmarks/db-perf/run.py"


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def utc():
    return datetime.now(timezone.utc).isoformat()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--database-root", type=Path, required=True)
    parser.add_argument("--source-label", required=True)
    parser.add_argument("--postgres-bin", type=Path)
    parser.add_argument("--sqlite-native", type=Path)
    parser.add_argument("--processes", type=int, default=3)
    parser.add_argument("--operations", type=int, default=128)
    parser.add_argument("--observer-ref", default="HEAD", help="Fixed Git object for the existing process/server observer")
    args = parser.parse_args()
    if not 1 <= args.processes <= 6 or not 1 <= args.operations <= 100_000:
        raise ValueError("Diagnostic process/operation count is outside bounds.")
    app, output, data_root = args.app.resolve(), args.output.resolve(), args.database_root.resolve()
    if output.exists() or data_root.exists():
        raise ValueError("Use fresh evidence and database directories; prior attempts are immutable.")
    build_path = app / "build-receipt.json"
    build = json.loads(build_path.read_text())
    if build["exit_code"] != 0 or build.get("validation_error"):
        raise ValueError("The outside-source AOT consumer did not build and validate.")
    executable = app / "out/BusinessResults"
    if sha(executable) != build["executable_sha256"]:
        raise ValueError("The actual executable changed.")
    for name, digest in build["sources"].items():
        if sha(app / name) != digest:
            raise ValueError("A copied consumer input changed: " + name)
    primary = [(key, value) for key, value in build["packages"].items() if key.startswith(build["primary_package"] + "/")]
    if len(primary) != 1 or sha(primary[0][1]["path"]) != primary[0][1]["sha256"]:
        raise ValueError("Actual primary package provenance is incomplete.")
    provider, route = build["provider"], build["route"]
    if provider == "postgresql" and args.postgres_bin is None:
        raise ValueError("Explicit PostgreSQL tools are required.")
    if provider == "sqlite" and route == "dapper" and args.sqlite_native is None:
        raise ValueError("Select the comparator's exact SQLite shared image.")
    # A fixed Git object avoids racing another worker's uncommitted observer edits.
    observer_commit = subprocess.check_output(["git", "rev-parse", args.observer_ref + "^{commit}"], cwd=ROOT, text=True).strip()
    observer_bytes = subprocess.check_output(["git", "show", observer_commit + ":" + OBSERVER_PATH], cwd=ROOT)
    observer = types.ModuleType("db_perf_business_observer")
    observer.__file__ = str(ROOT / OBSERVER_PATH)
    sys.modules[observer.__name__] = observer
    exec(compile(observer_bytes, observer.__file__, "exec"), observer.__dict__)
    spec = importlib.util.spec_from_file_location("db_perf_business_validation", HERE / "analyze.py")
    validation = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = validation
    spec.loader.exec_module(validation)
    output.mkdir(parents=True)
    data_root.mkdir(parents=True)
    receipt = {"schema": "DB-Perf-01.business-result-run.v1", "status": "started", "started_utc": utc(),
               "source_label": args.source_label, "route": route, "provider": provider,
               "runner_sha256": sha(__file__), "validator_sha256": sha(HERE / "analyze.py"),
               "observer": {"commit": observer_commit, "path": OBSERVER_PATH, "sha256": hashlib.sha256(observer_bytes).hexdigest()},
               "build_receipt": {"path": str(build_path), "sha256": sha(build_path)},
               "affinity": sorted(os.sched_getaffinity(0)), "processes": [], "accepted_long_seconds": 0,
               "framework_wide_zero_bytes": False,
               "scope": "Serial allocation diagnostic, not qualified performance timing. PostgreSQL live-tree/wait4 observations remain separate from client counts."}
    try:
        for repeat in range(args.processes):
            folder = output / f"process-{repeat}"
            folder.mkdir()
            database = data_root / f"process-{repeat}"
            database.mkdir()
            result = folder / "result.json"
            argv = [str(executable), "{connection}", str(result), primary[0][1]["path"], args.source_label,
                    str(args.sqlite_native.resolve()) if args.sqlite_native else "-", str(args.operations)]
            command = {"name": "probe", "phase": "buffered-business-result-allocation", "timeoutSeconds": 300, "argv": argv}
            plan = {"schemaVersion": 1, "provider": provider, "commands": [command]}
            if provider == "postgresql":
                plan["postgres"] = {"bin": str(args.postgres_bin.resolve()), "data": str(database / "pgdata"),
                                    "maxConnections": 10, "maxClientConcurrency": 1}
            else:
                plan["connection"] = str(database / "fresh.sqlite")
            observer.write_json(folder / "plan.json", plan)
            observer.validate_plan(plan, folder)
            item = {"repeat": repeat, "status": "started", "directory": str(folder)}
            receipt["processes"].append(item)
            try:
                with observer.database(plan, folder) as (connection, server_pid):
                    execution = observer.execute(command, connection, server_pid, folder, 0.1, check=False)
                item["execution_succeeded"] = execution["success"]
                if result.is_file():
                    item["raw_sha256"] = sha(result)
                    raw = validation.validate(result, verify_local=True)
                    item["process_id"] = raw["ProcessId"]
                    item["process_started_utc"] = raw["StartedUtc"]
                    item["executable_sha256"] = raw["ExecutableSha256"]
                    item["graph"] = raw["Graph"]
                    if args.sqlite_native and raw["NativeImages"] != {str(args.sqlite_native.resolve()): sha(args.sqlite_native)}:
                        raise ValueError("The actual independent SQLite engine differs from the selected comparator.")
                if not execution["success"] or not result.is_file():
                    raise ValueError("Buffered-result process failed; original evidence was preserved.")
                item["status"] = "validated"
            finally:
                item["files"] = {path.name: sha(path) for path in sorted(folder.iterdir()) if path.is_file()}
                observer.write_json(output / "receipt.json", receipt)
        if len({(item["process_id"], item["process_started_utc"], item["executable_sha256"])
                for item in receipt["processes"]}) != args.processes:
            raise ValueError("Independent process identities repeated.")
        receipt["status"] = "validated"
    except BaseException as error:
        receipt["status"] = "failed"
        receipt["error"] = {"type": type(error).__name__, "message": str(error)}
        raise
    finally:
        receipt["finished_utc"] = utc()
        observer.write_json(output / "receipt.json", receipt)
    print(json.dumps({"route": route, "provider": provider, "processes": len(receipt["processes"]), "status": receipt["status"]}))


if __name__ == "__main__":
    main()
