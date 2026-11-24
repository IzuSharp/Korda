#!/usr/bin/env python3
"""Run the public AOT resource probe only against fresh, driver-owned databases.

This is a separately bounded cold/concurrency/exhaustion/retention diagnostic.
It grants no sustained-load, framework-only allocation, or native-heap credit.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys

HERE = Path(__file__).resolve().parent
BENCH = HERE.parent
ROOT = BENCH.parents[1]


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec)
    sys.modules[name] = result
    spec.loader.exec_module(result)
    return result


RUN = module("db_perf_resource_process_observer", BENCH / "run.py")
PACKAGES = module("db_perf_resource_package_validation", BENCH / "managed/final_allocations.py")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(path):
    with Path(path).open("rb") as file:
        return hashlib.file_digest(file, "sha256").hexdigest()


def utc():
    return datetime.now(timezone.utc).isoformat()


def counters(start, end):
    require(type(start["AllocatedBytes"]) is int and type(end["AllocatedBytes"]) is int,
            "Cumulative all-process allocation counts must be exact integers")
    result = {"rawAllocatedBytes": end["AllocatedBytes"] - start["AllocatedBytes"]}
    require(result["rawAllocatedBytes"] >= 0 and end["Timestamp"] >= start["Timestamp"],
            "Cumulative allocation or timestamp moved backwards")
    result["gcCollections"] = [end["Gen" + str(i)] - start["Gen" + str(i)] for i in range(3)]
    require(all(value >= 0 for value in result["gcCollections"]), "A GC counter moved backwards")
    return result


def build_inputs(app, provider, package):
    receipt_path = app / "build-receipt.json"
    receipt = json.loads(receipt_path.read_text())
    require(receipt["kind"] == "resource-probe" and receipt["provider"] == provider,
            "Wrong consumer kind or provider")
    require(receipt["exit_code"] == 0 and not receipt.get("post_validation_error"),
            "Consumer publish/post-validation failed")
    for left, right in (("package", "package_id"), ("version", "version"),
                        ("package_sha256", "package_sha256")):
        require(receipt[left] == package[right], "Consumer package differs: " + left)
    require(receipt["project_references"] == 0, "Source-project consumption is not accepted")
    expected = {"Program.cs": HERE / "Program.cs", "ResourceProbe.csproj": HERE / "ResourceProbe.csproj",
                "Directory.Build.props": HERE / "Directory.Build.props", "global.json": ROOT / "global.json"}
    sources = {}
    for name, source in expected.items():
        sources[name] = sha(source)
        require(sha(app / name) == sources[name], "Consumer source differs: " + name)
    require(receipt["sources"] == {"Program.cs": sources["Program.cs"]}
            and receipt["project_sha256"] == sources["ResourceProbe.csproj"]
            and receipt["directory_build_props_sha256"] == sources["Directory.Build.props"]
            and receipt["global_json_sha256"] == sources["global.json"], "Build source receipt differs")
    require(receipt["builder_sha256"] == sha(BENCH / "managed/build.py"), "Builder identity differs")
    require(sha(app / "publish.log") == receipt["publish_log_sha256"], "Publish log differs")
    require(sha(app / "project.assets.json") == receipt["assets_sha256"], "Restore graph differs")
    assets = json.loads((app / "project.assets.json").read_text())
    require({key for key in assets["libraries"] if key.lower().startswith("korda.")}
            == {package["package_id"] + "/" + package["version"]}, "Unexpected Korda dependency graph")
    executable = app / "out/ResourceProbe"
    require(executable.read_bytes()[:4] == b"\x7fELF" and not executable.with_suffix(".dll").exists(),
            "Expected a standalone AOT executable")
    require(sha(executable) == receipt["executable_sha256"], "Executable differs from actual publish")
    native_name = "libnw_database_" + provider + ".so"
    require(receipt["native_images"] == {native_name: package["native_sha256"]},
            "Consumer does not contain exactly the independently packaged provider")
    require(sha(app / "out" / native_name) == package["native_sha256"], "Native payload differs")
    return {"buildReceiptSha256": sha(receipt_path), "sources": sources, "build": receipt,
            "executable": str(executable), "executableSha256": sha(executable)}


def validate(raw, provider, package, build, concurrency, processors):
    require(raw["Schema"] == "DB-Perf-01.public-resource-inventory.v1", "Wrong result schema")
    require(raw["Passed"] and raw["Error"] is None and raw["CleanupErrors"] == [],
            "A probe operation, result oracle, or cleanup failed")
    require(raw["NativeAot"] and raw["ProcessId"] > 0, "Actual AOT/process identity is absent")
    require(raw["SourceCommit"] == package["source_commit"]
            and raw["PackageSha256"] == package["package_sha256"]
            and raw["ExecutableSha256"] == build["executableSha256"]
            and raw["NativeSha256"] == package["native_sha256"], "Actual loaded package/executable provenance differs")
    require(raw["Provider"] == {"sqlite": "Sqlite", "postgresql": "PostgreSql"}[provider], "Loaded provider differs")
    require(raw["ProcessorCount"] == processors and raw["ProcessorCountOverride"] == str(processors)
            and raw["Concurrency"] == concurrency, "Concurrency/runtime cache capacity differs")
    windows = {value["Name"]: value for value in raw["Windows"]}
    require(len(windows) == len(raw["Windows"]), "Duplicate allocation window")
    names = ["cold-source-build", "cold-open-sessions", "cold-fluent-prepare-and-query-dispose",
             "cold-raw-suspended-burst", "pool-exhaustion-and-cancel", "dispose-prepared",
             "return-all-sessions", "close-source-including-public-cleanup-observation"]
    for repeat in range(3):
        names += ["warm-raw-suspended-burst-" + str(repeat), "prepared-fluent-c1-" + str(repeat),
                  "prepared-fluent-concurrent-" + str(repeat)]
    require(set(windows) == set(names), "Missing or unexpected diagnostic boundary")
    totals = {}
    for name, value in windows.items():
        delta = counters(value["Start"], value["End"])
        require(delta["rawAllocatedBytes"] == value["RawAllocatedBytes"]
                and value["FrameworkAllocatedBytes"] is None, "Raw byte arithmetic or framework scope differs")
        require(value["Operations"] == value["Succeeded"] + value["Failed"] == value["PlannedOperations"],
                "An issued, failed or planned operation disappeared")
        require(value["BoundaryError"] is None, "A result or enumerator disposal failed")
        burst = "burst" in name or name.startswith("prepared-fluent-")
        if burst:
            width = 1 if name.startswith("prepared-fluent-c1-") else concurrency
            is_raw = "raw-suspended-burst" in name
            row_count = 1 if is_raw else 8
            checksum = raw["SuspendedQuerySteps"] * (raw["SuspendedQuerySteps"] + 1) // 2 if is_raw else 36
            require(value["Succeeded"] == width and value["Failed"] == 0
                    and value["Rows"] == width * row_count and value["Checksum"] == width * checksum,
                    "Complete result consumption, checksum, or disposal ledger differs")
            require(len(value["PendingAtReturn"]) == len(value["PendingBeforeConsumption"]) == width,
                    "Incomplete first-Move status inventory")
            require(sum(value["PendingAtReturn"]) == value["PendingAtReturnCount"]
                    and sum(value["PendingBeforeConsumption"]) == value["PendingFirstMovesBeforeAnyConsumption"],
                    "Suspension counts differ")
            require(value["SingleIssuingThread"] and value["IssuingThread"] > 0,
                    "First moves did not share the recorded issuing thread")
            require(value["RuntimePoolCacheCapacityForIssuingThread"] == processors + 1
                    and value["UnconsumedSuspensionsExceedCacheCapacity"] == (value["PendingAtReturnCount"] > processors + 1),
                    "Capacity-pressure arithmetic differs")
        elif name == "pool-exhaustion-and-cancel":
            require(value["Succeeded"] == 0 and value["Failed"] == 3, "Exhausted pool admitted an extra session")
        else:
            count = concurrency if name in ("cold-open-sessions", "return-all-sessions") else 1
            require(value["Operations"] == value["Succeeded"] == count and value["Failed"] == 0,
                    "Cold/cleanup operation did not succeed")
        totals[name] = delta | {key: value[key] for key in ("Operations", "Succeeded", "Failed", "Rows", "Checksum")}
    warm = raw["WarmOwners"]
    require(len(warm) == 8 and warm[:5] == [1, concurrency, 0, 0, 0] and warm[5] >= concurrency + 1,
            "Warm baseline contains a transient native operation, cursor or buffer")
    settlements = raw["OwnerSettlements"]
    expected_phases = {name for name in windows if "burst" in name or name.startswith("prepared-fluent-")}
    expected_phases |= {"initial-open-session-baseline", "after-exhaustion", "final-source-close"}
    require(len(settlements) == len(expected_phases) and {item["Phase"] for item in settlements} == expected_phases,
            "Missing, duplicate or unexpected explicit native-drain interval")
    for item in settlements:
        counters(item["Start"], item["End"])
        require(item["Matched"] and item["Polls"] > 0 and len(item["Immediate"]) == len(item["Settled"]) == 8,
                "A bounded native owner settlement failed")
        if item["Phase"] == "initial-open-session-baseline":
            require(item["Expected"] is None and item["Polls"] >= 2 and item["Settled"] == warm,
                    "Initial stable owner observation differs")
        else:
            target = [0] * 6 if item["Phase"] == "final-source-close" else warm[:6]
            require(item["Expected"][:6] == item["Settled"][:6] == target, "Native owners did not return to target")
    require(raw["SourceReleased"] and len(raw["FinalOwners"]) == 8 and raw["FinalOwners"][:6] == [0] * 6,
            "Final native owners did not drain")
    require(raw["MatchingCanceledOpenResults"] + raw["ResourceLimitOpenResults"] == 3,
            "Exhaustion error classification differs")
    require(raw["PendingCancellationObserved"] == (raw["PendingOpensBeforeCancellation"] > 0
            and raw["MatchingCanceledOpenResults"] > 0), "Pending cancellation qualification differs")
    require(raw["IdleElapsedSeconds"] >= 1 and raw["IdleUserSeconds"] >= 0 and raw["IdleSystemSeconds"] >= 0,
            "Idle CPU boundary is missing or includes invalid counts")
    expected_memory = {"before-source", "sessions-open-before-first-stream", "after-cold-burst",
                       "after-warm-concurrent-bursts", "after-exhaustion", "idle-start", "idle-end",
                       "after-source-close", "after-close-idle"}
    require(len(raw["Memory"]) == len(expected_memory) and {item["Phase"] for item in raw["Memory"]} == expected_memory,
            "A labelled memory/forced-GC observation is absent")
    for item in raw["Memory"]:
        counters(item["BeforeForcedGc"], item["AfterForcedGc"])
        require(item["ManagedLiveBytesAfterForcedGc"] >= 0 and item["PeakRssBytes"] >= item["RssBytes"] > 0,
                "Memory sample differs")
    raw_bursts = [value for name, value in windows.items() if "raw-suspended-burst" in name]
    qualifications = {
        "actualSuspensionObservedInEveryRawBurst": all(value["PendingAtReturnCount"] > 0 for value in raw_bursts),
        "unconsumedSuspensionsExceedCacheCapacityInEveryRawBurst": all(value["UnconsumedSuspensionsExceedCacheCapacity"] for value in raw_bursts),
        "pendingOpenCancellationObserved": raw["PendingCancellationObserved"],
        "resourceLimitRejectionObserved": raw["ResourceLimitOpenResults"] > 0,
    }
    return {"windowTotals": totals, "qualifications": qualifications,
            "qualified": all(qualifications.values()), "frameworkAllocatedBytes": None,
            "fullFrameworkZeroAllocation": False, "nativeHeapPeak": None,
            "nativeHeapScope": "Owner counters and process RSS/PSS are not native malloc/free instrumentation."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--expected-source-commit", required=True)
    parser.add_argument("--provider", choices=("sqlite", "postgresql"), required=True)
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--postgres-bin", type=Path)
    parser.add_argument("--processes", type=int, default=3)
    parser.add_argument("--concurrency", type=int, default=16)
    parser.add_argument("--processor-count", type=int, default=4)
    args = parser.parse_args()
    require(3 <= args.processes <= 6 and 8 <= args.concurrency <= 32
            and 1 <= args.processor_count < args.concurrency - 1, "Diagnostic bounds/capacity pressure are invalid")
    require(args.provider != "postgresql" or args.postgres_bin is not None, "PostgreSQL tools are required")
    output = args.output.resolve()
    require(not output.exists(), "Use a fresh evidence directory; failed attempts are immutable")
    output.mkdir(parents=True)
    receipt = {"schemaVersion": 1, "startedUtc": utc(), "status": "started", "provider": args.provider,
               "driverSha256": sha(__file__), "observerSha256": sha(BENCH / "run.py"),
               "packageValidatorSha256": sha(BENCH / "managed/final_allocations.py"),
               "processes": [], "cpuAffinity": sorted(os.sched_getaffinity(0)),
               "processorCountOverride": args.processor_count, "concurrency": args.concurrency,
               "scope": "Fresh isolated database per process; diagnostic allocation/retention only, no long/timing credit. PG live-tree/wait4 observer is a cross-check, not complete per-window server CPU attribution.",
               "effectiveLongSeconds": 0, "fullFrameworkZeroAllocation": False, "subtractedBytes": 0}
    try:
        package = PACKAGES.candidate_inputs(args.artifacts.resolve(), args.expected_source_commit)[args.provider]
        build = build_inputs(args.app.resolve(), args.provider, package)
        receipt.update(package=package, build=build)
        for repetition in range(args.processes):
            folder = output / ("process-" + str(repetition))
            folder.mkdir()
            result = folder / "result.json"
            command = {"name": "probe", "phase": "resource-inventory", "timeoutSeconds": 300,
                       "argv": ["/usr/bin/env", "DOTNET_PROCESSOR_COUNT=" + str(args.processor_count),
                                build["executable"], "{connection}", str(result), package["package_path"],
                                package["source_commit"], str(args.concurrency)]}
            plan = {"provider": args.provider, "commands": [command], "schemaVersion": 1}
            if args.provider == "postgresql":
                plan["postgres"] = {"bin": str(args.postgres_bin.resolve()), "data": str(folder / "pgdata"),
                                    "maxConnections": args.concurrency + 8, "maxClientConcurrency": args.concurrency}
            else:
                plan["connection"] = str(folder / "isolated.sqlite")
            RUN.write_json(folder / "plan.json", plan)
            RUN.validate_plan(plan, folder)
            record = {"repetition": repetition, "directory": folder.name, "status": "started"}
            receipt["processes"].append(record)
            try:
                with RUN.database(plan, folder) as (connection, server_pid):
                    observed = RUN.execute(command, connection, server_pid, folder, 0.1, check=False)
                record["observerSuccess"] = observed["success"]
                if result.is_file():
                    record["rawSha256"] = sha(result)
                    raw = json.loads(result.read_text())
                    record["processId"] = raw.get("ProcessId")
                    record["validation"] = validate(raw, args.provider, package, build, args.concurrency, args.processor_count)
                require(observed["success"] and result.is_file(), "A resource probe process failed; raw evidence retained")
                require(record["validation"]["qualified"], "Required suspension/exhaustion coverage was not observed; no coverage credit")
                record["status"] = "qualified"
            finally:
                record["files"] = {str(path.relative_to(folder)): sha(path) for path in sorted(folder.iterdir()) if path.is_file()}
                RUN.write_json(output / "receipt.json", receipt)
        require(len({item["processId"] for item in receipt["processes"]}) == args.processes, "Independent process identities repeated")
        receipt["status"] = "qualified"
    except BaseException as error:
        receipt["status"] = "failed-or-unqualified"
        receipt["error"] = {"type": type(error).__name__, "message": str(error)}
        raise
    finally:
        receipt["finishedUtc"] = utc()
        RUN.write_json(output / "receipt.json", receipt)
    print(json.dumps({"status": receipt["status"], "provider": args.provider,
                      "processes": len(receipt["processes"]), "effectiveLongSeconds": 0}))


if __name__ == "__main__":
    main()
