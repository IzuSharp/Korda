#!/usr/bin/env python3
"""Run selected actual B22 fault cells against an immutable B21 package artifact."""
from __future__ import annotations

import argparse
import contextlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

from faults import Faults
from harness import Evidence, HERE, PostgresCluster, REFERENCE, require, sha, write_json


COMMON = ("outbox-after-publication-before-ack", "upload-before-durable-content",
          "upload-after-rename-before-commit", "orphan-collection-interrupted",
          "http1-half-close", "http2-reset")
POSTGRES = ("begin-transport-loss", "write-transport-loss", "commit-held-before-upstream-send",
            "commit-acknowledgement-discarded", "provider-drain-failure", "postgres-restart")
SQLITE = ("sqlite-busy", "sqlite-write-enospc")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifact-directory", type=Path, required=True)
    parser.add_argument("--mode", choices=("jit", "aot"), required=True)
    parser.add_argument("--provider", choices=("sqlite", "postgresql"), required=True)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--postgres-bin", type=Path)
    parser.add_argument("--certificate", type=Path)
    parser.add_argument("--private-key", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--cases", nargs="+")
    parser.add_argument("--filesystem-shim", type=Path)
    parser.add_argument("--observed", action="store_true", help="Require the explicitly built B22 public observer consumer")
    args = parser.parse_args()
    artifact = args.artifact_directory.resolve()
    executable = artifact / ("NativeWeave.Reference.TaskService.dll" if args.mode == "jit" else "NativeWeave.Reference.TaskService")
    require(executable.is_file(), "The selected immutable reference artifact does not exist")
    require((args.certificate is None) == (args.private_key is None), "Both TLS paths are required")
    if args.provider == "postgresql": require(args.postgres_bin is not None, "PostgreSQL fault rows require explicit PostgreSQL18 binaries")
    required = (*POSTGRES, *COMMON) if args.provider == "postgresql" else (*SQLITE, *COMMON)
    selected = args.cases or list(required)
    require(len(set(selected)) == len(selected) and all(case in required for case in selected), "Unknown or duplicate fault selection")
    command = [args.dotnet, str(executable)] if args.mode == "jit" else [str(executable)]
    if args.observed:
        modules = {}
        for provider in ("sqlite", "postgresql") if args.provider == "postgresql" else ("sqlite",):
            candidates = list(artifact.rglob("libnw_database_" + provider + ".so"))
            require(len(candidates) == 1, "Observer requires one exact selected provider module")
            modules[provider] = str(candidates[0])
        command = ["env", "NW_B22_DB_MODULES=" + json.dumps(modules, sort_keys=True), *command]
    tls = (args.certificate.resolve(), args.private_key.resolve()) if args.certificate else None
    identity = {"mode": args.mode, "provider": args.provider, "program": command,
        "artifactDirectory": str(artifact), "executableSha256": sha(executable),
        "artifacts": {str(path.relative_to(artifact)): sha(path) for path in sorted(artifact.rglob("*"))
                      if path.is_file() and path.suffix in (".so", ".dll", ".json")},
        "controller": {path.name: sha(path) for path in sorted(HERE.glob("*.py"))},
        "reusedController": {path.name: sha(path) for path in (REFERENCE / "run.py", REFERENCE / "golden.py", REFERENCE / "postgres_oracles.py")},
        "requiredFaultCasesForProvider": list(required), "selectedFaultCases": selected,
        "tlsCertificateSha256": sha(tls[0]) if tls else None,
        "performanceMeasurement": False, "fullB22Acceptance": False, "instrumentedObserver": args.observed}
    evidence = Evidence(args.output.resolve(), identity)
    exit_code = 0
    completed_lifecycle = False
    outer_failure = None
    try:
        shim = args.filesystem_shim.resolve() if args.filesystem_shim else evidence.output / "libb22_filesystem_fault.so"
        if args.filesystem_shim is None:
            source = HERE.parents[1] / "src/Verification.EndToEnd/FilesystemFaults/fs_fault.c"
            compiler = shutil.which("cc")
            require(compiler is not None, "A C compiler is required only for the isolated filesystem fault shim")
            argv = [compiler, "-std=c11", "-shared", "-fPIC", "-O2", "-Wall", "-Wextra", "-Werror", "-pthread", str(source), "-ldl", "-o", str(shim)]
            with (evidence.output / "shim-build.log").open("wb") as log:
                result = subprocess.run(argv, stdout=log, stderr=subprocess.STDOUT, timeout=60)
            require(result.returncode == 0, "The isolated filesystem fault shim failed to compile")
            evidence.record["filesystemShim"] = {"sourceSha256": sha(source), "sha256": sha(shim), "command": argv,
                "compilerSha256": sha(Path(compiler).resolve()), "scope": "disposable verification child only; no product native asset change"}
        else:
            evidence.record["filesystemShim"] = {"sha256": sha(shim), "path": str(shim)}
        cluster_context = PostgresCluster(args.postgres_bin, evidence.output / "postgres") if args.provider == "postgresql" else contextlib.nullcontext(None)
        with cluster_context as cluster:
            suite = Faults(command, args.provider, evidence, cluster=cluster, tls=tls, shim=shim, observed=args.observed)
            actions = {
                "begin-transport-loss": lambda p: suite.pg_disconnect(p, "begin"),
                "write-transport-loss": lambda p: suite.pg_disconnect(p, "items_insert"),
                "commit-held-before-upstream-send": lambda p: suite.pg_commit(p, acknowledgement_loss=False),
                "commit-acknowledgement-discarded": lambda p: suite.pg_commit(p, acknowledgement_loss=True),
                "provider-drain-failure": suite.pg_drain,
                "postgres-restart": suite.pg_restart,
                "sqlite-busy": suite.sqlite_busy,
                "sqlite-write-enospc": suite.sqlite_enospc,
                "outbox-after-publication-before-ack": suite.outbox_crash,
                "upload-before-durable-content": lambda p: suite.upload_crash(p, after_rename=False),
                "upload-after-rename-before-commit": lambda p: suite.upload_crash(p, after_rename=True),
                "orphan-collection-interrupted": suite.gc_crash,
                "http1-half-close": suite.half_close,
                "http2-reset": suite.http2_reset,
            }
            for name in selected:
                print("B22 START " + name, flush=True)
                try:
                    with evidence.case(name) as path:
                        actions[name](path)
                        logs = list(path.rglob("service.log"))
                        expected = "B21_RUNTIME dynamic=" + ("True" if args.mode == "jit" else "False")
                        evidence.check("actual-runtime-witness", bool(logs) and all(expected in log.read_text() for log in logs),
                                       expected=expected, serviceLogs=len(logs))
                        if args.observed:
                            for log in logs:
                                observations = [json.loads(line) for line in (log.parent / "observations.jsonl").read_text().splitlines()]
                                evidence.check("observer-has-no-failures", bool(observations) and all(row["observerFailures"] == 0 for row in observations))
                                if "CLOSED " in log.read_text():
                                    final = observations[-1]
                                    expected_providers = {"sqlite", "postgresql"} if args.provider == "postgresql" else {"sqlite"}
                                    evidence.check("actual-final-native-owner-retirement", final["phase"] == "observer-stop" and
                                        set(final["databaseNativeOwners"]) == expected_providers and
                                        all(len(values) == 6 and all(value == 0 for value in values) for values in final["databaseNativeOwners"].values()),
                                        counts=final["databaseNativeOwners"])
                                    fields = ["requests.active", "requests.queued", "requests.retiring", "payload.managed.bytes", "payload.native.bytes"]
                                    evidence.check("actual-final-server-permit-and-byte-retirement", final["serverMeterObserved"] and
                                        all(final["serverMeter"].get("nativeweave.server." + field) == 0 for field in fields),
                                        gauges=final["serverMeter"])
                    print("B22 END " + name + " " + evidence.record["cases"][-1]["status"], flush=True)
                except Exception as error:
                    print("B22 END " + name + " failed " + type(error).__name__, flush=True)
                    exit_code = 1
        completed_lifecycle = True
    except BaseException as error:
        outer_failure = {"type": type(error).__name__,
            "interrupted": isinstance(error, (KeyboardInterrupt, SystemExit))}
        exit_code = 1
    finally:
        # This controller owns the outer database/process lifetime. A partial
        # prefix of passed cases cannot become a passed receipt if interruption
        # or PostgreSQL shutdown prevents the selected run from completing.
        cases = evidence.record["cases"]
        executed = [case["id"] for case in cases]
        complete = completed_lifecycle and outer_failure is None and executed == selected and all(case["status"] == "passed" for case in cases)
        evidence.record.update(status="passed" if complete else "interrupted" if outer_failure and outer_failure["interrupted"] else "incomplete",
            completedOuterLifecycle=completed_lifecycle, outerFailure=outer_failure,
            selectedCasesComplete=executed == selected,
            notSelected=[case for case in required if case not in selected],
            selectedNotExecuted=[case for case in selected if case not in executed],
            longDurationAcceptance={"steady30m": "not-run", "regression24h": "not-run", "release72h": "not-run"})
        evidence.save()
        evidence.timeline.close()
    if evidence.record["status"] != "passed": exit_code = 1
    print(json.dumps({"status": evidence.record["status"], "receipt": str(evidence.output / "receipt.json"),
                      "selectedCases": len(selected), "fullB22Acceptance": False}, sort_keys=True), flush=True)
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
