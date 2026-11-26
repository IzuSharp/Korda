#!/usr/bin/env python3
"""Compare matching public-package allocation runs; never subtract controls."""
from __future__ import annotations

import argparse
from decimal import Decimal
import hashlib
import json
import os
from pathlib import Path


def require(value: bool, message: str) -> None:
    if not value:
        raise ValueError(message)


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def ratio(numerator: int, denominator: int) -> str | None:
    return str(Decimal(numerator) / denominator) if denominator else None


def interval(value: dict, context: str) -> None:
    require(value["allocated_bytes_total"] == value["end"]["allocated_bytes"] - value["start"]["allocated_bytes"] >= 0,
            context + ": cumulative allocation arithmetic differs")
    require(value["framework_allocated_bytes"] is None, context + ": raw attribution was altered")
    require(value["end"]["timestamp"] >= value["start"]["timestamp"], context + ": clock regressed")
    for generation in range(3):
        require(value[f"gc{generation}_delta"] == value["end"][f"gc{generation}"] - value["start"][f"gc{generation}"] >= 0,
                context + ": GC arithmetic differs")


def load_phase(root: Path) -> dict:
    manifest = json.loads((root / "build-manifest.json").read_text())
    records: dict[str, list[dict]] = {"postgresql": [], "sqlite": []}
    process_ids = set()
    for entry in manifest["runs"]:
        path = root / entry["result"]
        require(digest(path) == entry["result_sha256"], str(path) + ": result hash differs")
        record = json.loads(path.read_text())
        provider = entry["provider"]
        require(provider in records, "Unexpected provider")
        identity = manifest["providers"][provider]
        require(record["native_aot"] and record["architecture"] == "X64", str(path) + ": not actual x64 AOT")
        require(record["process_id"] not in process_ids, "A process was counted twice in one phase")
        process_ids.add(record["process_id"])
        require(record["warmup_per_scenario"] >= 50 and record["measured_iterations"] > 0, "Insufficient warmup/iterations")
        require(record["session_local_resources_released"] and record["source_local_resources_released"], "Managed owners remain")
        require(record["native_counters_after_source_close"][:6] == [0] * 6, "Native owners remain")
        for name in ("source_commit", "package_sha256", "executable_sha256"):
            require(record[name] == identity[name], str(path) + ": identity differs: " + name)
        require(record["loaded_native_sha256"] == identity["native_sha256"], str(path) + ": loaded native differs")
        require(identity["resolved_korda_packages"] == [identity["package_id"] + "/" + identity["version"]],
                str(path) + ": consumer is not an independent provider package")
        require(record["dataset_rows"] == 256 and record["batch_rows"] == 128 and record["expected_query_checksum"] == 32896,
                str(path) + ": fixed workload differs")
        names = {value["name"] for value in record["complete_queries"]}
        require(names == {"dynamic_complete", "prepared_parameterless_complete"}, "Complete query lanes differ")
        for value in record["complete_queries"]:
            interval(value, str(path))
            require(value["operations"] == record["measured_iterations"] and value["rows"] == value["operations"] * 256,
                    str(path) + ": complete operation/row count differs")
            require(value["checksum"] == value["operations"] * 32896, str(path) + ": complete checksum differs")
        for lane in ("within_first_batch", "supplemental_fetch_including_first_row", "empty_sync_control"):
            require(len(record[lane]) == record["measured_iterations"], str(path) + ": missing narrow windows")
            for value in record[lane]:
                interval(value, str(path))
                if lane == "supplemental_fetch_including_first_row":
                    require(value["operations"] == value["rows"] == 1 and value["checksum"] == 129, "Supplemental boundary differs")
                else:
                    require(value["operations"] == value["rows"] == 127 and value["checksum"] == 8255, "In-batch boundary differs")
                    require(value["incomplete_move_observations"] == value["await_thread_changes"] == 0, "Synchronous window suspended")
                    require(value["start"]["managed_thread_id"] == value["end"]["managed_thread_id"], "Synchronous window changed thread")
        interval(record["empty_async_control"], str(path))
        interval(record["source_open_idle_control"], str(path))
        records[provider].append({"entry": entry, "raw": record})
    for provider, values in records.items():
        require(len(values) >= 3, "Require at least three independent processes for " + provider)
    require(manifest["postgres_cluster"]["exit_code"] == 0, "PostgreSQL did not stop normally")
    require(manifest["postgres_cluster"]["unix_socket_directory"] == "" and manifest["postgres_cluster"]["dedicated_to_this_preflight"],
            "PostgreSQL cluster ownership differs")
    return {"manifest": manifest, "runs": records, "manifest_sha256": digest(root / "build-manifest.json")}


