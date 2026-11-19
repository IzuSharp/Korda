#!/usr/bin/env python3
"""Profiling only: unchanged package application plus PostgreSQL 18 server logs.

Reuse the existing private durable cluster and A21/B23 application driver. This
adds statement-duration logging and an independent pg_stat_activity observer;
its results must never enter an uninstrumented timing cohort.
"""
from __future__ import annotations

import argparse
from array import array
import asyncio
from collections import Counter, defaultdict
from datetime import datetime
import importlib.util
import json
import math
import os
from pathlib import Path
import re
import subprocess
import sys
import time

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("perf01_database_driver", HERE / "run.py")
driver = importlib.util.module_from_spec(spec)
spec.loader.exec_module(driver)

PREFIX = "PERF01|%n|%p|%d|%a| "
SETTINGS = {"log_min_duration_statement": "0", "log_line_prefix": PREFIX,
            "log_statement": "none", "log_duration": "off",
            "log_parameter_max_length": "0", "log_parameter_max_length_on_error": "0"}
SNAPSHOT = """SELECT jsonb_build_object('server_epoch',extract(epoch from clock_timestamp()),
 'backends',COALESCE((SELECT jsonb_agg(jsonb_build_object('pid',pid,'database',datname,
 'application',application_name,'state',state,'wait_event_type',wait_event_type,
 'wait_event',wait_event,'query_start',query_start,'transaction_start',xact_start,
 'query',query)) FROM pg_stat_activity WHERE datname LIKE 'b23_%'
 AND backend_type='client backend' AND pid<>pg_backend_pid()),'[]'::jsonb));"""


def psql(pg, sql, timeout=15):
    result = subprocess.run([pg["psql"], pg["postgres_base"], "-X", "-q", "-A", "-t",
                             "-v", "ON_ERROR_STOP=1", "--no-password"],
                            input=sql, capture_output=True, text=True, timeout=timeout)
    if result.returncode:
        raise RuntimeError("Profiling PostgreSQL control failed: " + result.stderr[-1024:])
    return result.stdout


def configure(pg, output):
    sql = "SET log_min_duration_statement=-1;\n" + "\n".join(
        "ALTER SYSTEM SET " + name + "='" + value.replace("'", "''") + "';" for name, value in SETTINGS.items())
    sql += "\nSELECT pg_reload_conf();\n"
    (output / "postgres-profile-settings.sql").write_text(sql)
    (output / "postgres-profile-settings.stdout").write_text(psql(pg, sql))
    names = [*SETTINGS, "fsync", "synchronous_commit", "full_page_writes", "server_version"]
    fields = ",".join("'" + key + "',current_setting('" + key + "')" for key in names)
    deadline = time.monotonic() + 5
    while True:
        settings = json.loads(psql(pg, "SELECT jsonb_build_object(" + fields + ");"))
        if all(settings[key] == value for key, value in SETTINGS.items()):
            break
        if time.monotonic() >= deadline:
            raise RuntimeError("PostgreSQL logging configuration did not reload")
        time.sleep(.05)
    if any(settings[key] != "on" for key in ["fsync", "synchronous_commit", "full_page_writes"]) or settings["server_version"] != "18.0":
        raise RuntimeError("Profiling changed the frozen PostgreSQL version/durability contract")
    driver.save(output / "postgres-profile-settings.json", settings)


