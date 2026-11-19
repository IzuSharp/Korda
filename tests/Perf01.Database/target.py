#!/usr/bin/env python3
"""Perf-01's application adapter; B23 owns business and persistence oracles."""
from __future__ import annotations

import argparse
import asyncio
from collections import Counter
from contextvars import ContextVar
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import ssl
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/Benchmarks.Domain"))
import httpx
import http_target
from common import digest, load, load_a21, mapping_sample, save
from http_target import Target, require
from supervision import register_process
from perf01_affinity import process_masks, process_group, require_process, require_group
from perf01_resources import identity, process_sample

CONTINUED_SERVICE_STATUSES = frozenset({408, 429, 500, 502, 503, 504})


def classify_failure(error, response, expected_protocol):
    """Classify failed observations; this never turns a failure into success."""
    if isinstance(error, httpx.TimeoutException):
        return {"continuable": True, "category": "client_timeout", "error_type": type(error).__name__,
                "http_status": None, "provider_code": None}
    if response is not None and response.http_version != expected_protocol:
        return {"continuable": False, "category": "protocol_integrity", "error_type": type(error).__name__,
                "http_status": response.status_code, "observed_protocol": response.http_version,
                "expected_protocol": expected_protocol, "provider_code": None}
    status = response.status_code if response is not None else None
    return {"continuable": status in CONTINUED_SERVICE_STATUSES,
            "category": "service_error_status" if status in CONTINUED_SERVICE_STATUSES else "integrity_or_unclassified_failure",
            "error_type": type(error).__name__, "http_status": status, "provider_code": None}


def utc():
    return datetime.now(timezone.utc).isoformat()


