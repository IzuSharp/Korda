#!/usr/bin/env python3
"""Perf-01's application adapter; B23 owns business and persistence oracles."""
from __future__ import annotations

import argparse
import asyncio
from collections import Counter
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
from common import digest, load, load_a21, mapping_sample, process_sample, save
from http_target import Target, require
from supervision import register_process


def utc():
    return datetime.now(timezone.utc).isoformat()


class PerfTarget(Target):
    def __init__(self, args):
        super().__init__(args)
        self.extra = {"schema_version": 1, "task": "Perf-01", "status": "running",
                      "business_retry_attempts": 0,
                      "retry_policy": "No retry; same-key replay is a declared business operation, not a retry",
                      "phases": {}, "loaded_files": {}}
        self.original_stop = self.application.stop
        self.application.stop = self.finish

    async def seed(self):
        # Every scenario starts from exactly the same 128 HTTP-created fixtures.
        # B23 assigns old fixed timestamps, so later writes cannot change page 1.
        await super().seed()
        mappings = mapping_sample(self.application.process.pid)
        for path, value in mappings.get("modules", {}).items():
            candidate = Path(path)
            if candidate.is_file() and (candidate.name.endswith(".so") or path == self.descriptor["command"][0]):
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
        finally:
            self.extra["persisted_before_shutdown"] = self.persisted_counts()
            await self.original_stop()

    async def run(self):
        result = 1
        try:
            result = await super().run()
            self.extra["status"] = "passed" if result == 0 else "failed"
            return result
        finally:
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
                "copied_bytes", "native_allocator_live_bytes")}
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
