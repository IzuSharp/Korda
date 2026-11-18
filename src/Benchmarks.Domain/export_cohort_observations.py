#!/usr/bin/env python3
"""Export numeric observations from every completed B23 invocation.

Analysis only: preserve search, calibration and confirmation observations without
executing an application or changing A21 validity, statistics or selected inputs.
Use alongside the ordinary report/timing export and export_diagnostics.py.
"""

import argparse
from collections import Counter
import csv
import hashlib
import json
from pathlib import Path
import re

import export_diagnostics as diagnostics
from report import app_costs, workload_concurrency


COUNTERS = (
    "all_thread_stacks_bytes", "child_process_bytes", "kernel_socket_bytes",
    "main_thread_stack_bytes", "mapped_code_bytes", "peak_rss_bytes",
    "private_working_set_bytes", "rss_bytes", "threads",
)
TARGET_COUNTERS = (
    "byte_credits", "callback_roots", "connections", "copied_bytes", "ffi_calls",
    "gc_heap_committed_bytes", "gc_heap_live_bytes", "live_owners", "live_permits",
    "managed_allocated_bytes", "native_allocated_bytes", "native_allocations",
    "native_allocator_live_bytes", "native_allocator_retained_bytes", "operations",
    "pinned_bytes", "queue_slots", "retained_bytes", "runtime_instances",
)
COSTS = (
    "application_cpu_seconds", "rss_before_bytes", "rss_after_bytes",
    "lifetime_peak_rss_bytes", "private_working_set_after_bytes", "threads_after",
    "fds_after", "sockets_after", "managed_allocated_bytes_delta",
    "gc_collection_deltas", "startup_to_ready_ns", "native_allocator_live_bytes",
    "native_allocator_retained_bytes", "ffi_calls", "copy_bytes",
)
HISTOGRAM_FIELDS = (
    "bins", "count", "maximum_ns", "overflow_count", "overflow_ns",
    "percentiles_from_exact_samples_ns", "resolution_ns",
)
TIMING_FIELDS = (
    "phase", "case", "route", "repetition", "observation_valid", "id",
    "scheduled_ns", "dispatch_ns", "admission_ns", "completion_ns",
    "deadline_ns", "target_completion_observed_ns", "attempts",
    "request_wire_bytes", "response_wire_bytes", "status",
)
OUTCOMES = {"ok", "failed", "cancelled", "timeout", "rejected", "unsupported"}
TERMINAL_STATES = {
    "complete-with-adverse-cells-preserved", "incomplete-attempts-retained",
    "blocked-further-invocations",
}


def load(path):
    return json.loads(Path(path).read_text())


def save(path, value):
    path.write_text(json.dumps(value, sort_keys=True, allow_nan=False,
                               separators=(",", ":")) + "\n")


def sha(path):
    return diagnostics.sha256(path)


def text_sha(value):
    return hashlib.sha256(str(value).encode()).hexdigest()


def digest(value):
    if not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{64}", value):
        raise ValueError("Expected an exact SHA-256 identity")
    return value


def boolean(value):
    if value is not None and type(value) is not bool:
        raise ValueError("Expected a boolean observation or null")
    return value


def numeric(value):
    if isinstance(value, list):
        return [numeric(item) for item in value]
    if isinstance(value, dict):
        return {diagnostics.identifier(str(key)): numeric(item)
                for key, item in value.items()}
    return diagnostics.number(value)


def failure_class(value):
    value = str(value).lower()
    for phrase, classification in (
        ("dispatch lag", "generator_dispatch_lag"),
        ("in-flight capacity", "generator_inflight_capacity"),
        ("durable oracle", "application_oracle_or_shutdown"),
        ("supervision", "external_supervision"), ("cleanup", "cleanup"),
        ("mismatch", "integrity_mismatch"), ("timeout", "timeout"),
        ("no equivalent", "no_equivalent_profile"),
    ):
        if phrase in value:
            return classification
    return "other_retained_failure"


