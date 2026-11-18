#!/usr/bin/env python3
"""Persist numeric B23 observer snapshots without exporting runtime content.

This is an analysis-only projection of a completed cohorts report. It neither
executes a target nor changes its validity, workload, or comparison decisions.
"""

import argparse
import hashlib
import json
import math
from pathlib import Path
import re


IDENTIFIER = re.compile(r"^[A-Za-z0-9_.-]+$")
MODES = {
    "release-disabled", "debug-default", "debug-local", "sampled", "full",
    "meter-only", "log-only", "exporter-saturation", "exporter-failure",
}
SCALARS = (
    "activities", "allocatedBytes", "cpuTimeSpanTicks", "exportFailures",
    "gen0Collections", "gen1Collections", "gen2Collections",
    "heapCommittedBytes", "heapLiveBytes", "logCalls", "logExports",
    "measurements", "observerFailures", "workingSetBytes",
)
COUNTERS = (
    "activities", "allocatedBytes", "cpuTimeSpanTicks", "exportFailures",
    "gen0Collections", "gen1Collections", "gen2Collections", "logCalls",
    "logExports", "measurements", "observerFailures",
)
METRICS = (
    "nativeweave.server.callback.duration.max",
    "nativeweave.server.callback.duration.mean",
    "nativeweave.server.callbacks.failed",
    "nativeweave.server.diagnostics.dropped",
    "nativeweave.server.diagnostics.queue.bytes",
    "nativeweave.server.diagnostics.queue.entries",
    "nativeweave.server.payload.managed.bytes",
    "nativeweave.server.payload.native.bytes",
    "nativeweave.server.request.callback_retirement.duration",
    "nativeweave.server.request.duration",
    "nativeweave.server.requests.active",
    "nativeweave.server.requests.queued",
    "nativeweave.server.requests.rejected",
    "nativeweave.server.requests.retiring",
)
METRIC_COUNTERS = (
    "nativeweave.server.callbacks.failed",
    "nativeweave.server.diagnostics.dropped",
    "nativeweave.server.requests.rejected",
)


def sha256(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def identifier(value):
    if not isinstance(value, str) or not IDENTIFIER.fullmatch(value):
        raise ValueError("Invalid fixed observation identifier")
    return value


def number(value):
    if value is None:
        return None
    if type(value) not in (int, float) or not math.isfinite(value):
        raise ValueError("Expected a finite numeric observation or null")
    return value


def snapshot(value):
    if value is None:
        return None
    if value.get("configuration") not in ("Release", "Debug"):
        raise ValueError("Unknown application build configuration")
    if value.get("mode") not in MODES:
        raise ValueError("Unknown declared observer mode")
    result = {key: number(value.get(key)) for key in SCALARS}
    result.update(configuration=value["configuration"], mode=value["mode"])
    for key in ("metrics", "metricPeaks"):
        values = value.get(key)
        result[key] = None if values is None else {
            name: number(values.get(name)) for name in METRICS
        }
    return result


def differences(before, after, fields):
    return {
        key: after[key] - before[key]
        if before and after and before.get(key) is not None
        and after.get(key) is not None else None
        for key in fields
    }


def project(report):
    rows = []
    for case in report["cases"]:
        value = case.get("report")
        if value is None:
            continue
        source_sha256 = value["source_summary"]["sha256"]
        if not re.fullmatch(r"[0-9a-f]{64}", source_sha256):
            raise ValueError("Invalid source summary digest")
        for row in value["independent_process_observations"]:
            if type(row["valid"]) is not bool:
                raise ValueError("Observation validity must be a boolean")
            costs = row.get("application_costs") or {}
            before = snapshot(costs.get("probe_before"))
            after = snapshot(costs.get("probe_after"))
            rows.append({
                "case": identifier(case["case"]),
                "phase": identifier(case["phase"]),
                "route": identifier(row["route"]),
                "repetition": number(row["repetition"]),
                "observation_valid": row["valid"],
                "source_summary_sha256": source_sha256,
                "availability": "measured" if before and after else
                    "partial" if before or after else "unavailable",
                "before": before,
                "after": after,
                "counter_deltas": differences(before, after, COUNTERS),
                "metric_counter_deltas": differences(
                    before.get("metrics") if before else None,
                    after.get("metrics") if after else None,
                    METRIC_COUNTERS),
            })
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = json.loads(args.report.read_text())
    source = report["source_cohorts"]
    if sha256(source["path"]) != source["sha256"]:
        raise ValueError("Cohorts changed after report generation")
    result = {
        "schema_version": 1,
        "task": "B23",
        "platform": "linux-x64",
        "source_report_sha256": sha256(args.report),
        "source_cohorts_sha256": source["sha256"],
        "exporter_sha256": sha256(__file__),
        "rows": project(report),
        "scope": {
            "validity": "Original process validity is preserved; partial or invalid rows are not performance acceptance.",
            "units": "Duration metrics use seconds. cpuTimeSpanTicks uses 100ns TimeSpan ticks. Heap, RSS, allocation and payload metrics use bytes.",
            "boundaries": "Before/after observer snapshots bracket the declared measurement and settling boundary; overhead and concurrent allocations are included.",
            "peaks": "metricPeaks are sampled cumulative peaks, including setup; short peaks between samples may be missed.",
            "disabled_modes": "No listener means unavailable metrics, not measured zero callback or allocation cost.",
            "native_memory": "Native payload gauges do not measure whole native allocator memory, copying, FFI count or process RSS bounds.",
            "content": "Fixed identifiers, hashes and allowlisted numeric observations only; no config, key, database, HTTP content, free-form log or binary archive.",
        },
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("x") as stream:
        stream.write(json.dumps(result, indent=2, sort_keys=True, allow_nan=False) + "\n")
    print(json.dumps({"output": str(args.output), "rows": len(result["rows"]),
                      "sha256": sha256(args.output)}))


if __name__ == "__main__":
    main()
