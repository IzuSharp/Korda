#!/usr/bin/env python3
"""Validate buffered-result counters and report an explicitly unresolved allocation remainder."""
import argparse
from collections import defaultdict
from datetime import datetime
from fractions import Fraction
import hashlib
import json
from pathlib import Path

SCHEMA = "DB-Perf-01.business-result-control.v1"
MASK = (1 << 64) - 1


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def result_digest():
    digest = 14695981039346656037
    for number in range(1, 101):
        digest = ((digest ^ number) * 1099511628211) & MASK
        text = None if number % 5 == 0 else "note-Ω-" + str(number % 16)
        for char in [255] if text is None else map(ord, text):
            digest = ((digest ^ char) * 1099511628211) & MASK
    return digest


def combined_digest(operations):
    digest = 14695981039346656037
    one = result_digest()
    for _ in range(operations):
        digest = ((digest ^ one) * 1099511628211) & MASK
    return digest


def validate_window(window, synchronous):
    start, end = window["Start"], window["End"]
    for field in ("AllocatedBytes", "Gen0", "Gen1", "Gen2", "Timestamp"):
        require(type(start[field]) is int and type(end[field]) is int and 0 <= start[field] <= end[field], "Invalid raw counter: " + field)
    require(window["RawAllocatedBytes"] == end["AllocatedBytes"] - start["AllocatedBytes"], "Raw byte delta changed")
    for gen in range(3):
        require(window[f"Gen{gen}Collections"] == end[f"Gen{gen}"] - start[f"Gen{gen}"], "GC counter delta changed")
    operations = window["Succeeded"]
    require(type(operations) is int and operations > 0 and window["Planned"] == window["Issued"] == operations
            and window["Failed"] == 0 and window["Rows"] == 100 * operations and window["Error"] is None,
            "Incomplete or failed buffered-result window")
    require(window["Digest"] == combined_digest(operations), "Complete output checksum changed")
    require(window["SynchronousControl"] is synchronous and window["ExactFrameworkBytes"] is None,
            "Control scope changed or exact framework attribution was invented")
    require(0 <= window["ObservedIncompleteReturns"] <= operations and 0 <= window["CrossThreadAwaits"] <= operations,
            "Invalid asynchronous observation")
    if synchronous:
        require(start["ThreadId"] == end["ThreadId"] and window["ObservedIncompleteReturns"] == 0 and window["CrossThreadAwaits"] == 0,
                "Synchronous graph control changed threads")
        require(window["SameThreadAllocatedBytes"] == end["ThreadAllocatedBytes"] - start["ThreadAllocatedBytes"]
                and 0 < window["SameThreadAllocatedBytes"] <= window["RawAllocatedBytes"], "Invalid synchronous thread allocation")
    else:
        require(window["SameThreadAllocatedBytes"] is None, "A per-thread counter was used for an asynchronous total")


def validate(path, verify_local):
    report = json.loads(path.read_text())
    require(report["Schema"] == SCHEMA and report["Passed"] is True and report["NativeAot"] is True
            and report["DatabaseDisposed"] is True and report["Error"] is None and report["CleanupError"] is None,
            "The process did not finish with real AOT correctness and cleanup")
    require(report["FrameworkWideZeroBytes"] is False, "Unsupported whole-framework zero claim")
    require(type(report["ProcessId"]) is int and report["ProcessId"] > 0, "Missing actual process identity")
    started = datetime.fromisoformat(report["StartedUtc"].replace("Z", "+00:00"))
    finished = datetime.fromisoformat(report["FinishedUtc"].replace("Z", "+00:00"))
    require(started.tzinfo is not None and finished.tzinfo is not None and started <= finished, "Invalid process interval")
    require(report["Route"] in {"korda", "dapper"} and report["Provider"] in {"sqlite", "postgresql"}, "Unknown route/provider")
    require(len(report["EndToEnd"]) == len(report["BusinessControls"]) == 3, "Missing original window")
    graph = report["Graph"]
    chars = sum(len("note-Ω-" + str(i % 16)) for i in range(1, 101) if i % 5)
    require(graph["Dtos"] == 100 and graph["IndependentStrings"] == 80 and graph["NullStrings"] == 20
            and graph["Utf16Characters"] == chars and graph["ListCapacity"] >= 100 and graph["Digest"] == result_digest(),
            "The sampled business output graph changed")
    for window in report["EndToEnd"]:
        validate_window(window, False)
        require(window["Planned"] == report["OperationsPerWindow"], "E2E operation count changed")
    for window in report["BusinessControls"]:
        validate_window(window, True)
        require(window["Planned"] == 1024, "Business control operation count changed")
    if report["Route"] == "korda":
        require(len(report["NativeOwnersAfterDrain"]) == 8 and not any(report["NativeOwnersAfterDrain"][:6]), "Korda native owners remain live")
        require(len(report["NativeImages"]) == 1, "Missing actual Korda native image")
    else:
        require(report["NativeOwnersAfterDrain"] is None, "Uninstrumented provider owners were invented")
        require(len(report["NativeImages"]) == (1 if report["Provider"] == "sqlite" else 0), "Wrong independent driver native-image scope")
    if verify_local:
        build_path = Path(report["BuildReceiptPath"])
        require(sha(build_path) == report["BuildReceiptSha256"], "Build receipt bytes changed")
        build = json.loads(build_path.read_text())
        require(build["sources"] == report["ConsumerSources"] and build["executable_sha256"] == report["ExecutableSha256"], "Consumer binding changed")
        require(sha(build_path.parent / "out/BusinessResults") == report["ExecutableSha256"], "Actual AOT executable changed")
        require(sha(report["PrimaryPackagePath"]) == report["PrimaryPackageSha256"], "Primary package bytes changed")
        for name, digest in report["ConsumerSources"].items():
            require(sha(build_path.parent / name) == digest, "Copied consumer source changed: " + name)
        for image, digest in report["NativeImages"].items():
            require(sha(image) == digest, "Loaded native image changed")
        if report["Route"] == "korda":
            expected = {str((build_path.parent / "out" / name).resolve()): digest for name, digest in build["native_images"].items()}
            require(report["NativeImages"] == expected, "Actual Korda native image differs from the package consumer build")
    return report