def owned_path(root, value):
    path = Path(value).resolve()
    if not path.is_relative_to(root):
        raise ValueError("Observation reference lies outside this cohort")
    return path


def bound_json(root, reference):
    path = owned_path(root, reference["path"])
    if sha(path) != digest(reference["sha256"]):
        raise ValueError("Bound observation or receipt changed")
    return load(path)


def counter_values(counters, allowed):
    if set(counters) - set(allowed) - {"observation_note"}:
        raise ValueError("Unreviewed resource counter would otherwise be omitted")
    result = {}
    for key, item in counters.items():
        if key == "observation_note":
            result[key] = {"classification": "observation_unavailable",
                           "sha256": text_sha(item)}
            continue
        availability = item.get("availability")
        if availability not in {"measured", "unknown", "unavailable"}:
            raise ValueError("Unknown resource availability")
        unit = item.get("unit")
        if unit not in {"bytes", "count", "seconds", "nanoseconds"}:
            raise ValueError("Unknown numeric counter unit")
        result[key] = {"availability": availability, "unit": unit,
                       "value": numeric(item.get("value"))}
    return result


def resource(value):
    if value is None:
        return None
    result = {key: numeric(value.get(key)) for key in (
        "time_ns", "cpu_ticks", "clock_ticks_per_second", "fd_count", "socket_count",
    )}
    result["counters"] = counter_values(value.get("counters", {}), COUNTERS)
    return result


def adapter_resource_series(values):
    if values is None:
        return {"availability": "not-recorded", "rows": None}
    if not isinstance(values, list):
        raise ValueError("Expected an adapter resource sample list")
    fields = ("time_ns", "cpu_ticks", "clock_ticks_per_second", "fd_count", "socket_count")
    result = {"availability": "recorded", "columns": [
        *fields, *COUNTERS, "counter_metadata_index", "note_index",
    ], "counter_metadata": [], "notes": [], "rows": []}
    indexes = {"counter_metadata": {}, "notes": {}}

    def intern(group, value):
        if value is None:
            return None
        encoded = json.dumps(value, sort_keys=True, allow_nan=False, separators=(",", ":"))
        if encoded not in indexes[group]:
            indexes[group][encoded] = len(result[group])
            result[group].append(value)
        return indexes[group][encoded]

    for value in values:
        projected = resource(value)
        if projected is None:
            result["rows"].append(None)
            continue
        counters = projected["counters"]
        metadata = {key: {"availability": counters[key]["availability"], "unit": counters[key]["unit"]}
                    if key in counters else None for key in COUNTERS}
        result["rows"].append([
            *[projected[key] for key in fields],
            *[counters.get(key, {}).get("value") for key in COUNTERS],
            intern("counter_metadata", metadata), intern("notes", counters.get("observation_note")),
        ])
    result["row_count"] = len(result["rows"])
    return result


def image_mappings(value):
    if value is None:
        return None
    availability = value.get("availability")
    if availability not in {"measured", "unknown", "unavailable"}:
        raise ValueError("Unknown image-mapping availability")
    return {"availability": availability, "modules": [{
        "module_path_sha256": text_sha(path),
        "name": Path(path).name if diagnostics.IDENTIFIER.fullmatch(Path(path).name) else None,
        "virtual_mapped_bytes": numeric(item.get("virtual_mapped_bytes")),
        "executable_virtual_bytes": numeric(item.get("executable_virtual_bytes")),
    } for path, item in value.get("modules", {}).items()]}


def closed_receipt(value):
    if value is None:
        return None
    return {**{key: diagnostics.identifier(value[key]) if value.get(key) is not None else None
               for key in ("serverState", "databaseStatus", "consumerStatus")},
            **{key: boolean(value.get(key)) for key in
               ("databaseResourcesReleased", "consumerResourcesReleased")}}


