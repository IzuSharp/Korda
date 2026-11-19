#!/usr/bin/env python3
"""Profiling-only launcher for the existing Perf-01 DB A21 scheduler process.

The formal driver, A21 source, application and GC policy remain unchanged.
In-memory hooks add overhead; no output from this launcher is formal timing.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import gc
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import runpy
import sys
import time

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
DOMAIN = ROOT / "src/Benchmarks.Domain"
METHODS = ("raw_event_write", "raw_event_flush", "json_loads", "json_dumps", "state_event", "observe_process")
SLOW_NS = 1_000_000
MAX_CALLS = MAX_COLLECTIONS = 20_000


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, sort_keys=True, allow_nan=False) + "\n")
    temporary.replace(path)


def anchor():
    return {"monotonic_ns": time.monotonic_ns(), "utc": datetime.now(timezone.utc).isoformat()}


def exact_python():
    require(sys.version_info[:3] == (3, 12, 14), "The diagnostic requires the frozen Python 3.12.14")
    return {"version": sys.version, "executable": str(Path(sys.executable).resolve()), "sha256": sha(Path(sys.executable).resolve())}


class Diagnostics:
    def __init__(self, receipt):
        self.receipt = receipt
        self.active = None
        self.phase = "worker-startup"
        self.previous_event = None
        self.calls, self.collections = [], []
        self.call_dropped = self.collection_dropped = 0
        self.methods = {name: {"calls": 0, "errors": 0, "total_ns": 0, "maximum_ns": 0, "maximum_interval": None} for name in METHODS}
        self.generations = {generation: {"collections": 0, "total_ns": 0, "maximum_ns": 0, "maximum_interval": None,
                                         "collected": 0, "uncollectable": 0} for generation in (0, 1, 2)}
        self.gc_start = None
        self.callback = self.gc_event
        self.result_shape = None
        self.retained_result = None
        self.retained_state = None
        self.initial = {"task": "Perf-01", "formal_timing_eligible": False, "status": "running", "pid": os.getpid(),
                        "python": exact_python(), "started": anchor(), "gc_enabled_before": gc.isenabled(),
                        "gc_threshold_before": gc.get_threshold(), "gc_count_before": gc.get_count(),
                        "gc_stats_before": gc.get_stats(), "other_gc_callbacks_before": len(gc.callbacks), "profiler_sha256": sha(Path(__file__)),
                        "slow_call_threshold_ns": SLOW_NS, "maximum_call_records": MAX_CALLS,
                        "maximum_gc_records": MAX_COLLECTIONS,
                        "scope": "Child A21 scheduler only; original HTTP target, package application, PostgreSQL and formal driver files are unchanged"}
        require(self.initial["gc_enabled_before"], "GC was already disabled; do not change policy to profile this workload")
        save(receipt, self.initial)
        gc.callbacks.append(self.callback)

    def gc_event(self, phase, info):
        now = time.monotonic_ns()
        if phase == "start":
            self.gc_start = (now, info["generation"], self.active, self.phase, self.previous_event, gc.get_count())
            return
        if phase != "stop" or self.gc_start is None:
            return
        start, generation, active, scope, previous, counts = self.gc_start
        self.gc_start = None
        duration = now - start
        stats = self.generations[generation]
        stats["collections"] += 1; stats["total_ns"] += duration
        stats["collected"] += info.get("collected", 0); stats["uncollectable"] += info.get("uncollectable", 0)
        if duration > stats["maximum_ns"]:
            stats.update(maximum_ns=duration, maximum_interval=[start, now])
        if len(self.collections) < MAX_COLLECTIONS:
            self.collections.append((start, now, generation, active, scope, previous, counts, gc.get_count(),
                                     info.get("collected", 0), info.get("uncollectable", 0)))
        else:
            self.collection_dropped += 1

    def call(self, name, function, *args, **kwargs):
        previous = self.active
        self.active = name
        started = time.monotonic_ns()
        error = False
        try:
            return function(*args, **kwargs)
        except BaseException:
            error = True
            raise
        finally:
            finished = time.monotonic_ns()
            self.active = "profiler-bookkeeping"
            duration = finished - started
            stats = self.methods[name]
            stats["calls"] += 1; stats["errors"] += error; stats["total_ns"] += duration
            if duration > stats["maximum_ns"]:
                stats.update(maximum_ns=duration, maximum_interval=[started, finished])
            if duration >= SLOW_NS:
                if len(self.calls) < MAX_CALLS:
                    self.calls.append((started, finished, name, self.phase, self.previous_event, error))
                else:
                    self.call_dropped += 1
            self.active = previous

    def finish(self, status, error=None):
        if self.callback in gc.callbacks:
            gc.callbacks.remove(self.callback)
        self.phase = "post-run-introspection"
        entries = getattr(getattr(self.retained_state, "oracle", None), "entries", None)
        oracle = {"availability": "unavailable", "reason": "No retained expected-value Oracle"}
        if isinstance(entries, list):
            pages = [entry for entry in entries if isinstance(entry.get("expected"), dict)
                     and entry["expected"].get("kind") == "list"]
            oracle = {"availability": "measured-after-run", "entries": len(entries),
                      "distinct_entry_object_ids": len({id(entry) for entry in entries}),
                      "distinct_expected_object_ids": len({id(entry["expected"]) for entry in entries}),
                      "distinct_input_tokens": len({entry["input"] for entry in entries}), "page_entries": len(pages),
                      "distinct_page_expected_object_ids": len({id(entry["expected"]) for entry in pages}),
                      "distinct_page_titles_list_ids": len({id(entry["expected"]["titles"]) for entry in pages}),
                      "distinct_page_versions_list_ids": len({id(entry["expected"]["versions"]) for entry in pages}),
                      "page_title_lengths": sorted({len(entry["expected"]["titles"]) for entry in pages}),
                      "page_version_lengths": sorted({len(entry["expected"]["versions"]) for entry in pages}),
                      "distinct_page_input_tokens": len({entry["input"] for entry in pages}),
                      "all_page_expected_equal_first": all(entry["expected"] == pages[0]["expected"] for entry in pages) if pages else None,
                      "scope": "Post-run traversal only; identity cardinality and equality, not allocated bytes. No expected payload is copied into this receipt."}
            del pages
        # No full tracked-graph enumeration runs during the application load.
        # This allocates a temporary list and is deliberately after run_cell.
        graph = {str(generation): len(gc.get_objects(generation)) for generation in (0, 1, 2)}
        receipt = dict(self.initial, status=status, finished=anchor(), error=error,
                       gc_enabled_after=gc.isenabled(), gc_threshold_after=gc.get_threshold(),
                       gc_count_after=gc.get_count(), gc_stats_after=gc.get_stats(),
                       gc_callback_incomplete=self.gc_start, methods=self.methods, generations=self.generations,
                       slow_call_records_dropped=self.call_dropped, gc_records_dropped=self.collection_dropped,
                       tracked_objects_after_run=graph, retained_result_shape=self.result_shape, retained_oracle_shape=oracle,
                       tracked_graph_scope="len(gc.get_objects(generation)) once after the worker run while the original returned result and RunState/Oracle are retained; enumeration allocates temporary lists and is not a during-load sample",
                       gc_count_scope="gc.get_count reports allocation/collection counters, not the number of live tracked objects",
                       final_gc_stats_scope="gc_count_after/gc_stats_after include post-run introspection after this profiler's callback was removed; use gc_events for the observed callback intervals",
                       clock="Linux time.monotonic_ns; directly align with this child's A21 dispatch and observed-event timestamps",
                       limitations=["Hooks and bounded numeric buffers add CPU/allocation overhead; profiling only.",
                                    "Method durations are wall time and can include descheduling or nested GC; they are not CPU time.",
                                    "GC duration is start-to-stop callback observation and includes callback overhead.",
                                    "previous_state_event is the last completed event-hook call, not necessarily the event being parsed/written.",
                                    "active_method is a wall-interval label, not proof of the initiating stack or thread.",
                                    "No syscall/scheduler stack trace is collected. Uninstrumented awaits and synchronous work remain unattributed.",
                                    "A terminated worker can leave only the initial running receipt; that is an incomplete profile."])
        receipt["slow_calls"] = [dict(zip(("start_ns", "end_ns", "method", "phase", "previous_state_event", "raised"), row)) for row in self.calls]
        receipt["gc_events"] = [dict(zip(("start_ns", "end_ns", "generation", "active_method", "phase", "previous_state_event",
                                          "count_before", "count_after", "collected", "uncollectable"), row)) for row in self.collections]
        receipt["gc_policy_unchanged"] = (receipt["gc_enabled_before"] == receipt["gc_enabled_after"]
                                          and receipt["gc_threshold_before"] == receipt["gc_threshold_after"])
        save(self.receipt, receipt)

    def instrument(self, a21):
        diagnostics = self
        original_json, original_event = a21.json, a21.RunState.event
        original_observe, original_run = a21.observe_process, a21.run_cell

        class JsonProxy:
            def loads(self, *args, **kwargs):
                return diagnostics.call("json_loads", original_json.loads, *args, **kwargs)

            def dumps(self, *args, **kwargs):
                return diagnostics.call("json_dumps", original_json.dumps, *args, **kwargs)

            def __getattr__(self, name):
                return getattr(original_json, name)

        class EventWriter:
            def __init__(self, stream):
                self.stream = stream

            def write(self, value):
                return diagnostics.call("raw_event_write", self.stream.write, value)

            def flush(self):
                return diagnostics.call("raw_event_flush", self.stream.flush)

            def __getattr__(self, name):
                return getattr(self.stream, name)

        class ProfilePath(type(Path())):
            def open(self, *args, **kwargs):
                stream = super().open(*args, **kwargs)
                mode = args[0] if args else kwargs.get("mode", "r")
                return EventWriter(stream) if self == event_path and "w" in mode else stream

        event_path = None

        def event(state, value, *args, **kwargs):
            if diagnostics.retained_state is None:
                diagnostics.retained_state = state
            if diagnostics.phase == "run-cell-startup-and-warmup":
                diagnostics.phase = "measurement-events"
            result = diagnostics.call("state_event", original_event, state, value, *args, **kwargs)
            diagnostics.previous_event = (value.get("kind"), value.get("id"))
            if len(state.seen_completion) == len(state.records):
                diagnostics.phase = "after-last-controller-completion"
            return result

        async def run_cell(plan, route, repetition, base, output, **options):
            nonlocal event_path
            event_path = Path(output).with_suffix(".events.jsonl")
            diagnostics.phase = "run-cell-startup-and-warmup"
            result = await original_run(plan, route, repetition, base, ProfilePath(output), **options)
            diagnostics.phase = "after-run-cell"
            diagnostics.retained_result = result
            rows = result.get("records", [])
            diagnostics.result_shape = {"raw_valid": result.get("valid"), "invalid_reasons": result.get("invalid_reasons"),
                "records": len(rows), "records_with_target_counters": sum("target_counters" in row for row in rows),
                "retained_target_counter_entries": sum(len(row.get("target_counters", {})) for row in rows),
                "records_with_normalized_value": sum("value" in row for row in rows), "resource_samples": len(result.get("resource_samples", [])),
                "scope": "Container cardinalities of the retained result immediately after run_cell; not allocated bytes or unique-object counts"}
            return result

        a21.json = JsonProxy()
        a21.RunState.event = event
        if hasattr(a21.RunState, "rehydrate_completion_payloads"):
            original_rehydrate = a21.RunState.rehydrate_completion_payloads

            def rehydrate(state, *args, **kwargs):
                diagnostics.phase = "post-window-completion-replay"
                return original_rehydrate(state, *args, **kwargs)

            a21.RunState.rehydrate_completion_payloads = rehydrate
        a21.observe_process = lambda *args, **kwargs: diagnostics.call("observe_process", original_observe, *args, **kwargs)
        a21.run_cell = run_cell


def import_driver():
    sys.path.insert(0, str(HERE)); sys.path.insert(0, str(DOMAIN))
    spec = importlib.util.spec_from_file_location("perf01_profile_db_driver", HERE / "run.py")
    driver = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(driver)
    return driver


def worker(args):
    require(args.command and Path(args.command[0]).resolve() == DOMAIN / "cell_worker.py", "Only the existing B23 cell worker may be profiled")
    require(args.expected_a21.resolve() == ROOT / "src/Benchmarks.Harness/nativeweave_bench.py", "Unexpected A21 source")
    sys.path.insert(0, str(DOMAIN))
    import common
    diagnostics = Diagnostics(args.receipt)
    original_load = common.load_a21
    calls = 0

    def load(path=None):
        nonlocal calls
        require(Path(path).resolve() == args.expected_a21.resolve() and calls == 0, "Unexpected or repeated child A21 load")
        calls += 1
        result = original_load(path)
        diagnostics.initial["a21_sha256"] = sha(args.expected_a21)
        diagnostics.instrument(result)
        return result

    common.load_a21 = load
    previous_argv = sys.argv
    status, error, code = "completed", None, 0
    primary = None
    try:
        sys.argv = args.command
        try:
            runpy.run_path(args.command[0], run_name="__main__")
        except SystemExit as stopped:
            code = stopped.code if isinstance(stopped.code, int) else 0 if stopped.code is None else 1
            if code:
                raise
        require(calls == 1 and diagnostics.result_shape is not None, "A21 run did not produce its retained result")
        require(all(diagnostics.methods[name]["calls"] > 0 for name in METHODS), "At least one requested A21 hook observed no call")
    except BaseException as failure:
        primary = failure
        status, error = "failed", type(failure).__name__ + ": " + str(failure)[:512]
        raise
    finally:
        sys.argv = previous_argv
        common.load_a21 = original_load
        try:
            diagnostics.finish(status, error)
        except BaseException as receipt_error:
            if primary is None:
                raise
            print("A21_PROFILE_RECEIPT_FAILURE " + type(receipt_error).__name__, file=sys.stderr)
    return code


def launch(args):
    python = exact_python()
    command = args.command
    require(command.count("--output") == 1 and command.count("--variant") == 1, "Use explicit --output and --variant arguments")
    output = Path(command[command.index("--output") + 1]).resolve()
    require(command[command.index("--variant") + 1] == "diagnostic", "Use --variant diagnostic; formal labels are forbidden")
    require(not output.exists(), "Diagnostic output must be new")
    driver = import_driver()
    original_prepare, original_supervised = driver.prepare, driver.supervised_command
    marker = {"task": "Perf-01", "formal_timing_eligible": False, "status": "starting", "started": anchor(), "python": python,
              "source_files": {str(path): sha(path) for path in (Path(__file__), HERE / "run.py", DOMAIN / "cell_worker.py", ROOT / "src/Benchmarks.Harness/nativeweave_bench.py")},
              "driver_arguments": command, "profile_scope": "Existing application load with instrumented child A21 generator; no GC policy change"}
    launcher_receipt = output.with_name(output.name + "-launcher.json")
    require(not launcher_receipt.exists(), "Launcher receipt already exists")
    save(launcher_receipt, marker)

    def prepare(*values, **kwargs):
        plan, frozen = original_prepare(*values, **kwargs)
        frozen["formal_timing_eligible"] = False
        frozen["generator_profiling"] = marker
        driver.save(output / "frozen-inputs.json", frozen)
        save(output / "profile-driver.json", marker)
        return plan, frozen

    async def supervised(command, *values, **kwargs):
        require(Path(command[0]).resolve() == Path(sys.executable).resolve() and Path(command[1]).resolve() == DOMAIN / "cell_worker.py", "Unexpected scheduler command")
        wrapped = [command[0], str(Path(__file__).resolve()), "worker", "--receipt", str(output / "a21-generator-profile.json"),
                   "--expected-a21", str(ROOT / "src/Benchmarks.Harness/nativeweave_bench.py"), "--", *command[1:]]
        marker["actual_child_command"] = wrapped
        save(output / "profile-driver.json", marker)
        return await original_supervised(wrapped, *values, **kwargs)

    driver.prepare, driver.supervised_command = prepare, supervised
    previous_argv = sys.argv
    primary, result = None, None
    try:
        sys.argv = [str(HERE / "run.py"), *command]
        result = driver.main()
        child_receipt = output / "a21-generator-profile.json"
        require(child_receipt.is_file(), "Worker did not leave a profiling receipt")
        child_result = json.loads(child_receipt.read_text())
        require(child_result.get("status") == "completed" and child_result.get("gc_policy_unchanged") is True,
                "Worker profile is incomplete or GC policy changed")
        return result
    except BaseException as failure:
        primary = failure
        marker["error"] = type(failure).__name__ + ": " + str(failure)[:512]
        raise
    finally:
        sys.argv = previous_argv
        marker.update(status="failed" if primary is not None else "completed", finished=anchor(), driver_exit_code=result)
        try:
            receipt = output / "a21-generator-profile.json"
            marker["worker_profile"] = {"path": str(receipt), "sha256": sha(receipt)} if receipt.exists() else None
            save(launcher_receipt, marker)
            if output.is_dir(): save(output / "profile-driver.json", marker)
        except BaseException:
            if primary is None:
                raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_subparsers(dest="mode", required=True)
    start = modes.add_parser("launch"); start.add_argument("command", nargs=argparse.REMAINDER)
    child = modes.add_parser("worker")
    child.add_argument("--receipt", type=Path, required=True)
    child.add_argument("--expected-a21", type=Path, required=True)
    child.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if args.command and args.command[0] == "--": args.command = args.command[1:]
    return launch(args) if args.mode == "launch" else worker(args)


if __name__ == "__main__":
    raise SystemExit(main())