def execution_identity(report):
    # PID alone can be reused by the OS; a copied receipt retains all three fields.
    return report["ProcessId"], report["StartedUtc"], report["ExecutableSha256"]


def validate_independent_group(records):
    require(len(records) >= 3, "A formal cell requires at least three independent process records")
    require(len({execution_identity(report) for _, report in records}) == len(records),
            "Copied receipts cannot count as independent process executions")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", action="append", required=True, help="label=/absolute/result.json; repeat for every independent process")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--verify-local", action="store_true", help="Rehash the original consumer, archive, executable and native paths")
    args = parser.parse_args()
    if args.output.exists():
        raise ValueError("Never replace a previous summary.")
    groups, all_sources, paths, executions = defaultdict(list), set(), set(), set()
    for item in args.input:
        label, value = item.split("=", 1)
        require(bool(label), "An explicit before/after/independent label is required")
        path = Path(value).resolve()
        require(path not in paths, "One original process cannot be counted twice")
        paths.add(path)
        report = validate(path, args.verify_local)
        identity = execution_identity(report)
        require(identity not in executions, "One original execution cannot be counted again under another file or label")
        executions.add(identity)
        all_sources.add(json.dumps(report["ConsumerSources"], sort_keys=True))
        groups[(label, report["Provider"], report["Route"])].append((path, report))
    require(len(all_sources) == 1, "Before/after/independent consumers do not share exact source/project/global inputs")
    summary = []
    for (label, provider, route), records in sorted(groups.items()):
        validate_independent_group(records)
        identities = {(r["PrimaryPackageSha256"], r["ExecutableSha256"], r["SourceLabel"], json.dumps(r["NativeImages"], sort_keys=True))
                      for _, r in records}
        require(len(identities) == 1, "A group combines different actual package/executable/native identities")
        capacities = {r["Graph"]["ListCapacity"] for _, r in records}
        require(len(capacities) == 1, "Actual retained list capacity differs between processes")
        e2e = [window for _, r in records for window in r["EndToEnd"]]
        business = [window for _, r in records for window in r["BusinessControls"]]
        operations = sum(w["Succeeded"] for w in e2e)
        raw = sum(w["RawAllocatedBytes"] for w in e2e)
        control_operations = sum(w["Succeeded"] for w in business)
        control_raw = sum(w["RawAllocatedBytes"] for w in business)
        control_thread = sum(w["SameThreadAllocatedBytes"] for w in business)
        e2e_per_op = Fraction(raw, operations)
        business_per_op = Fraction(control_thread, control_operations)
        summary.append({"label": label, "provider": provider, "route": route, "processes": len(records),
            "independent_process_requirement_met": len(records) >= 3,
            "source_label": records[0][1]["SourceLabel"], "primary_package_sha256": records[0][1]["PrimaryPackageSha256"],
            "executable_sha256": records[0][1]["ExecutableSha256"], "graph": records[0][1]["Graph"],
            "end_to_end": {"raw_allocated_bytes": raw, "operations": operations, "rows": operations * 100,
                "bytes_per_operation": float(e2e_per_op), "bytes_per_row": float(e2e_per_op / 100),
                "gc_counts": [sum(w[f"Gen{generation}Collections"] for w in e2e) for generation in range(3)]},
            "isolated_business_graph_control": {"process_raw_bytes": control_raw, "same_thread_raw_bytes": control_thread,
                "operations": control_operations, "bytes_per_graph_current_thread": float(business_per_op),
                "per_window_bytes_per_graph": [float(Fraction(w["SameThreadAllocatedBytes"], w["Succeeded"])) for w in business],
                "same_graph_allocation_in_actual_query_proven": False},
            "unresolved_remainder_estimate_bytes_per_query": float(e2e_per_op - business_per_op),
            "exact_framework_bytes": None, "framework_zero_claim": False,
            "raw_results": [{"path": str(path), "sha256": sha(path)} for path, _ in records]})
    result = {"schema": "DB-Perf-01.business-result-summary.v1", "scope": "Actual 100-row buffered result totals plus isolated same-graph business construction controls. The remainder is framework + runtime + harness and possible control/model mismatch; it is not exact framework allocation. No GC collections are assigned to an ownership category.",
              "local_bytes_verified": args.verify_local, "consumer_sources": json.loads(next(iter(all_sources))),
              "cells": summary, "long_segments_credited": 0}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
