#!/usr/bin/env python3
"""A21 external open-loop controller. No application/runtime package dependency.

All request timestamps use this observer's monotonic clock. Admission is the
time the observer receives the target's admission event, not a cross-process
subtraction of unrelated clocks. IPC and observer scheduling are included.
"""
from __future__ import annotations

import argparse
import asyncio
import base64
from collections import Counter
import hashlib
import heapq
import json
import math
import os
from pathlib import Path
import platform
import random
import statistics
import sys
import time

ROUTES = {"direct-rust", "nativeweave-aot-rust", "managed-aot"}
OUTCOMES = {"ok", "failed", "rejected", "cancelled", "timeout", "unsupported"}
COUNTERS = {
    "managed_allocated_bytes": "bytes", "native_allocations": "count",
    "native_allocated_bytes": "bytes", "copied_bytes": "bytes",
    "pinned_bytes": "bytes", "retained_bytes": "bytes", "ffi_calls": "count",
    "live_owners": "count", "live_permits": "count", "callback_roots": "count",
    "operations": "count", "queue_slots": "count", "byte_credits": "bytes",
    "connections": "count", "runtime_instances": "count",
    "gc_heap_live_bytes": "bytes", "gc_heap_committed_bytes": "bytes",
    "native_allocator_live_bytes": "bytes", "native_allocator_retained_bytes": "bytes",
}


def sha256(path):
    """Hash actual artifact bytes, never a command/path label."""
    with open(path, "rb") as stream:
        digest = hashlib.sha256()
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
        return digest.hexdigest()


def unknown(unit, reason):
    """An unavailable measurement must not be represented by zero."""
    return {"value": None, "unit": unit, "availability": "unknown", "method": reason}


def measured(value, unit, method):
    return {"value": value, "unit": unit, "availability": "measured", "method": method}


def validate_counters(counters):
    if not isinstance(counters, dict):
        raise ValueError("Counter observations must be an object")
    if set(counters) != set(COUNTERS):
        raise ValueError("Counter set must exactly match the versioned A21 schema")
    for name, unit in COUNTERS.items():
        item = counters[name]
        if not isinstance(item, dict):
            raise ValueError("Each counter observation must be an object: " + name)
        if item.get("unit") != unit or not item.get("method"):
            raise ValueError("Missing counter unit or observation method: " + name)
        if item.get("availability") == "unknown":
            if item.get("value") is not None:
                raise ValueError("Unknown counter is not zero: " + name)
        elif item.get("availability") in {"measured", "derived"}:
            if type(item.get("value")) is not int or item["value"] < 0:
                raise ValueError("Counter must be a nonnegative integer: " + name)
        else:
            raise ValueError("Unknown availability classification: " + name)


def mix64(value):
    """Shared scalar fixture oracle, explicitly not a domain benchmark."""
    for _ in range(8):
        value = ((value ^ (value >> 27)) * 0x3C79AC492BA7B653) & ((1 << 64) - 1)
    return value


class Oracle:
    """A fixture oracle or immutable domain input/expected-value dataset supplied by B23."""

    def __init__(self, plan, base):
        definition = plan.get("oracle", {"kind": "scalar-mix64"})
        self.entries = None
        if definition["kind"] == "expected-values":
            path = (base / definition["dataset"]).resolve()
            if sha256(path) != definition["sha256"]:
                raise ValueError("Functional oracle dataset hash mismatch")
            self.entries = json.loads(path.read_text())
            if not isinstance(self.entries, list) or len(self.entries) != plan["timing"]["requests"]:
                raise ValueError("Expected-value dataset must cover every scheduled request")
            for entry in self.entries:
                if "expected" not in entry or type(entry.get("input")) not in {str, int}:
                    raise ValueError("Oracle entries require an input token and expected result")
                if any(character in str(entry["input"]) for character in "\t\r\n"):
                    raise ValueError("Input tokens cannot contain line-protocol separators")
        elif definition["kind"] != "scalar-mix64":
            raise ValueError("Unsupported functional oracle")

    def input(self, identifier):
        return identifier + 1 if self.entries is None else self.entries[identifier % len(self.entries)]["input"]

    def expected(self, identifier):
        return mix64(identifier + 1) if self.entries is None else self.entries[identifier % len(self.entries)]["expected"]


