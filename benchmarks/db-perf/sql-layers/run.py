#!/usr/bin/env python3
"""Run nine uninstrumented Find processes and three separate actual-plan processes serially."""
import argparse
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import subprocess
import sys
import tarfile
import zipfile

from analyze import validate
from contracts import BUNDLE_SHA, FIXTURE, LANES, MODEL_REF, WORKLOAD_SHA, read_contract, sha, write_json

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
RUNTIME_SHA = "aecbd4e949e333399704ba141f7d52e9225323e658f6180262355831b5aec7e8"
ROUND_ORDER = [("original", "qualified", "limited"), ("limited", "qualified", "original"), ("qualified", "original", "limited")]


def utc(): return datetime.now(timezone.utc).isoformat()


def load_frozen_module(name, relative, output):
    data = subprocess.check_output(["git", "show", MODEL_REF + ":" + relative], cwd=ROOT)
    path = output / "controller-source" / Path(relative).name
    path.parent.mkdir(exist_ok=True)
    with path.open("xb") as stream: stream.write(data)
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module, {"git_ref": MODEL_REF, "repository_path": relative, "sha256": sha(path)}


def restore_runtime(archive_path, output):
    if sha(archive_path) != RUNTIME_SHA:
        raise ValueError("The fixed recovered GitHub runtime artifact changed")
    with zipfile.ZipFile(archive_path) as archive:
        preparation = json.loads(archive.read("preparation.json"))
        with archive.open("runtime-bundle.tar.gz") as stream:
            if hashlib.file_digest(stream, "sha256").hexdigest() != preparation["sha256"]:
                raise ValueError("The prepared runtime tar bytes changed")
        with archive.open("runtime-bundle.tar.gz") as stream:
            with tarfile.open(fileobj=stream, mode="r|gz") as tar:
                tar.extractall(output / "runtime", filter="data")
    bundle = output / "runtime/bundle"
    if sha(bundle / "bundle.json") != BUNDLE_SHA:
        raise ValueError("The runtime manifest differs from the original artifact")
    manifest = json.loads((bundle / "bundle.json").read_text())
    actual = {str(path.relative_to(bundle)) for path in bundle.rglob("*") if path.is_file()}
    if actual != set(manifest["files"]) | {"bundle.json"}:
        raise ValueError("Restored runtime membership changed")
    for relative, expected in manifest["files"].items():
        path = bundle / relative
        if (Path(relative).is_absolute() or not path.resolve().is_relative_to(bundle)
                or not path.is_file() or path.stat().st_size != expected["bytes"] or sha(path) != expected["sha256"]
                or path.stat().st_mode & 0o777 != expected["mode"]
                or (os.readlink(path) if path.is_symlink() else None) != expected["symlink"]):
            raise ValueError("A restored runtime member changed: " + relative)
    if sha(bundle / "workload.json") != WORKLOAD_SHA:
        raise ValueError("Wrong fixed workload")
    initializer = bundle / "apps/postgresql/korda_before"
    build = json.loads((initializer / "build-receipt.json").read_text())
    if build["exit_code"] != 0 or sha(initializer / "out/DbPerf.Korda") != build["executable_sha256"]:
        raise ValueError("The archived dataset initializer was not a successful exact AOT build")
    for name, expected in build["sources"].items():
        if sha(initializer / name) != expected:
            raise ValueError("Archived initializer source differs: " + name)
    if sha(initializer / "Dataset.cs") != sha(ROOT / "benchmarks/db-perf/managed/Korda/Dataset.cs"):
        raise ValueError("Do not reuse an initializer with a different fixture source")
    for name, expected in build["native_images"].items():
        if sha(initializer / "out" / name) != expected:
            raise ValueError("Archived initializer native image differs")
    return bundle, {"archive_sha256": RUNTIME_SHA, "inner_archive_sha256": preparation["sha256"],
        "bundle_manifest_sha256": BUNDLE_SHA, "workload_sha256": WORKLOAD_SHA,
        "initializer_build_receipt_sha256": sha(initializer / "build-receipt.json"),
        "initializer_executable_sha256": build["executable_sha256"],
        "initializer_native_images": build["native_images"],
        "initializer_dataset_source_sha256": sha(initializer / "Dataset.cs"),
        "postgres_binary_sha256": sha(bundle / "postgres/bin/postgres"),
        "auto_explain_sha256": sha(bundle / "auto-explain/auto_explain.so"),
        "verified_runtime_file_count": len(manifest["files"]),
        "scope": "Exact original compiled initializer and its matching PostgreSQL/auto_explain runtime, used only to recreate unchanged queries-v3 fixture. New Find consumer is built separately."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--runtime-zip", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    output, app = args.output.resolve(), args.app.resolve()
    if output.exists(): raise ValueError("Use a fresh output directory; interrupted attempts are immutable")
    build_path = app / "build-receipt.json"
    build = json.loads(build_path.read_text())
    if build.get("exit_code") != 0 or build.get("validation_error") or sha(app / "out/FindLayers") != build["executable_sha256"]:
        raise ValueError("The exact independent Native AOT consumer did not build successfully")
    for name, expected in build["sources"].items():
        if sha(app / name) != expected: raise ValueError("Copied consumer source changed: " + name)
    for item in build["packages"].values():
        if sha(item["path"]) != item["sha256"]: raise ValueError("Actual restored archive changed")
    output.mkdir(parents=True)
    state = {"schema": "DB-Perf-01.find-sql-layer-run.v1", "status": "started", "started_utc": utc(),
             "runner_sha256": sha(__file__), "contracts_sha256": sha(HERE / "contracts.py"),
             "validator_sha256": sha(HERE / "analyze.py"), "app": str(app), "build_receipt_sha256": sha(build_path),
             "affinity": sorted(os.sched_getaffinity(0)), "runs": [], "long_segments_credited": 0,
             "scope": "Nine new uninstrumented diagnostic processes and three separate auto_explain processes. No formal performance acceptance or campaign seconds."}
    os.environ.pop("DOTNET_PROCESSOR_COUNT", None)
    state["runtime_DOTNET_PROCESSOR_COUNT"] = os.environ.get("DOTNET_PROCESSOR_COUNT")
    state["environment"] = {"kernel": platform.uname()._asdict(), "python": platform.python_version(),
                            "os_release": Path("/etc/os-release").read_text(),
                            "runtime_tuning": {name: value for name, value in os.environ.items()
                                               if name.startswith(("DOTNET_", "COMPlus_"))}}
    try:
        source_copy = output / "diagnostic-source"
        source_copy.mkdir()
        state["diagnostic_sources"] = {}
        for path in sorted(HERE.iterdir()):
            if path.is_file():
                with (source_copy / path.name).open("xb") as stream: stream.write(path.read_bytes())
                state["diagnostic_sources"][path.name] = sha(path)
        bundle, runtime = restore_runtime(args.runtime_zip, output)
        state["runtime"] = runtime
        contract = read_contract(bundle / "workload.json")
        if contract != build["contract"]: raise ValueError("Build and runtime fixture/SQL contracts differ")
        observer, state["observer"] = load_frozen_module("db_perf_find_observer", "benchmarks/db-perf/run.py", output)
        collector, state["collector"] = load_frozen_module("db_perf_find_collector", "benchmarks/db-perf/plan_diagnostics.py", output)
        os.environ["LD_LIBRARY_PATH"] = str(bundle / "postgres/lib") + ":" + str(bundle / "native")
        initializer = bundle / "apps/postgresql/korda_before/out/DbPerf.Korda"
        schedule = [("timing", repeat, lane) for repeat, lanes in enumerate(ROUND_ORDER) for lane in lanes]
        schedule += [("plan", 0, lane) for lane in LANES]
        write_json(output / "schedule.json", {"created_utc": utc(), "schedule": schedule,
            "warmup_and_operations": {"timing": [1024, 8192], "plan": [8, 8]},
            "post_measurement_full_target_queries_per_process": 1,
            "runtime_identity": runtime, "build_receipt_sha256": sha(build_path),
            "instrumented_timings_combined_with_uninstrumented": False})
        for index, (phase, repeat, lane_name) in enumerate(schedule):
            folder = output / f"{index:02d}-{phase}-{lane_name}-r{repeat}"
            setup, measurement = folder / "setup", folder / "process"
            setup.mkdir(parents=True); measurement.mkdir()
            data = folder / "database/pgdata"
            data.parent.mkdir()
            item = {"index": index, "phase": phase, "round": repeat, "lane": lane_name,
                    "started_utc": utc(), "status": "started", "directory": str(folder)}
            state["runs"].append(item)
            try:
                seed_command = {"name": "initialize", "phase": "fixed-fixture-setup", "timeoutSeconds": 180,
                    "argv": [str(initializer), "--connection", "{connection}", "--initialize", "--tenants", "8",
                             "--orders-per-tenant", "1024", "--concurrency", "1"]}
                seed_plan = {"schemaVersion": 1, "provider": "postgresql", "postgres": {
                    "bin": str(bundle / "postgres/bin"), "data": str(data), "maxConnections": 10, "maxClientConcurrency": 1},
                    "commands": [seed_command]}
                write_json(setup / "plan.json", seed_plan)
                observer.validate_plan(seed_plan, setup)
                with observer.database(seed_plan, setup) as (connection, server_pid):
                    seed_result = observer.execute(seed_command, connection, server_pid, setup, .1, check=False)
                if not seed_result["success"]: raise ValueError("Original initializer execution failed; original evidence retained")
                initialization_lines = [line.removeprefix("DB_PERF_INITIALIZED ") for line in (setup / "initialize.stdout.log").read_text().splitlines()
                                        if line.startswith("DB_PERF_INITIALIZED ")]
                if len(initialization_lines) != 1: raise ValueError("Missing exact initializer receipt")
                initialization = json.loads(initialization_lines[0])
                expected_lines = 8 * sum(0 if i % 5 == 0 else 1 + i % 3 for i in range(1, 1025))
                if initialization != {"SchemaVersion": 1, "Provider": "PostgreSql", "Tenants": 8, "OrdersPerTenant": 1024, "Lines": expected_lines}:
                    raise ValueError("Initializer output differs from fixed seed formulas")
                write_json(setup / "initialization.json", initialization)
                warm, operations = (1024, 8192) if phase == "timing" else (8, 8)
                result_path = measurement / "result.json"
                command = {"name": "find", "phase": phase, "timeoutSeconds": 300,
                    "argv": [str(app / "out/FindLayers"), "{connection}", lane_name, phase, str(result_path), str(warm), str(operations)]}
                settings = dict(seed_plan["postgres"])
                if phase == "plan":
                    settings["queryPlans"] = {"library": str(bundle / "auto-explain/auto_explain.so"), "sha256": runtime["auto_explain_sha256"]}
                plan = {"schemaVersion": 1, "provider": "postgresql", "postgres": settings, "commands": [command]}
                write_json(measurement / "plan.json", plan)
                observer.validate_plan(plan, measurement)
                with observer.database(plan, measurement) as (connection, server_pid):
                    execution = observer.execute(command, connection, server_pid, measurement, .1, check=False)
                item["execution_succeeded"] = execution["success"]
                if result_path.exists(): item["raw_sha256"] = sha(result_path)
                if not execution["success"] or not result_path.exists():
                    raise ValueError("The original Find process failed; no replacement or hidden retry")
                raw = validate(result_path)
                if phase == "plan":
                    lane = next(value for value in contract["lanes"] if value["name"] == lane_name)
                    mini_manifest = {"schemaVersion": 3, "dataset": FIXTURE, "source_workload_sha256": WORKLOAD_SHA,
                        "queries": [{"name": lane_name, "expectedRows": 1, "columns": contract["columns"],
                            "sql": {"postgresql": lane["expected_server_sql"]},
                            "parameters": [{"name": p["name"], "kind": p["kind"], "value": p["value"]} for p in lane["parameters"]]}]}
                    adapter = {"actualCaseIds": [lane_name], "caseCounts": {lane_name: {
                        "started": raw["Issued"], "succeeded": raw["Succeeded"], "failed": raw["Failed"], "rows": raw["Rows"]}},
                        "started": raw["Issued"], "succeeded": raw["Succeeded"], "completed": raw["Succeeded"],
                        "failed": raw["Failed"], "rows": raw["Rows"], "warmupPerWorker": raw["WarmupSucceeded"],
                        "concurrency": 1, "emptyDriver": False, "actualSql": {lane_name: raw["ClientSql"]},
                        "original_result_sha256": sha(result_path),
                        "separate_post_measurement_queries": raw["PostMeasurementQueriesIssued"],
                        "scope": "Explicit field adapter for the existing actual-log collector. Original raw result remains unchanged; post-measurement readback is an additional observed statement, not a timed operation."}
                    write_json(measurement / "sql-contract.json", mini_manifest)
                    write_json(measurement / "collector-adapter.json", adapter)
                    evidence = collector.collect_postgres(measurement / "postgres-query-plans.json", mini_manifest, "independent_csharp_orm", adapter)
                    write_json(measurement / "query-plan-evidence.json", evidence)
                    if not evidence["passed"]: raise ValueError("Actual server SQL/parameter/plan collection failed")
                item.update(status="validated", raw_path=str(result_path), raw_sha256=sha(result_path),
                            process_id=raw["ProcessId"], started_utc_actual=raw["StartedUtc"])
                print(json.dumps({"index": index, "phase": phase, "lane": lane_name, "status": "validated"}), flush=True)
            except BaseException as error:
                item["status"] = "failed"
                item["error"] = {"type": type(error).__name__, "message": str(error)}
                raise
            finally:
                item["finished_utc"] = utc()
                write_json(output / f"checkpoint-{index:02d}.json", state)
        command = [sys.executable, "-B", str(HERE / "analyze.py"), "--output", str(output / "summary.json")]
        for item in state["runs"]: command += ["--input", item["raw_path"]]
        write_json(output / "analysis-command.json", {"argv": command, "analyzer_sha256": sha(HERE / "analyze.py")})
        with (output / "analysis.log").open("x") as log:
            completed = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, cwd=output)
        if completed.returncode: raise ValueError("Final independent analysis rejected collected evidence")
        state["summary_sha256"] = sha(output / "summary.json")
        state["status"] = "validated"
    except BaseException as error:
        state["status"] = "failed"
        state["error"] = {"type": type(error).__name__, "message": str(error)}
        raise
    finally:
        state["finished_utc"] = utc()
        write_json(output / "receipt.json", state)
    print(json.dumps({"status": state["status"], "runs": len(state["runs"]), "summary_sha256": state["summary_sha256"]}))


if __name__ == "__main__":
    main()
