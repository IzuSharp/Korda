#!/usr/bin/env python3
"""One immutable Perf-01 whole-application DB cell using A21/B23 machinery.

Runs a single selected route. Independent process ordering and before/after
cohort ownership belong to the Perf-01 integration controller. No product build
or timing/statistics replacement is performed here.
"""
from __future__ import annotations

import argparse
import asyncio
import base64
from collections import Counter
from contextlib import nullcontext
from datetime import datetime, timezone
import hashlib
import importlib.metadata
import importlib.util
import json
import math
import os
from pathlib import Path
import shutil
import sys

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
DOMAIN = ROOT / "src/Benchmarks.Domain"
sys.path.insert(0, str(DOMAIN))
from common import check_files, digest, expected_value, load, load_a21, save, source_identity
import run as domain
from supervision import supervised_command
from perf01_affinity import cpu_list, process_masks, process_group, require_process, require_group
from perf01_resources import identity, summarize as summarize_resources


def utc():
    return datetime.now(timezone.utc).isoformat()


def payload(scenario, index, seed):
    kind = "cycle" if scenario == "cycle" or scenario == "mixed" and index % 2 == 0 else "list"
    return {"kind": kind, "title": ("perf01-" + str(seed) + "-" + str(index))[:32]} if kind == "cycle" else {"kind": "list", "limit": 100}


def dataset(scenario, count, seed):
    result = []
    for index in range(count):
        value = payload(scenario, index, seed)
        encoded = base64.urlsafe_b64encode(json.dumps(value, separators=(",", ":")).encode()).decode()
        result.append({"input": encoded, "expected": expected_value(value)})
    return result


def observed_resources(path, role=None):
    with path.open() as stream:
        rows = (json.loads(line) for line in stream)
        by_phase = summarize_resources(row for row in rows if role is None or row["role"] == role)
    if role is None:
        for phase in by_phase.values():
            phase["app_cpu_seconds"] = phase["cpu_seconds"]
            phase["app_cpu_percent_one_core"] = phase["cpu_percent_one_core"]
    return by_phase


def descriptor(args):
    if args.route == "nativeweave-aot-rust":
        value = domain.product_route(args.package_probe)
        if args.observer:
            observer = load(args.observer)
            if observer.get("state") != "built":
                raise ValueError("Exception observer must have an exact completed build receipt")
            check_files(observer["artifacts"])
            expected = load(args.package_probe / "progress.json")["inputs"]["native_sha256"]
            if observer["native_sha256"] != expected:
                raise ValueError("Observer does not use the exact baseline/candidate package native modules")
            value.update(command=[observer["executable"]], artifacts=observer["artifacts"],
                         perf01_exception_observer=True, observer_manifest={"path": str(args.observer.resolve()), "sha256": digest(args.observer)})
        return value
    if not args.comparators:
        raise ValueError("A comparator route needs its actual immutable build manifest")
    return domain.comparator_route(args.route, load(args.comparators)["routes"][args.route], args.comparators)


