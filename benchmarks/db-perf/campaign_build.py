#!/usr/bin/env python3
"""Prepare relocatable, source-bound campaign inputs outside the measurement VM."""
from __future__ import annotations

import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import zipfile

from run import sha, utc, write_json

PROVIDERS = ("postgresql", "sqlite")
ROUTES = ("direct_cpp", "korda_before", "korda_after", "independent_csharp_orm")
BASELINE = "771bc77cbd68d3f932b99ed2497c0697bbba5f02"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def duration_budget(request):
    """Configured durations/idle for one attempt per step; retries use the remaining hard budget."""
    settings = request["measurement"]
    idle = settings["idleSeconds"]
    databases = len(PROVIDERS)
    def cells(count, seconds, routes=ROUTES, retained_recovery=False):
        return databases * count * sum(seconds + idle * (2 if route == "direct_cpp" and not retained_recovery else 1)
                                       for route in routes)
    widths = min(2, len(settings["capacityConcurrency"]))
    parts = {"idleAdmission": request["admission"]["idleSeconds"],
        "noise": cells(settings["noiseReplicates"], settings["calibrationSeconds"]),
        "nativeLayers": cells(len(request["nativeLayers"]), settings["calibrationSeconds"], ("direct_cpp",)),
        "closedLoopRamp": cells(len(settings["capacityConcurrency"]), settings["capacitySeconds"]),
        "scheduledGrid": cells(widths * len(settings["scheduledRateFractions"]), settings["scheduledCapacitySeconds"]),
        "boundedMidpointRefinement": cells(widths * settings["capacityRefinementSteps"], settings["capacityRefinementSeconds"]),
        "fixedRateValidation": cells(1, settings["fixedValidationSeconds"])}
    if request.get("shortQueries"):
        value = request["shortQueries"]
        parts["shortProjections"] = cells(len(value["constructionModes"]), value["seconds"])
    if request.get("heavyWorkload"):
        value = request["heavyWorkload"]
        parts["heavyStream"] = cells(len(value["concurrency"]), value["seconds"])
    diagnostics = request.get("diagnostics", {})
    if "sameProcessRecovery" in diagnostics:
        value = diagnostics["sameProcessRecovery"]
        parts["sameProcessRecovery"] = cells(1, value["seconds"] * 2 + value["overloadSeconds"], retained_recovery=True)
    if "nativeAllocation" in diagnostics:
        value = diagnostics["nativeAllocation"]
        parts["nativeAllocationProfiles"] = cells(len(value["concurrency"]), value["seconds"], value["routes"])
    preflight = sum(parts.values())
    long = cells(1, settings["secondsPerSegment"])
    budget = request["timeBudget"]
    return {"configuredPreflightSecondsByLayer": parts, "maximumConfiguredPreflightSeconds": preflight,
        "configuredLongAndIdleSeconds": long, "configuredTotalSeconds": preflight + long,
        "preflightOverheadAllowanceSeconds": budget["preflightStepMinutes"] * 60 - preflight,
        "controllerOverheadAllowanceSeconds": budget["controllerMinutes"] * 60 - preflight - long,
        "jobBeyondControllerSeconds": (budget["jobMinutes"] - budget["controllerMinutes"]) * 60,
        "scope": "one attempt per step, up to two distinct scheduled concurrencies and every bounded refinement executes; reviewed retries consume the remaining finite budget and may prevent completion; includes configured measurement and idle only; excludes initialization, warmup, startup, drain, hashing and artifact transfer, whose actual clocks remain recorded"}


def identity(value, length):
    return isinstance(value, str) and len(value) == length and set(value) <= set("0123456789abcdef") and value != "0" * length