def resource_series(root, receipt_path, receipt):
    reference = receipt.get("resources")
    if reference is None:
        return {"availability": "not-recorded", "source_sha256": None, "rows": None}
    if reference != "application/resources.jsonl":
        raise ValueError("Unexpected application resource reference")
    path = owned_path(root, receipt_path.parent / reference)
    if not path.is_file():
        return {"availability": "not-recorded", "source_sha256": None, "rows": None}
    before_sha = sha(path)
    fields = ("time_ns", "cpu_ticks", "clock_ticks_per_second", "fd_count", "socket_count")
    phases = ("startup-and-seed", "measurement", "reconciliation-and-shutdown")
    columns = [*fields, *COUNTERS, "counter_metadata_index", "phase_index", "managed_probe_index", "note_index"]
    result = {"availability": "recorded", "source_sha256": before_sha,
              "columns": columns, "phase_names": phases, "counter_metadata": [],
              "managed_probes": [], "notes": [], "rows": []}
    indexes = {"counter_metadata": {}, "managed_probes": {}, "notes": {}}

    def intern(group, value):
        if value is None:
            return None
        encoded = json.dumps(value, sort_keys=True, allow_nan=False, separators=(",", ":"))
        if encoded not in indexes[group]:
            indexes[group][encoded] = len(result[group])
            result[group].append(value)
        return indexes[group][encoded]

    line_count = 0
    with path.open() as stream:
        for line_count, line in enumerate(stream, 1):
            try:
                row = json.loads(line)
            except json.JSONDecodeError:
                result.setdefault("unparsed_lines", []).append({
                    "line": line_count, "sha256": text_sha(line),
                })
                result["availability"] = "partial"
                continue
            projected = resource(row)
            counters = projected["counters"]
            phase = row.get("phase")
            if phase is not None and phase not in phases:
                raise ValueError("Unexpected application resource phase")
            metadata = {key: {"availability": counters[key]["availability"], "unit": counters[key]["unit"]}
                        if key in counters else None for key in COUNTERS}
            result["rows"].append([
                *[projected[key] for key in fields],
                *[counters.get(key, {}).get("value") for key in COUNTERS],
                intern("counter_metadata", metadata), phases.index(phase) if phase is not None else None,
                intern("managed_probes", diagnostics.snapshot(row.get("managed_probe"))),
                intern("notes", counters.get("observation_note")),
            ])
    result["row_count"] = len(result["rows"])
    result["source_line_count"] = line_count
    if sha(path) != before_sha:
        raise ValueError("Application resource observations changed during export")
    return result


def references(row):
    result = {}
    for key in ("application_receipt", "supervision_receipt", "original_a21_observation"):
        value = row.get(key)
        result[key] = None if not value else digest(value["sha256"])
    return result


def diagnostic_projection(case, phase, row, costs, source_sha256):
    # Reuse the reviewed 785a8c3 allowlist, counter deltas and unavailable rules.
    value = {"cases": [{"case": case, "phase": phase, "report": {
        "source_summary": {"sha256": source_sha256},
        "independent_process_observations": [{
            "route": row["route"], "repetition": row["repetition"],
            "valid": row["valid"], "application_costs": costs,
        }],
    }}]}
    return diagnostics.project(value)[0]


