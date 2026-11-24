#!/usr/bin/env python3
"""Validate and summarize raw short-probe counts without subtracting controls."""
from __future__ import annotations

import argparse
from decimal import Decimal
import hashlib
import json
from pathlib import Path


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("evidence", type=Path)
    args = parser.parse_args()
    root = args.evidence.resolve()
    manifest = json.loads((root / "build-manifest.json").read_text())
    summary = {"schema": "DB-Perf-01.allocation-summary.v1", "status": "measured baseline, framework split unproved", "subtracted_bytes": 0, "runs": []}
    lines = ["# Korda public-package allocation baseline", "", "This short preflight uses original candidate packages from fixed main `771bc77cbd68d3f932b99ed2497c0697bbba5f02`. It measures unoptimized behavior; there are no production changes or before/after performance claims in these results.", "",
             "All figures below are process-wide managed allocation totals. Exact framework attribution remains **unproved**; no control value is subtracted. Each process warms each query/advance scenario 64 times before its 128 measured repetitions; the idle control is one separate 100 ms interval. The generated entity contains one `long`; queries return scalar values consumed into checked numeric sums without business DTO/string/list/array output inside the intervals.", "",
             "Configured limits are one connection, four waiters, 128 rows / 65536 encoded bytes per batch, 1024 field bytes, 512 buffered rows and 512 query nodes. Other limits retain the fixed package's defaults. The same session is reused for the query measurements; session-open/return allocation is not inside those query numbers.", "",
             "## Complete queries", "", "Every measured query consumes 256 rows, the terminating `false`, enumerator cleanup, and dynamic-query disposal where applicable. A prepared parameterless plan is created before its measurement. The result count and checksum are checked on every query.", "",
             "| Provider / independent process | Path | Raw allocated bytes | Queries | Rows | Total B/query | Total B/row | GC0 / GC1 / GC2 |", "| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |"]
    run_ids = set()
    for entry in manifest["runs"]:
        path = root / entry["result"]
        assert sha(path) == entry["result_sha256"], path
        record = json.loads(path.read_text())
        assert record["native_aot"] and record["warmup_per_scenario"] >= 50
        assert record["source_commit"] == "771bc77cbd68d3f932b99ed2497c0697bbba5f02"
        assert record["process_id"] not in run_ids
        run_ids.add(record["process_id"])
        assert record["native_counters_after_source_close"][:6] == [0] * 6
        provider = entry["provider"]
        identity = manifest["providers"][provider]
        assert record["package_sha256"] == identity["package_sha256"]
        assert record["executable_sha256"] == identity["executable_sha256"]
        assert record["loaded_native_sha256"] == identity["native_sha256"]
        complete = []
        intervals = record["complete_queries"] + record["within_first_batch"] + record["supplemental_fetch_including_first_row"] + record["empty_sync_control"] + [record["empty_async_control"], record["source_open_idle_control"]]
        for value in intervals:
            assert value["allocated_bytes_total"] == value["end"]["allocated_bytes"] - value["start"]["allocated_bytes"] >= 0
            assert value["framework_allocated_bytes"] is None
            for generation in range(3):
                assert value[f"gc{generation}_delta"] == value["end"][f"gc{generation}"] - value["start"][f"gc{generation}"] >= 0
        for value in record["complete_queries"]:
            assert value["operations"] == record["measured_iterations"]
            assert value["rows"] == value["operations"] * 256
            assert value["checksum"] == value["operations"] * 32896
            bpq = str(Decimal(value["allocated_bytes_total"]) / value["operations"])
            bpr = str(Decimal(value["allocated_bytes_total"]) / value["rows"])
            gc = " / ".join(str(value[f"gc{i}_delta"]) for i in range(3))
            lines.append(f"| [{provider} / {entry['repetition']}]({entry['result']}) | {value['name']} | {value['allocated_bytes_total']} | {value['operations']} | {value['rows']} | {bpq} | {bpr} | {gc} |")
            complete.append({key: value[key] for key in ("name", "allocated_bytes_total", "operations", "rows", "checksum", "incomplete_move_observations", "await_thread_changes")})
        sample_totals = {}
        for name in ("within_first_batch", "supplemental_fetch_including_first_row", "empty_sync_control"):
            values = record[name]
            assert len(values) == record["measured_iterations"]
            for value in values:
                if name == "supplemental_fetch_including_first_row":
                    assert value["operations"] == value["rows"] == 1 and value["checksum"] == 129
                else:
                    assert value["operations"] == value["rows"] == 127 and value["checksum"] == 8255
                    assert value["incomplete_move_observations"] == value["await_thread_changes"] == 0
                    assert value["start"]["managed_thread_id"] == value["end"]["managed_thread_id"]
            counts = [value["allocated_bytes_total"] for value in values]
            sample_totals[name] = {"windows": len(counts), "raw_delta_sum": sum(counts), "raw_delta_min": min(counts), "raw_delta_max": max(counts), "rows": sum(value["rows"] for value in values), "incomplete_move_observations": sum(value["incomplete_move_observations"] for value in values), "await_thread_changes": sum(value["await_thread_changes"] for value in values)}
        summary["runs"].append({"provider": provider, "repetition": entry["repetition"], "raw_result": entry["result"], "process_id": record["process_id"], "complete_queries": complete, "windows": sample_totals, "empty_async_bytes": record["empty_async_control"]["allocated_bytes_total"], "source_open_idle_bytes": record["source_open_idle_control"]["allocated_bytes_total"], "native_counters_after_source_close": record["native_counters_after_source_close"]})
    for provider in ("postgresql", "sqlite"):
        assert sum(entry["provider"] == provider for entry in summary["runs"]) >= 3
    assert manifest["postgres_cluster"]["exit_code"] == 0
    lines += ["", "## Narrow windows and controls", "", "The within-batch interval starts after the first row has arrived. Each of its 127 subsequent moves must be synchronously completed; the probe fails if that claim is false. The supplemental-fetch interval includes one fetch and the first mapped row of the next batch. Its final row count is therefore one, not 128.", "",
              "| Provider / process | Interval | Windows | Sum of raw interval deltas | Min / max B per window | Rows in windows | Incomplete observations / actual thread changes |",
              "| --- | --- | ---: | ---: | --- | ---: | --- |"]
    for entry in summary["runs"]:
        for name, value in entry["windows"].items():
            lines.append(f"| {entry['provider']} / {entry['repetition']} | {name} | {value['windows']} | {value['raw_delta_sum']} | {value['raw_delta_min']} / {value['raw_delta_max']} | {value['rows']} | {value['incomplete_move_observations']} / {value['await_thread_changes']} |")
    lines += ["", "An incomplete observation means the returned move was pending when checked; it can race with the subsequent await. A managed-thread change across the await demonstrates an actual suspension/resumption in those instances. Neither counter claims that every other move completed synchronously.", "",
              "The raw within-batch values expose the current row cost without hiding query or batch costs. The separate source audit locates an unconditional `TaskCompletionSource` per move. The empty synchronous control has the same counter and advance-loop shape. A control reading is retained as evidence, not used as an estimated subtraction or a whole-framework zero-allocation claim.", "",
              "| Provider / process | Empty async control total B | Source-open 100 ms idle total B | Native live owners after source close |",
              "| --- | ---: | ---: | --- |"]
    for entry in summary["runs"]:
        lines.append(f"| {entry['provider']} / {entry['repetition']} | {entry['empty_async_bytes']} | {entry['source_open_idle_bytes']} | {entry['native_counters_after_source_close'][:6]} |")
    lines += ["", "The native owner order is sources, sessions, operations, cursors, buffers and handle-table entries. All six must be zero. The raw JSON also records cumulative SQL dispatch/protocol counters over setup, warmup, measurement and cleanup. They are not per-window counts and do not establish physical network round trips.", "",
              "## Identity and execution evidence", "", "[Build manifest](build-manifest.json) preserves package and executable paths, SHA-256 values, exact resolved provider IDs, source inputs, SDK support package hashes and the isolated PostgreSQL command. Every process verifies the loaded provider file observed in `/proc/self/maps` against that receipt.", "",
              "| Provider | Candidate package SHA-256 | Native AOT executable SHA-256 | Actually loaded native SHA-256 |", "| --- | --- | --- | --- |"]
    for provider, value in manifest["providers"].items():
        lines.append(f"| {provider} | `{value['package_sha256']}` | `{value['executable_sha256']}` | `{value['native_sha256']}` |")
    lines += ["", "Both candidate versions are `0.1.0-preview.3`. These are fixed-main candidate artifacts, not evidence of a new public NuGet download or post-merge release. [PostgreSQL release receipt](postgresql-release.json) and [SQLite release receipt](sqlite-release.json) preserve the source binding.", "",
              "The actual SDK, compiler and server are recorded in [dotnet-info.log](dotnet-info.log), [clang-version.log](clang-version.log) and [postgres-version.log](postgres-version.log). PostgreSQL runs in the probe's own TCP-only cluster, with `-k ''`, `fsync=on`, `synchronous_commit=on` and `full_page_writes=on`; [shutdown](postgres-stop.log) completed with exit code zero. SQLite receives a distinct file for each process. Providers are interleaved, with only one probe consumer running at a time.", "",
              "## Build recovery and limits", "", "The first restore lacked two SDK support packages; the next attempt exhausted workspace storage while duplicating ILCompiler/runtime packages. The `.previous-*.log` files preserve these failures. Only this probe's reproducible duplicate support-package extractions were removed. The successful build uses a fallback view containing the six exact SDK support packages and isolated provider-specific Korda caches; no product source/project reference or launcher is involved.", "",
              "Physical-host exclusivity is not established and other work may compile on the host. Elapsed ticks are diagnostic only. This report supplies no throughput ranking, CPU-memory comparison, four-way ORM comparison, tail-latency claim, transaction/high-concurrency allocation acceptance or 30-minute segment. The eight required long segments remain separate work.", "",
              "Regenerate these totals with `python3 benchmarks/Database/allocations/summarize.py /absolute/path/to/this/evidence-directory`. The script verifies raw arithmetic, expected rows/checksums, synchronous-window conditions, package/binary/native hashes, independent process identities and native retirement before writing the summary.", ""]
    (root / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    (root / "summary.md").write_text("\n".join(lines))
    print(json.dumps({"verified_independent_processes": len(summary["runs"]), "summary": str(root / "summary.md")}))


if __name__ == "__main__":
    main()