def request_record(path, *, enabled=True):
    request = json.loads(Path(path).read_text())
    require(request["schemaVersion"] == 1 and request["task"] == "DB-Perf-01", "Wrong campaign request")
    require(type(request["enabled"]) is bool, "Request enabled flag must be boolean")
    if enabled:
        require(request["enabled"], "Campaign request is intentionally disabled until reviewed inputs are frozen")
        require(identity(request["harnessCommit"], 40), "Freeze the harness commit before execution")
        require(request["before"]["sourceCommit"] == BASELINE, "The original fixed main must not change")
        for lane in ("before", "after"):
            section = request[lane]
            require(identity(section["sourceCommit"], 40) and type(section["runId"]) is int, "Missing source/run identity")
            require(set(section["providers"]) == set(PROVIDERS), "Both independent package inputs are required")
            for provider, package in section["providers"].items():
                require(type(package["artifactId"]) is int and package["artifactId"] > 0, "Missing artifact ID")
                for key in ("artifactZipSha256", "nupkgSha256", "nativeSha256"):
                    require(identity(package[key], 64), "Missing frozen " + lane + "/" + provider + "/" + key)
                require(bool(package["version"]), "Package version is required")
        require(identity(request["workload"]["sha256"], 64), "Freeze workload manifest bytes")
        short = request.get("shortQueries")
        if short is not None:
            require(short["cases"] == ["numeric", "text", "money", "json", "wide"]
                    and set(short["cases"]) <= set(request["workload"]["cases"])
                    and short["constructionModes"] == ["prepared", "dynamic"] and short["concurrency"] == 1
                    and type(short["seconds"]) is int and 5 <= short["seconds"] <= 30,
                    "Freeze the finite prepared/dynamic short-projection comparisons")
        heavy = request.get("heavyWorkload")
        if heavy is not None:
            require(heavy["id"] == "wide8192" and heavy["cases"] == ["wide"]
                    and identity(heavy["sha256"], 64), "Freeze the separate heavy-stream contract")
            require(heavy["concurrency"] == [1, 4] and type(heavy["seconds"]) is int
                    and 5 <= heavy["seconds"] <= 30 and type(heavy["warmupOperations"]) is int
                    and 1 <= heavy["warmupOperations"] <= 64, "Heavy lanes must remain finite C1/C4 comparisons")
        for provider, arguments in request.get("providerExtraArguments", {}).items():
            require(provider in PROVIDERS and isinstance(arguments, dict), "Invalid provider-specific arguments")
            require(all(route in ROUTES and isinstance(values, list) and all(isinstance(value, str) for value in values)
                        for route, values in arguments.items()), "Invalid provider-specific route arguments")
        require("--native-library" not in request.get("extraArguments", {}).get("independent_csharp_orm", []),
                "SQLite native-library argument cannot be sent to PostgreSQL")
        diagnostics = request.get("diagnostics", {})
        require(set(diagnostics) <= {"sameProcessRecovery", "nativeAllocation"}, "Unknown pre-timing diagnostic")
        if "sameProcessRecovery" in diagnostics:
            recovery = diagnostics["sameProcessRecovery"]
            require(type(recovery["seconds"]) is int and 5 <= recovery["seconds"] <= 15
                    and type(recovery["overloadSeconds"]) is int and 1 <= recovery["overloadSeconds"] <= 5
                    and type(recovery["overloadRate"]) is int and 1 <= recovery["overloadRate"] <= 1000000,
                    "Same-process recovery diagnostic must remain bounded")
        if "nativeAllocation" in diagnostics:
            allocation = diagnostics["nativeAllocation"]
            require(type(allocation["seconds"]) is int and 3 <= allocation["seconds"] <= 10
                    and allocation["concurrency"] == [1, 4] and allocation["tableCapacity"] == 1048576
                    and allocation["routes"] == list(ROUTES),
                    "Native allocation diagnostic capacity/duration differs from its reviewed bounded profile")
        require(request["measurement"]["secondsPerSegment"] >= 1800, "Each long cell needs at least 1800 effective seconds")
        require(request["measurement"]["secondsPerSegment"] * 8 <= 18000, "Leave finite room for calibration, drain and upload")
        order = request["measurement"]["order"]
        require(len(order) == 8 and set(order) == {f"{p}/{r}" for p in PROVIDERS for r in ROUTES}, "All eight distinct cells are required")
        measured, admission = request["measurement"], request["admission"]
        require(measured["constructionMode"] in ("prepared", "dynamic"), "Unknown construction contract")
        native = request["nativeBaseline"]
        require(native == {"physicalPrepare": "reuse" if measured["constructionMode"] == "prepared" else "per-operation",
                           "protocolConfirmation": "drain-only"},
                "Primary native column must use reusable preparation where requested and ordinary complete driver drain")
        require(isinstance(request["nativeLayers"], list) and len(request["nativeLayers"]) <= 3, "Native attribution layers must remain finite")
        for layer in request["nativeLayers"]:
            require(set(layer) == {"physicalPrepare", "protocolConfirmation"}
                    and layer["physicalPrepare"] in ("reuse", "per-operation") and layer["protocolConfirmation"] in ("product", "drain-only")
                    and (measured["constructionMode"] != "dynamic" or layer["physicalPrepare"] == "per-operation"), "Invalid native attribution contract")
        require(type(measured["concurrency"]) is int and 1 <= measured["concurrency"] <= 256, "Unbounded client concurrency")
        capacities = measured["capacityConcurrency"]
        require(isinstance(capacities, list) and capacities == sorted(set(capacities)) and capacities[0] == 1
                and all(type(n) is int and 1 <= n <= 256 for n in capacities), "Freeze an ascending bounded concurrency ramp including one connection")
        require(measured["concurrency"] in capacities, "Long concurrency must be explicitly measured in the ramp")
        fractions = measured["scheduledRateFractions"]
        require(isinstance(fractions, list) and 2 <= len(fractions) <= 6 and fractions == sorted(set(fractions))
                and all(type(n) in (int, float) and math.isfinite(n) and 0 < n <= 2 for n in fractions),
                "Freeze a finite ascending scheduled-rate grid")
        require(type(measured["maximumAttemptsPerStep"]) is int and 1 <= measured["maximumAttemptsPerStep"] <= 3,
                "Retries must be explicitly reviewed and bounded")
        require(type(measured["capacityRefinementSteps"]) is int and 0 <= measured["capacityRefinementSteps"] <= 3
                and type(measured["capacityRefinementSeconds"]) is int and 5 <= measured["capacityRefinementSeconds"] <= 10
                and measured["capacityRefinementMaximumRelativeGap"] == 0.05,
                "Capacity midpoint refinement must remain bounded and preserve the frozen five-percent bracket target")
        budget = request["timeBudget"]
        require(budget == {"preflightStepMinutes": 85, "controllerMinutes": 340, "jobMinutes": 360},
                "Freeze the reviewed preflight/controller/job time budget")
        estimate = duration_budget(request)
        require(request.get("timeBudgetEstimate") == estimate, "Recorded budget formula/allowances do not match frozen layer settings")
        require(estimate["preflightOverheadAllowanceSeconds"] > 0 and estimate["controllerOverheadAllowanceSeconds"] > 180,
                "Configured layers leave no time for preparation/drain overhead and the unchanged eight long segments")
        require(type(measured["noiseReplicates"]) is int and measured["noiseReplicates"] >= 3, "At least three independent before processes are required")
        require(type(measured["queuePerWorker"]) is int and 1 <= measured["queuePerWorker"] * max(capacities) <= 65536, "Queue must remain bounded")
        for field in ("calibrationSeconds", "capacitySeconds", "scheduledCapacitySeconds", "fixedValidationSeconds", "maximumP99Microseconds"):
            require(type(measured[field]) in (int, float) and math.isfinite(measured[field]) and measured[field] > 0, "Invalid " + field)
        require(0.05 <= measured["sampleSeconds"] <= 2 and 0 < measured["sustainableFraction"] <= 0.8,
                "Sampling interval/sustainable load fraction is outside bounds")
        require(0 <= measured["maximumErrorAndRejectionRate"] < 1, "Invalid common error/rejection SLO")
        for field in ("maximumStealFraction", "maximumIdleBusyFraction", "maximumBeforeCv"):
            require(type(admission[field]) in (int, float) and math.isfinite(admission[field]) and 0 <= admission[field] < 1, "Invalid admission threshold " + field)
        require(5 <= admission["idleSeconds"] <= 30, "Idle admission duration must be finite and meaningful")
        require(admission.get("postgresCpuAccounting") == "task-owned-cgroup-v2",
                "Freeze task-owned cumulative PostgreSQL cgroup CPU accounting")
    return request


