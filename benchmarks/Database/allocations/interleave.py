#!/usr/bin/env python3
"""Replay two verified external AOT consumers in interleaved fresh processes.

The C# consumer and its original binaries are unchanged. This is an allocation
diagnostic, not a dedicated throughput/latency campaign or a new package build.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import pwd
import socket
import subprocess
import time

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def utc():
    return datetime.now(timezone.utc).isoformat()


def write(path, value):
    Path(path).write_text(json.dumps(value, indent=2) + "\n")


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--before-evidence", type=Path, required=True)
    parser.add_argument("--after-evidence", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--postgres-bin", type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    if output.exists() or output == ROOT or ROOT in output.parents:
        raise ValueError("Use a fresh output outside the source repository")
    runner = load_module("db_perf_original_allocation_runner", HERE / "run.py")
    validator_path = ROOT / "benchmarks/db-perf/managed/final_allocations.py"
    validator = load_module("db_perf_allocation_validation", validator_path)
    originals = {"before": args.before_evidence.resolve(), "after": args.after_evidence.resolve()}
    manifests = {}
    for lane, evidence in originals.items():
        validator.validate(evidence)
        manifest = json.loads((evidence / "build-manifest.json").read_text())
        for identity in manifest["providers"].values():
            for path_key, hash_key in (("package_path", "package_sha256"),
                                       ("executable_path", "executable_sha256"),
                                       ("native_path", "native_sha256")):
                if sha(identity[path_key]) != identity[hash_key]:
                    raise ValueError("A reused package or binary changed")
        manifests[lane] = {"schema": "DB-Perf-01.allocation-reused-build.v1",
            "source_files": manifest["source_files"], "providers": manifest["providers"],
            "original_build_manifest": {"path": str(evidence / "build-manifest.json"),
                                        "sha256": sha(evidence / "build-manifest.json")},
            "runs": [], "new_builds": 0}
    if manifests["before"]["source_files"] != manifests["after"]["source_files"]:
        raise ValueError("The two consumers do not have identical source inputs")
    for name, digest in manifests["before"]["source_files"].items():
        if sha(HERE / name) != digest:
            raise ValueError("Consumer source differs from the verified builds")
    output.mkdir(parents=True)
    for lane in manifests:
        (output / lane).mkdir()
    receipt = {"schemaVersion": 1, "startedUtc": utc(), "status": "started",
        "driverSha256": sha(__file__), "runnerSha256": sha(HERE / "run.py"),
        "validatorSha256": sha(validator_path), "warmupPerScenario": 64,
        "iterationsPerScenario": 128, "independentProcessesPerProviderAndLane": 3,
        "scope": "Whole-process raw allocation comparison only; no control subtraction, no timing acceptance, no business/framework or GC ownership split",
        "cells": [], "acceptedLongSegments": 0}
    pgdata = output / "postgres-data"
    env = dict(os.environ)
    server = None
    server_log = None
    failure = None
    failure_traceback = None
    phase = "postgres-initialize"

    def retain_failure(error, failed_phase):
        nonlocal failure, failure_traceback
        detail = {"phase": failed_phase, "type": type(error).__name__, "message": str(error)}
        receipt["status"] = "failed"
        if failure is None:
            failure = error
            failure_traceback = error.__traceback__
            receipt["error"] = detail
        else:
            receipt.setdefault("secondaryErrors", []).append(detail)

    try:
        runner.run([str(args.postgres_bin / "initdb"), "-D", str(pgdata), "--auth=trust",
                    "--no-locale", "--encoding=UTF8"], output, output / "postgres-initdb.log", env)
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            port = listener.getsockname()[1]
        command = [str(args.postgres_bin / "postgres"), "-D", str(pgdata), "-h", "127.0.0.1",
            "-p", str(port), "-k", "", "-c", "fsync=on", "-c", "synchronous_commit=on", "-c", "full_page_writes=on"]
        server_log = (output / "postgres-server.log").open("x")
        phase = "postgres-start"
        server = subprocess.Popen(command, env=env, stdout=server_log, stderr=subprocess.STDOUT)
        cluster = {"data_directory": str(pgdata), "process_id": server.pid, "port": port,
                   "command": command, "dedicated_to_this_preflight": True,
                   "physical_host_exclusivity": "not established"}
        receipt["postgres_cluster"] = cluster
        for manifest in manifests.values():
            manifest["postgres_cluster"] = cluster
        phase = "postgres-readiness"
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            if server.poll() is not None:
                raise RuntimeError("Dedicated PostgreSQL exited; preserve its original log")
            ready = subprocess.run([str(args.postgres_bin / "pg_isready"), "-h", "127.0.0.1", "-p", str(port)],
                                   env=env, stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
            if ready.returncode == 0:
                break
            time.sleep(0.1)
        else:
            raise TimeoutError("PostgreSQL readiness timed out")
        phase = "measurement"
        for repeat in range(1, 4):
            providers = ("postgresql", "sqlite") if repeat % 2 else ("sqlite", "postgresql")
            for provider_index, provider in enumerate(providers):
                lanes = ("before", "after") if (repeat + provider_index) % 2 else ("after", "before")
                for lane in lanes:
                    identity = manifests[lane]["providers"][provider]
                    connection = (f"postgresql://{pwd.getpwuid(os.getuid()).pw_name}@127.0.0.1:{port}/postgres"
                        if provider == "postgresql" else str(output / f"{provider}-{lane}-{repeat}.db"))
                    result = output / lane / f"{provider}-run-{repeat}.json"
                    log = result.with_suffix(".log")
                    argv = [identity["executable_path"], connection, str(result), "64", "128",
                            identity["package_path"], identity["source_commit"]]
                    cell = {"provider": provider, "lane": lane, "repeat": repeat, "startedUtc": utc(), "status": "started"}
                    receipt["cells"].append(cell)
                    write(output / "execution.json", receipt)
                    runner.run(argv, output, log, env, timeout=300)
                    raw = json.loads(result.read_text())
                    for field in ("package_sha256", "executable_sha256"):
                        if raw[field] != identity[field]:
                            raise ValueError("Actual reused consumer identity changed")
                    if raw["loaded_native_sha256"] != identity["native_sha256"]:
                        raise ValueError("The loaded native image changed")
                    manifests[lane]["runs"].append({"provider": provider, "repetition": repeat,
                        "result": result.name, "result_sha256": sha(result), "log": log.name})
                    cell.update(status="completed", resultSha256=sha(result), finishedUtc=utc())
    except BaseException as error:
        retain_failure(error, phase)
    finally:
        if server is not None:
            if server.poll() is None:
                try:
                    runner.run([str(args.postgres_bin / "pg_ctl"), "-D", str(pgdata), "stop", "-m", "fast", "-w", "-t", "20"],
                               output, output / "postgres-stop.log", env, timeout=30)
                except BaseException as error:
                    retain_failure(error, "postgres-stop")
            try:
                server.wait(timeout=30)
            except BaseException as error:
                retain_failure(error, "postgres-wait")
            if "postgres_cluster" in receipt:
                receipt["postgres_cluster"]["exit_code"] = server.returncode
        if server_log is not None:
            try:
                server_log.close()
            except BaseException as error:
                retain_failure(error, "postgres-log-close")
        for lane, manifest in manifests.items():
            try:
                write(output / lane / "build-manifest.json", manifest)
            except BaseException as error:
                retain_failure(error, "manifest-" + lane)
    if failure is None:
        for lane in manifests:
            try:
                write(output / lane / "validation.json", validator.validate(output / lane))
            except BaseException as error:
                retain_failure(error, "final-validation-" + lane)
                break
    if failure is None:
        receipt["status"] = "completed"
    receipt["finishedUtc"] = utc()
    try:
        write(output / "execution.json", receipt)
    except BaseException as error:
        retain_failure(error, "execution-receipt")
    if failure is not None:
        raise failure.with_traceback(failure_traceback)
    print(json.dumps({"status": receipt["status"], "cells": len(receipt["cells"]), "output": str(output)}))


if __name__ == "__main__":
    main()
