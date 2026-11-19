"""Linux affinity observations for the optional Perf-01 generator isolation."""
from __future__ import annotations

import argparse
import os
from pathlib import Path
import time


def cpu_list(value):
    try:
        cpus = set()
        for part in value.split(","):
            bounds = part.split("-")
            if len(bounds) == 1:
                cpus.add(int(bounds[0]))
            elif len(bounds) == 2 and int(bounds[0]) <= int(bounds[1]):
                if int(bounds[0]) < 0 or int(bounds[1]) >= 65536:
                    raise ValueError("Invalid CPU range")
                cpus.update(range(int(bounds[0]), int(bounds[1]) + 1))
            else:
                raise ValueError("Invalid CPU range")
        if not cpus or min(cpus) < 0 or max(cpus) >= 65536:
            raise ValueError("Invalid CPU number")
        return sorted(cpus)
    except ValueError as error:
        raise argparse.ArgumentTypeError("Use a comma-separated CPU list, for example 0,1") from error


def process_masks(pid):
    directory = Path("/proc") / str(pid)
    fields = (directory / "stat").read_text().rsplit(")", 1)[1].split()
    threads = {}
    for thread in (directory / "task").iterdir():
        try:
            lines = (thread / "status").read_text().splitlines()
            value = next(line.split(":", 1)[1].strip() for line in lines if line.startswith("Cpus_allowed_list:"))
            threads[thread.name] = cpu_list(value)
        except FileNotFoundError:
            pass  # A finished thread is not a surviving affinity violation.
    return {"pid": pid, "parent_pid": int(fields[1]), "start_ticks": int(fields[19]),
            "main_cpus": sorted(os.sched_getaffinity(pid)), "threads": threads,
            "observed_ns": time.monotonic_ns()}


def require_process(snapshot, cpus):
    expected = set(cpus)
    if snapshot["main_cpus"] != sorted(expected) or not snapshot["threads"]:
        raise ValueError("Process main-thread affinity differs from the frozen mask: " + str(snapshot["pid"]))
    if any(not mask or not set(mask).issubset(expected) for mask in snapshot["threads"].values()):
        raise ValueError("A process thread can run outside the frozen CPU mask: " + str(snapshot["pid"]))


def _process_identity(pid):
    fields = (Path("/proc") / str(pid) / "stat").read_text().rsplit(")", 1)[1].split()
    return {"pid": pid, "parent_pid": int(fields[1]), "start_ticks": int(fields[19]), "state": fields[0]}


def process_group(root):
    """Observe the private postmaster tree without requiring procfs children files."""
    initial = _process_identity(root)
    if initial["state"] in ("Z", "X", "x"):
        raise ValueError("Private PostgreSQL postmaster has exited")
    captured, unavailable = {root: initial}, []
    # Some Linux execution environments expose stat/status/task but not
    # task/<tid>/children. Discover one parent/start-time snapshot instead.
    for directory in Path("/proc").iterdir():
        if not directory.name.isdigit():
            continue
        pid = int(directory.name)
        try:
            captured[pid] = _process_identity(pid)
        except (FileNotFoundError, ProcessLookupError):
            if pid == root:
                raise
            unavailable.append({"pid": pid, "reason": "exited_during_stat_discovery"})
    if captured[root]["start_ticks"] != initial["start_ticks"]:
        raise ValueError("Private PostgreSQL postmaster identity changed during discovery")
    children = {}
    for row in captured.values():
        children.setdefault(row["parent_pid"], []).append(row)
    pending, seen, rows, skipped = [(captured[root], None)], set(), [], []
    while pending:
        expected, parent = pending.pop()
        pid = expected["pid"]
        identity = (pid, expected["start_ticks"])
        if identity in seen:
            continue
        seen.add(identity)
        # Traverse the complete captured ancestry even if an intermediate
        # process exits during later observations. Each live link is checked.
        for child in children.get(pid, []):
            if child["start_ticks"] < expected["start_ticks"]:
                skipped.append({**child, "reason": "parent_pid_reuse_or_inconsistent_start", "expected_parent": expected})
            else:
                pending.append((child, expected))
        reason = None
        try:
            row = process_masks(pid)
            current = _process_identity(pid)
            if row["start_ticks"] != expected["start_ticks"] or current["start_ticks"] != expected["start_ticks"]:
                reason = "identity_changed_during_masks"
            elif current["state"] in ("Z", "X", "x"):
                reason = "exited_during_masks"
            elif parent is not None:
                live_parent = _process_identity(parent["pid"])
                if live_parent["start_ticks"] != parent["start_ticks"] or live_parent["state"] in ("Z", "X", "x"):
                    reason = "parent_identity_changed_or_exited"
                elif row["parent_pid"] != parent["pid"] or current["parent_pid"] != parent["pid"]:
                    reason = "parent_changed_during_masks"
        except (FileNotFoundError, ProcessLookupError):
            if pid == root:
                raise
            reason = "process_or_captured_parent_exited_during_masks"
        if reason is not None:
            if pid == root:
                raise ValueError("Private PostgreSQL postmaster identity/liveness changed: " + reason)
            skipped.append({**expected, "reason": reason, "expected_parent": parent})
            continue
        rows.append(row)
    # Retain both root observations so an earlier bad mask cannot be hidden by
    # a later valid one. Missing/reused root always invalidates the snapshot.
    root_after = process_masks(root)
    final = _process_identity(root)
    if root_after["start_ticks"] != initial["start_ticks"] or final["start_ticks"] != initial["start_ticks"] or final["state"] in ("Z", "X", "x"):
        raise ValueError("Private PostgreSQL postmaster identity/liveness changed after discovery")
    return {"root_pid": root, "processes": rows, "root_after": root_after,
            "skipped_descendants": skipped, "discovery_unavailable": unavailable,
            "scope": "Parent-PID/start-time discovery and live mask snapshots; non-atomic, no continuous observation of newly spawned or exited backends"}


def require_group(snapshot, cpus, expected_start=None):
    root = next(row for row in snapshot["processes"] if row["pid"] == snapshot["root_pid"])
    if expected_start is not None and root["start_ticks"] != expected_start:
        raise ValueError("Private PostgreSQL postmaster identity changed")
    for row in snapshot["processes"]:
        require_process(row, cpus)
    if "root_after" in snapshot:
        if snapshot["root_after"]["start_ticks"] != root["start_ticks"]:
            raise ValueError("Private PostgreSQL postmaster identity changed after observation")
        require_process(snapshot["root_after"], cpus)
