#!/usr/bin/env python3
"""Summarize raw local query-phase receipts; this does not qualify an environment."""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path


def quantiles(values, scale):
    ordered = sorted(values)
    if not ordered or any(value < 0 for value in ordered):
        raise ValueError("Missing or negative phase durations")
    return {
        "meanMicroseconds": sum(ordered) / len(ordered) * scale,
        "p50Microseconds": ordered[math.ceil(len(ordered) * .50) - 1] * scale,
        "p95Microseconds": ordered[math.ceil(len(ordered) * .95) - 1] * scale,
        "p99Microseconds": ordered[math.ceil(len(ordered) * .99) - 1] * scale,
        "method": "nearest-rank observed durations; no trimming or subtraction",
    }


def summarize(path):
    raw = path.read_bytes()
    data = json.loads(raw)
    if not data["NativeAot"] or data["StopwatchFrequency"] <= 0:
        raise ValueError("A real Native AOT receipt with a clock frequency is required")
    owners = ("Sources", "Sessions", "Operations", "Cursors", "Buffers", "Owners")
    if any(data["Drained"][name] != 0 for name in owners):
        raise ValueError("Native ownership did not drain")
    scale = 1_000_000 / data["StopwatchFrequency"]
    result = {name: data.get(name) for name in (
        "SchemaVersion", "Provider", "DatabaseVersion", "PackageVersion", "ExecutableSha256",
        "NativeSha256", "Sql", "RawTemplate", "ParametersAndResults", "ResultRows", "Scope")}
    result_rows = data.get("ResultRows", 100)
    if result_rows not in (1, 100, 1000):
        raise ValueError("Unexpected result shape")
    result.update(input=str(path), inputSha256=hashlib.sha256(raw).hexdigest(),
                  preparationMicroseconds=data["PreparationTicks"] * scale,
                  firstExecutionRouteOrder=["fluent", "prepared", "host-sql"],
                  firstExecutions=data["FirstExecutions"], routes=[])
    phases = ("ConstructTicks", "SubmitToFirstRowTicks", "ReadCompleteTicks", "ReleaseTicks")
    for route in ("fluent", "prepared", "host-sql"):
        windows = [window for window in data["Windows"] if window["Route"] == route]
        samples = [sample for window in windows for sample in window["Samples"]]
        operations = sum(window["Operations"] for window in windows)
        if not windows or operations != len(samples) or any(sample["Rows"] != result_rows for sample in samples):
            raise ValueError("Incomplete same-result operation accounting")
        elapsed = sum(window["FinishedTicks"] - window["StartedTicks"] for window in windows)
        allocated = sum(window["AllocatedAfter"] - window["AllocatedBefore"] for window in windows)
        cpu_ticks = sum(window["CpuAfterTicks"] - window["CpuBeforeTicks"] for window in windows)
        if elapsed <= 0 or allocated < 0 or cpu_ticks < 0:
            raise ValueError("Invalid cumulative measurement counters")
        result["routes"].append({
            "route": route, "windows": len(windows), "operations": operations,
            "allocatedBytes": allocated, "bytesPerOperation": allocated / operations,
            "gc": {name: sum(window[name] for window in windows) for name in ("Gen0", "Gen1", "Gen2")},
            "processCpuSeconds": cpu_ticks / 10_000_000,
            "processCpuMicrosecondsPerOperation": cpu_ticks / 10 / operations,
            "windowSecondsIncludingVerification": elapsed / data["StopwatchFrequency"],
            "operationsPerSecondIncludingVerification": operations * data["StopwatchFrequency"] / elapsed,
            "maxObservedWindowRssBytes": max(window["ResidentBytes"] for window in windows),
            "processLifetimePeakRssBytes": max(window["PeakResidentBytes"] for window in windows),
            "phases": {phase: quantiles([sample[phase] for sample in samples], scale) for phase in phases},
            "fourPhaseTotal": quantiles([sum(sample[phase] for phase in phases) for sample in samples], scale),
            "sqlExecutions": sum(window["NativeAfter"]["SqlExecutions"] - window["NativeBefore"]["SqlExecutions"] for window in windows),
            "protocolSubmissions": sum(window["NativeAfter"]["ProtocolSubmissions"] - window["NativeBefore"]["ProtocolSubmissions"] for window in windows),
        })
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("receipts", nargs="+", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if args.output.exists():
        raise ValueError("Use a fresh summary output; do not overwrite earlier observations")
    summaries = [summarize(path) for path in args.receipts]
    args.output.write_text(json.dumps({
        "scope": "Computed summaries only. Source receipts retain environment and measurement eligibility; no acceptance is inferred.",
        "summarizerSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "inputs": summaries,
    }, indent=2) + "\n")


if __name__ == "__main__":
    main()