def validate_heavy_workload(primary, heavy, primary_sha):
    """Permit a separately seeded larger result using the same compiled SQL/shape."""
    require(heavy.get("sourceManifest", {}).get("sha256") == primary_sha, "Heavy workload is not derived from the frozen primary contract")
    require([query["name"] for query in heavy["queries"]] == ["wide"], "Heavy lane must fully consume the explicit wide projection")
    query = heavy["queries"][0]
    original = next((item for item in primary["queries"] if item["name"] == "wide"), None)
    require(original is not None, "Primary compiled contract has no wide query")
    for field in ("sql", "columns", "materialization", "queryExecutionsPerOperation", "construction"):
        require(query.get(field) == original.get(field), "Heavy compiled query contract changed: " + field)
    data = heavy["dataset"]
    require(data["tenants"] == 1 and data["ordersPerTenant"] == data["requestedRows"] == query["expectedRows"] == 8192
            and data["batchRows"] > 0 and query["materialization"] == "stream", "Heavy fixture/result consumption changed")
    require([(p["name"], p["kind"], p["value"]) for p in query["parameters"]]
            == [("tenant", "int64", 1), ("take", "int64", 8192)], "Heavy parameter contract changed")
    require(not query.get("stopAfterRows") and not query.get("writesDatabase", False), "Early termination/write cannot stand in for full heavy consumption")