def process(root, row, source_sha256, scope, unit, phase, observation_sha256):
    case = diagnostics.identifier(row["case"])
    receipt = (bound_json(root, row["application_receipt"])
               if row.get("application_receipt") else None)
    costs = app_costs(receipt) if receipt else {}
    concurrency = workload_concurrency(row.get("records"))
    result = {
        "scope": scope, "unit": unit, "phase": phase, "case": case,
        "route": diagnostics.identifier(row["route"]),
        "repetition": numeric(row["repetition"]), "valid": boolean(row["valid"]),
        "source_summary_sha256": source_sha256,
        "observation_sha256": digest(observation_sha256),
        "receipt_sha256": references(row),
        "failure_classes": [failure_class(x) for x in row.get("invalid_reasons", [])],
        "failure_sha256": [text_sha(x) for x in row.get("invalid_reasons", [])],
        "counts": {key: numeric(row.get(key)) for key in (
            "offered", "admitted", "completed", "successful", "exit_code",
        )},
        "outcome_counts": numeric(row.get("outcome_counts")),
        "measurement_window_ns": numeric(row.get("measurement_window_ns")),
        "rates_per_second": numeric(row.get("rates_per_second")),
        "last_target_counters": counter_values(row.get("last_target_counters") or {}, TARGET_COUNTERS),
        "histograms": {diagnostics.identifier(name): {
            key: numeric(histogram[key]) for key in HISTOGRAM_FIELDS if key in histogram
        } for name, histogram in (row.get("histograms") or {}).items()},
        "application_costs": {key: numeric(costs.get(key)) for key in COSTS},
        "observed_concurrency": {key: numeric(value) for key, value in concurrency.items()
                                 if key not in ("availability", "reason", "scope")},
        "concurrency_availability": diagnostics.identifier(concurrency["availability"]),
        "adapter_resource_samples": adapter_resource_series(row.get("resource_samples")),
        "diagnostics": diagnostic_projection(case, phase, row, costs, source_sha256),
        "application": None,
    }
    if receipt is not None:
        receipt_path = owned_path(root, row["application_receipt"]["path"])
        statuses = receipt.get("http_status_counts", {})
        if any(not re.fullmatch(r"[1-5][0-9]{2}", key) for key in statuses):
            raise ValueError("Unexpected HTTP status count key")
        oracle = receipt.get("durable_oracle") or {}
        diagnostic_oracle = receipt.get("diagnostics_oracle") or {}
        result["application"] = {
            "closed": closed_receipt(receipt.get("closed")),
            "status_sha256": text_sha(receipt.get("status")),
            "measurement_resources_before": resource(receipt.get("measurement_resources_before")),
            "measurement_resources_after": resource(receipt.get("measurement_resources_after")),
            "resource_series": resource_series(root, receipt_path, receipt),
            "image_mappings_before": image_mappings(receipt.get("image_mappings_before")),
            "image_mappings_after": image_mappings(receipt.get("image_mappings_after")),
            "last_probe": diagnostics.snapshot(receipt.get("last_probe")),
            "http_status_counts_including_setup": numeric(statuses),
            "http_error_code_counts_by_label_sha256": {
                text_sha(key): numeric(value)
                for key, value in receipt.get("http_error_codes", {}).items()
            },
            "expected_diagnostic_http_500": case.startswith("diagnostics-"),
            "expected_upload_capacity_rejections": case.startswith("upload-overload-"),
            "durable_oracle": {"passed": boolean(oracle.get("passed")), **{
                key: numeric(oracle.get(key)) for key in (
                    "attachments", "items", "operations", "outbox", "unique_blobs_verified",
                )}},
            "diagnostics_oracle_passed": boolean(diagnostic_oracle.get("passed")),
            "observed_listeners": {key: boolean(value) for key, value in
                                   diagnostic_oracle.get("observed_listeners", {}).items()
                                   if key in ("Activity", "ILogger", "Meter")},
        }
    return result


def not_run(value):
    result = []
    for item in value:
        if isinstance(item, (list, tuple)) and len(item) == 3:
            case, route, repetition = item
        elif isinstance(item, dict) and all(key in item for key in ("case", "route", "repetition")):
            case, route, repetition = item["case"], item["route"], item["repetition"]
        else:
            raise ValueError("Unreviewed not-run identity shape")
        result.append({"case": diagnostics.identifier(case),
                       "route": diagnostics.identifier(route), "repetition": numeric(repetition)})
    return result


