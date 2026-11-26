#!/usr/bin/env python3
"""Derive a Go-only local plan from an immutable DB-Perf-01 final plan.

This tool creates inputs only. It does not run, admit or accept a benchmark.
"""
import argparse
import hashlib
import json
from pathlib import Path


def identity(path):
    path = Path(path).resolve()
    return {"path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--reference-plan", type=Path, required=True)
    p.add_argument("--consumer", type=Path, required=True)
    p.add_argument("--manifest", type=Path, required=True)
    p.add_argument("--runner", type=Path, required=True)
    p.add_argument("--sqlite-library", type=Path)
    p.add_argument("--fixture-executable", type=Path)
    p.add_argument("--postgres-bin", type=Path)
    p.add_argument("--kind", choices=("preflight", "final"), required=True)
    p.add_argument("--evidence", type=Path, required=True)
    p.add_argument("--output", type=Path, required=True)
    a = p.parse_args()
    ref = json.loads(a.reference_plan.read_text())
    provider = ref["provider"]
    if provider not in ("sqlite", "postgresql"):
        raise ValueError("invalid reference provider")
    manifest = identity(a.manifest)
    if manifest["sha256"] != "76443b1c88bf011f57b3c9819dc85a8bedb5376121d3ef2670304f96b6d2aae2":
        raise ValueError("the frozen 35-case manifest is required")
    runner = identity(a.runner)
    if runner["sha256"] != "296e6b802d8bc1bcf964d7faa71d4adaa37226a867c17a9a3b5f00284f769b86":
        raise ValueError("the frozen local resource/ownership runner is required")
    target = a.evidence.resolve()
    if target.exists() and any(target.iterdir()):
        raise ValueError("fresh evidence path required")
    fixture = dict(ref["commands"][0])
    fixture["argv"] = list(fixture["argv"])
    if fixture.get("phase") != "fixture" or "--initialize" not in fixture["argv"]:
        raise ValueError("reference initializer contract is missing")
    ix = fixture["argv"].index("--operations")
    if fixture["argv"][ix + 1] != "0":
        raise ValueError("fixture must not run measured operations")
    if a.fixture_executable:
        fixture["argv"][0] = str(a.fixture_executable.resolve())
    fixture["cohort"] = "fixture-only"
    fixture["comparisonRole"] = "setup-only-no-Korda-timing-credit"
    concurrency, rate = (4, 3000) if provider == "sqlite" else (8, 3600)
    seconds, idle = (5, 0) if a.kind == "preflight" else (1800, 15)
    name = f"go-c{concurrency}"
    argv = ["env", "GOGC=100", "GOMEMLIMIT=off", "GODEBUG=", str(a.consumer.resolve()),
            "--provider", provider, "--connection", "{connection}", "--manifest", str(a.manifest.resolve()),
            "--concurrency", str(concurrency), "--gomaxprocs", "4", "--warmup", "64", "--queue-capacity", "64",
            "--seconds", str(seconds), "--rate", str(rate), "--idle-seconds", str(idle),
            "--output", f"{{output}}/{name}.json"]
    inputs = {"referencePlan": identity(a.reference_plan), "runner": runner, "manifest": manifest,
              "consumer": identity(a.consumer), "fixture": identity(fixture["argv"][0]),
              "generator": identity(__file__)}
    if provider == "sqlite":
        if a.sqlite_library is None:
            raise ValueError("exact SQLite library is required")
        lib = identity(a.sqlite_library)
        if lib["sha256"] != "2f693e43707c8174c862283185610abca5c671bbf17b6b1af7866bfbcd290282":
            raise ValueError("SQLite native image differs from the earlier campaign")
        inputs["sqliteLibrary"] = lib
        argv += ["--native-library", lib["path"]]
    plan = {"schemaVersion": 1, "provider": provider, "lane": "go-" + a.kind,
            "scope": "Only Go receives new timed workload; prior Korda/Dapper cells are not rerun",
            "longRunAcceptance": False, "inputs": inputs,
            "manifestCaseNames": ref["manifestCaseNames"], "mixedDataset": ref["mixedDataset"],
            "affinity": ref["affinity"], "commands": [fixture, {
                "name": name, "cohort": "go", "phase": a.kind, "timingRequested": True,
                "longRunAcceptance": False, "argv": argv, "cpus": "0-3",
                "timeoutSeconds": 2115 if a.kind == "final" else 120}],
            "runnerCommand": ["taskset", "-c", "8", "python3", str(a.runner.resolve()),
                              str(a.output.resolve()), "--output", str(target)],
            "runtimePolicy": {"GOMAXPROCS": 4, "GOGC": "100", "GOMEMLIMIT": "off", "GODEBUG": "",
                              "prepared": "SQLite retained read statements; pgx exec mode, no retained PG statements"}}
    if provider == "sqlite":
        plan["connection"] = str(target / "fixture.sqlite")
    else:
        plan["postgres"] = dict(ref["postgres"])
        plan["postgres"]["data"] = str(target / "postgres-data")
        if a.postgres_bin:
            plan["postgres"]["bin"] = str(a.postgres_bin.resolve())
        for key in ("queryPlans", "activeCpuGroup"):
            if key in plan["postgres"]:
                raise ValueError("formal comparison cannot inherit profiling or a live prior CPU group")
        inputs["postgresTools"] = {name: identity(Path(plan["postgres"]["bin"]) / name)
                                  for name in ("postgres", "initdb", "pg_isready", "psql")}
    a.output.parent.mkdir(parents=True, exist_ok=True)
    with a.output.open("x") as f:
        json.dump(plan, f, indent=2)
        f.write("\n")
    print(json.dumps({"plan": str(a.output.resolve()), "provider": provider, "kind": a.kind,
                      "seconds": seconds, "consumerSha256": inputs["consumer"]["sha256"]}))


if __name__ == "__main__":
    main()