def prepare(args, output, route, pg):
    a21 = load_a21()
    compact_observation = bool(getattr(args, "compact_observation", False))
    service_cpus = sorted(os.sched_getaffinity(0))
    generator_cpus = getattr(args, "generator_cpus", None)
    affinity = {"mode": "inherited", "service_cpus": service_cpus, "generator_cpus": None}
    if generator_cpus is not None:
        if len(service_cpus) != 2 or len(generator_cpus) != 2 or set(service_cpus).intersection(generator_cpus):
            raise ValueError("Generator isolation requires two service CPUs and two distinct generator CPUs")
        launcher = shutil.which("taskset")
        if launcher is None:
            raise ValueError("Optional generator isolation requires the existing taskset executable")
        affinity.update(mode="split", generator_cpus=generator_cpus, launcher=str(Path(launcher).resolve()))
        affinity["controller_before"] = process_masks(os.getpid())
        require_process(affinity["controller_before"], service_cpus)
        if args.provider == "postgresql":
            pid = int((output / "postgres/data/postmaster.pid").read_text().splitlines()[0])
            group = process_group(pid); require_group(group, service_cpus)
            root = next(row for row in group["processes"] if row["pid"] == pid)
            affinity.update(postgres_root_pid=pid, postgres_start_ticks=root["start_ticks"], postgres_before=group)
    period = round(1e9 / args.rate)
    # A21 permits up to 100 ms dispatch lag. The first dispatch can be late
    # while the last catches up, so reserve 250 ms beyond a named long stage.
    # Acceptance below still checks the actual first-to-last dispatch span.
    guard_seconds = .25 if args.stage == "soak" else 0
    count = math.ceil((args.seconds + guard_seconds) * 1e9 / period) + 1
    if count > 1_000_000:
        raise ValueError("A21 finite accounting limit is one million requests")
    if args.stage == "soak" and args.seconds < 1800:
        raise ValueError("A Perf-01 soak needs at least 1800 seconds of scheduled offered load")
    if args.stage != "smoke" and len(os.sched_getaffinity(0)) != 2:
        raise ValueError("Timed DB cells require an explicit fixed two-CPU affinity; launch through taskset")
    resource_observer = {"controller": identity(os.getpid()), "generator_interval_seconds": 1,
                         "application_interval_seconds": .05,
                         "scope": "Independent process CPU/memory/fd fields; generator roles are target, scheduler and controller"}
    paths = [HERE / "run.py", HERE / "target.py", HERE / "perf01_affinity.py", HERE / "perf01_resources.py",
             DOMAIN / "run.py", DOMAIN / "http_target.py", DOMAIN / "common.py",
             DOMAIN / "cell_worker.py", DOMAIN / "supervision.py", ROOT / "src/Benchmarks.Harness/nativeweave_bench.py",
             ROOT / "tests/Reference.TaskService/golden.py"]
    files = {str(path): digest(path) for path in paths}
    if generator_cpus is not None:
        files[affinity["launcher"]] = digest(Path(affinity["launcher"]))
    client = {}
    for name in ("httpx", "httpcore", "h11", "h2", "hpack", "hyperframe"):
        path = Path(importlib.util.find_spec(name).origin).resolve()
        files[str(path)] = digest(path)
        client[name] = importlib.metadata.version(name)
    for name in ("postgres", "psql", "initdb", "pg_ctl") if args.provider == "postgresql" else ():
        path = args.postgres_bin.resolve() / name
        files[str(path)] = digest(path)
    if args.protocol != "http1":
        for name in ("localhost.pem", "localhost.private.pem"):
            path = args.package_probe.resolve() / "tls" / name
            files[str(path)] = digest(path)
    data = dataset(args.scenario, count, args.seed)
    data_path = output / "dataset.json"
    save(data_path, data)
    profile = {"id": "perf01-" + args.scenario, "kind": "list", "rate": args.rate,
               "requests": count, "warmup": args.warmup, "client_concurrency": args.concurrency,
               "cache": "128 fixed ordered seed rows, equivalent new database, warm mappings; new durable mutation keys"}
    manifest = {"schema_version": 1, "profile": profile, "provider": args.provider, "protocol": args.protocol,
                "routes": {args.route: route}, "requests": count, "max_in_flight": 512,
                "diagnostic_snapshot_settle_ms": 1100, "run_directory": str(output / "applications"),
                "golden": str(ROOT / "tests/Reference.TaskService/golden.py"), "files": files, **pg,
                "perf01": {"scenario": args.scenario, "stage": args.stage, "idle_seconds": args.idle_seconds,
                           "recovery_requests": args.recovery_requests,
                           "compact_observation": compact_observation,
                           "continue_classified_overload": args.continue_classified_overload, "affinity": affinity,
                           "resource_observer": resource_observer}}
    if args.protocol != "http1":
        manifest.update(tls_certificate=str(args.package_probe.resolve() / "tls/localhost.pem"),
                        tls_private_key=str(args.package_probe.resolve() / "tls/localhost.private.pem"))
    manifest_path = output / "target.json"
    save(manifest_path, manifest)
    python = Path(sys.executable).resolve()
    target = {"id": args.route, "command": [str(python), str(HERE / "target.py"), "--manifest", str(manifest_path),
              "--route", args.route, "--a21", str(ROOT / "src/Benchmarks.Harness/nativeweave_bench.py")],
              "artifacts": {**route["artifacts"], **files, str(python): digest(python), str(manifest_path): digest(manifest_path)},
              "lock_hashes": route["lock_hashes"], "toolchain": route["toolchain"]}
    settings = load(DOMAIN / "profile-v1.json")["settings"]
    plan = {"schema_version": 1, "workload_id": "Perf-01-" + args.provider + "-" + args.scenario,
            "workload_kind": "domain", "seed": args.seed, "repetitions": 3,
            "oracle": {"kind": "expected-values", "dataset": str(data_path), "sha256": digest(data_path)},
            "settings": {"cache": profile["cache"], "connection_reuse": "Persistent HTTPX bounded client pool",
                "durability": settings[args.provider], "tls": args.protocol, "serialization": "Unchanged B21 static JSON, typed Fluent and DTO path",
                "allocator": "Unchanged package/comparator allocator", "isa_profile": "portable-x86-64-default-no-host-native",
                "warmup_requests": args.warmup, "features": {"scenario": args.scenario, "concurrency": args.concurrency,
                    "continue_classified_overload": args.continue_classified_overload,
                    "compact_observation": compact_observation,
                    "service_cpus": service_cpus, "generator_cpus": generator_cpus},
                "instrumentation": "External procfs application at nominal 20 Hz and target/scheduler/controller at nominal 1 Hz, independent CPU/memory/fd fields; optional identical exception catch/log/rethrow observer"},
            "timing": {"requests": count, "period_ns": period, "timeout_ns": 30_000_000_000,
                "max_dispatch_lag_ns": 100_000_000, "max_in_flight": 512, "cleanup_timeout_ns": 180_000_000_000},
            "histogram": {"resolution_ns": 1000, "maximum_ns": 60_000_000_000},
            "regression": {"enabled": False, "baseline_id": "Perf-01-independent-cohort-analysis", "absolute_noise_ns": 0, "relative_noise": 0},
            "routes": [target]}
    # A21's command-level comparative schema demands all three routes. This is
    # explicitly one scoped cell; run_cell never claims
    # unexecuted route/cohort acceptance. Artifact/oracle checks still run here.
    check_files(target["artifacts"]); check_files(target["lock_hashes"]); a21.Oracle(plan, output)
    save(output / "plan.json", plan)
    frozen = {"task": "Perf-01", "stage": args.stage, "variant": args.variant, "created_utc": utc(),
              "driver_source": source_identity(ROOT), "source_files": files, "route": route,
              "host": a21.host_metadata(), "python_libraries": client, "isolation_note": args.isolation_note,
              "affinity": affinity,
              "compact_observation": compact_observation,
              "resource_observer": resource_observer,
              "plan_sha256": digest(output / "plan.json"), "dataset_sha256": digest(data_path),
              "planned_load_seconds": (count - 1) * period / 1e9,
              "service_standard": {"max_unexpected_errors": 0, "max_p99_ms": args.max_p99_ms,
                                   "max_app_rss_mib": args.max_rss_mib, "max_generator_dispatch_lag_ms": 100},
              "scope": "Whole application, real HTTP and durable DB; no isolated database latency attribution"}
    save(output / "frozen-inputs.json", frozen)
    return plan, frozen