async def profile(args, output, pg):
    plan, frozen = driver.prepare(args, output, driver.descriptor(args), pg)
    plan["settings"]["instrumentation"] += "; profiling-only PostgreSQL statement-duration log and independent activity sampler"
    driver.save(output / "plan.json", plan)
    frozen["plan_sha256"] = driver.digest(output / "plan.json")
    frozen["scope"] = "PROFILING ONLY: server logging and wait polling are enabled; not comparable formal timing"
    driver.save(output / "frozen-inputs.json", frozen)
    stopped = asyncio.Event()
    samples = output / "postgres-waits.jsonl"
    failures = []
    observer = sampler = stderr = None
    primary_failure = None
    result = 1
    receipt = {"status": "starting", "errors": failures,
        "requested_idle_between_samples_ms": args.wait_sample_ms,
        "timing": "Actual query start/end is retained per sample; query time adds to the requested interval",
        "scope": "Sampled wait occupancy only; no per-request lock or pool duration is inferred"}
    driver.save(output / "postgres-observer-receipt.json", receipt)

    async def sample():
        try:
            with samples.open("w") as stream:
                while not stopped.is_set():
                    started = time.monotonic_ns()
                    observer.stdin.write((SNAPSHOT + "\n").encode())
                    await asyncio.wait_for(observer.stdin.drain(), 5)
                    line = await asyncio.wait_for(observer.stdout.readline(), 5)
                    value = json.loads(line)
                    value.update(observer_started_ns=started, observer_finished_ns=time.monotonic_ns())
                    stream.write(json.dumps(value, separators=(",", ":")) + "\n"); stream.flush()
                    try:
                        await asyncio.wait_for(stopped.wait(), args.wait_sample_ms / 1000)
                    except asyncio.TimeoutError:
                        pass
        except Exception as error:
            failures.append("sample: " + type(error).__name__ + ": " + str(error))

    try:
        # Ownership begins before spawn/setup. Even a failed initial stdin
        # write must close and reap the private observer and retain a receipt.
        stderr = (output / "postgres-observer.stderr").open("wb")
        observer = await asyncio.create_subprocess_exec(pg["psql"], pg["postgres_base"], "-X", "-q", "-A", "-t",
            "-v", "ON_ERROR_STOP=1", "--no-password", stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE, stderr=stderr)
        receipt["observer_pid"] = observer.pid
        observer.stdin.write(b"SET application_name='perf01_wait_observer'; SET log_min_duration_statement=-1; SET stats_fetch_consistency='none';\n")
        await asyncio.wait_for(observer.stdin.drain(), 5)
        receipt["status"] = "running"
        driver.save(output / "postgres-observer-receipt.json", receipt)
        sampler = asyncio.create_task(sample())
        result = await driver.execute(args, output, plan, frozen)
    except BaseException as error:
        primary_failure = error
        failures.append("profile: " + type(error).__name__ + ": " + str(error)[:1024])
        raise
    finally:
        stopped.set()
        if sampler is not None:
            try:
                await asyncio.wait_for(sampler, 11)
            except BaseException as error:
                failures.append("sampler cleanup: " + type(error).__name__)
        if observer is not None:
            try:
                observer.stdin.close()
                await asyncio.wait_for(observer.wait(), 5)
            except BaseException as error:
                failures.append("observer cleanup: " + type(error).__name__)
                if observer.returncode is None:
                    try:
                        observer.kill()
                        await asyncio.wait_for(observer.wait(), 5)
                    except BaseException as cleanup_error:
                        failures.append("observer kill/reap: " + type(cleanup_error).__name__)
            receipt["observer_exit_code"] = observer.returncode
            if observer.returncode != 0:
                failures.append("wait observer exit " + str(observer.returncode))
        if stderr is not None:
            try:
                stderr.close()
            except Exception as error:
                failures.append("observer stderr close: " + type(error).__name__)
        receipt["status"] = "failed" if failures else "completed"
        try:
            driver.save(output / "postgres-observer-receipt.json", receipt)
        except Exception:
            if primary_failure is None:
                raise  # A receipt failure must not replace an existing setup/load failure.
    return result if not failures else 1


def epoch(value):
    return datetime.fromisoformat(value).timestamp() if value else None


def windows(output):
    receipts = list((output / "applications").glob("*/perf01.json"))
    if len(receipts) != 1:
        return []
    phases = driver.load(receipts[0]).get("phases", {})
    result = []
    for name in ["load", "recovery", "idle"]:
        phase = phases.get(name, {})
        start = epoch(phase.get("start_utc"))
        end = epoch(phase.get("last_completion_utc") if name == "load" else phase.get("end_utc"))
        if start is not None and end is not None:
            result.append((start, end, name))
    return result