class PerfTarget(Target):
    def __init__(self, args):
        # Replace only this benchmark process's observation function. Historical
        # B23/A21 sources, application behavior and request timing stay unchanged.
        http_target.process_sample = process_sample
        super().__init__(args)
        self.extra = {"schema_version": 1, "task": "Perf-01", "status": "running",
                      "business_retry_attempts": 0,
                      "retry_policy": "No retry; same-key replay is a declared business operation, not a retry",
                      "phases": {}, "loaded_files": {}}
        self._request = ContextVar("perf01_request", default=None)
        self._integrity_halt = False
        self._classified_counts = Counter()
        self.extra["error_policy"] = {
            "continue_classified_overload": self.manifest["perf01"].get("continue_classified_overload", False),
            "continuable_http_statuses": sorted(CONTINUED_SERVICE_STATUSES),
            "continuable_exceptions": ["httpx.ConnectTimeout", "httpx.ReadTimeout", "httpx.WriteTimeout", "httpx.PoolTimeout"],
            "all_observed_errors_fail_cell": True,
            "halt_rule": "Successful-response payload/protocol violations, authentication/validation statuses, and unclassified errors permanently stop measured HTTP dispatch",
            "provider_rule": "No HTTP status or timeout implies SQLite BUSY; exact provider codes come only from the exception observer"}
        self.affinity = self.manifest["perf01"].get("affinity", {"mode": "inherited"})
        if self.affinity["mode"] == "split":
            # taskset execs the real app in the same PID before it creates any
            # .NET/Rust threads. The Python target never leaves generator CPUs.
            command = [self.affinity["launcher"], "--cpu-list", ",".join(map(str, self.affinity["service_cpus"])),
                       *self.descriptor["command"]]
            self.application.descriptor = dict(self.descriptor, command=command)
            self.extra["affinity"] = {"plan": self.affinity, "application_launch_command": command, "observations": {}}
        self.original_stop = self.application.stop
        self.application.stop = self.finish

    async def sample_generator_resources(self, stopped, receipt):
        try:
            with (self.directory / "generator-resources.jsonl").open("w") as stream:
                while True:
                    for role, process in receipt["roles"].items():
                        row = process_sample(self.a21, process["pid"], process["start_ticks"])
                        row.update(role=role, phase=self.application.phase)
                        if row["identity_matches"] is False and role not in receipt["identity_failures"]:
                            receipt["identity_failures"].append(role)
                            receipt["errors"].append("Process start identity changed for generator role " + role)
                        stream.write(json.dumps(row, separators=(",", ":")) + "\n")
                        receipt["samples"][role] += 1
                    stream.flush()
                    if stopped.is_set():
                        break
                    try:
                        await asyncio.wait_for(stopped.wait(), receipt["interval_seconds"])
                    except asyncio.TimeoutError:
                        pass
            receipt["status"] = "failed" if receipt["errors"] else "completed"
        except BaseException as error:
            receipt["status"] = "failed"
            receipt["errors"].append(type(error).__name__ + ": " + str(error)[:512])
        finally:
            receipt["finished_utc"] = utc()
            save(self.directory / "generator-observer-receipt.json", receipt)

    def observe_affinity(self, phase):
        if self.affinity["mode"] != "split":
            return
        observation = {"target": process_masks(os.getpid()), "scheduler": process_masks(os.getppid()),
                       "application": process_masks(self.application.process.pid)}
        self.extra["affinity"]["observations"][phase] = observation
        require_process(observation["target"], self.affinity["generator_cpus"])
        require_process(observation["scheduler"], self.affinity["generator_cpus"])
        require_process(observation["application"], self.affinity["service_cpus"])
        if self.manifest["provider"] == "postgresql":
            observation["postgres"] = process_group(self.affinity["postgres_root_pid"])
            require_group(observation["postgres"], self.affinity["service_cpus"], self.affinity["postgres_start_ticks"])

    async def request(self, method, path, *, body=None, headers=None, stage=None):
        self._request.set({"method": method, "path": path, "stage": stage})
        return await super().request(method, path, body=body, headers=headers, stage=stage)

    def capture_error(self, identifier, error):
        # B23 has already marked this operation failed and set halt_dispatch.
        # Keep its exact error evidence and failed verdict before deciding
        # whether subsequent ordinary arrivals may still reach the real app.
        super().capture_error(identifier, error)
        observed = self.last_response.get()
        response = observed[0] if observed is not None else None
        expected = "HTTP/2" if self.manifest["protocol"] == "http2-tls" else "HTTP/1.1"
        classification = classify_failure(error, response, expected)
        if not classification["continuable"]:
            self._integrity_halt = True
        enabled = self.extra["error_policy"]["continue_classified_overload"]
        continued = enabled and classification["continuable"] and not self._integrity_halt
        if continued:
            self.halt_dispatch = False
        self._classified_counts[classification["category"]] += 1
        row = {"logical_id": identifier, "observed_ns": time.monotonic_ns(), **classification,
               "next_arrivals_continue": continued, "permanent_integrity_halt": self._integrity_halt,
               "request_attempt": self._request.get()}
        with (self.directory / "classified-errors.jsonl").open("a") as stream:
            stream.write(json.dumps(row, separators=(",", ":")) + "\n")

    async def seed(self):
        # Every scenario starts from exactly the same 128 HTTP-created fixtures.
        # B23 assigns old fixed timestamps, so later writes cannot change page 1.
        await super().seed()
        self.observe_affinity("after_seed")
        executable = Path(f"/proc/{self.application.process.pid}/exe")
        executable_hash = digest(executable)
        route_executable = self.descriptor["command"][0]
        expected_hash = self.descriptor["artifacts"].get(route_executable)
        require(expected_hash is not None and executable_hash == expected_hash,
                "Actual mapped application executable differs from the pinned route")
        self.extra["loaded_files"][os.readlink(executable)] = {"sha256": executable_hash,
            "route_executable": route_executable, "expected_sha256": expected_hash,
            "method": "Actual /proc/application_pid/exe after READY, including taskset exec replacement"}
        mappings = mapping_sample(self.application.process.pid)
        for path, value in mappings.get("modules", {}).items():
            candidate = Path(path)
            if candidate.is_file() and candidate.name.endswith(".so"):
                self.extra["loaded_files"][path] = {"sha256": digest(candidate), **value}
        self.extra["phases"]["seed_finished"] = {"utc": utc(), "monotonic_ns": time.monotonic_ns()}

    async def operation(self, identifier, item):
        if isinstance(identifier, int) and identifier < self.manifest["requests"]:
            phase = self.extra["phases"].setdefault("load", {"start_utc": utc(), "first_dispatch_ns": time.monotonic_ns()})
            phase["last_dispatch_ns"] = time.monotonic_ns()
            phase["dispatches"] = phase.get("dispatches", 0) + 1
        result = await super().operation(identifier, item)
        if isinstance(identifier, int) and identifier < self.manifest["requests"]:
            self.extra["phases"]["load"].update(last_completion_ns=time.monotonic_ns(), last_completion_utc=utc())
        return result

    def persisted_counts(self):
        """Keep independent committed volume even when B23's exact oracle fails."""
        result = {"utc": utc(), "monotonic_ns": time.monotonic_ns(),
                  "method": "Separate SQLite/psql SELECTs after measured dispatch; no application counters"}
        try:
            rows = self.database.sql(f"SELECT request_key FROM operations WHERE request_key LIKE '{self.prefix}%'")
            phases = Counter()
            for row in rows:
                suffix = row[0][len(self.prefix):]
                if suffix.startswith("seed-"):
                    phase = "seed"
                elif suffix.startswith("recovery-"):
                    phase = "recovery"
                elif suffix.isdigit():
                    identifier = int(suffix)
                    phase = "measurement" if identifier < self.manifest["requests"] else "warmup"
                else:
                    phase = "unclassified"
                phases[phase] += 1
            counts = {"operations": len(rows)}
            for table in ("items", "outbox"):
                counts[table] = int(self.database.scalar(
                    f"SELECT COUNT(*) FROM {table} WHERE operation_id IN "
                    f"(SELECT operation_id FROM operations WHERE request_key LIKE '{self.prefix}%')"))
            result.update(availability="measured", committed_operations_by_phase=dict(phases), persisted_rows=counts,
                          scope="Committed mutation rows visible to the independent reader; paging has no mutation. Exact content/cardinality acceptance remains the unchanged B23 durable oracle.")
        except Exception as error:
            result.update(availability="unavailable", reason=type(error).__name__,
                          committed_operations_by_phase=None, persisted_rows=None)
        return result

    async def finish(self):
        """After measured work drains, check low-rate recovery in the SAME process."""
        try:
            if self.application.process is not None and self.application.process.returncode is None and hasattr(self.application, "url"):
                self.observe_affinity("after_load_before_recovery")
                self.extra["persisted_before_recovery"] = self.persisted_counts()
                verify = ssl.create_default_context(cafile=self.config["tlsCertificatePem"]) if self.config.get("tlsCertificatePem") else True
                self.application.phase = "post-load-recovery"
                recovery = {"start_utc": utc(), "start_ns": time.monotonic_ns(), "operations": []}
                self.extra["phases"]["recovery"] = recovery
                async with httpx.AsyncClient(base_url=self.application.url, http2=self.manifest["protocol"] == "http2-tls",
                        verify=verify, trust_env=False, timeout=30,
                        headers={"Authorization": "Bearer " + self.token},
                        limits=httpx.Limits(max_connections=1, max_keepalive_connections=1)) as self.client:
                    for index in range(self.manifest["perf01"]["recovery_requests"]):
                        scheduled = recovery["start_ns"] + index * 250_000_000
                        await asyncio.sleep(max(0, (scheduled - time.monotonic_ns()) / 1e9))
                        started = time.monotonic_ns()
                        try:
                            status, _ = await super().operation("recovery-" + str(index), {"kind": "cycle", "title": "recovery-" + str(index)})
                            require(status == "ok", "Post-load recovery failed")
                            recovery["operations"].append({"scheduled_ns": scheduled, "started_ns": started,
                                                          "completed_ns": time.monotonic_ns(), "status": status})
                        except Exception as error:
                            recovery["operations"].append({"scheduled_ns": scheduled, "started_ns": started,
                                                          "completed_ns": time.monotonic_ns(), "status": "failed", "error": type(error).__name__})
                            raise
                    recovery.update(end_ns=time.monotonic_ns(), end_utc=utc(), oracle=self.durable_oracle())
                self.application.phase = "post-load-idle"
                idle = {"start_utc": utc(), "start_ns": time.monotonic_ns(),
                        "before": process_sample(self.a21, self.application.process.pid)}
                self.extra["phases"]["idle"] = idle
                deadline = idle["start_ns"] + int(self.manifest["perf01"]["idle_seconds"] * 1e9)
                while time.monotonic_ns() < deadline:
                    await asyncio.sleep(min(1, (deadline - time.monotonic_ns()) / 1e9))
                    require(self.application.process.returncode is None, "Application exited during idle observation")
                idle.update(end_ns=time.monotonic_ns(), end_utc=utc(),
                            after=process_sample(self.a21, self.application.process.pid))
                self.observe_affinity("after_idle_before_shutdown")
        finally:
            self.application.phase = "final-reconciliation-and-shutdown"
            self.extra["persisted_before_shutdown"] = self.persisted_counts()
            await self.original_stop()

    async def run(self):
        result = 1
        controller = self.manifest["perf01"]["resource_observer"]["controller"]
        actual_controller = identity(controller["pid"])
        target, scheduler = identity(os.getpid()), identity(os.getppid())
        require(actual_controller["start_ticks"] == controller["start_ticks"] and
                scheduler["parent_pid"] == controller["pid"], "Generator role process identity differs from its controller")
        receipt = {"status": "running", "started_utc": utc(), "errors": [], "identity_failures": [],
                   "interval_seconds": self.manifest["perf01"]["resource_observer"]["generator_interval_seconds"],
                   "roles": {"target": target, "scheduler": scheduler, "controller": controller},
                   "samples": {role: 0 for role in ("target", "scheduler", "controller")},
                   "scope": "Nominal 1 Hz process observations including a final sample; actual timestamps and phase labels are retained. Read failures remain per-field unavailability."}
        save(self.directory / "generator-observer-receipt.json", receipt)
        stopped = asyncio.Event()
        sampler = asyncio.create_task(self.sample_generator_resources(stopped, receipt))
        try:
            result = await super().run()
            self.extra["status"] = "passed" if result == 0 else "failed"
            return result
        finally:
            stopped.set()
            try:
                await sampler
            except BaseException as error:
                receipt["status"] = "failed"
                receipt["errors"].append(type(error).__name__ + ": " + str(error)[:512])
            resources = self.directory / "generator-resources.jsonl"
            self.extra["generator_resource_observer"] = {**receipt,
                "path": resources.name, "sha256": digest(resources) if resources.exists() else None}
            if receipt["status"] != "completed":
                self.extra["status"] = "failed"
            self.extra["classified_error_counts"] = dict(self._classified_counts)
            classified = self.directory / "classified-errors.jsonl"
            self.extra["classified_error_evidence"] = {"path": classified.name, "sha256": digest(classified)} if classified.exists() else None
            self.extra["permanent_integrity_halt"] = self._integrity_halt
            log = self.directory / "application/stderr.log"
            counts = Counter()
            codes = Counter()
            if self.descriptor.get("perf01_exception_observer"):
                for line in log.read_text(errors="replace").splitlines() if log.exists() else []:
                    if not line.startswith("B23_ADVERSE_EXCEPTION "):
                        continue
                    counts["observed_exceptions"] += 1
                    match = re.search(r" provider=(\S*) correlation=", line)
                    if match and match[1]:
                        codes[match[1]] += 1
                        if self.manifest["provider"] == "sqlite" and match[1].isdigit():
                            primary = int(match[1]) & 255
                            if primary == 5:
                                counts["sqlite_busy"] += 1
                            elif primary == 6:
                                counts["sqlite_locked"] += 1
                self.extra["provider_errors"] = {"availability": "measured", "provider_code_counts": dict(codes),
                    "sqlite_busy": counts["sqlite_busy"] if self.manifest["provider"] == "sqlite" else None,
                    "sqlite_locked": counts["sqlite_locked"] if self.manifest["provider"] == "sqlite" else None,
                    "observed_exceptions": counts["observed_exceptions"],
                    "method": "Catch/log/rethrow observer; complete process stderr, including setup and recovery; primary result code = extended code & 255"}
            else:
                self.extra["provider_errors"] = {"availability": "unavailable", "sqlite_busy": None,
                    "sqlite_locked": None, "provider_code_counts": None,
                    "method": "Ordinary HTTP status cannot identify a database provider code"}
            self.extra["counter_limits"] = {name: {"availability": "unavailable", "value": None} for name in (
                "database_execution_ns", "database_lock_wait_ns", "connection_pool_wait_ns", "ffi_calls",
                "copied_bytes", "native_allocator_live_bytes", "sqlite_busy_handler_attempts")}
            save(self.directory / "perf01.json", self.extra)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--route", required=True, choices=sorted(load_a21().ROUTES))
    parser.add_argument("--a21", type=Path)
    args = parser.parse_args()
    register_process("http-adapter", os.getpid())
    return asyncio.run(PerfTarget(args).run())


if __name__ == "__main__":
    raise SystemExit(main())