def gh_json(endpoint):
    return json.loads(subprocess.check_output(["gh", "api", endpoint], text=True, timeout=60))


def candidate(request, lane, provider, directory):
    section, expected = request[lane], request[lane]["providers"][provider]
    run = gh_json(f"repos/IzuSharp/Korda/actions/runs/{section['runId']}")
    require(run["conclusion"] == "success", "Only successful normal CI candidates may be prepared")
    artifact = gh_json(f"repos/IzuSharp/Korda/actions/artifacts/{expected['artifactId']}")
    require(not artifact["expired"] and artifact["workflow_run"]["id"] == section["runId"], "Artifact provenance/expiry differs")
    directory.mkdir(parents=True)
    write_json(directory / "workflow-run.json", run)
    write_json(directory / "artifact.json", artifact)
    archive_path = directory / "candidate.zip"
    with archive_path.open("wb") as output:
        subprocess.run(["gh", "api", f"repos/IzuSharp/Korda/actions/artifacts/{expected['artifactId']}/zip"],
                       stdout=output, check=True, timeout=180)
    require(sha(archive_path) == expected["artifactZipSha256"], "Candidate ZIP changed")
    with zipfile.ZipFile(archive_path) as archive:
        require(sum(item.file_size for item in archive.infolist()) < 512 * 1024 * 1024, "Unexpected oversized candidate")
        require(all((directory / item.filename).resolve().is_relative_to(directory) for item in archive.infolist()), "Archive path escapes destination")
        archive.extractall(directory)
    manifests = list(directory.rglob("release.json"))
    require(len(manifests) == 1, "Expected one producer release manifest")
    manifest = json.loads(manifests[0].read_text())
    require(manifest["source_commit"] == section["sourceCommit"] and manifest["source_dirty"] is False, "Candidate source/cleanliness differs")
    source = gh_json(f"repos/IzuSharp/Korda/git/commits/{section['sourceCommit']}")
    require(manifest["source_tree"] == source["tree"]["sha"], "Candidate tree is not its actual recorded Git commit tree")
    write_json(directory / "source-commit.json", source)
    package_id = "Korda.Database." + ("Sqlite" if provider == "sqlite" else "PostgreSql")
    entries = [item for item in manifest["packages"] if item["id"] == package_id]
    require(len(entries) == 1 and len(manifest["packages"]) == 1, "Provider package is not independent")
    entry = entries[0]
    require(entry["source_commit"] == section["sourceCommit"] and entry["version"] == expected["version"]
            and identity(entry["native"]["source_commit"], 40)
            and entry["native"]["sha256"] == expected["nativeSha256"], "Producer package identity differs")
    packages = list(directory.rglob(package_id + "." + expected["version"] + ".nupkg"))
    require(len(packages) == 1 and sha(packages[0]) == expected["nupkgSha256"], "Original package bytes differ")
    with zipfile.ZipFile(packages[0]) as archive:
        names = [name for name in archive.namelist() if not name.endswith("/")]
        require(len(names) == len(set(names)) and set(names) == set(entry["unsigned_entries"]), "Unsigned package member set differs")
        for name, digest in entry["unsigned_entries"].items():
            require(hashlib.sha256(archive.read(name)).hexdigest() == digest, "Package payload differs: " + name)
    return packages[0].parent


