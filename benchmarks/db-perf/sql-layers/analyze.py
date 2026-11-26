#!/usr/bin/env python3
"""Validate full Order results, raw counters and separate SQL/plan instrumentation."""
import argparse
from datetime import datetime
from decimal import Decimal
import json
import math
from pathlib import Path

from contracts import LANES, WORKLOAD_SHA, combined_digest, expected_row_digest, sha, write_json


def require(condition, message):
    if not condition: raise ValueError(message)


def validate(path, verify_local=True):
    raw = json.loads(Path(path).read_text())
    require(raw["Schema"] == "DB-Perf-01.find-sql-layer.v1" and raw["Passed"] is True and raw["NativeAot"] is True,
            "Missing successful standalone Native AOT result")
    require(raw["Error"] is None and raw["CleanupError"] is None and raw["ConnectionDisposed"] and raw["PoolsCleared"],
            "Execution or cleanup failed")
    require(raw["Lane"] in LANES and raw["Phase"] in ("timing", "plan"), "Unknown lane/phase")
    warm, operations = (1024, 8192) if raw["Phase"] == "timing" else (8, 8)
    require(raw["WarmupPlanned"] == raw["WarmupIssued"] == raw["WarmupSucceeded"] == warm and raw["WarmupFailed"] == 0,
            "Warmup accounting changed or contains failures")
    require(raw["Planned"] == raw["Issued"] == raw["Succeeded"] == raw["Rows"] == operations
            and raw["Failed"] == raw["Retries"] == 0, "Operation/row accounting changed or contains failures")
    require(raw["PostMeasurementQueriesIssued"] == 1 and raw["PersistentTargetVerified"], "Missing separate full target readback")
    require(raw["LogicalConnections"] == 1 and raw["MaxAutoPrepare"] == 0 and raw["CommandCaching"] is False,
            "Connection/command preparation contract changed")
    require(raw["WorkloadSha256"] == WORKLOAD_SHA and raw["ExactFrameworkBytes"] is None, "Wrong fixture or invented framework attribution")
    require(type(raw["ProcessId"]) is int and raw["ProcessId"] > 0 and type(raw["ServerBackendPid"]) is int and raw["ServerBackendPid"] > 0,
            "Actual process/backend identities are required")
    start = datetime.fromisoformat(raw["StartedUtc"].replace("Z", "+00:00"))
    end = datetime.fromisoformat(raw["FinishedUtc"].replace("Z", "+00:00"))
    require(start.tzinfo is not None and end.tzinfo is not None and start <= end, "Invalid process interval")
    before, after = raw["Before"], raw["After"]
    for key in ("AllocatedBytes", "Gen0", "Gen1", "Gen2", "Timestamp"):
        require(type(before[key]) is int and type(after[key]) is int and 0 <= before[key] <= after[key], "Invalid raw cumulative counter: " + key)
    for key in ("UserCpuSeconds", "SystemCpuSeconds"):
        require(math.isfinite(before[key]) and math.isfinite(after[key]) and 0 <= before[key] <= after[key], "Invalid CPU endpoint")
    require(0 <= raw["ObservedIncompleteReturns"] <= operations and 0 <= raw["CrossThreadAwaits"] <= operations, "Invalid async observation")
    # Preserve the original full array, but only successful observations can
    # participate in any quantile calculation. Failed-tail zeros are never data.
    valid_latencies = raw["LatencyTicks"][:raw["Succeeded"]]
    require(len(raw["LatencyTicks"]) == raw["Planned"] and len(valid_latencies) == operations
            and all(type(ticks) is int and ticks > 0 for ticks in valid_latencies), "Invalid successful latency range")
    build_path = Path(raw["BuildReceiptPath"])
    build = json.loads(build_path.read_text())
    contract = build["contract"]
    lane = next(item for item in contract["lanes"] if item["name"] == raw["Lane"])
    require(raw["ClientSql"] == lane["client_sql"] and raw["ExpectedServerSql"] == lane["expected_server_sql"], "SQL layer changed")
    expected_parameters = [{"Ordinal": p["ordinal"], "Name": p["name"], "ClrType": p["clr_type"], "Value": p["value"]} for p in lane["parameters"]]
    require(raw["Parameters"] == expected_parameters, "Ordered typed parameter contract changed")
    expected = dict(contract["initial_order"])
    cents = expected.pop("TotalCents")
    actual = dict(raw["Expected"])
    require(Decimal(str(actual.pop("Total"))) * 100 == cents and actual == expected, "Expected full typed Order changed")
    row_digest = expected_row_digest(contract["initial_order"])
    require(raw["ExpectedRowDigest"] == row_digest and raw["Digest"] == combined_digest(row_digest, operations),
            "Full nine-field/decimal/typed-JSON consumption digest changed")
    if verify_local:
        require(sha(build_path) == raw["BuildReceiptSha256"], "Build receipt bytes changed")
        require(build["exit_code"] == 0 and not build.get("validation_error") and build["project_references"] == 0, "Independent AOT build was not validated")
        require(build["executable_sha256"] == raw["ExecutableSha256"] == sha(build_path.parent / "out/FindLayers"), "Actual ELF differs")
        require(build["sources"] == raw["ConsumerSources"], "Source identities changed")
        for name, expected_sha in build["sources"].items():
            require(sha(build_path.parent / name) == expected_sha, "Copied consumer source changed: " + name)
        for item in build["packages"].values():
            require(sha(item["path"]) == item["sha256"], "Actual restored package changed")
    return raw