def windows(runs: list[dict], lane: str) -> dict:
    values = [value for run in runs for value in run["raw"][lane]]
    total = sum(value["allocated_bytes_total"] for value in values)
    rows = sum(value["rows"] for value in values)
    operations = sum(value["operations"] for value in values)
    return {"windows": len(values), "allocated_bytes_total": total, "operations": operations, "rows": rows,
            "min_bytes_per_window": min(value["allocated_bytes_total"] for value in values),
            "max_bytes_per_window": max(value["allocated_bytes_total"] for value in values),
            "total_bytes_per_operation": ratio(total, operations), "total_bytes_per_row": ratio(total, rows),
            "incomplete_move_observations": sum(value["incomplete_move_observations"] for value in values),
            "await_thread_changes": sum(value["await_thread_changes"] for value in values),
            "gc_deltas": [sum(value[f"gc{i}_delta"] for value in values) for i in range(3)]}


def complete(runs: list[dict], name: str) -> dict:
    values = [next(value for value in run["raw"]["complete_queries"] if value["name"] == name) for run in runs]
    total = sum(value["allocated_bytes_total"] for value in values)
    operations = sum(value["operations"] for value in values)
    rows = sum(value["rows"] for value in values)
    return {"allocated_bytes_total": total, "operations": operations, "rows": rows,
            "total_bytes_per_operation": ratio(total, operations), "total_bytes_per_row": ratio(total, rows),
            "process_totals": [value["allocated_bytes_total"] for value in values],
            "process_gc_deltas": [[value[f"gc{i}_delta"] for i in range(3)] for value in values],
            "incomplete_move_observations": sum(value["incomplete_move_observations"] for value in values),
            "await_thread_changes": sum(value["await_thread_changes"] for value in values)}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--before", type=Path, required=True)
    parser.add_argument("--after", type=Path, required=True)
    args = parser.parse_args()
    before_root, after_root = args.before.resolve(), args.after.resolve()
    require(before_root != after_root, "Before and after must be separate evidence directories")
    before, after = load_phase(before_root), load_phase(after_root)
    require(before["manifest"]["source_files"] == after["manifest"]["source_files"], "Consumer sources differ between phases")
    support = lambda phase: sorted(value["sha256"] for value in phase["manifest"]["support_packages"])
    require(support(before) == support(after), "SDK support package hashes differ")
    baseline_link = os.path.relpath(before_root, after_root)
    summary = {"schema": "DB-Perf-01.allocation-comparison.v1", "scope": "Short matching allocation preflight only; no throughput, CPU, RSS or latency ranking.",
               "before_manifest_sha256": before["manifest_sha256"], "after_manifest_sha256": after["manifest_sha256"],
               "controls_subtracted_bytes": 0, "consumer_sources_identical": True, "providers": {}}
    lines = ["# Matching public-package allocation comparison", "",
             "This report compares the retained original-package baseline with the after candidate using identical external consumer source bytes, fixed 256-row input, 128-row batches and the same SDK support packages. It contains short allocation evidence only. Host-resource isolation is recorded separately; these elapsed timestamps do not support a throughput, CPU, RSS or latency ranking.", "",
             "All allocation totals are process-wide `GC.GetTotalAllocatedBytes(true)` deltas. Original start/end counters and GC counts remain in each raw JSON. No control is subtracted. Numeric `long` values are checked without reference DTO/string/list/result-array output in a measured interval. Positive total bytes are not assigned an estimated framework fraction.", "",
             "The same probe source deliberately retains its original `measured-baseline` status text in after raw files. Package/source/executable identities and this comparison identify the phase; raw records have not been rewritten to change their labels.", "",
             f"Inputs: [before manifest]({baseline_link}/build-manifest.json), [after manifest](build-manifest.json).", "",
             "## Complete queries", "",
             "Each operation includes construction or prepared-stream creation, all 256 mapped rows, final false, enumerator disposal and dynamic-query disposal. The parameterless prepared plan exists before the interval. Session acquisition/return is outside these query measurements.", "",
             "| Provider | Path | Before raw B / queries / rows | After raw B / queries / rows | Before B/query | After B/query | Change B/query | Change % |",
             "| --- | --- | --- | --- | ---: | ---: | ---: | ---: |"]
    for provider in ("postgresql", "sqlite"):
        original, candidate = before["manifest"]["providers"][provider], after["manifest"]["providers"][provider]
        require(original["package_id"] == candidate["package_id"], "Provider identity changed")
        require(original["native_sha256"] == candidate["native_sha256"], "Native binary changed: this is no longer a managed-only comparison")
        left, right = before["runs"][provider], after["runs"][provider]
        reference = left[0]["raw"]
        for run in left + right:
            for key in ("provider", "runtime", "architecture", "sql", "dataset_rows", "batch_rows", "expected_query_checksum", "warmup_per_scenario", "measured_iterations"):
                require(run["raw"][key] == reference[key], "Workload/runtime changed: " + key)
        item = {"before_identity": original, "after_identity": candidate,
                "complete_queries": {}, "narrow_windows": {}, "before_processes": len(left), "after_processes": len(right)}
        for name in ("dynamic_complete", "prepared_parameterless_complete"):
            first, second = complete(left, name), complete(right, name)
            change = Decimal(second["total_bytes_per_operation"]) - Decimal(first["total_bytes_per_operation"])
            percentage = change * 100 / Decimal(first["total_bytes_per_operation"])
            item["complete_queries"][name] = {"before": first, "after": second, "change_bytes_per_operation": str(change),
                                                "change_percent": str(percentage), "framework_allocated_bytes": None, "framework_attribution": "unproved; no estimated subtraction"}
            lines.append(f"| {provider} | {name} | {first['allocated_bytes_total']} / {first['operations']} / {first['rows']} | {second['allocated_bytes_total']} / {second['operations']} / {second['rows']} | {first['total_bytes_per_operation']} | {second['total_bytes_per_operation']} | {change} | {percentage:.6f}% |")
        for lane in ("within_first_batch", "supplemental_fetch_including_first_row", "empty_sync_control"):
            item["narrow_windows"][lane] = {"before": windows(left, lane), "after": windows(right, lane)}
        item["synchronous_in_batch_zero_gate"] = item["narrow_windows"]["within_first_batch"]["after"]["max_bytes_per_window"] == 0
        item["controls"] = {phase: [{"repetition": value["entry"]["repetition"],
                                     "empty_async_bytes": value["raw"]["empty_async_control"]["allocated_bytes_total"],
                                     "source_open_idle_bytes": value["raw"]["source_open_idle_control"]["allocated_bytes_total"]}
                                    for value in runs] for phase, runs in (("before", left), ("after", right))}
        summary["providers"][provider] = item
    lines += ["", "The ratios above divide accumulated raw bytes by accumulated successful query counts; individual process totals and GC deltas are retained below and in `comparison.json`. These are allocation differences, not a noise estimate or a latency comparison.", "",
              "## Individual full-query processes", "",
              "| Phase | Provider / process | Path | Raw B | Queries | Rows | GC0 / GC1 / GC2 |", "| --- | --- | --- | ---: | ---: | ---: | --- |"]
    for phase, data, prefix in (("before", before, baseline_link + "/"), ("after", after, "")):
        for provider, runs in data["runs"].items():
            for run in runs:
                for value in run["raw"]["complete_queries"]:
                    gc = " / ".join(str(value[f"gc{i}_delta"]) for i in range(3))
                    lines.append(f"| {phase} | [{provider} / {run['entry']['repetition']}]({prefix}{run['entry']['result']}) | {value['name']} | {value['allocated_bytes_total']} | {value['operations']} | {value['rows']} | {gc} |")
    lines += ["", "## Narrow windows and separate controls", "",
              "Within-batch windows start after the first fetched/parsed row; each remaining 127 moves is required to be completed successfully and synchronous. Supplemental windows include one replenishment fetch and its first mapped row, so their measured row count is one. Final false and disposal remain mandatory outside these narrow intervals and are included in the complete-query measurements.", "",
              "| Provider | Interval | Phase | Windows | Raw B sum | Min / max B per window | Rows | Incomplete / thread changes |",
              "| --- | --- | --- | ---: | ---: | --- | ---: | --- |"]
    for provider, item in summary["providers"].items():
        for lane, phases in item["narrow_windows"].items():
            for phase, value in phases.items():
                lines.append(f"| {provider} | {lane} | {phase} | {value['windows']} | {value['allocated_bytes_total']} | {value['min_bytes_per_window']} / {value['max_bytes_per_window']} | {value['rows']} | {value['incomplete_move_observations']} / {value['await_thread_changes']} |")
    lines += ["", "An incomplete observation can race with awaiting; the distinct managed-thread-change count demonstrates actual suspension/resumption in those instances. Neither number permits claiming that all other moves were synchronous.", "",
              "| Provider | Every after in-batch window has exactly zero raw B |", "| --- | --- |"]
    for provider, item in summary["providers"].items():
        lines.append(f"| {provider} | {item['synchronous_in_batch_zero_gate']} |")
    lines += ["", "**Zero B inside a row window is not zero B for a query or a batch.** Where every exact raw process-wide window is zero, that specific warmed synchronous numeric path passes the zero-allocation gate. No attribution subtraction is required for a zero total. The positive complete-query and replenishment totals remain nonzero; their exact framework/runtime split remains unproved. Cancellation, exceptions, session acquisition/return and high-concurrency allocation are not accepted by this probe.", "",
              "| Phase | Provider / process | Empty async raw B | Source-open idle raw B |", "| --- | --- | ---: | ---: |"]
    for provider, item in summary["providers"].items():
        for phase, controls in item["controls"].items():
            for value in controls:
                lines.append(f"| {phase} | {provider} / {value['repetition']} | {value['empty_async_bytes']} | {value['source_open_idle_bytes']} |")
    lines += ["", "The async control uses `Task.Yield` and is not equivalent to a native operation. Every control is reported independently and none is subtracted. All six live native owner counters (sources, sessions, operations, cursors, buffers, handles) returned to zero after source close in every verified process. Cumulative SQL/protocol counters include setup and warmup and do not measure physical network round trips.", "",
              "## Exact identities", "", "| Phase | Provider | Package version / source commit | Package SHA-256 | Executable SHA-256 | Loaded native SHA-256 |",
              "| --- | --- | --- | --- | --- | --- |"]
    for phase, data in (("before", before), ("after", after)):
        for provider, value in data["manifest"]["providers"].items():
            lines.append(f"| {phase} | {provider} | `{value['version']}` / `{value['source_commit']}` | `{value['package_sha256']}` | `{value['executable_sha256']}` | `{value['native_sha256']}` |")
    lines += ["", "Both phases loaded byte-identical native libraries per provider. The changed managed package and AOT executable are bound to their recorded source identities and release receipts. This evidence uses candidate feeds; it does not establish a public NuGet publication or post-merge download.", "",
              "Regenerate with `python3 benchmarks/Database/allocations/compare.py --before /absolute/before/evidence --after /absolute/after/evidence`. The command validates raw arithmetic, checksums, all per-window synchronous conditions, independent processes, owner retirement, exact consumer inputs, support packages and native binary equality before writing this comparison. It does not rerun either phase.", ""]
    summary["synchronous_in_batch_zero_gate"] = all(value["synchronous_in_batch_zero_gate"] for value in summary["providers"].values())
    (after_root / "comparison.json").write_text(json.dumps(summary, indent=2) + "\n")
    (after_root / "comparison.md").write_text("\n".join(lines))
    print(json.dumps({"comparison": str(after_root / "comparison.md"), "synchronous_in_batch_zero_gate": summary["synchronous_in_batch_zero_gate"]}))


if __name__ == "__main__":
    main()