def logged(command, cwd, output, timeout=1200):
    with output.open("w") as log:
        result = subprocess.run(list(map(str, command)), cwd=cwd, stdout=log, stderr=subprocess.STDOUT, timeout=timeout)
    require(result.returncode == 0, "Preparation failed; preserved log: " + str(output))


def retain_app(app, destination):
    destination.mkdir(parents=True)
    for path in app.iterdir():
        if path.is_file() and path.suffix in (".cs", ".csproj", ".json", ".log", ".Config"):
            shutil.copy2(path, destination / path.name)
    shutil.copytree(app / "out", destination / "out", ignore=shutil.ignore_patterns("*.dbg"))
    if (app / "generated").is_dir():
        shutil.copytree(app / "generated", destination / "generated")
    # These are this preparation's reproducible intermediates, after the complete
    # source/restore/build receipts and runtime files have been copied.
    for name in ("bin", "obj", "packages", "sdk-cache"):
        path = app / name
        if path.exists():
            shutil.rmtree(path)


def prepare(request_path, output, repo):
    request_path, output, repo = Path(request_path).resolve(), Path(output).resolve(), Path(repo).resolve()
    request = request_record(request_path)
    require(not output.exists() and not output.is_relative_to(repo), "Use a fresh preparation directory outside Korda")
    subprocess.run(["git", "diff", "--exit-code", request["harnessCommit"], "--", "benchmarks/db-perf",
                    ":!benchmarks/db-perf/campaign-request.json"], cwd=repo, check=True)
    untracked = subprocess.check_output(["git", "ls-files", "--others", "--exclude-standard", "--", "benchmarks/db-perf"],
                                        cwd=repo, text=True).splitlines()
    require(not untracked, "Untracked benchmark inputs cannot be attributed to the frozen harness commit")
    require(sha(repo / request["workload"]["path"]) == request["workload"]["sha256"], "Workload manifest differs")
    if request.get("heavyWorkload") is not None:
        heavy = request["heavyWorkload"]
        require(sha(repo / heavy["path"]) == heavy["sha256"], "Heavy workload manifest differs")
        validate_heavy_workload(json.loads((repo / request["workload"]["path"]).read_text()),
                                json.loads((repo / heavy["path"]).read_text()), request["workload"]["sha256"])
    output.mkdir(parents=True)
    bundle = output / "bundle"
    bundle.mkdir()
    shutil.copy2(request_path, bundle / "request.json")
    artifact_root = output / "candidates"
    feeds = {}
    for lane in ("before", "after"):
        for provider in PROVIDERS:
            feeds[lane, provider] = candidate(request, lane, provider, artifact_root / lane / provider)
    apps = {}
    for provider in PROVIDERS:
        receipts = []
        for lane in ("before", "after"):
            app = output / "build" / f"korda-{lane}-{provider}"
            log = output / f"build-korda-{lane}-{provider}.log"
            logged([sys.executable, repo / "benchmarks/db-perf/managed/build.py", "--provider", provider,
                    "--feed", feeds[lane, provider], "--version", request[lane]["providers"][provider]["version"],
                    "--manifest", repo / request["workload"]["path"], "--output", app], repo, log)
            receipt = json.loads((app / "build-receipt.json").read_text())
            receipts.append(receipt)
            dest = bundle / "apps" / provider / ("korda_" + lane)
            retain_app(app, dest)
            apps[provider + "/korda_" + lane] = str((dest / "out/DbPerf.Korda").relative_to(bundle))
        require(receipts[0]["sources"] == receipts[1]["sources"] and receipts[0]["project_sha256"] == receipts[1]["project_sha256"],
                "Before and after did not use identical consumer source/project bytes")
        app = output / "build" / f"orm-{provider}"
        logged([sys.executable, repo / "benchmarks/db-perf/managed/build_orm.py", "--kind", "benchmark", "--provider", provider,
                "--manifest", repo / request["workload"]["path"], "--output", app], repo, output / f"build-orm-{provider}.log")
        dest = bundle / "apps" / provider / "independent_csharp_orm"
        retain_app(app, dest)
        apps[provider + "/independent_csharp_orm"] = str((dest / "out/DbPerf.Dapper").relative_to(bundle))
    sys.path.insert(0, str(repo / "eng"))
    from cpp_toolchain import sources
    dependencies = sources(repo / "artifacts/dependencies")
    pg_bin = Path(os.environ.get("NW_POSTGRES_BIN", repo / "artifacts/postgres-tools/install/bin")).resolve()
    native = output / "build/native"
    logged([sys.executable, repo / "benchmarks/db-perf/native/build.py", "--sqlite-source", dependencies["sqlite"]["source"],
            "--json-source", dependencies["nlohmann-json"]["source"], "--postgres-prefix", pg_bin.parent,
            "--manifest", repo / request["workload"]["path"], "--output", native], repo, output / "build-native.log")
    shutil.copytree(native, bundle / "native")
    shutil.copytree(pg_bin.parent, bundle / "postgres", symlinks=True)
    shutil.copytree(artifact_root, bundle / "packages")
    for provider in PROVIDERS:
        apps[provider + "/direct_cpp"] = "native/db-perf-native"
    harness = bundle / "harness"
    harness.mkdir()
    for name in ("run.py", "campaign.py", "campaign_build.py"):
        shutil.copy2(repo / "benchmarks/db-perf" / name, harness / name)
    shutil.copy2(repo / request["workload"]["path"], bundle / "workload.json")
    if request.get("heavyWorkload") is not None:
        shutil.copy2(repo / request["heavyWorkload"]["path"], bundle / "heavy-workload.json")
    metadata = {"schemaVersion": 1, "task": "DB-Perf-01", "preparedUtc": utc(), "requestSha256": sha(bundle / "request.json"),
                "harnessCommit": request["harnessCommit"],
                "preparationImage": {key: os.environ.get(key) for key in ("ImageOS", "ImageVersion")},
                "checkoutCommit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repo, text=True).strip(),
                "applications": apps, "postgresBin": "postgres/bin", "sqliteLibrary": "native/libsqlite3.so",
                "workload": "workload.json", "heavyWorkload": "heavy-workload.json" if request.get("heavyWorkload") else None, "files": {}}
    for path in sorted(bundle.rglob("*")):
        if path.is_file():
            metadata["files"][str(path.relative_to(bundle))] = {"sha256": sha(path), "bytes": path.stat().st_size,
                "mode": path.stat().st_mode & 0o777, "symlink": os.readlink(path) if path.is_symlink() else None}
    write_json(bundle / "bundle.json", metadata)
    archive_path = output / "runtime-bundle.tar.gz"
    with tarfile.open(archive_path, "w:gz") as archive:
        archive.add(bundle, arcname="bundle", recursive=True)
    receipt = {"schemaVersion": 1, "task": "DB-Perf-01", "archive": archive_path.name, "sha256": sha(archive_path),
               "bundleManifestSha256": sha(bundle / "bundle.json"), "requestSha256": metadata["requestSha256"],
               "harnessCommit": request["harnessCommit"], "preparedUtc": utc(),
               "performanceExecutionClaimed": False}
    write_json(output / "preparation.json", receipt)
    if os.environ.get("GITHUB_OUTPUT"):
        with Path(os.environ["GITHUB_OUTPUT"]).open("a") as stream:
            stream.write("bundle_sha256=" + receipt["sha256"] + "\n")
    print(json.dumps(receipt, indent=2))
    return receipt