def summarize(output):
    phases = windows(output)
    def phase(at):
        return next((name for start, end, name in phases if start <= at <= end), "outside_known_phase")
    line_pattern = re.compile(r"^PERF01\|([0-9.]+)\|(\d+)\|([^|]*)\|([^|]*)\|\s+([A-Z]+):\s+(.*)$")
    duration_pattern = re.compile(r"^duration: ([0-9.]+) ms\s+(statement|parse|bind|execute)(?: [^:]*):\s*(.*)$")
    # Simple-protocol statement has no portal name between its kind and colon.
    statement_pattern = re.compile(r"^duration: ([0-9.]+) ms\s+statement:\s*(.*)$")
    records = output / "postgres-statements.jsonl"
    groups = defaultdict(lambda: {"durations_ms": array("d"), "sql": None})
    unknown = 0
    with (output / "postgres/server.log").open(errors="replace") as log, records.open("w") as sink:
        for line in log:
            match = line_pattern.match(line.rstrip("\n"))
            if not match:
                continue
            at, pid, database, application, severity, message = match.groups()
            if not database.startswith("b23_") or application == "psql" or application.startswith("perf01_"):
                continue
            duration = duration_pattern.match(message)
            simple = statement_pattern.match(message) if duration is None else None
            if duration is None and simple is None:
                if "duration:" in message:
                    unknown += 1
                continue
            if duration:
                milliseconds, kind, sql = duration.groups()
            else:
                milliseconds, sql = simple.groups(); kind = "statement"
            # Multiline statements retain full text in server.log; this row's
            # grouping is explicitly the first log line, never a full SQL hash.
            sql = " ".join(sql.split())
            label = phase(float(at))
            row = {"server_epoch": float(at), "pid": int(pid), "database": database,
                   "application": application, "phase": label, "protocol_step": kind,
                   "server_elapsed_ms": float(milliseconds), "sql_first_line": sql}
            sink.write(json.dumps(row, separators=(",", ":")) + "\n")
            group = groups[(label, kind, sql)]
            group["durations_ms"].append(float(milliseconds)); group["sql"] = sql
    summaries = []
    a21 = driver.load_a21()
    for (label, kind, sql), value in groups.items():
        durations = value["durations_ms"]
        summaries.append({"phase": label, "protocol_step": kind, "sql_first_line": sql,
            "observations": len(durations), "total_server_elapsed_ms": sum(durations),
            "mean_server_elapsed_ms": sum(durations) / len(durations),
            "p50_ms": a21.percentile(durations, .5), "p95_ms": a21.percentile(durations, .95),
            "p99_ms": a21.percentile(durations, .99)})
    waits = Counter(); sample_count = 0
    with (output / "postgres-waits.jsonl").open() as source:
        for line in source:
            row = json.loads(line); sample_count += 1
            for backend in row["backends"]:
                if backend["application"] == "psql" or backend["application"].startswith("perf01_"):
                    continue
                waits[(phase(float(row["server_epoch"])), backend["state"],
                       backend["wait_event_type"], backend["wait_event"])] += 1
    observer_path = output / "postgres-observer-receipt.json"
    observer_receipt = driver.load(observer_path) if observer_path.is_file() else {
        "status": "unavailable", "errors": ["Observer receipt was not created"]}
    evidence = ["postgres/server.log", "postgres-statements.jsonl", "postgres-waits.jsonl",
                "postgres-observer-receipt.json", "postgres-profile-settings.json"]
    driver.save(output / "postgres-profile-summary.json", {
        "task": "Perf-01", "kind": "profiling-only", "formal_timing_eligible": False,
        "wait_observer_passed": sample_count > 0 and observer_receipt.get("status") == "completed" and not observer_receipt["errors"],
        "wait_observer_receipt": observer_receipt,
        "load_statement_logging_observed": any(value["phase"] == "load" and value["protocol_step"] in {"execute", "statement"} for value in summaries),
        "application_summary_sha256": driver.digest(output / "summary.json") if (output / "summary.json").exists() else None,
        "statement_observations": summaries, "unparsed_duration_lines": unknown,
        "activity_snapshots": sample_count, "backend_wait_samples": [dict(zip(
            ["phase", "state", "wait_event_type", "wait_event", "samples"], [*key, count])) for key, count in waits.items()],
        "unavailable": ["exact_per_request_lock_wait_ns", "connection_pool_wait_ns", "pure_database_cpu_ns", "sqlite_execution_or_lock_wait_ns"],
        "scope": "Server elapsed times include waits. Parse/Bind/Execute are distinct and must not be counted as separate business queries. Concurrent durations cannot be subtracted from aggregate application wall time. Activity rows are unsynchronized sampled states, not exact duration. PostgreSQL and observer overhead exclude this profile from formal timing.",
        "files": {name: driver.digest(output / name) for name in evidence if (output / name).is_file()},
        "missing_files": [name for name in evidence if not (output / name).is_file()]})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package-probe", type=Path, required=True)
    parser.add_argument("--observer", type=Path)
    parser.add_argument("--comparators", type=Path)
    parser.add_argument("--route", choices=sorted(driver.load_a21().ROUTES), default="nativeweave-aot-rust")
    parser.add_argument("--postgres-bin", type=Path, required=True)
    parser.add_argument("--protocol", choices=["http1", "http1-tls", "http2-tls"], default="http1")
    parser.add_argument("--scenario", choices=["cycle", "page", "mixed"], default="mixed")
    parser.add_argument("--rate", type=float, required=True)
    parser.add_argument("--seconds", type=float, default=20)
    parser.add_argument("--concurrency", type=int, default=16)
    parser.add_argument("--wait-sample-ms", type=int, default=100)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if len(os.sched_getaffinity(0)) != 2:
        parser.error("Launch this profiling process with the same fixed two-CPU taskset as the baseline")
    if not math.isfinite(args.rate) or not 0 < args.rate <= 1000 or not math.isfinite(args.seconds) or not 1 <= args.seconds <= 300:
        parser.error("Profiling is finite: positive rate <=1000 and duration 1..300 seconds")
    if not 1 <= args.concurrency <= 128 or not 20 <= args.wait_sample_ms <= 1000:
        parser.error("Concurrency 1..128 and wait sample interval 20..1000ms required")
    vars(args).update(provider="postgresql", stage="smoke", variant="diagnostic", warmup=16,
        idle_seconds=0, recovery_requests=8, continue_classified_overload=True,
        max_p99_ms=500, max_rss_mib=256, seed=20261119, repetition=0,
        isolation_note="Exclusive profiling window; PostgreSQL duration logging and independent wait sampling enabled; excluded from formal timing")
    output = args.output.resolve(); output.mkdir(parents=True, exist_ok=False)
    driver.save(output / "profile-driver.json", {"path": str(Path(__file__).resolve()), "sha256": driver.digest(Path(__file__)),
        "source": driver.source_identity(driver.ROOT), "query": SNAPSHOT, "formal_timing_eligible": False})
    failure = None
    try:
        with driver.domain.postgres(output / "postgres", args.postgres_bin) as pg:
            configure(pg, output)
            result = asyncio.run(profile(args, output, pg))
    except BaseException as error:
        failure = error
        raise
    finally:
        if (output / "postgres-waits.jsonl").exists():
            try:
                summarize(output)
                print(output / "postgres-profile-summary.json")
            except Exception as error:
                # Partial evidence can be malformed or missing after failure;
                # retain that fact without replacing the original exception.
                try:
                    driver.save(output / "postgres-profile-summary-failure.json", {
                        "error": type(error).__name__, "message": str(error)[:1024],
                        "original_failure": type(failure).__name__ if failure is not None else None,
                        "formal_timing_eligible": False})
                except Exception:
                    if failure is None:
                        raise
                if failure is None:
                    raise
    return result


if __name__ == "__main__":
    raise SystemExit(main())
