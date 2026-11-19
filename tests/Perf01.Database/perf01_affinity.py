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


def process_group(root):
    """Observe the private postmaster and currently live descendants, read-only."""
    pending, seen, rows = [root], set(), []
    while pending:
        pid = pending.pop()
        if pid in seen:
            continue
        seen.add(pid)
        try:
            row = process_masks(pid)
            children = (Path("/proc") / str(pid) / "task" / str(pid) / "children").read_text()
            pending.extend(int(child) for child in children.split())
            rows.append(row)
        except (FileNotFoundError, ProcessLookupError):
            if pid == root:
                raise
    return {"root_pid": root, "processes": rows,
            "scope": "Live descendant/thread snapshots; transient exited backends are not continuously observed"}


def require_group(snapshot, cpus, expected_start=None):
    root = next(row for row in snapshot["processes"] if row["pid"] == snapshot["root_pid"])
    if expected_start is not None and root["start_ticks"] != expected_start:
        raise ValueError("Private PostgreSQL postmaster identity changed")
    for row in snapshot["processes"]:
        require_process(row, cpus)