def quantile(values, fraction):
    ordered = sorted(values)
    return ordered[max(0, math.ceil(len(ordered) * fraction) - 1)]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, action="append", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    groups = {(phase, lane): [] for phase in ("timing", "plan") for lane in LANES}
    paths, executions, binaries = set(), set(), set()
    for path in args.input:
        path = path.resolve()
        require(path not in paths, "Repeated original process file")
        paths.add(path)
        raw = validate(path)
        identity = raw["ProcessId"], raw["StartedUtc"], raw["ExecutableSha256"]
        require(identity not in executions, "One execution cannot be counted twice")
        executions.add(identity)
        binaries.add((raw["ExecutableSha256"], raw["BuildReceiptSha256"]))
        groups[(raw["Phase"], raw["Lane"])].append((path, raw))
    require(len(binaries) == 1, "All lanes must use the same actual consumer ELF and build")
    cells = []
    for (phase, lane), records in groups.items():
        require(len(records) == (3 if phase == "timing" else 1), "Missing frozen independent processes: " + phase + "/" + lane)
        cell = {"phase": phase, "lane": lane, "processes": len(records),
                "raw": [{"path": str(path), "sha256": sha(path)} for path, _ in records],
                "formal_performance_acceptance": False, "long_segments_credited": 0}
        if phase == "timing":
            count = sum(raw["Succeeded"] for _, raw in records)
            allocated = sum(raw["After"]["AllocatedBytes"] - raw["Before"]["AllocatedBytes"] for _, raw in records)
            elapsed = sum((raw["After"]["Timestamp"] - raw["Before"]["Timestamp"]) / raw["StopwatchFrequency"] for _, raw in records)
            user = sum(raw["After"]["UserCpuSeconds"] - raw["Before"]["UserCpuSeconds"] for _, raw in records)
            system = sum(raw["After"]["SystemCpuSeconds"] - raw["Before"]["SystemCpuSeconds"] for _, raw in records)
            latencies = [ticks / raw["StopwatchFrequency"] * 1000 for _, raw in records
                         for ticks in raw["LatencyTicks"][:raw["Succeeded"]]]
            cell.update(successful_operations=count, rows=count, raw_allocated_bytes=allocated,
                bytes_per_operation=allocated/count, elapsed_query_window_seconds=elapsed,
                diagnostic_success_ops_per_second=count/elapsed,
                user_cpu_seconds=user, system_cpu_seconds=system, cpu_seconds_per_success=(user+system)/count,
                gc_counts=[sum(raw["After"][f"Gen{g}"]-raw["Before"][f"Gen{g}"] for _, raw in records) for g in range(3)],
                latency_ms={"p50": quantile(latencies,.50), "p95": quantile(latencies,.95), "p99": quantile(latencies,.99)},
                exact_framework_bytes=None)
        else:
            path, raw = records[0]
            evidence_path = path.parent / "query-plan-evidence.json"
            evidence = json.loads(evidence_path.read_text())
            require(evidence["passed"] and not evidence["errors"] and not evidence["timingComparisonEligible"], "Actual server plan evidence failed")
            leg, = evidence["legs"]
            observed = raw["WarmupSucceeded"] + raw["Succeeded"] + raw["PostMeasurementQueriesIssued"]
            require(len(leg["statementIndexes"]) == len(leg["planIndexes"]) == observed, "Actual server business query count changed")
            parameters = [str(p["Value"]) for p in raw["Parameters"]]
            for index in leg["statementIndexes"]:
                item = evidence["statements"][index]
                require(item["sql"] == raw["ExpectedServerSql"] and item["parameters"] == parameters
                        and item["pid"] == raw["ServerBackendPid"], "Actual server SQL/parameter/connection mismatch")
            for index in leg["planIndexes"]:
                item = evidence["plans"][index]
                require(item["autoExplain"]["Query Text"] == raw["ExpectedServerSql"] and item["parameters"] == parameters
                        and item["pid"] == raw["ServerBackendPid"], "Actual auto_explain SQL/parameter/connection mismatch")
            require(evidence["serverParameterOids"] is None and evidence["genericCustomPlanCounts"] is None, "Unobserved cache/OID inference")
            cell.update(actual_business_statements=observed, actual_business_plans=observed,
                        plan_evidence={"path": str(evidence_path), "sha256": sha(evidence_path)},
                        diagnostic_timing_excluded=True, server_parameter_oids=None, generic_custom_plan_counts=None)
        cells.append(cell)
    write_json(args.output, {"schema": "DB-Perf-01.find-sql-layer-summary.v1", "cells": cells,
        "scope": "Three SQL variants within the same Dapper.AOT/Npgsql consumer. Independent diagnostic timings and separately instrumented actual plans; not a replacement for original ORM or formal performance results.",
        "all_original_local_bytes_verified": True, "instrumented_timings_combined_with_uninstrumented": False,
        "exact_framework_bytes": None, "long_segments_credited": 0})
    print(json.dumps({"status": "validated", "output": str(args.output), "sha256": sha(args.output)}))


if __name__ == "__main__":
    main()
