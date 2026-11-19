"""Scoped Perf-01 procfs observations; unavailable fields do not erase others."""
from __future__ import annotations

import os
from pathlib import Path
import time


def identity(pid):
    fields = (Path("/proc") / str(pid) / "stat").read_text().rsplit(")", 1)[1].split()
    return {"pid": pid, "parent_pid": int(fields[1]), "start_ticks": int(fields[19])}


def process_sample(a21, pid, expected_start_ticks=None):
    """Preserve A21 memory observations independently from CPU/fd/affinity reads."""
    directory = Path("/proc") / str(pid)
    value = {"pid": pid, "time_ns": time.monotonic_ns(), "utc_ns": time.time_ns(),
             "start_ticks": None, "parent_pid": None, "identity_matches": None,
             "clock_ticks_per_second": os.sysconf("SC_CLK_TCK"), "cpu_ticks": None,
             "cpu_observed_ns": None, "fd_count": None, "socket_count": None,
             "descriptor_scan_misses": None, "affinity_cpus": None,
             "method": "Linux procfs for the named PID; fields and descriptor entries are non-atomic observations"}
    if expected_start_ticks is not None:
        value["expected_start_ticks"] = expected_start_ticks
    try:
        fields = (directory / "stat").read_text().rsplit(")", 1)[1].split()
        value.update(cpu_ticks=int(fields[11]) + int(fields[12]), start_ticks=int(fields[19]),
                     parent_pid=int(fields[1]), cpu_observed_ns=time.monotonic_ns())
        value["identity_matches"] = expected_start_ticks is None or value["start_ticks"] == expected_start_ticks
        if not value["identity_matches"]:
            value["identity_error"] = "Observed PID start identity differs from its bound role"
    except (OSError, ValueError, IndexError) as error:
        value["cpu_error"] = type(error).__name__
    value["counters"] = a21.observe_process(pid)
    if "observation_note" in value["counters"]:
        value["memory_error"] = value["counters"]["observation_note"]
    try:
        value["affinity_cpus"] = sorted(os.sched_getaffinity(pid))
    except OSError as error:
        value["affinity_error"] = type(error).__name__
    try:
        descriptors = list((directory / "fd").iterdir())
        missed = sockets = 0
        complete = True
        for descriptor in descriptors:
            try:
                sockets += os.readlink(descriptor).startswith("socket:")
            except FileNotFoundError:
                # The self-enumeration FD or another process's FD can disappear.
                missed += 1
            except OSError as error:
                complete = False
                value["descriptor_error"] = type(error).__name__
        value["descriptor_scan_misses"] = missed
        value["fd_count"] = len(descriptors) - missed
        value["socket_count"] = sockets if complete else None
    except OSError as error:
        value["descriptor_error"] = type(error).__name__
    value["finished_ns"] = time.monotonic_ns()
    return value


def summarize(rows):
    """Aggregate each phase without requiring CPU and RSS in the same samples."""
    by_phase = {}
    for row in rows:
        phase = by_phase.setdefault(row["phase"], {
            "samples": 0, "rss_peak_bytes": None, "fd_peak_count": None, "socket_peak_count": None,
            "cpu_samples": 0, "cpu_first": None, "cpu_last": None, "first": row, "last": row,
            "error_samples": {name: 0 for name in ("cpu", "memory", "descriptor", "affinity", "identity")},
            "descriptor_scan_misses": None, "identities": [], "affinity_masks": []})
        phase["samples"] += 1
        phase["last"] = row
        for name in phase["error_samples"]:
            phase["error_samples"][name] += bool(row.get(name + "_error"))
        if row.get("descriptor_scan_misses") is not None:
            phase["descriptor_scan_misses"] = (phase["descriptor_scan_misses"] or 0) + row["descriptor_scan_misses"]
        observed_identity = {"pid": row["pid"], "start_ticks": row.get("start_ticks")}
        if observed_identity not in phase["identities"]:
            phase["identities"].append(observed_identity)
        mask = row.get("affinity_cpus")
        if mask is not None and mask not in phase["affinity_masks"]:
            phase["affinity_masks"].append(mask)
        if ("expected_start_ticks" in row and row.get("identity_matches") is not True) or row.get("identity_matches") is False:
            continue  # Preserve the raw observation, but do not attribute it to this role.
        for key, value in (("rss_peak_bytes", row["counters"]["rss_bytes"]["value"]),
                           ("fd_peak_count", row.get("fd_count")), ("socket_peak_count", row.get("socket_count"))):
            if value is not None:
                phase[key] = max(phase[key] if phase[key] is not None else value, value)
        if row.get("cpu_ticks") is not None and row.get("start_ticks") is not None:
            phase["cpu_samples"] += 1
            if phase["cpu_first"] is None:
                phase["cpu_first"] = row
            phase["cpu_last"] = row
    for phase in by_phase.values():
        first, last = phase["cpu_first"], phase["cpu_last"]
        phase.update(cpu_seconds=None, cpu_percent_one_core=None, cpu_span_seconds=None,
                     cpu_unavailable_reason="Fewer than two CPU-bearing samples")
        if phase["cpu_samples"] < 2:
            continue
        if (first["pid"], first["start_ticks"], first["clock_ticks_per_second"]) != (
                last["pid"], last["start_ticks"], last["clock_ticks_per_second"]):
            phase["cpu_unavailable_reason"] = "CPU endpoint identity or clock frequency changed"
            continue
        span = last["cpu_observed_ns"] - first["cpu_observed_ns"]
        ticks = last["cpu_ticks"] - first["cpu_ticks"]
        if span <= 0 or ticks < 0:
            phase["cpu_unavailable_reason"] = "CPU endpoints have invalid time or counter order"
            continue
        seconds = ticks / last["clock_ticks_per_second"]
        phase.update(cpu_seconds=seconds, cpu_percent_one_core=seconds * 1e11 / span,
                     cpu_span_seconds=span / 1e9, cpu_unavailable_reason=None)
    return by_phase