async def execute(args, output, plan, frozen):
    affinity = frozen["affinity"]
    if affinity["mode"] != "split":
        return await execute_cell(args, output, plan, frozen)
    before = process_masks(os.getpid())
    require_process(before, affinity["service_cpus"])
    after = None
    try:
        # PostgreSQL has already started on the original service CPUs. Only
        # subsequently spawned A21/HTTPX processes inherit the generator mask.
        os.sched_setaffinity(0, affinity["generator_cpus"])
        during = process_masks(os.getpid()); require_process(during, affinity["generator_cpus"])
        save(output / "controller-affinity-before.json", {"before": before, "during": during, "plan": affinity})
        return await execute_cell(args, output, plan, frozen)
    finally:
        try:
            after = process_masks(os.getpid())
        finally:
            os.sched_setaffinity(0, affinity["service_cpus"])
            save(output / "controller-affinity-after.json", {"before_restore": after,
                 "restored": process_masks(os.getpid()), "scope": "Controller main mask restored before PostgreSQL teardown"})


async def execute_cell(args, output, plan, frozen):
    command = [str(Path(sys.executable).resolve()), str(DOMAIN / "cell_worker.py"), "--plan", str(output / "plan.json"),
               "--route", args.route, "--repetition", str(args.repetition), "--output", str(output / "observation.json"),
               "--a21", str(ROOT / "src/Benchmarks.Harness/nativeweave_bench.py")]
    if frozen.get("compact_observation", False):
        command.append("--compact-observation")
    save(output / "progress.json", {"state": "running", "started_utc": utc(), "frozen_inputs_sha256": digest(output / "frozen-inputs.json")})
    timeout = 20 * args.warmup + frozen["planned_load_seconds"] + 225
    receipt = await supervised_command(command, output / "supervision", timeout, 5, output / "control.json", load_a21())
    if frozen["affinity"]["mode"] == "split":
        affinity = frozen["affinity"]
        observed = {"controller": process_masks(os.getpid())}
        if args.provider == "postgresql":
            observed["postgres"] = process_group(affinity["postgres_root_pid"])
        save(output / "controller-affinity-end.json", observed)
        require_process(observed["controller"], affinity["generator_cpus"])
        if "postgres" in observed:
            require_group(observed["postgres"], affinity["service_cpus"], affinity["postgres_start_ticks"])
    summary = {"task": "Perf-01", "variant": args.variant, "stage": args.stage, "route": args.route,
               "provider": args.provider, "scenario": args.scenario, "rate": args.rate, "concurrency": args.concurrency,
               "finished_utc": utc(), "valid": False, "qualifies_service_standard": False,
               "supervision": receipt["status"], "frozen_inputs_sha256": digest(output / "frozen-inputs.json")}
    if receipt["status"] == "completed" and (output / "observation.json").exists():
        row = load(output / "observation.json")
        domain.validate_observation(row, args.route, args.repetition, plan["timing"]["requests"], load_a21())
        directory = Path(row["hello"]["receipt_directory"])
        app = load(directory / "receipt.json")
        extra = load(directory / "perf01.json")
        records = row.get("records", [])
        sent = [value for value in records if value["dispatch_ns"] is not None]
        span = (sent[-1]["dispatch_ns"] - sent[0]["dispatch_ns"]) / 1e9 if len(sent) > 1 else 0
        expected_span = frozen["planned_load_seconds"]
        # A scheduler may send the first request late then catch up. The full
        # >=1800 s requirement therefore uses observed dispatch span as well.
        duration_valid = args.stage != "soak" or span >= 1800
        resources = observed_resources(directory / "application/resources.jsonl")
        generator_resources = {role: observed_resources(directory / "generator-resources.jsonl", role)
                               for role in ("target", "scheduler", "controller")}
        phase = resources.get("measurement", {})
        latency = row["histograms"]["arrival_success"]["percentiles_from_exact_samples_ns"]
        valid = bool(row["valid"] and app["status"] == "passed" and extra["status"] == "passed" and duration_valid
                     and not receipt.get("live_owned_processes_after_cleanup") and not receipt.get("cleanup_error"))
        p99 = latency["p99"]
        peak = phase.get("rss_peak_bytes")
        qualifies = valid and row["successful"] == len(records) and p99 is not None and p99 <= args.max_p99_ms * 1e6 and peak is not None and peak <= args.max_rss_mib * 1024**2
        by_kind = {}
        a21 = load_a21()
        for kind in ("cycle", "list"):
            selected = [r for r in records if payload(args.scenario, r["id"], args.seed)["kind"] == kind]
            values = [r["completion_ns"] - r["scheduled_ns"] for r in selected if r["status"] == "ok"]
            by_kind[kind] = {"offered": len(selected), "successful": len(values), "outcomes": dict(Counter(r["status"] for r in selected)),
                             "p50_ns": a21.percentile(values, .5), "p95_ns": a21.percentile(values, .95), "p99_ns": a21.percentile(values, .99)}
        summary.update(valid=valid, qualifies_service_standard=qualifies, outcomes=row["outcome_counts"], by_kind=by_kind,
            successful=row["successful"], offered=row["offered"], dispatched=len(sent), p50_ns=latency["p50"], p95_ns=latency["p95"], p99_ns=p99,
            successful_per_second=row["successful"] * 1e9 / row["measurement_window_ns"]["elapsed"],
            planned_load_seconds=expected_span, actual_dispatch_span_seconds=span, duration_valid=duration_valid,
            observation_seconds=row["measurement_window_ns"]["elapsed"] / 1e9,
            invalid_reasons=row.get("invalid_reasons", []) + ([] if duration_valid else ["Actual uninterrupted dispatched span below 1800 seconds"]),
            application_receipt={"path": str(directory / "receipt.json"), "sha256": digest(directory / "receipt.json")},
            perf01_receipt={"path": str(directory / "perf01.json"), "sha256": digest(directory / "perf01.json")},
            resources=resources, generator_resources=generator_resources,
            generator_resource_observer=extra["generator_resource_observer"],
            provider_errors=extra["provider_errors"], business_retry_attempts=extra["business_retry_attempts"],
            error_policy=extra["error_policy"], classified_error_counts=extra["classified_error_counts"],
            classified_error_evidence=extra["classified_error_evidence"], permanent_integrity_halt=extra["permanent_integrity_halt"],
            affinity=extra.get("affinity", frozen["affinity"]),
            durable_oracle=app.get("durable_oracle"), persisted_before_recovery=extra.get("persisted_before_recovery"),
            persisted_before_shutdown=extra.get("persisted_before_shutdown"), closed=app.get("closed"),
            loaded_files=extra["loaded_files"], phases=extra["phases"])
    save(output / "summary.json", summary)
    save(output / "progress.json", {"state": "completed" if summary["valid"] else "completed-invalid", "summary_sha256": digest(output / "summary.json")})
    print(json.dumps({key: summary[key] for key in ("valid", "qualifies_service_standard", "stage", "route", "provider", "scenario")}))
    return 0 if summary["valid"] else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package-probe", required=True, type=Path)
    parser.add_argument("--comparators", type=Path)
    parser.add_argument("--observer", type=Path)
    parser.add_argument("--route", choices=sorted(load_a21().ROUTES), default="nativeweave-aot-rust")
    parser.add_argument("--provider", choices=["sqlite", "postgresql"], required=True)
    parser.add_argument("--protocol", choices=["http1", "http1-tls", "http2-tls"], default="http1")
    parser.add_argument("--scenario", choices=["cycle", "page", "mixed"], default="mixed")
    parser.add_argument("--stage", choices=["smoke", "short", "soak"], default="smoke")
    parser.add_argument("--variant", choices=["before", "after", "direct", "managed", "diagnostic"], required=True)
    parser.add_argument("--rate", type=float, required=True)
    parser.add_argument("--seconds", type=float, required=True)
    parser.add_argument("--concurrency", type=int, default=16)
    parser.add_argument("--generator-cpus", type=cpu_list,
                        help="Optional separate two-CPU generator mask; launch this controller on the two service CPUs first")
    parser.add_argument("--warmup", type=int, default=32)
    parser.add_argument("--idle-seconds", type=float, default=60)
    parser.add_argument("--recovery-requests", type=int, default=32)
    parser.add_argument("--continue-classified-overload", action="store_true",
                        help="Continue real HTTP after declared service-error statuses/timeouts; every error still fails the cell")
    parser.add_argument("--compact-observation", action="store_true",
                        help="Share bounded immutable oracle values before timing and replay accepted result payloads after timing")
    parser.add_argument("--max-p99-ms", type=float, default=500)
    parser.add_argument("--max-rss-mib", type=float, default=256)
    parser.add_argument("--seed", type=int, default=20261119)
    parser.add_argument("--repetition", type=int, default=0)
    parser.add_argument("--postgres-bin", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--isolation-note", required=True)
    args = parser.parse_args()
    if not all(math.isfinite(v) and v > 0 for v in [args.rate, args.seconds, args.max_p99_ms, args.max_rss_mib]) or args.rate > 10000 or not 1 <= args.concurrency <= 128:
        parser.error("Positive finite load/limits and 1..128 concurrency required")
    if not 0 <= args.warmup <= 10000 or not 0 <= args.recovery_requests <= 1024 or not 0 <= args.idle_seconds <= 300:
        parser.error("Invalid bounded warmup/recovery/idle settings")
    if args.provider == "postgresql" and args.postgres_bin is None:
        parser.error("PostgreSQL requires exact PostgreSQL 18.0 --postgres-bin")
    if args.stage == "soak" and (args.idle_seconds < 60 or args.recovery_requests < 32):
        parser.error("Soak requires at least 60 seconds idle and 32 same-process recovery requests")
    if args.stage != "smoke" and not args.continue_classified_overload:
        parser.error("Perf-01 short/capacity/soak cohorts require explicit --continue-classified-overload; freeze the same policy for both versions")
    route = descriptor(args)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    with domain.postgres(output / "postgres", args.postgres_bin) if args.provider == "postgresql" else nullcontext({}) as pg:
        plan, frozen = prepare(args, output, route, pg)
        return asyncio.run(execute(args, output, plan, frozen))


if __name__ == "__main__":
    raise SystemExit(main())