def percentile(values, quantile):
    """Nearest rank, withheld when fewer than one sample represents the tail."""
    if not 0 < quantile < 1:
        raise ValueError("Quantile must be in (0, 1)")
    if len(values) < math.ceil(1 / (1 - quantile) - 1e-9):
        return None
    return sorted(values)[math.ceil(len(values) * quantile) - 1]


def histogram(values, resolution_ns, maximum_ns, correction="none"):
    """Keep raw integer bins and overflow; observed arrivals are never corrected."""
    if correction != "none":
        raise ValueError("Expected-interval correction would double-correct observed arrivals")
    if resolution_ns <= 0 or maximum_ns < resolution_ns:
        raise ValueError("Invalid histogram resolution or range")
    bins, overflow = Counter(), []
    for value in values:
        if type(value) is not int or value < 0:
            raise ValueError("Latency must be a nonnegative integer")
        if value > maximum_ns:
            overflow.append(value)
        else:
            bins[str(value // resolution_ns * resolution_ns)] += 1
    return {
        "resolution_ns": resolution_ns, "maximum_ns": maximum_ns,
        "bucket_convention": "inclusive lower bound, exclusive lower+resolution",
        "correction": "none; scheduled arrivals observed directly",
        "count": len(values), "bins": dict(sorted(bins.items(), key=lambda x: int(x[0]))),
        "overflow_count": len(overflow), "overflow_ns": overflow,
        "percentiles_from_exact_samples_ns": {
            label: percentile(values, q)
            for label, q in [("p50", .5), ("p90", .9), ("p95", .95), ("p99", .99), ("p99.9", .999)]
        },
    }


def omission_counterexample():
    """Finite negative control: service p99 1 ms, arrival latency p99 100 ms."""
    scheduled = list(range(100))
    service = [100] + [1] * 99
    completed, clock = [], 0
    for arrival, duration in zip(scheduled, service):
        clock = max(clock, arrival) + duration
        completed.append(clock)
    arrival_latency = [end - start for end, start in zip(completed, scheduled)]
    return {"kind": "finite_accounting_negative_control", "unit": "synthetic_ms",
            "service_p99": percentile(service, .99), "arrival_p99": percentile(arrival_latency, .99),
            "scheduled": scheduled, "service": service, "completed": completed,
            "claim": "Accounting only; no measured domain performance or runtime race proof."}


def observe_process(pid):
    """Linux observations; unknown on other OS. No inference of a whole RSS bound."""
    units = {"rss_bytes": "bytes", "peak_rss_bytes": "bytes", "private_working_set_bytes": "bytes",
             "threads": "count", "main_thread_stack_bytes": "bytes", "all_thread_stacks_bytes": "bytes",
             "mapped_code_bytes": "bytes", "kernel_socket_bytes": "bytes", "child_process_bytes": "bytes"}
    result = {name: unknown(unit, "No portable observation is available") for name, unit in units.items()}
    if platform.system() != "Linux":
        return result
    try:
        values = {}
        for line in Path(f"/proc/{pid}/status").read_text().splitlines():
            key, _, value = line.partition(":")
            if value.strip():
                values[key] = value.strip().split()[0]
        for name, key, scale in [("rss_bytes", "VmRSS", 1024), ("peak_rss_bytes", "VmHWM", 1024),
                                 ("threads", "Threads", 1), ("main_thread_stack_bytes", "VmStk", 1024),
                                 ("mapped_code_bytes", "VmExe", 1024)]:
            if key in values:
                result[name] = measured(int(values[key]) * scale, units[name], f"Linux /proc/pid/status {key}")
        private = 0
        fields = 0
        for line in Path(f"/proc/{pid}/smaps_rollup").read_text().splitlines():
            if line.startswith(("Private_Clean:", "Private_Dirty:")):
                private += int(line.split()[1]) * 1024
                fields += 1
        if fields == 2:
            result["private_working_set_bytes"] = measured(private, "bytes", "Linux smaps_rollup Private_Clean + Private_Dirty")
    except (OSError, ValueError) as error:
        result["observation_note"] = type(error).__name__ + ": process data unavailable or process exited"
    return result


def host_metadata():
    """Capture relevant reproducibility context without host/user identifiers."""
    cpu = {}
    try:
        first = Path("/proc/cpuinfo").read_text().split("\n\n")[0]
        for line in first.splitlines():
            key, _, value = line.partition(":")
            if key.strip() in {"vendor_id", "cpu family", "model", "model name", "stepping", "flags"}:
                cpu[key.strip()] = value.strip()
    except OSError:
        cpu["unavailable"] = True
    governors = []
    for path in sorted(Path("/sys/devices/system/cpu").glob("cpu[0-9]*/cpufreq/scaling_governor")):
        try:
            governors.append(path.read_text().strip())
        except OSError:
            pass
    limits = {}
    for key in ["cpu.max", "memory.max", "memory.high"]:
        try:
            limits[key] = Path("/sys/fs/cgroup", key).read_text().strip()
        except OSError:
            limits[key] = None
    return {"os": platform.system(), "kernel": platform.release(), "arch": platform.machine(),
            "python": platform.python_version(), "cpu": cpu, "logical_cpu_count": os.cpu_count(),
            "affinity": sorted(os.sched_getaffinity(0)) if hasattr(os, "sched_getaffinity") else None,
            "power_governors": governors or None, "cgroup_limits": limits,
            "background_load": list(os.getloadavg()) if hasattr(os, "getloadavg") else None,
            "clock": {"name": "time.monotonic_ns", "resolution_seconds": time.get_clock_info("monotonic").resolution},
            "power_policy_note": "Unknown when the host does not expose cpufreq; container does not control host power."}


def validate_plan(plan, base):
    if plan.get("schema_version") != 1:
        raise ValueError("Unsupported workload plan version")
    if len(plan.get("routes", [])) != 3 or {x["id"] for x in plan["routes"]} != ROUTES:
        raise ValueError("Declare exactly the three comparison routes")
    for key in ["workload_id", "workload_kind", "source_commit", "settings", "engineering_cost", "regression"]:
        if key not in plan:
            raise ValueError("Missing predeclared input: " + key)
    if plan["workload_kind"] not in {"harness-smoke", "domain"}:
        raise ValueError("Workload kind must distinguish harness smoke from domain results")
    if plan["workload_kind"] == "domain" and plan.get("oracle", {}).get("kind") != "expected-values":
        raise ValueError("B23 domain workloads require an immutable independent expected-values oracle")
    if type(plan.get("seed")) is not int or type(plan.get("repetitions")) is not int or plan["repetitions"] < 2:
        raise ValueError("Use a fixed seed and at least two independent process repetitions")
    for key in ["cache", "connection_reuse", "durability", "tls", "serialization", "allocator",
                "isa_profile", "warmup_requests", "features", "instrumentation"]:
        if key not in plan["settings"]:
            raise ValueError("Missing comparable workload setting: " + key)
    if plan["settings"]["isa_profile"] in {"native", "host-native", ""}:
        raise ValueError("A portable release comparison must override host-native ISA")
    if type(plan["settings"]["warmup_requests"]) is not int or not 0 <= plan["settings"]["warmup_requests"] <= 10000:
        raise ValueError("Invalid warmup request count")
    timing = plan["timing"]
    for key in ["requests", "period_ns", "timeout_ns", "max_dispatch_lag_ns", "max_in_flight", "cleanup_timeout_ns"]:
        if type(timing.get(key)) is not int or timing[key] <= 0:
            raise ValueError("Timing/queue inputs must be positive integers: " + key)
    if timing["requests"] > 1_000_000:
        raise ValueError("Finite run size exceeds supported observation bound")
    Oracle(plan, base)
    histogram([], plan["histogram"]["resolution_ns"], plan["histogram"]["maximum_ns"])
    regression = plan["regression"]
    if not regression.get("baseline_id") or regression.get("absolute_noise_ns", -1) < 0 or regression.get("relative_noise", -1) < 0:
        raise ValueError("Predeclare a baseline and its measured noise band")
    for route in plan["routes"]:
        command = route.get("command")
        if not command or any(not isinstance(x, str) or not x for x in command):
            raise ValueError("Target command must be an argument vector")
        if not route.get("artifacts") or not route.get("toolchain") or not route.get("lock_hashes"):
            raise ValueError("Pin target artifacts, compiler/linker identity and lockfiles")
        for group in ["artifacts", "lock_hashes"]:
            for file, expected in route[group].items():
                path = (base / file).resolve()
                if sha256(path) != expected:
                    raise ValueError("Artifact/lockfile hash mismatch: " + file)
        executable = str((base / command[0]).resolve())
        if executable not in {str((base / f).resolve()) for f in route["artifacts"]}:
            raise ValueError("Executed target must be included in the artifact hashes")


class RunState:
    """Bounded finite request ledger and strict event-state validation."""

    def __init__(self, count, oracle=None):
        self.records = [{"id": i, "scheduled_ns": None, "dispatch_ns": None, "admission_ns": None,
                         "completion_ns": None, "status": None, "deadline_ns": None,
                         "attempts": 0, "request_wire_bytes": 0, "response_wire_bytes": 0} for i in range(count)]
        self.errors = []
        self.late_events = []
        self.seen_completion = set()
        self.last_counters = None
        self.last_completion_observed_ns = None
        self.last_admission_observed_ns = None
        self.in_flight = set()
        self.deadlines = []
        self.oracle = oracle

    def expire(self, now):
        while self.deadlines and self.deadlines[0][0] <= now:
            deadline, identifier = heapq.heappop(self.deadlines)
            row = self.records[identifier]
            if row["status"] is None:
                row["completion_ns"], row["status"] = now, "timeout"
                row["timeout_observed_ns"] = now

    def event(self, event, now, wire_bytes=0):
        if not isinstance(event, dict):
            raise ValueError("Target event must be an object")
        identifier = event.get("id")
        if type(identifier) is not int or not 0 <= identifier < len(self.records):
            raise ValueError("Target event has an unknown request ID")
        row = self.records[identifier]
        if row["dispatch_ns"] is None:
            raise ValueError("Target event arrived before dispatch")
        row["response_wire_bytes"] += wire_bytes
        if row["status"] is None and row["deadline_ns"] is not None and now >= row["deadline_ns"]:
            row["completion_ns"], row["status"] = now, "timeout"
            row["timeout_observed_ns"] = now
        if event.get("kind") == "admitted":
            if row["admission_ns"] is not None or identifier in self.seen_completion:
                raise ValueError("Duplicate or out-of-order admission")
            row["admission_ns"] = now
            self.last_admission_observed_ns = now
            return
        if event.get("kind") != "completed" or identifier in self.seen_completion:
            raise ValueError("Unknown or duplicate completion event")
        status = event.get("status")
        if status not in OUTCOMES:
            raise ValueError("Unknown outcome classification")
        if status == "ok" and row["admission_ns"] is None:
            raise ValueError("Successful completion without admission")
        expected = mix64(identifier + 1) if self.oracle is None else self.oracle.expected(identifier)
        if status == "ok" and event.get("value") != expected:
            raise ValueError("Functional oracle mismatch")
        validate_counters(event["counters"])
        self.seen_completion.add(identifier)
        self.in_flight.discard(identifier)
        self.last_completion_observed_ns = now
        self.last_counters = event["counters"]
        row["target_counters"] = event["counters"]
        row["target_completion_observed_ns"] = now
        if row["status"] is not None:
            self.late_events.append({"observed_ns": now, "event": event})
            return
        row["completion_ns"] = now
        row["status"] = status
        row["value"] = event.get("value")


async def run_cell(plan, route, repetition, base, output):
    """One fresh process per cell; dispatch scheduling never awaits responses."""
    timing = plan["timing"]
    oracle = Oracle(plan, base)
    state = RunState(timing["requests"], oracle)
    command = list(route["command"])
    command[0] = str((base / command[0]).resolve())
    process = await asyncio.create_subprocess_exec(*command, cwd=base, stdin=asyncio.subprocess.PIPE,
                                                  stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
                                                  limit=65536)
    stop = asyncio.Event()
    stderr_task = asyncio.create_task(process.stderr.read(1_048_576))
    resources = []
    raw_path = output.with_suffix(".events.jsonl")
    raw_events = raw_path.open("w", encoding="utf-8")
    try:
        hello = json.loads(await asyncio.wait_for(process.stdout.readline(), 10))
        if not isinstance(hello, dict) or hello.get("kind") != "hello" or hello.get("route") != route["id"]:
            raise ValueError("Target did not identify the declared route")
        if route["id"] != "direct-rust" and hello.get("dynamic_code_supported") is not False:
            raise ValueError("A JIT target cannot be reported as a Native AOT comparator")
        # Warmup is outside the measurement window and explicitly counted.
        for i in range(plan["settings"]["warmup_requests"]):
            identifier = timing["requests"] + i
            process.stdin.write(f"{identifier}\t{oracle.input(identifier)}\tok\n".encode())
            await process.stdin.drain()
            admitted = json.loads(await asyncio.wait_for(process.stdout.readline(), 10))
            complete = json.loads(await asyncio.wait_for(process.stdout.readline(), 10))
            if not isinstance(admitted, dict) or not isinstance(complete, dict) or admitted.get("kind") != "admitted" or admitted.get("id") != identifier or complete.get("kind") != "completed" or complete.get("status") != "ok" or complete.get("id") != identifier or complete.get("value") != oracle.expected(identifier):
                raise ValueError("Warmup oracle mismatch")
            validate_counters(complete["counters"])
        origin = time.monotonic_ns() + 20_000_000
        for row in state.records:
            row["scheduled_ns"] = origin + row["id"] * timing["period_ns"]
            row["deadline_ns"] = row["scheduled_ns"] + timing["timeout_ns"]

        async def read_events():
            try:
                while line := await process.stdout.readline():
                    observed = time.monotonic_ns()
                    # Persist exact bytes before parsing; malformed events are evidence too.
                    raw_events.write(json.dumps({"observed_ns": observed,
                                                 "wire_base64": base64.b64encode(line).decode("ascii")}) + "\n")
                    raw_events.flush()
                    state.event(json.loads(line), observed, len(line))
            except (ValueError, KeyError, TypeError) as error:
                state.errors.append("integrity: " + str(error))
                stop.set()

        async def sample_resources():
            while not stop.is_set():
                resources.append({"time_ns": time.monotonic_ns(), "counters": observe_process(process.pid)})
                try:
                    await asyncio.wait_for(stop.wait(), .02)
                except asyncio.TimeoutError:
                    pass

        reader = asyncio.create_task(read_events())
        sampler = asyncio.create_task(sample_resources())
        for row in state.records:
            remaining = row["scheduled_ns"] - time.monotonic_ns()
            if remaining > 0:
                await asyncio.sleep(remaining / 1e9)
            now = time.monotonic_ns()
            state.expire(now)
            if stop.is_set():
                row["completion_ns"], row["status"] = now, "cancelled"
                continue
            if now >= row["deadline_ns"]:
                row["completion_ns"], row["status"] = now, "timeout"
                row["timeout_observed_ns"] = now
                row["reason"] = "generator_missed_deadline"
                state.errors.append("generator saturation: scheduled deadline passed before dispatch")
                continue
            outstanding = len(state.in_flight)
            if outstanding >= timing["max_in_flight"]:
                row["completion_ns"], row["status"] = now, "rejected"
                row["reason"] = "generator_capacity"
                state.errors.append("generator saturation: in-flight capacity")
                continue
            row["dispatch_ns"] = now
            row["attempts"] = 1
            state.in_flight.add(row["id"])
            heapq.heappush(state.deadlines, (row["scheduled_ns"] + timing["timeout_ns"], row["id"]))
            if now - row["scheduled_ns"] > timing["max_dispatch_lag_ns"]:
                state.errors.append("generator saturation: dispatch lag")
            mode = plan.get("faults", {}).get(str(row["id"]), "ok")
            request = f"{row['id']}\t{oracle.input(row['id'])}\t{mode}\n".encode()
            row["request_wire_bytes"] = len(request)
            process.stdin.write(request)
            try:
                await asyncio.wait_for(process.stdin.drain(), timing["timeout_ns"] / 1e9)
            except (BrokenPipeError, ConnectionResetError, asyncio.TimeoutError):
                state.errors.append("generator/target input stalled or closed")
                stop.set()
        while any(row["status"] is None for row in state.records) and not stop.is_set():
            now = time.monotonic_ns()
            state.expire(now)
            await asyncio.sleep(.001)
        for row in state.records:
            if row["status"] is None:
                row["completion_ns"], row["status"] = time.monotonic_ns(), "cancelled"
        process.stdin.close()
        cleanup = "process_exited; not a resource-retirement proof"
        try:
            await asyncio.wait_for(process.wait(), timing["cleanup_timeout_ns"] / 1e9)
        except asyncio.TimeoutError:
            cleanup = "process_killed_after_cleanup_deadline; retirement_unknown"
            process.kill()
            await process.wait()
        await reader
        stop.set()
        await sampler
        stderr = (await stderr_task).decode("utf-8", errors="replace")
        if process.returncode != 0:
            state.errors.append("target exit: " + str(process.returncode))
        observation_end = max(max(row["completion_ns"] for row in state.records),
                              state.last_completion_observed_ns or origin, state.last_admission_observed_ns or origin)
        elapsed = observation_end - origin
        histograms = {}
        for label, start, end, success_only in [
                ("arrival_all_outcomes", "scheduled_ns", "completion_ns", False),
                ("arrival_success", "scheduled_ns", "completion_ns", True),
                ("dispatch_delay", "scheduled_ns", "dispatch_ns", False),
                ("admission_observer_delay", "dispatch_ns", "admission_ns", False),
                ("service_observer", "admission_ns", "completion_ns", True)]:
            values = [row[end] - row[start] for row in state.records
                      if row[start] is not None and row[end] is not None and (not success_only or row["status"] == "ok")]
            histograms[label] = histogram(values, plan["histogram"]["resolution_ns"], plan["histogram"]["maximum_ns"])
        result = {"schema_version": 1, "route": route["id"], "repetition": repetition,
                  "valid": not state.errors, "invalid_reasons": sorted(set(state.errors)),
                  "workload_kind": plan["workload_kind"], "hello": hello, "records": state.records,
                  "outcome_counts": dict(Counter(row["status"] for row in state.records)),
                  "offered": len(state.records), "admitted": sum(row["admission_ns"] is not None for row in state.records),
                  "completed": len(state.seen_completion), "successful": sum(row["status"] == "ok" for row in state.records),
                  "measurement_window_ns": {"start": origin, "end": observation_end, "elapsed": elapsed},
                  "rates_per_second": {"offered": 1e9 / timing["period_ns"],
                                       "admitted": sum(row["admission_ns"] is not None for row in state.records) * 1e9 / max(1, elapsed),
                                       "completed": len(state.seen_completion) * 1e9 / max(1, elapsed)},
                  "histograms": histograms, "late_events": state.late_events,
                  "raw_events": {"path": raw_path.name, "sha256": sha256(raw_path), "encoding": "JSONL records with exact base64 wire bytes"},
                  "last_target_counters": state.last_counters, "resource_samples": resources,
                  "cleanup": cleanup, "stderr": stderr, "exit_code": process.returncode,
                  "clock_scope": "one controller monotonic clock; admission event observed over IPC",
                  "target_artifacts": route["artifacts"], "instrumentation": plan["settings"]["instrumentation"]}
        output.write_text(json.dumps(result, indent=2) + "\n")
        return result
    finally:
        raw_events.close()
        stop.set()
        if process.returncode is None:
            process.kill()
            await process.wait()
        if not stderr_task.done():
            stderr_task.cancel()


def noise_comparison(baseline, candidate, band, seed):
    """Bootstrap independent process means; a finite descriptive interval, not proof."""
    if len(baseline) < 2 or len(candidate) < 2:
        return {"decision": "inconclusive", "reason": "At least two independent processes per route required"}
    rng = random.Random(seed)
    effect = statistics.mean(candidate) - statistics.mean(baseline)
    boot = sorted(statistics.mean(rng.choices(candidate, k=len(candidate))) -
                  statistics.mean(rng.choices(baseline, k=len(baseline))) for _ in range(2000))
    low, high = boot[49], boot[1949]
    margin = max(band["absolute_noise_ns"], abs(statistics.mean(baseline)) * band["relative_noise"])
    decision = "regression" if low > margin else "improvement" if high < -margin else "within_band" if low >= -margin and high <= margin else "inconclusive"
    return {"effect_ns": effect, "bootstrap_95_percent_interval_ns": [low, high], "noise_band_ns": margin,
            "decision": decision, "method": "2000 seeded independent-process bootstrap resamples",
            "baseline_id": band["baseline_id"], "warning": "Small repetition counts limit statistical precision."}


async def run_plan(plan_path, output):
    plan = json.loads(plan_path.read_text())
    base = plan_path.resolve().parent
    validate_plan(plan, base)
    output.mkdir(parents=True, exist_ok=False)
    (output / "plan.json").write_text(json.dumps(plan, indent=2) + "\n")
    jobs = [(route, rep) for rep in range(plan["repetitions"]) for route in plan["routes"]]
    random.Random(plan["seed"]).shuffle(jobs)
    cells = []
    for route, rep in jobs:
        path = output / f"{route['id']}-{rep}.json"
        try:
            cells.append(await run_cell(plan, route, rep, base, path))
        except (OSError, ValueError, KeyError, asyncio.TimeoutError) as error:
            failure = {"route": route["id"], "repetition": rep, "valid": False,
                       "invalid_reasons": [type(error).__name__ + ": " + str(error)],
                       "stage": "startup/protocol", "claim": "Failed cell retained, not a performance success"}
            path.write_text(json.dumps(failure, indent=2) + "\n")
            cells.append(failure)
    means = {}
    for route in ROUTES:
        matching = [cell for cell in cells if cell["route"] == route]
        means[route] = [statistics.mean(row["completion_ns"] - row["scheduled_ns"] for row in cell["records"])
                       for cell in matching if cell["valid"] and all(row["status"] == "ok" for row in cell.get("records", []))]
    comparable = all(cell["valid"] and cell.get("successful") == plan["timing"]["requests"] for cell in cells)
    if not plan["regression"].get("enabled", True):
        comparisons = {"decision": "not_requested", "reason": "Harness smoke has no reviewed domain baseline; no performance claim"}
    elif comparable:
        comparisons = {route: noise_comparison(means["direct-rust"], means[route], plan["regression"], plan["seed"])
                       for route in sorted(ROUTES - {"direct-rust"})}
    else:
        comparisons = {"decision": "inconclusive", "reason": "Failed, rejected, timed-out or invalid cells retained"}
    report = {"schema_version": 1, "plan_sha256": sha256(plan_path), "host": host_metadata(),
              "run_order": [[route["id"], rep] for route, rep in jobs],
              "all_cells_valid": all(cell["valid"] for cell in cells), "comparisons": comparisons,
              "cell_sha256": {path.name: sha256(path) for path in sorted(output.iterdir()) if path.is_file()},
              "claim": "Harness accounting and route integration only; no Database/ORM/Server performance conclusion."
                       if plan["workload_kind"] == "harness-smoke" else
                       "Only the declared workload, supplied independent oracle and exact artifacts; invalid cells never establish performance."}
    (output / "summary.json").write_text(json.dumps(report, indent=2) + "\n")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    run = commands.add_parser("run", help="Run a predeclared three-route workload plan")
    run.add_argument("plan", type=Path)
    run.add_argument("output", type=Path)
    commands.add_parser("omission", help="Execute the finite coordinated-omission negative control")
    commands.add_parser("host", help="Record local observation context without machine identifiers")
    args = parser.parse_args()
    if args.command == "omission":
        print(json.dumps(omission_counterexample(), indent=2))
    elif args.command == "host":
        print(json.dumps(host_metadata(), indent=2))
    else:
        report = asyncio.run(run_plan(args.plan, args.output))
        print(json.dumps({"all_cells_valid": report["all_cells_valid"], "output": str(args.output)}))
        return 0 if report["all_cells_valid"] else 1
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, KeyError) as error:
        print("ERROR: " + str(error), file=sys.stderr)
        raise SystemExit(2)