def invocations(state):
    for unit in state["search_units"]:
        yield "search", unit["id"], "calibration", state["search"].get(unit["id"])
    for case in state.get("matrix_order", state["expected_cases"]):
        for phase in ("calibration", "confirmation"):
            yield "matrix", case, phase, state["matrix"].get(case, {}).get(phase)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("cohorts", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    root = args.cohorts.resolve()
    state = load(root / "cohorts.json")
    if state["state"] not in TERMINAL_STATES:
        raise ValueError("Export only after the timed execution reaches a terminal state")
    report = load(root / "report.json")
    cohort_sha = sha(root / "cohorts.json")
    if report["source_cohorts"]["sha256"] != cohort_sha:
        raise ValueError("Report and cohort identity differ")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    (output / "search-timings").mkdir()
    (output / "observations").mkdir()
    summaries = []
    counts = Counter()
    for scope, unit, phase, entry in invocations(state):
        unit = diagnostics.identifier(unit)
        process_path = output / "observations" / (scope + "--" + unit + "--" + phase + ".jsonl")
        with process_path.open("x") as processes:
            item = {"scope": scope, "unit": unit, "phase": phase,
                    "state": diagnostics.identifier(entry["state"]) if entry else "not-run",
                    "attempted": bool(entry and entry.get("path")),
                    "completed": bool(entry and entry.get("state") in
                                      ("completed", "completed-with-invalid-cells")),
                    "valid": boolean(entry.get("valid")) if entry else None}
            item["process_observations"] = str(process_path.relative_to(output))
            if not entry or not entry.get("summary_sha256"):
                item["reason_sha256"] = text_sha((entry or {}).get("reason", "not recorded"))
                summaries.append(item)
                continue
            directory = owned_path(root, entry["path"])
            summary_sha = digest(entry["summary_sha256"])
            summary = bound_json(root, {"path": str(directory / "summary.json"), "sha256": summary_sha})
            item.update(source_summary_sha256=summary_sha, valid=boolean(entry.get("valid")),
                profile_sha256=digest(summary["profile_sha256"]),
                a21_sha256=digest(summary["a21_sha256"]), fingerprint=digest(summary["fingerprint"]),
                all_selected_cells_executed=boolean(summary.get("all_selected_cells_executed")),
                all_executed_cells_valid=boolean(summary.get("all_executed_cells_valid")),
                declared_processes=len(summary["run_order"]), recorded_processes=len(summary["runs"]),
                valid_processes=sum(row["valid"] for row in summary["runs"]),
                not_run=not_run(summary.get("not_run", [])),
                unsupported=[{"case": diagnostics.identifier(row["case"]),
                    "route": diagnostics.identifier(row["route"]),
                    "reason_class": failure_class(row["reason"]), "reason_sha256": text_sha(row["reason"])}
                    for row in summary["unsupported"]],
                noise_bands={diagnostics.identifier(name): {
                    key: numeric(value.get(key))
                    for key in ("absolute_noise_ns", "relative_noise", "process_means_ns")
                } for name, value in (summary.get("noise_bands") or {}).items()})
            timing_path = output / "search-timings" / (unit + ".csv")
            timing = timing_path.open("x", newline="") if scope == "search" else None
            writer = csv.DictWriter(timing, fieldnames=TIMING_FIELDS) if timing else None
            if writer:
                writer.writeheader()
            try:
                for reference in summary["runs"]:
                    observation = bound_json(root, {
                        "path": str(directory / reference["path"]), "sha256": reference["sha256"],
                    })
                    projected = process(root, observation, summary_sha, scope, unit, phase, reference["sha256"])
                    processes.write(json.dumps(projected, sort_keys=True, allow_nan=False,
                                               separators=(",", ":")) + "\n")
                    counts[scope + "_" + phase + "_recorded_processes"] += 1
                    counts[scope + "_" + phase + "_valid_processes"] += observation["valid"]
                    if writer:
                        records = observation.get("records")
                        if not isinstance(records, list):
                            counts["missing_or_nonlist_search_record_lists"] += 1
                            continue
                        for record in records:
                            if not isinstance(record, dict):
                                counts["malformed_search_timing_records"] += 1
                                continue
                            row = {"phase": "search-calibration", "case": observation["case"],
                                "route": observation["route"], "repetition": observation["repetition"],
                                "observation_valid": observation["valid"], "status": record.get("status")
                                if record.get("status") in OUTCOMES else "unavailable-invalid-status"}
                            row.update({key: record[key] if type(record.get(key)) is int else ""
                                        for key in TIMING_FIELDS[5:-1]})
                            writer.writerow(row)
                            counts["search_timing_rows"] += 1
            finally:
                if timing:
                    timing.close()
            summaries.append(item)
    if sha(root / "cohorts.json") != cohort_sha:
        raise ValueError("Cohort changed during analysis-only export")
    save(output / "summary.json", {
        "schema_version": 2, "task": "B23", "platform": "linux-x64",
        "source_cohorts_sha256": cohort_sha, "source_report_sha256": sha(root / "report.json"),
        "exporter_sha256": sha(__file__), "diagnostics_exporter_sha256": sha(diagnostics.__file__),
        "report_projection_source_sha256": sha(Path(__file__).with_name("report.py")),
        "state": state["state"], "counts": dict(counts), "invocations": summaries,
        "cohort_declared_counts": numeric(state.get("counts")),
        "declared_search_units": len(state["search_units"]),
        "declared_matrix_cases": len(state["expected_cases"]),
        "known_http_error_labels": {text_sha(label): label for label in
                                    ("upload_capacity", "unstructured_error", "outcome_unknown")},
        "scope": {
            "timing": "Search numeric timing rows supplement both matrix phases in the ordinary timing export.",
            "wire_bytes": "A21 request_wire_bytes/response_wire_bytes count JSONL IPC bytes; they are not HTTP or TLS wire sizes.",
            "application_resources": "Observed application PID resources, separately from adapter resource samples and PostgreSQL.",
            "resource_units": "Resource counters are bytes except threads; CPU ticks use each snapshot's clock_ticks_per_second; times are monotonic nanoseconds.",
            "resource_series": "Per-process column tables preserve every original application and adapter sample. Counter metadata, phase names, repeated managed probes and observation notes use explicit table indices; null means unavailable, not zero. Original application resources.jsonl and adapter observation hashes remain recorded. A null adapter row preserves a null source sample; a missing sample list is not-recorded.",
            "image_mappings": "Module names and path hashes identify virtual mappings only; mapped bytes are not resident memory, allocator bytes or code-page residency.",
            "diagnostics": "The unchanged reviewed exporter projects both before/after boundaries for every search/calibration/confirmation process; duration metrics use seconds and cpuTimeSpanTicks is 100ns.",
            "failure_meaning": "Expected diagnostic HTTP 500 and upload-capacity rejection counts remain separate from logical validity. Free-form reasons and error labels are represented by exact hashes.",
            "regression": "A21 regression labels mean slower than the comparison route under measured noise; no previous-release performance comparison is implied.",
            "cleanup": "Closed processes and supervised exits do not prove native resource retirement; scoped B22 evidence remains authoritative.",
            "content": "Fixed IDs, hashes and numeric observations only. No HTTP body/header, private configuration/key, database, trace, free-form log or archive is exported.",
        },
    })
    files = [{"path": str(path.relative_to(output)), "bytes": path.stat().st_size, "sha256": sha(path)}
             for path in sorted(output.rglob("*")) if path.is_file()]
    save(output / "manifest.json", {"schema_version": 2, "files": files,
        "file_count": len(files), "total_bytes": sum(row["bytes"] for row in files),
        "source_cohorts_sha256": cohort_sha})
    print(json.dumps({"output": str(output), "counts": dict(counts),
                      "files": len(files) + 1, "bytes": sum(row["bytes"] for row in files)}))


if __name__ == "__main__":
    main()
