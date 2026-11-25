#!/usr/bin/env python3
"""Recover and independently reparse existing native plan evidence; never execute a workload."""
import argparse
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tarfile
import zipfile

HERE = Path(__file__).resolve().parent
PROFILE_SHA = "9f23034ed6784fab5f8ae59ede4a8c1ed1085d44e57e47b858adb802cf304595"
RUNTIME_SHA = "aecbd4e949e333399704ba141f7d52e9225323e658f6180262355831b5aec7e8"
WORKLOAD_SHA = "76443b1c88bf011f57b3c9819dc85a8bedb5376121d3ef2670304f96b6d2aae2"
COLLECTOR_SHA = "a65a5bb9b91070a7e15cc161ca6c932ca0f333d20ddcb94fd233deb1457595f2"


def digest(data):
    return hashlib.sha256(data).hexdigest()


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("xb") as stream:
        stream.write(data)


def write_json(path, value):
    write(path, (json.dumps(value, indent=2) + "\n").encode())


def plan_shape(node):
    keys = ("Node Type", "Index Name", "Index Cond", "Filter", "Sort Key", "Recheck Cond")
    return {**{key: node[key] for key in keys if key in node},
            "Plans": [plan_shape(child) for child in node.get("Plans", [])]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profiles", type=Path, required=True)
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    if output.exists():
        raise ValueError("Use a fresh recovery directory; never replace original evidence")
    if sha(args.profiles) != PROFILE_SHA or sha(args.runtime) != RUNTIME_SHA:
        raise ValueError("The archived GitHub artifact bytes changed")
    collector_path = HERE.parent / "plan_diagnostics.py"
    if sha(collector_path) != COLLECTOR_SHA:
        raise ValueError("Use the exact original plan collector for this recovery")
    spec = importlib.util.spec_from_file_location("db_perf_recovered_plan_collector", collector_path)
    collector = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = collector
    spec.loader.exec_module(collector)
    selected = {"bundle/bundle.json", "bundle/workload.json", "bundle/native/db-perf-native",
                "bundle/native/build-receipt.json", "bundle/apps/postgresql/korda_before/Dataset.cs",
                "bundle/apps/postgresql/korda_before/build-receipt.json"}
    members = {}
    with zipfile.ZipFile(args.runtime) as archive:
        preparation_bytes = archive.read("preparation.json")
        preparation = json.loads(preparation_bytes)
        with archive.open("runtime-bundle.tar.gz") as stream:
            if hashlib.file_digest(stream, "sha256").hexdigest() != preparation["sha256"]:
                raise ValueError("The runtime tar archive differs from its preparation receipt")
        with archive.open("runtime-bundle.tar.gz") as stream:
            with tarfile.open(fileobj=stream, mode="r|gz") as tar:
                for member in tar:
                    if member.isfile() and member.name in selected:
                        members[member.name] = tar.extractfile(member).read()
    if set(members) != selected or digest(members["bundle/bundle.json"]) != preparation["bundleManifestSha256"]:
        raise ValueError("Runtime selected files or bundle identity are incomplete")
    bundle = json.loads(members["bundle/bundle.json"])
    for name, data in members.items():
        if name != "bundle/bundle.json":
            entry = bundle["files"][name.removeprefix("bundle/")]
            if digest(data) != entry["sha256"] or len(data) != entry["bytes"]:
                raise ValueError("Runtime entry changed: " + name)
    workload = json.loads(members["bundle/workload.json"])
    if digest(members["bundle/workload.json"]) != WORKLOAD_SHA or workload["dataset"] != {
            "tenants": 8, "ordersPerTenant": 1024, "customers": 256, "requestedRows": 32, "batchRows": 32}:
        raise ValueError("The original fixture contract changed")
    native_sha = digest(members["bundle/native/db-perf-native"])
    build_sha = digest(members["bundle/native/build-receipt.json"])
    native_build = json.loads(members["bundle/native/build-receipt.json"])
    if native_build["outputs"]["db-perf-native"] != native_sha:
        raise ValueError("The actual archived ELF differs from its native build receipt")
    for name, data in members.items():
        write(output / "runtime-proof" / name.removeprefix("bundle/"), data)
    write(output / "runtime-proof/preparation.json", preparation_bytes)
    result = {"schema": "DB-Perf-01.native-plan-recovery.v1",
              "recovered_utc": datetime.now(timezone.utc).isoformat(),
              "scope": "Recover existing successful actual auto_explain traces only. Zero database/consumer executions and zero formal timing or long-segment credit.",
              "profiles_archive_sha256": PROFILE_SHA, "runtime_archive_sha256": RUNTIME_SHA,
              "runtime_inner_archive_sha256": preparation["sha256"],
              "runtime_bundle_manifest_sha256": preparation["bundleManifestSha256"],
              "workload_sha256": WORKLOAD_SHA, "native_executable_sha256": native_sha,
              "native_build_receipt_sha256": build_sha, "collector_sha256": COLLECTOR_SHA,
              "recovery_source_sha256": sha(__file__), "layers": []}
    with zipfile.ZipFile(args.profiles) as archive:
        for physical in ("per-operation", "reuse"):
            name = "native-plan-" + physical + "-drain-only"
            prefix = "db-perf-cpu/database-diagnostics/runs/postgresql/" + name + "/attempt-00/"
            files = {entry.filename.removeprefix(prefix): archive.read(entry.filename)
                     for entry in archive.infolist() if entry.filename.startswith(prefix) and not entry.is_dir()}
            if len(files) != 14 or any("/" in item or item in (".", "..") for item in files):
                raise ValueError("Unexpected original plan evidence membership")
            for filename, data in files.items():
                write(output / "originals" / name / filename, data)
            raw = json.loads(files["result.json"])
            original = json.loads(files["query-plan-evidence.json"])
            normalized = json.loads(files["normalized.json"])
            command = json.loads(files["client.command.json"])
            owner = json.loads(files["postgres-owner.json"])
            required = {"provider": "postgresql", "route": "direct-native-cpp", "concurrency": 1,
                        "mode": "prepared", "physicalPreparation": physical, "protocolConfirmation": "drain-only",
                        "warmupPerWorker": 350, "planned": 35, "succeeded": 35, "failed": 0,
                        "oracleFailures": 0, "ownersReturnedToZero": True,
                        "executableSha256": native_sha, "buildReceiptSha256": build_sha,
                        "manifestSha256": WORKLOAD_SHA, "manifestSha256After": WORKLOAD_SHA}
            if any(raw.get(key) != value for key, value in required.items()) or raw["firstOracleFailure"] is not None:
                raise ValueError("Native execution identity, correctness or cleanup changed")
            if len(raw["caseCounts"]) != 35 or any(c["succeeded"] != 1 or c["failed"] for c in raw["caseCounts"].values()):
                raise ValueError("Original 35-case oracle coverage is incomplete")
            if normalized["rawSha256"] != digest(files["result.json"]) or not normalized["sloPassed"] or normalized["timingComparisonEligible"]:
                raise ValueError("Original normalization no longer matches raw evidence")
            if not command["success"] or command["exitCode"] or command["timedOut"] or command["observerErrors"]:
                raise ValueError("The original client observer did not complete successfully")
            if owner["exitCode"] or any(key in owner for key in ("shutdownError", "error", "finalSampleError")):
                raise ValueError("The original PostgreSQL owner did not retire cleanly")
            if not original["passed"] or original["errors"] or original["collectorSha256"] != COLLECTOR_SHA:
                raise ValueError("The original actual-plan validation did not pass")
            log_name = Path(original["logIdentity"]["path"]).name
            if digest(files[log_name]) != original["logIdentity"]["sha256"]:
                raise ValueError("The original server log bytes changed")
            reparsed = collector.collect_postgres(output / "originals" / name / log_name,
                                                 workload, "direct_cpp", raw)
            if not reparsed["passed"] or reparsed["errors"]:
                raise ValueError("Independent offline reparse rejected the original logs")
            for key in ("plans", "statements", "legs", "signatureCoverage", "caseCounts",
                        "consumerSqlChecks", "completeCaseCount", "completeLegCount"):
                if reparsed[key] != original[key]:
                    raise ValueError("Independent reparse differs: " + key)
            if original["serverParameterOids"] is not None or original["genericCustomPlanCounts"] is not None:
                raise ValueError("Unexpected parameter OID or plan-cache instrumentation scope")
            write_json(output / (name + "-reparsed.json"), reparsed)
            cases = []
            for leg in reparsed["legs"]:
                if leg["caseId"] not in ("range", "offset", "early-stop"):
                    continue
                if leg["expectedExecutionsIncludingWarmup"] != 11 or len(leg["statementIndexes"]) != 11 or len(leg["planIndexes"]) != 11:
                    raise ValueError("Expected eleven actual executions of the target case")
                observations, shapes = [], {}
                for ordinal, index in enumerate(leg["planIndexes"], 1):
                    plan = reparsed["plans"][index]
                    shape = plan_shape(plan["autoExplain"]["Plan"])
                    shape_key = json.dumps(shape, sort_keys=True, separators=(",", ":"))
                    shapes.setdefault(shape_key, []).append(ordinal)
                    observations.append({"ordinal": ordinal, "original_plan_index": index,
                        "server_log_line": plan["line"], "pid": plan["pid"], "session_id": plan["sessionId"],
                        "actual_parameters": plan["parameters"], "actual_auto_explain": plan["autoExplain"]})
                cases.append({"case": leg["caseId"], "sql": leg["sql"], "typed_parameter_contract": leg["parameters"],
                    "typed_parameter_scope": "Manifest client types; actual server parameter values are separately retained. No raw server OID observation.",
                    "actual_statement_count": 11, "actual_plan_count": 11, "observations": observations,
                    "observed_shapes": [{"occurrence_ordinals": ordinals, "shape": json.loads(shape)} for shape, ordinals in shapes.items()]})
            result["layers"].append({"physical_preparation": physical, "protocol_confirmation": "drain-only",
                "original_started_utc": raw["startedUtc"], "original_finished_utc": raw["finishedUtc"],
                "raw_sha256": digest(files["result.json"]), "original_plan_evidence_sha256": digest(files["query-plan-evidence.json"]),
                "original_files": {filename: {"sha256": digest(data), "bytes": len(data)} for filename, data in files.items()},
                "all_35_cases_succeeded": True, "oracle_failures": 0, "native_owners_returned_to_zero": True,
                "postgres_exit_code": 0, "actual_plan_evidence_reparsed": True,
                "server_parameter_oids": None, "generic_custom_plan_counts": None,
                "generic_custom_scope": original["genericCustomPlanCountStatus"], "cases": cases})
    left, right = result["layers"]
    for a, b in zip(left["cases"], right["cases"], strict=True):
        if (a["case"], a["sql"], a["typed_parameter_contract"]) != (b["case"], b["sql"], b["typed_parameter_contract"]):
            raise ValueError("Layer comparison changed SQL or typed parameters")
    result["status"] = "verified_original_evidence_recovered"
    write_json(output / "recovery.json", result)
    print(json.dumps({"status": result["status"], "output": str(output), "sha256": sha(output / "recovery.json"),
                      "executions_per_case_per_layer": 11, "new_consumer_executions": 0}))


if __name__ == "__main__":
    main()
