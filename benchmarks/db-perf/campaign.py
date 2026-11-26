#!/usr/bin/env python3
"""Prepare, admit and resume one sequential DB-Perf-01 Linux VM campaign.

A measured segment is not whole-batch acceptance. Missing workload, allocation,
publication or metric evidence remains explicit even after eight timed segments.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import re
import shutil
import signal
import statistics
import subprocess
import time

from campaign_build import PROVIDERS, ROUTES, duration_budget, prepare, require, request_record, validate_heavy_workload
from run import cgroup_snapshot, database, execute, host_cpu, process, sample, server_cpu_group_delta, sha, utc, write_json
from plan_diagnostics import collect_postgres, replay_sqlite


def get(value, *keys, default=None):
    lowered = {str(k).replace("_", "").lower(): v for k, v in value.items()}
    for key in keys:
        key = key.replace("_", "").lower()
        if key in lowered:
            return lowered[key]
    return default


def validate_provider_identity(raw, provider, route):
    expected = {"direct_cpp": {"postgresql": "postgresql", "sqlite": "sqlite"},
                "korda_before": {"postgresql": "PostgreSql", "sqlite": "Sqlite"},
                "korda_after": {"postgresql": "PostgreSql", "sqlite": "Sqlite"},
                "independent_csharp_orm": {"postgresql": "Npgsql", "sqlite": "Microsoft.Data.Sqlite.Core"}}
    require(route in expected and provider in expected[route] and get(raw, "provider") == expected[route][provider],
            "Executed provider identity differs from the exact route-specific driver")


def validate_uninstrumented_timing(raw):
    require(get(raw, "nativeAllocationProfile") is None and get(raw, "timingComparisonEligible", default=True) is True,
            "An instrumented or timing-ineligible result cannot enter a normal comparison lane")


def validate_subset_consumption(raw, manifest, case_ids):
    expected = {query["name"]: query["expectedRows"] for query in manifest["queries"] if query["name"] in case_ids}
    require(set(expected) == set(case_ids), "Short-query subset lacks a frozen row-count oracle")
    counts = get(raw, "cases", "caseCounts")
    if isinstance(counts, dict):
        counts = [dict(value, name=name) for name, value in counts.items()]
    require(isinstance(counts, list) and {get(value, "name") for value in counts} == set(case_ids),
            "Short-query subset lacks actual per-case consumption counts")
    require(all(get(value, "rows") == get(value, "succeeded") * expected[get(value, "name")] for value in counts),
            "Short-query subset did not consume every expected row of each successful case")


def finite(value, name, minimum=0):
    require(type(value) in (int, float) and math.isfinite(value) and value >= minimum, "Invalid " + name)
    return value


def quantile(histogram, fraction):
    require(isinstance(histogram, list) and len(histogram) == 8192 and all(type(n) is int and n >= 0 for n in histogram), "Invalid raw latency histogram")
    count = sum(histogram)
    require(count > 0, "No latency observations")
    target, total = math.ceil(count * fraction), 0
    for index, value in enumerate(histogram):
        total += value
        if total >= target:
            return (2 ** (index // 128)) * (1 + ((index % 128) + 1) / 128) / 1000
    raise ValueError("Histogram count did not reconcile")


def verify_bundle(bundle):
    manifest = json.loads((bundle / "bundle.json").read_text())
    require(manifest["task"] == "DB-Perf-01", "Wrong runtime bundle")
    actual = {str(path.relative_to(bundle)) for path in bundle.rglob("*") if path.is_file()}
    require(actual == set(manifest["files"]) | {"bundle.json"}, "Bundle file inventory changed")
    for relative, expected in manifest["files"].items():
        path = bundle / relative
        require(not Path(relative).is_absolute() and path.resolve().is_relative_to(bundle), "Bundle path escapes runtime root")
        require(path.is_file() and path.stat().st_size == expected["bytes"] and sha(path) == expected["sha256"], "Bundle bytes changed: " + relative)
        require((path.stat().st_mode & 0o777) == expected["mode"], "Runtime file mode changed: " + relative)
        require((os.readlink(path) if path.is_symlink() else None) == expected["symlink"], "Runtime symlink target changed: " + relative)
    require(sha(bundle / "request.json") == manifest["requestSha256"], "Request differs from prepared inputs")
    return manifest


def readable(path):
    try:
        return {"path": str(path), "value": Path(path).read_text().strip()}
    except OSError as error:
        return {"path": str(path), "unavailableErrno": error.errno}


def environment_identity():
    return {"hostname": platform.node(), "bootId": readable("/proc/sys/kernel/random/boot_id"),
            "githubRunId": os.environ.get("GITHUB_RUN_ID"), "githubRunAttempt": os.environ.get("GITHUB_RUN_ATTEMPT"),
            "githubJob": os.environ.get("GITHUB_JOB"), "runnerName": os.environ.get("RUNNER_NAME"),
            "runnerEnvironment": os.environ.get("RUNNER_ENVIRONMENT"), "imageOS": os.environ.get("ImageOS"),
            "imageVersion": os.environ.get("ImageVersion")}


def cpu_delta(before, after):
    keys = ("user", "nice", "system", "idle", "iowait", "irq", "softirq", "steal")
    delta = {key: after.get(key, 0) - before.get(key, 0) for key in keys}
    require(all(value >= 0 for value in delta.values()), "Host CPU counters reset")
    total = sum(delta.values())
    require(total > 0, "Empty host CPU observation")
    return {"totalTicks": total, "stealFraction": delta["steal"] / total,
            "busyFraction": (total - delta["idle"] - delta["iowait"]) / total, "delta": delta}


def quota_assessment(snapshot):
    require(snapshot.get("available") is True and snapshot.get("mountRoot") == "/"
            and snapshot.get("sameCgroupNamespaceAsInit") is True and snapshot.get("hostInit") in ("systemd", "init"),
            "The VM's complete cgroup-v2 quota ancestry is not observable")
    capacities, signature, throttles = [], [], {}
    for node in snapshot["ancestors"]:
        signature.append({key: node.get(key) for key in ("path", "root", "quotaStatus", "cpu.max", "cpuset.cpus.effective")})
        stats = {key: int(value) for key, value in (line.split() for line in node["cpu.stat"].splitlines())}
        require(all(key in stats and stats[key] >= 0 for key in ("usage_usec", "user_usec", "system_usec")), "Cgroup CPU usage counters are incomplete")
        if node["quotaStatus"] == "read":
            quota, period = node["cpu.max"].split()
            require(int(period) > 0 and (quota == "max" or int(quota) > 0), "Malformed cgroup bandwidth limit")
            if quota != "max":
                capacities.append(int(quota) / int(period))
            require("nr_throttled" in stats and stats["nr_throttled"] >= 0, "Enabled cgroup CPU bandwidth counters are missing")
            throttles[node["path"]] = stats["nr_throttled"]
        else:
            require(node["quotaStatus"] in ("root_has_no_cpu_max", "cpu_controller_not_enabled_for_group"), "CPU quota absence is not explained")
    return {"maximumCpuCapacity": min(capacities) if capacities else None,
            "quotaSignature": signature, "localThrottleCounters": throttles}


def admission(request, output):
    settings = request["admission"]
    available = sorted(os.sched_getaffinity(0))
    groups = [settings[key] for key in ("clientCpus", "serverCpus", "observerCpus")]
    require(all(group and len(group) == len(set(group)) for group in groups), "CPU groups must be nonempty and unique")
    require(len(set(sum(groups, []))) == sum(map(len, groups)) and set(sum(groups, [])) <= set(available), "Client/server/observer affinity must be disjoint and available")
    require(os.environ.get("GITHUB_ACTIONS") == "true" and os.environ.get("RUNNER_ENVIRONMENT") == "github-hosted", "Only the reviewed dedicated hosted VM path is currently admitted")
    require(platform.system() == "Linux" and platform.machine() == "x86_64", "Campaign needs Linux x64")
    require(shutil.disk_usage(output).free >= settings["minimumFreeDiskBytes"], "Insufficient measured disk reserve")
    limits = cgroup_snapshot()
    write_json(output / "quota-observation.json", {"schemaVersion": 1, "task": "DB-Perf-01",
        "kind": "pre_admission_quota_observation", "observedUtc": utc(), "identity": environment_identity(),
        "availableCpus": available, "limits": limits, "qualified": False, "longSegmentsCredited": 0})
    quota = quota_assessment(limits)
    require(quota["maximumCpuCapacity"] is None or quota["maximumCpuCapacity"] >= len(available), "Guest CPU quota is smaller than the frozen visible resource set")
    identity = environment_identity()
    require("value" in identity["bootId"], "Guest boot identity is unavailable")
    os.sched_setaffinity(0, settings["observerCpus"])
    start, first = time.monotonic_ns(), host_cpu()
    idle_samples = []
    while time.monotonic_ns() - start < settings["idleSeconds"] * 1e9:
        idle_samples.append(sample())
        time.sleep(min(1, settings["idleSeconds"]))
    idle = cpu_delta(first, host_cpu())
    record = {"schemaVersion": 1, "kind": "hosted_vm_admission_candidate", "identity": identity,
              "availableCpus": available, "clientCpus": groups[0], "serverCpus": groups[1], "observerCpus": groups[2],
              "uname": list(platform.uname()), "cpuinfo": readable("/proc/cpuinfo"), "meminfo": readable("/proc/meminfo"),
              "limits": limits, "quotaAssessment": quota, "idle": idle, "idleSamples": idle_samples,
              "exclusiveScope": "One GitHub-hosted job VM reserved for this campaign; physical-host core exclusivity is not asserted",
              "idleAccepted": idle["stealFraction"] <= settings["maximumStealFraction"] and idle["busyFraction"] <= settings["maximumIdleBusyFraction"],
              "qualified": False, "pending": ["before-only independent-process noise calibration"]}
    write_json(output / "environment.json", record)
    require(record["idleAccepted"], "Idle VM noise/steal exceeded frozen admission limits; retain this rejected admission and start a new campaign")
    return record


def validate_admission(record, request, identity):
    settings = request["admission"]
    require(record["identity"] == identity, "The admitted VM changed")
    require(record.get("idleAccepted") is True and record["idle"]["stealFraction"] <= settings["maximumStealFraction"]
            and record["idle"]["busyFraction"] <= settings["maximumIdleBusyFraction"],
            "Previously failed idle admission cannot be bypassed by resume")
    for key in ("clientCpus", "serverCpus", "observerCpus"):
        require(record[key] == settings[key], "Frozen CPU partition changed")
    require(set(record["availableCpus"]) <= set(os.sched_getaffinity(0)), "The resumed process lost admitted CPUs")
    require(quota_assessment(cgroup_snapshot())["quotaSignature"] == record["quotaAssessment"]["quotaSignature"], "Guest CPU quota ancestry changed after admission")


def runtime_dependencies(bundle, manifest):
    paths = {bundle / path for path in manifest["applications"].values()}
    paths.add(bundle / manifest["postgresBin"] / "postgres")
    paths.update(path for path in (bundle / "apps").rglob("*.so") if path.is_file())
    images, libraries = {}, {}
    for path in sorted(paths):
        result = subprocess.run(["ldd", str(path)], text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=30)
        require(result.returncode == 0 and "not found" not in result.stdout, "Runtime dependency could not be resolved for " + str(path))
        images[str(path.relative_to(bundle))] = {"sha256": sha(path), "ldd": result.stdout}
        for target in re.findall(r"(?:=>\s*)?(/\S+)\s+\(0x[0-9a-f]+\)", result.stdout):
            library = Path(target).resolve()
            libraries[str(library)] = {"sha256": sha(library), "bytes": library.stat().st_size}
    return {"schemaVersion": 1, "observedUtc": utc(), "images": images, "libraries": libraries,
            "glibc": subprocess.check_output(["getconf", "GNU_LIBC_VERSION"], text=True, timeout=10).strip(),
            "scope": "ELF dependencies resolved on the measurement VM; each consumer separately hashes its actual dlopened provider/SQLite image"}


def raw_histogram(value, count, name):
    require(isinstance(value, list) and len(value) == 8192 and all(type(n) is int and n >= 0 for n in value)
            and sum(value) == count, name + " histogram does not reconcile")
    return value


def validate_oracle(raw, *, interval=True):
    """Require new producer evidence; absent historical counters never mean zero.

    The process-lifetime counter is set where a result or persistence check
    fails, before cleanup can replace its exception. It cannot be diluted into
    the permitted rate of ordinary provider errors or reset between phases.
    """
    missing = object()
    total = get(raw, "oracleFailures", default=missing)
    require(type(total) is int and total >= 0, "Explicit cumulative oracle failure count is required")
    first = get(raw, "firstOracleFailure", default=missing)
    require(first is not missing, "Explicit first oracle failure evidence is required")
    before = after = None
    if interval:
        before = get(raw, "oracleFailuresBefore", default=missing)
        after = get(raw, "oracleFailuresAfter", default=missing)
        require(type(before) is int and type(after) is int and 0 <= before <= after <= total,
                "Oracle interval counters are missing or do not reconcile with the process total")
    require(total == 0 and first is None,
            "A workload oracle violation invalidates the command regardless of SLO or cleanup error classification")
    return {"before": before, "after": after, "total": total}


def normalized_result(raw, expected_cases, seconds, rate=None, *, require_coverage=True, ownership="final"):
    oracle = validate_oracle(raw)
    declared = get(raw, "declaredCaseIds", "actualCaseIds")
    require(declared == expected_cases, "Declared case sequence differs from the frozen shared workload")
    case_counts = get(raw, "cases", "caseCounts")
    if isinstance(case_counts, dict):
        case_counts = [dict(value, name=name) for name, value in case_counts.items()]
    require(isinstance(case_counts, list), "Per-case measured operation counts are required")
    names = [get(case, "name") for case in case_counts]
    require(len(names) == len(set(names)) and set(names) <= set(expected_cases), "Duplicate or foreign per-case count")
    totals = {name: 0 for name in ("started", "succeeded", "failed", "rows")}
    observed_cases = set()
    for case in case_counts:
        values = {name: get(case, name) for name in totals}
        require(all(type(n) is int and n >= 0 for n in values.values()), "Per-case count is missing or negative")
        require(values["started"] == values["succeeded"] + values["failed"], "Per-case completion counts differ")
        if values["started"]:
            observed_cases.add(get(case, "name"))
        for name in totals:
            totals[name] += values[name]
    actual = get(raw, "executedCaseIds")
    if actual is not None:
        require(isinstance(actual, list) and len(actual) == len(set(actual)) and set(actual) == observed_cases,
                "Declared actual coverage differs from per-case executions")
    missing = [name for name in expected_cases if name not in observed_cases]
    require(not require_coverage or not missing, "At least one frozen case never actually started")
    if ownership == "warm":
        owner_scope = "returned to the same retained warm owners; connections and plans remain alive between recovery phases"
        require(get(raw, "sameWarmOwners", "returnedToWarmOwnerBaseline") is True, "Phase owners did not return to the warm baseline")
    else:
        require(ownership == "final", "Unknown ownership boundary")
        owner_scope = "tracked native/framework owners" if get(raw, "ownersReturnedToZero") is not None else "application connections only; independent ORM internal owners are not asserted"
        require(get(raw, "ownersReturnedToZero", "applicationConnectionsReturnedToZero") is True, "Tracked owners did not drain")
    require(not get(raw, "timeSeriesTruncated", default=False), "Time series was truncated")
    require(get(raw, "accountingReconciles", default=True) is True, "Consumer rejected its own operation accounting")
    offered = get(raw, "offeredLoad", "offered")
    if rate is not None:
        require(isinstance(offered, dict), "An open-loop run requires its complete offered-load ledger")
        offered_oracle = validate_oracle(offered)
        require(offered_oracle["before"] == oracle["before"] and offered_oracle["after"] == oracle["after"],
                "Offered and enclosing oracle intervals differ")
        names = ("planned", "attempted", "sent", "started", "completed", "succeeded", "failed", "rejected", "retries", "rows")
        counts = {name: get(offered, name) for name in names}
        require(all(type(value) is int and value >= 0 for value in counts.values()), "Offered operation counters are missing")
        require(counts["planned"] == math.ceil(rate * seconds) == counts["attempted"] == counts["sent"] + counts["rejected"], "Planned arrivals vanished from accounting")
        require(counts["sent"] == counts["started"] == counts["completed"] == counts["succeeded"] + counts["failed"], "Queue drain/completion ledger differs")
        require(counts["retries"] == 0, "The frozen workload does not retry requests")
        require(get(offered, "accountingReconciles", default=True) is True, "Consumer rejected offered-load accounting")
        frequency = finite(get(offered, "timerFrequency"), "timer frequency", 1)
        def timer(name):
            return finite(get(offered, name), name) / frequency * 1e9
        start, deadline, offered_end, end = (timer(name) for name in ("startedTicks", "deadlineTicks", "offeredEndTicks", "drainedTicks"))
        require(start <= deadline <= offered_end <= end and abs((deadline - start) / 1e9 - seconds) < 0.01, "Offered duration/timer boundary differs")
        histogram = raw_histogram(get(offered, "successScheduledHistogram"), counts["succeeded"], "Success scheduled")
        raw_histogram(get(offered, "allCompletedScheduledHistogram"), counts["completed"], "All completed scheduled")
        raw_histogram(get(offered, "successServiceHistogram"), counts["succeeded"], "Success service")
        raw_histogram(get(offered, "startDelayHistogram"), counts["started"], "Start delay")
        series = get(offered, "seconds")
        require(isinstance(series, list) and series, "Offered per-second time series is missing")
        series_names = ("planned", "sent", "started", "succeeded", "failed", "rejected", "rows")
        require([get(item, "second") for item in series] == list(range(len(series))), "Per-second time series has gaps or duplicates")
        for name in series_names:
            values = [get(item, name) for item in series]
            require(all(type(value) is int and value >= 0 for value in values) and sum(values) == counts[name],
                    "Per-second " + name + " does not reconcile")
        require(not get(offered, "timeSeriesTruncated", default=False), "Offered time series truncated")
        duration = (end - start) / 1e9
    else:
        succeeded = get(raw, "succeeded", "completed")
        failed, started = get(raw, "failed"), get(raw, "started")
        require(all(type(n) is int and n >= 0 for n in (succeeded, failed, started)), "Closed-loop counters are missing")
        require(started == succeeded + failed, "Closed-loop completions do not reconcile")
        counts = {"planned": started, "attempted": started, "sent": started, "started": started,
                  "completed": succeeded + failed, "succeeded": succeeded, "failed": failed,
                  "rejected": 0, "retries": get(raw, "retries", default=0), "rows": get(raw, "rows")}
        duration = finite(get(raw, "effectiveSeconds"), "effective seconds", 0.000001)
        require(duration >= seconds - 0.05, "Closed-loop measurement ended before the requested duration")
        latency = get(raw, "latencies")
        histogram = get(latency, "histogram")
        start = get(raw, "steadyStartMonotonicNs")
        end = get(raw, "steadyEndMonotonicNs")
        deadline = end
        require(type(start) in (int, float) and type(end) in (int, float) and end > start, "Consumer monotonic measurement boundaries are required")
        require(abs((end - start) / 1e9 - duration) < 0.05, "Closed-loop elapsed and monotonic boundaries disagree")
        offered_end = end
    raw_histogram(histogram, counts["succeeded"], "Success latency")
    require(all(totals[name] == counts[name] for name in totals), "Per-case totals differ from the global operation/row ledger")
    return {"counts": counts, "correctnessPassed": True, "oracleFailures": oracle["total"],
            "elapsedThroughDrainSeconds": duration, "offeredSeconds": seconds if rate else None,
            "steadyStartMonotonicNs": int(start), "plannedOfferedEndMonotonicNs": int(deadline),
            "offeredEndMonotonicNs": int(offered_end), "drainedMonotonicNs": int(end),
            "missingExecutedCaseIds": missing, "fullCaseCoverage": not missing,
            "ownershipCheckScope": owner_scope,
            "successOpsPerSecond": counts["succeeded"] / duration, "rowsPerSecond": counts["rows"] / duration,
            "errorAndRejectionRate": (counts["failed"] + counts["rejected"]) / max(1, counts["planned"]),
            "p50MicrosecondsUpper": quantile(histogram, 0.50) if counts["succeeded"] else None,
            "p95MicrosecondsUpper": quantile(histogram, 0.95) if counts["succeeded"] else None,
            "p99MicrosecondsUpper": quantile(histogram, 0.99) if counts["succeeded"] else None,
            "latencyBoundary": "scheduled-to-completion" if rate else "service time; closed-loop capacity only"}


def normalized_recovery(raw, expected_cases, settings, fixed_rate):
    oracle = validate_oracle(raw, interval=False)
    phases = get(raw, "phases")
    require(isinstance(phases, list) and [get(phase, "name") for phase in phases] == ["reference", "overload", "recovery"],
            "Recovery must retain all three ordered phases")
    native = get(raw, "sameConnectionsThroughout") is not None
    retained = (get(raw, "sameConnectionsThroughout") is True and get(raw, "sameWorkerObjectsThroughout") is True) if native else (
        get(raw, "sameSourceInstance") is True and get(raw, "sameWorkerAndSessionInstances") is True
        and get(raw, "poolRebuiltBetweenPhases") is False)
    require(retained, "Recovery rebuilt its source, connections or worker objects")
    final_zero = get(raw, "ownersReturnedToZero", "trackedResourcesReturnedToZero") is True
    require(final_zero, "Recovery final tracked owners did not drain")
    if not native:
        require(get(raw, "actualCaseIds") == expected_cases, "Recovery declared a different case sequence")
    concurrency = get(raw, "concurrency")
    require(type(concurrency) is int and concurrency > 0, "Recovery concurrency is missing")
    owner_fields = ("cancellationOwners", "connections", "pgResults", "sqliteStatements") if native else (
        "sources", "connections", "operations", "cursors", "buffers", "owners")
    def owner_counts(value):
        require(isinstance(value, dict), "Raw recovery ownership snapshot is missing")
        missing = object()
        counts = {name: get(value, name, default=missing) for name in owner_fields}
        require(all(type(value) is int and value >= 0 or (not native and value is None)
                    for value in counts.values()), "Raw recovery owner count is missing or invalid")
        require(type(counts["connections"]) is int, "Application connection ownership must be observable")
        return counts
    first = get(phases[0], "measurement") if native else phases[0]
    warm = owner_counts(get(first, "beforeOwners") if native else get(raw, "warmOwners"))
    require(warm["connections"] == concurrency, "Warm connection count differs from frozen concurrency")
    transient_fields = ("cancellationOwners", "pgResults") if native else ("operations", "cursors", "buffers")
    require(all(warm[name] in (None, 0) for name in transient_fields), "Warm owners include unfinished work")
    final = owner_counts(get(raw, "finalOwners") if native else get(raw, "drained"))
    require(all(final[name] == (None if value is None else 0) for name, value in warm.items()),
            "Raw final recovery owners did not drain, or observability changed")
    connection_ids = get(first, "connectionIds") if native else None
    if native:
        require(isinstance(connection_ids, list) and len(connection_ids) == concurrency
                and all(type(value) is int and value > 0 for value in connection_ids)
                and len(set(connection_ids)) == concurrency, "Native recovery connection identities are missing or duplicated")
    output = []
    totals = {key: 0 for key in ("planned", "attempted", "sent", "started", "completed", "succeeded", "failed", "rejected", "retries", "rows")}
    for index, phase in enumerate(phases):
        value = get(phase, "measurement") if native else dict(phase, actualCaseIds=expected_cases)
        require(get(value, "nativeAllocationProfile") is None, "Recovery phases cannot enable allocator instrumentation")
        require(owner_counts(get(value, "beforeOwners")) == warm == owner_counts(get(value, "afterOwners")),
                "Raw phase owners differ from the retained warm baseline")
        if native:
            require(get(value, "connectionIds") == connection_ids, "Native recovery changed connection identities")
        duration = settings["overloadSeconds"] if index == 1 else settings["seconds"]
        rate = settings["overloadRate"] if index == 1 else fixed_rate
        measurement = normalized_result(value, expected_cases, duration, rate, require_coverage=index != 1, ownership="warm")
        require(not output or output[-1]["measurement"]["drainedMonotonicNs"] <= measurement["steadyStartMonotonicNs"],
                "Recovery phase measurement intervals overlap or are out of order")
        for key in totals:
            totals[key] += measurement["counts"][key]
        output.append({"name": get(phase, "name"), "measurement": measurement})
    return {"counts": totals, "correctnessPassed": True, "oracleFailures": oracle["total"],
            "phases": output, "sameSourceConnectionsAndWorkers": True,
            "ownersReturnedToZero": final_zero, "consumerPassed": get(raw, "passed") is True,
            "warmOwners": warm, "finalOwners": final, "connectionIds": connection_ids,
            "saturationObserved": output[1]["measurement"]["counts"]["rejected"] > 0,
            "scope": "same retained source/session/connection/worker objects; phase schedulers and worker threads may restart; no long-segment credit"}


def recovery_meets_slo(measurement, settings):
    return (measurement.get("correctnessPassed") is True and type(measurement.get("oracleFailures")) is int
            and measurement["oracleFailures"] == 0 and measurement["consumerPassed"] and measurement["saturationObserved"] and measurement["counts"]["failed"] == 0
            and all(meets_slo(measurement["phases"][index]["measurement"], settings) for index in (0, 2)))


def validate_native_profile(profile, library_sha, capacity):
    require(isinstance(profile, dict) and get(profile, "librarySha256") == library_sha
            and get(profile, "observedAllocationCoverageComplete") is True and get(profile, "requestedByteConservation") is True
            and get(profile, "timingComparisonEligible") is False and get(profile, "fullAddressSpaceHeapPeakProven") is False,
            "Native allocation coverage/conservation/library identity is unproven")
    snapshots = [get(profile, key) for key in ("before", "after", "afterDrain")]
    bad = ("unknownFrees", "unknownReallocs", "droppedAllocations", "duplicateAddresses", "initializationFailures")
    counters = ("mallocCalls", "callocCalls", "reallocCalls", "alignedCalls", "freeCalls", "nullFrees",
                "successfulAllocations", "failedAllocations", "deallocations", "allocatedRequestedBytes",
                "releasedRequestedBytes", "allocatedUsableBytes", "releasedUsableBytes")
    for snapshot in snapshots:
        require(isinstance(snapshot, dict) and get(snapshot, "version") == 1 and get(snapshot, "tableCapacity") == capacity
                and get(snapshot, "tableBytes") == capacity * 24, "Native profiler version/capacity/overhead differs")
        require(all(get(snapshot, name) == 0 for name in bad), "Raw native allocation coverage failures cannot be hidden by a success flag")
        require(all(type(get(snapshot, name)) is int and get(snapshot, name) >= 0 for name in counters), "Missing raw allocation counter")
        live = get(snapshot, "liveAllocations")
        require(type(live) is int and live >= 0
                and get(snapshot, "successfulAllocations") == get(snapshot, "deallocations") + live,
                "Raw native live allocation count does not conserve successful allocations and deallocations")
        for peak, current in (("peakLiveAllocations", "liveAllocations"), ("peakRequestedBytes", "liveRequestedBytes"),
                              ("peakUsableBytes", "liveUsableBytes")):
            require(type(get(snapshot, peak)) is int and type(get(snapshot, current)) is int
                    and get(snapshot, peak) >= get(snapshot, current) >= 0, "Raw native heap peak is below live tracked usage")
    for before, after in zip(snapshots, snapshots[1:]):
        require(all(get(after, name) >= get(before, name) for name in counters), "Native allocation cumulative counters reset")
        for unit in ("Requested", "Usable"):
            allocated = get(after, "allocated" + unit + "Bytes") - get(before, "allocated" + unit + "Bytes")
            released = get(after, "released" + unit + "Bytes") - get(before, "released" + unit + "Bytes")
            require(get(before, "live" + unit + "Bytes") + allocated == get(after, "live" + unit + "Bytes") + released,
                    "Raw native allocation conservation differs")
        require(get(before, "liveAllocations") + get(after, "successfulAllocations") - get(before, "successfulAllocations")
                == get(after, "liveAllocations") + get(after, "deallocations") - get(before, "deallocations"),
                "Raw native allocation event counts do not conserve across boundaries")
        require(all(get(after, name) >= get(before, name) for name in ("peakLiveAllocations", "peakRequestedBytes", "peakUsableBytes")),
                "Native tracked peaks reset after the measurement boundary")
    for result, counter in (("allocationCalls", "successfulAllocations"), ("deallocationEvents", "deallocations"),
                            ("allocatedRequestedBytes", "allocatedRequestedBytes"), ("releasedRequestedBytes", "releasedRequestedBytes"),
                            ("allocatedUsableBytes", "allocatedUsableBytes"), ("releasedUsableBytes", "releasedUsableBytes")):
        require(get(profile, result) == get(snapshots[1], counter) - get(snapshots[0], counter), "Native profile derived count differs from raw cumulative bytes")
    for unit in ("Requested", "Usable"):
        require(get(profile, "tracked" + unit + "HeapPeakBytes") == get(snapshots[1], "peak" + unit + "Bytes"),
                "Native reported heap peak differs from the raw measured snapshot")


def summarize_resources(path, measurement):
    begin, end = measurement["steadyStartMonotonicNs"], measurement["drainedMonotonicNs"]
    samples = [json.loads(line) for line in path.read_text().splitlines() if line]
    steady = [s for s in samples if s["monotonicNs"] >= begin and s["acquiredThroughMonotonicNs"] <= end]
    require(len(steady) >= 2, "Too few independently timestamped steady resource samples")
    result = {"samples": len(steady), "firstMonotonicNs": steady[0]["monotonicNs"],
              "lastMonotonicNs": steady[-1]["acquiredThroughMonotonicNs"],
              "boundaryCoverage": "Whole acquisition intervals inside client steady+drain boundary; unsampled edges are not extrapolated",
              "hostCpu": cpu_delta(steady[0]["hostCpuTicks"], steady[-1]["hostCpuTicks"])}
    intervals = [cpu_delta(left["hostCpuTicks"], right["hostCpuTicks"])
                 for left, right in zip(steady, steady[1:])]
    result["maximumIntervalStealFraction"] = max(item["stealFraction"] for item in intervals)
    try:
        quotas = [quota_assessment(s["guestQuota"]) for s in steady]
        result["quotaStableAndAvailable"] = all(value["quotaSignature"] == quotas[0]["quotaSignature"] for value in quotas)
        first, last = quotas[0]["localThrottleCounters"], quotas[-1]["localThrottleCounters"]
        require(set(first) == set(last), "Cgroup throttle ancestry changed")
        deltas = {path: last[path] - value for path, value in first.items()}
        require(all(value >= 0 for value in deltas.values()), "Cgroup throttle counters reset")
        result["guestThrottleEvents"] = sum(deltas.values())
        result["guestThrottleEventsByAncestor"] = deltas
        result["guestThrottleScope"] = "sum of local bandwidth events across every enabled ancestor; no local bandwidth controller means not applicable"
    except (ValueError, KeyError) as error:
        result.update(quotaStableAndAvailable=False, guestThrottleEvents=None, quotaObservationError=str(error))
    ticks = os.sysconf("SC_CLK_TCK")
    for side in ("client", "server"):
        readings = [s[side + "Totals"] for s in steady if s[side + "Totals"]["processCount"]]
        if not readings:
            result[side] = {"status": "not_present"}
            continue
        memory = [s for s in readings if s["rssComplete"]]
        pss = [s["sampledPssBytes"] for s in readings if s["pssComplete"]]
        user = (readings[-1]["ownAndReapedUserTicks"] - readings[0]["ownAndReapedUserTicks"]) / ticks
        system = (readings[-1]["ownAndReapedSystemTicks"] - readings[0]["ownAndReapedSystemTicks"]) / ticks
        result[side] = {"status": "sampled_nonatomic", "userCpuSeconds": user, "systemCpuSeconds": system,
            "counterMonotonic": user >= 0 and system >= 0, "steadyMedianRssBytes": statistics.median(s["sampledRssBytes"] for s in memory) if memory else None,
            "sampledPeakRssBytes": max((s["sampledRssBytes"] for s in memory), default=None),
            "steadyMedianPssBytes": statistics.median(pss) if pss else None, "sampledPeakPssBytes": max(pss, default=None),
            "rssCompleteSamples": len(memory), "pssCompleteSamples": len(pss), "processTreeSnapshotsAreNonAtomic": True}
        if side == "server":
            result[side].update(procTreeUserCpuSeconds=user, procTreeSystemCpuSeconds=system,
                                procTreeCounterMonotonic=user >= 0 and system >= 0)
            groups = [item.get("serverCpuGroup") for item in steady]
            if all(isinstance(group, dict) for group in groups):
                try:
                    for first, last in zip(groups, groups[1:]):
                        server_cpu_group_delta(first, last)
                    result[side].update(server_cpu_group_delta(groups[0], groups[-1]))
                    result[side]["cpuScope"] = "Task-owned cgroup cumulative user/system includes exited PostgreSQL children; complete acquisition intervals inside client steady+drain; unsampled edges are not extrapolated"
                except (ValueError, KeyError, TypeError) as error:
                    result[side].update(cpuAccounting="invalid_task_group", counterMonotonic=False, cpuAccountingError=str(error))
            else:
                result[side].update(cpuAccounting="unavailable_task_group", counterMonotonic=False)
    return result


def meets_slo(value, settings):
    return (value.get("correctnessPassed") is True and type(value.get("oracleFailures")) is int and value["oracleFailures"] == 0
            and value["p99MicrosecondsUpper"] is not None and value["errorAndRejectionRate"] <= settings["maximumErrorAndRejectionRate"]
            and value["p99MicrosecondsUpper"] <= settings["maximumP99Microseconds"])


def resource_quality(resources, provider, admission_settings):
    reasons = []
    if resources.get("quotaStableAndAvailable") is not True:
        reasons.append("guest quota ancestry was unavailable or changed")
    if type(resources.get("guestThrottleEvents")) is not int or resources["guestThrottleEvents"] != 0:
        reasons.append("guest CPU throttling was observed or unmeasurable")
    for value, maximum, label in ((resources.get("hostCpu", {}).get("stealFraction"), admission_settings["maximumStealFraction"], "overall VM steal"),
                                  (resources.get("maximumIntervalStealFraction"), admission_settings.get("maximumIntervalStealFraction", 0.05), "interval VM steal")):
        if type(value) not in (int, float) or not math.isfinite(value) or not 0 <= value <= maximum:
            reasons.append(label + " exceeded the frozen limit or was unavailable")
    for side in ("client", "server") if provider == "postgresql" else ("client",):
        if resources.get(side, {}).get("counterMonotonic") is not True:
            reasons.append(side + " CPU counters were uncertain")
    if provider == "postgresql":
        server = resources.get("server", {})
        if server.get("cpuAccounting") != "task_owned_cgroup_v2" or server.get("taskGroupThrottleEvents") != 0:
            reasons.append("PostgreSQL task-group cumulative CPU was unavailable or throttled")
    return {"passed": not reasons, "reasons": reasons}


def server_owner_accounting(record):
    """A retired, independently owned group is required for formal PG cells."""
    require(record.get("exitCode") == 0 and not any(record.get(key) for key in
            ("shutdownError", "cpuGroupError", "finalSampleError")), "PostgreSQL owner/accounting did not retire cleanly")
    group = record.get("serverCpuGroup", {})
    require(all(group.get(key) is True for key in ("required", "created", "removed", "parentControlsUnchanged")),
            "PostgreSQL task-group creation/cleanup/parent constraints are unverified")
    descriptor = group["descriptor"]
    require(descriptor["serverIdentity"] == {key: record["identity"][key] for key in ("pid", "startTicks")},
            "PostgreSQL task-group owner identity differs")
    final = group["emptyAfterOwnerJoin"]
    for snapshot in (group["beforeClients"], final):
        require(all(snapshot[key] == descriptor[key] for key in ("path", "deviceInode", "controls", "serverIdentity")),
                "PostgreSQL raw cumulative snapshot differs from its owned group")
    require(final.get("members") == [] and final.get("exitedDuringMemberAcquisition") == [],
            "PostgreSQL task-group still has observable owners")
    measured = server_cpu_group_delta(group["beforeClients"], final)
    require(measured == group["clientsThroughShutdownCpu"], "PostgreSQL owner CPU differs from raw cumulative counters")
    cross = group.get("wait4CrossCheck", {})
    require(all(type(record.get(key)) in (int, float) and math.isfinite(record[key]) and record[key] >= 0 for key in
            ("processLifetimeUserCpuSeconds", "processLifetimeSystemCpuSeconds")), "PostgreSQL OS lifetime CPU is unavailable")
    require(cross.get("groupLifetimeUserSeconds") == final["cpu"]["user_usec"] / 1e6
            and cross.get("groupLifetimeSystemSeconds") == final["cpu"]["system_usec"] / 1e6
            and cross.get("wait4ProcessLifetimeUserSeconds") == record.get("processLifetimeUserCpuSeconds")
            and cross.get("wait4ProcessLifetimeSystemSeconds") == record.get("processLifetimeSystemCpuSeconds"),
            "PostgreSQL OS lifetime cross-check is missing or differs from raw counters")
    return {"groupControls": descriptor["controls"], "parentControls": group["parentControlsBefore"]}


def capacity_refinement(points, concurrency, maximum_relative_gap):
    selected = sorted((point for point in points if point["concurrency"] == concurrency), key=lambda point: point["rate"])
    inputs = [{key: point[key] for key in ("name", "rate", "sloPassed", "rawPath", "rawSha256")} for point in selected]
    passing = [point for point in selected if point["sloPassed"]]
    result = {"concurrency": concurrency, "selectionInputs": inputs, "execute": False,
              "assumption": "local observed success/failure bracket; monotonic capacity and global maximum are not proven"}
    if not passing:
        if not selected:
            return dict(result, status="no_observed_rate", lower=None, upper=None, relativeGap=None)
        # A closed-loop estimate can exceed the scheduled-latency capacity even
        # at the grid's lowest fraction. Spend the existing finite refinement
        # budget on lower rates before a passing/failing bracket is available.
        # Every failed point remains an input; this never retries or accepts it.
        upper = selected[0]
        rate = math.floor(upper["rate"] / 2 * 1000) / 1000
        result.update(lower=None, upper={key: upper[key] for key in ("name", "rate", "rawSha256")},
                      relativeGap=None, proposedRate=rate)
        if rate <= 0 or rate >= upper["rate"] or any(point["rate"] == rate for point in selected):
            return dict(result, status="rate_resolution_or_existing_observation_limit")
        return dict(result, status="next_bounded_halving", execute=True)
    lower = max(passing, key=lambda point: point["rate"])
    failed = [point for point in selected if not point["sloPassed"] and point["rate"] > lower["rate"]]
    result.update(lower={key: lower[key] for key in ("name", "rate", "rawSha256")},
                  nonMonotonicSloObserved=any(not point["sloPassed"] and point["rate"] < lower["rate"] for point in selected))
    if not failed:
        return dict(result, status="upper_failure_unobserved", upper=None, relativeGap=None)
    upper = min(failed, key=lambda point: point["rate"])
    relative_gap = (upper["rate"] - lower["rate"]) / lower["rate"]
    rate = math.floor((lower["rate"] + upper["rate"]) / 2 * 1000) / 1000
    result.update(upper={key: upper[key] for key in ("name", "rate", "rawSha256")}, relativeGap=relative_gap, proposedRate=rate)
    if relative_gap <= maximum_relative_gap:
        return dict(result, status="within_requested_bracket_precision")
    if rate <= lower["rate"] or rate >= upper["rate"] or any(point["rate"] == rate for point in selected):
        return dict(result, status="rate_resolution_or_existing_observation_limit")
    return dict(result, status="next_bounded_midpoint", execute=True)


def verify_observation(output, stored):
    path = (output / stored["path"]).resolve()
    require(path.is_relative_to(output) and path.is_file() and sha(path) == stored["sha256"], "Completed checkpoint changed")
    observed = json.loads(path.read_text())
    files = observed["evidenceFiles"]
    required = {observed["rawPath"], str(path.with_name("client.command.json").relative_to(output)),
                str(path.with_name("client.resources.jsonl").relative_to(output)),
                str(path.with_name("runner.json").relative_to(output)), str(path.with_name("plan.json").relative_to(output))}
    require(required <= set(files), "Completed checkpoint lacks raw/command/resource/source evidence bindings")
    for relative, digest in files.items():
        source = (output / relative).resolve()
        require(source.is_relative_to(output) and source.is_file() and sha(source) == digest, "Bound checkpoint evidence changed: " + relative)
    require(files[observed["rawPath"]] == observed["rawSha256"], "Raw result hash conflicts with its evidence inventory")
    return observed


class Campaign:
    def __init__(self, bundle, output, resume):
        self.bundle, self.output = bundle.resolve(), output.resolve()
        self.bundle_manifest = verify_bundle(self.bundle)
        for key, expected in self.bundle_manifest["preparationImage"].items():
            require(expected and os.environ.get(key) == expected, "Prepare/measurement runner images differ: " + key)
        self.request = request_record(self.bundle / "request.json")
        require(self.request["admission"].get("postgresCpuAccounting") == "task-owned-cgroup-v2",
                "Formal PostgreSQL measurement requires task-owned cumulative cgroup CPU")
        self.workload = json.loads((self.bundle / "workload.json").read_text())
        self.cases = self.request["workload"]["cases"]
        require(self.cases and len(self.cases) == len(set(self.cases)), "Freeze a nonempty unique case sequence")
        require(set(self.cases) <= {q["name"] for q in self.workload["queries"]}, "Selected case is absent from workload contract")
        require(sha(self.bundle / "workload.json") == self.request["workload"]["sha256"], "Workload bytes changed")
        self.workloads = {"primary": {"id": "primary", "manifest": self.workload, "cases": self.cases,
            "path": self.bundle / "workload.json", "sha256": self.request["workload"]["sha256"]}}
        if self.request.get("heavyWorkload") is not None:
            specification = self.request["heavyWorkload"]
            path = self.bundle / "heavy-workload.json"
            require(sha(path) == specification["sha256"], "Frozen heavy workload bytes changed")
            manifest = json.loads(path.read_text())
            validate_heavy_workload(self.workload, manifest, self.request["workload"]["sha256"])
            self.workloads[specification["id"]] = {"id": specification["id"], "manifest": manifest,
                "cases": specification["cases"], "path": path, "sha256": specification["sha256"],
                "warmupOperations": specification["warmupOperations"]}
        self.settings = self.request["measurement"]
        self.identity = environment_identity()
        self.state_path = self.output / "campaign.json"
        if self.output.exists() and any(self.output.iterdir()):
            require(resume and self.state_path.exists(), "Use a fresh output directory or explicit matching resume")
            self.state = json.loads(self.state_path.read_text())
            require(self.state["requestSha256"] == sha(self.bundle / "request.json")
                    and self.state["bundleManifestSha256"] == sha(self.bundle / "bundle.json")
                    and self.state["environmentIdentity"] == self.identity, "Resume inputs/VM differ; retain previous observations and start a new campaign")
        else:
            self.output.mkdir(parents=True, exist_ok=True)
            self.state = {"schemaVersion": 1, "task": "DB-Perf-01", "state": "in_progress", "startedUtc": utc(),
                "requestSha256": sha(self.bundle / "request.json"), "bundleManifestSha256": sha(self.bundle / "bundle.json"),
                "environmentIdentity": self.identity, "qualifiedLongSegments": [], "observedLongSegments": [],
                "calibration": {}, "fixedRates": {}, "completedSteps": {}, "wholeBatchAccepted": False}
            self.persist()
        self.state.setdefault("campaignStartedMonotonicNs", time.monotonic_ns())
        self.state["durationBudget"] = duration_budget(self.request)
        self.deadline = self.state["campaignStartedMonotonicNs"] / 1e9 + self.request["timeBudget"]["controllerMinutes"] * 60

    def retry(self, step, reason):
        require(reason and reason.strip(), "Retry needs an explicit retained investigation reason")
        active = self.state.setdefault("activeSteps", {})
        require(step in self.state["completedSteps"] or step in active, "Retry must name a preserved failed or unqualified attempt")
        provider, name = step.split("/", 1)
        if name.startswith("long-"):
            require(provider + "/" + name.removeprefix("long-") not in self.state["qualifiedLongSegments"], "A qualifying long segment cannot be replaced by a preferred result")
        if step in self.state["completedSteps"]:
            prior = self.state["completedSteps"][step]
            observation = verify_observation(self.output, prior)
            require(not observation["sloPassed"] or observation.get("resourceQualityPassed") is False or name.startswith("long-"),
                    "A successful calibration observation cannot be replaced by a preferred sample")
        else:
            prior = active[step]
            directory = self.output / prior["attempt"]
            command_path = directory / "client.command.json"
            require(command_path.exists(), "Interrupted attempt lacks a joined client receipt; preserve it and investigate owners before another campaign")
            command = json.loads(command_path.read_text())
            require(command.get("exitCode") is not None, "Interrupted client owner has no proven terminal state")
            identity = command.get("clientIdentity")
            if identity:
                live = process(identity["pid"])
                require(live is None or live["startTicks"] != identity["startTicks"], "Interrupted client owner is still alive")
            if provider == "postgresql":
                owner_path = directory / "postgres-owner.json"
                require(owner_path.exists(), "Interrupted PostgreSQL owner has no retirement receipt")
                owner = json.loads(owner_path.read_text())
                server_owner_accounting(owner)
            prior = dict(prior, evidenceFiles={str(path.relative_to(self.output)): sha(path) for path in directory.iterdir()
                if path.is_file() and not path.name.startswith("database.sqlite")})
        retries = self.state.setdefault("reviewedRetries", [])
        require(sum(item["step"] == step for item in retries) + 1 < self.settings["maximumAttemptsPerStep"], "Frozen maximum attempts exhausted")
        retries.append({"step": step, "reason": reason, "utc": utc(), "preservedCheckpoint": prior})
        self.state["completedSteps"].pop(step, None)
        active.pop(step, None)
        self.persist()

    def persist(self):
        self.state["updatedUtc"] = utc()
        write_json(self.state_path, self.state)

    def executable(self, provider, route):
        return self.bundle / self.bundle_manifest["applications"][provider + "/" + route]

    def dbplan(self, provider, directory):
        return {"provider": provider, "connection": str(directory / "database.sqlite"), "commands": [],
                "postgres": {"bin": str(self.bundle / self.bundle_manifest["postgresBin"]), "data": str(directory / "pgdata"),
                    "cpus": ",".join(map(str, self.request["admission"]["serverCpus"])),
                    "cpuAccounting": self.request["admission"]["postgresCpuAccounting"],
                    "maxConnections": max(32, max(self.settings["capacityConcurrency"]) + 8),
                    "maxClientConcurrency": max(self.settings["capacityConcurrency"])}}

    def verify_server_accounting(self, directory):
        path = directory / "postgres-owner.json"
        settings = server_owner_accounting(json.loads(path.read_text()))
        if "postgresCpuAccountingSettings" in self.state:
            require(settings == self.state["postgresCpuAccountingSettings"],
                    "PostgreSQL task-group scheduling/ancestor constraints changed across comparison cells")
        else:
            self.state["postgresCpuAccountingSettings"] = settings
            self.persist()
        return sha(path)

    def seed_path(self, provider, workload="primary"):
        return self.output / "seeds" / provider if workload == "primary" else self.output / "seeds" / workload / provider

    def initialize(self, provider, workload="primary"):
        context = self.workloads[workload]
        target = self.seed_path(provider, workload)
        receipt = target / "seed.json"
        if receipt.exists():
            previous = json.loads(receipt.read_text())
            require(previous["workloadSha256"] == context["sha256"], "Cached seed belongs to a different workload")
            for name, digest in previous["files"].items():
                require(sha(target / name) == digest, "Frozen seed was changed")
            if provider == "postgresql":
                require(self.verify_server_accounting(target) == previous.get("serverCpuOwnerSha256"),
                        "Cached PostgreSQL seed owner/accounting evidence changed")
            return
        require(not target.exists(), "Interrupted seed initialization must be retained and investigated before retry")
        target.mkdir(parents=True)
        plan = self.dbplan(provider, target)
        data = context["manifest"]["dataset"]
        item = {"name": "initialize", "phase": "dataset-initialization-excluded", "timeoutSeconds": 300,
                "cpus": ",".join(map(str, self.request["admission"]["clientCpus"])),
                "argv": [str(self.executable(provider, "korda_before")), "--connection", "{connection}", "--initialize",
                         "--tenants", str(data["tenants"]), "--orders-per-tenant", str(data["ordersPerTenant"])]}
        with database(plan, target) as (connection, server):
            execute(item, connection, server, target, 1, server_cpu_group=plan["postgres"].get("activeCpuGroup"))
        cpu_owner = self.verify_server_accounting(target) if provider == "postgresql" else None
        files = {str(p.relative_to(target)): sha(p) for p in target.rglob("*") if p.is_file() and
                 (p.is_relative_to(target / "pgdata") or p.name.startswith("database.sqlite"))}
        require(bool(files), "Dataset initialization produced no snapshot")
        write_json(receipt, {"task": "DB-Perf-01", "workloadId": workload, "workloadSha256": context["sha256"],
                             "files": files, "serverCpuOwnerSha256": cpu_owner})

    def cell_context(self, workload, construction_mode=None, case_ids=None):
        context = self.workloads[workload]
        construction_mode = construction_mode or self.settings["constructionMode"]
        require(construction_mode in ("prepared", "dynamic"), "Unknown cell construction mode")
        selected = context["cases"] if case_ids is None else case_ids
        require(isinstance(selected, list) and selected and len(selected) == len(set(selected))
                and set(selected) <= set(context["cases"]), "Cell case subset differs from its frozen workload")
        return dict(context, cases=list(selected)), construction_mode

    def native_contract(self, construction_mode, explicit=None):
        contract = explicit or (self.request["nativeBaseline"] if construction_mode == self.settings["constructionMode"] else {
            "physicalPrepare": "reuse" if construction_mode == "prepared" else "per-operation", "protocolConfirmation": "drain-only"})
        require(construction_mode != "dynamic" or contract["physicalPrepare"] == "per-operation",
                "Dynamic native construction requires per-operation preparation")
        return contract

    def argv(self, provider, route, seconds, concurrency, rate, output, native_contract=None, *, workload="primary", construction_mode=None, case_ids=None):
        context, construction_mode = self.cell_context(workload, construction_mode, case_ids)
        argv = [str(self.executable(provider, route)), "--connection", "{connection}", "--case", "mixed", "--cases", ",".join(context["cases"]),
                "--mode", construction_mode, "--session", "persistent", "--concurrency", str(concurrency),
                "--warmup", str(context.get("warmupOperations", self.settings["warmupOperations"])), "--seconds", str(seconds), "--idle-seconds", str(self.settings["idleSeconds"]),
                "--output", str(output)]
        if route == "direct_cpp":
            native_contract = self.native_contract(construction_mode, native_contract)
            argv += ["--provider", provider, "--manifest", str(context["path"]),
                     "--physical-prepare", native_contract["physicalPrepare"], "--protocol-confirmation", native_contract["protocolConfirmation"]]
        else:
            data = context["manifest"]["dataset"]
            argv += ["--rows", str(data.get("requestedRows", 32)), "--batch-rows", str(data.get("batchRows", 32)),
                     "--tenants", str(data["tenants"]), "--orders-per-tenant", str(data["ordersPerTenant"])]
        substitutions = {"{manifest}": str(context["path"]), "{sqliteLibrary}": str(self.bundle / self.bundle_manifest["sqliteLibrary"]), "{provider}": provider}
        arguments = self.request.get("extraArguments", {}).get(route, []) + self.request.get("providerExtraArguments", {}).get(provider, {}).get(route, [])
        for argument in arguments:
            for old, new in substitutions.items():
                argument = argument.replace(old, new)
            argv.append(argument)
        if rate is not None:
            argv += ["--arrival-rate", str(rate), "--queue-capacity", str(self.settings["queuePerWorker"] * concurrency)]
        return argv

    def run_cell(self, provider, route, name, seconds, concurrency, rate=None, *, require_coverage=False, native_contract=None, workload="primary", diagnostic=None, construction_mode=None, case_ids=None):
        context, construction_mode = self.cell_context(workload, construction_mode, case_ids)
        key = provider + "/" + name
        native_contract = self.native_contract(construction_mode, native_contract) if route == "direct_cpp" else None
        if key in self.state["completedSteps"]:
            stored = self.state["completedSteps"][key]
            observed = verify_observation(self.output, stored)
            require(all(observed[field] == value for field, value in (("provider", provider), ("route", route),
                    ("name", name), ("seconds", seconds), ("concurrency", concurrency), ("rate", rate),
                    ("nativeContract", native_contract), ("workloadId", workload), ("workloadSha256", context["sha256"]),
                    ("workloadCaseIds", context["cases"]), ("diagnostic", diagnostic), ("constructionMode", construction_mode))), "Resumed cell arguments differ")
            require(not require_coverage or observed["measurement"]["fullCaseCoverage"], "Cached cell did not execute the complete workload")
            if provider == "postgresql":
                require(self.verify_server_accounting((self.output / observed["rawPath"]).parent) == observed.get("serverCpuOwnerSha256"),
                        "Cached PostgreSQL owner/accounting evidence changed")
            if diagnostic is None:
                quality = resource_quality(observed["resources"], provider, self.request["admission"])
                require(observed["resourceQualityPassed"] == quality["passed"]
                        and observed["resourceQualityReasons"] == quality["reasons"], "Cached resource-quality flags differ from raw resource summaries")
                require(quality["passed"], "Cached timing cell failed environment quality; preserve it and use an explicit reviewed retry")
            return observed
        require(key not in self.state.setdefault("activeSteps", {}),
                "A previous attempt is unfinished or failed; preserve it and use an explicit reviewed --retry-step/--retry-reason")
        require(time.monotonic() + seconds + 180 < self.deadline, "Measurement VM time budget is exhausted; retain completed checkpoints")
        parent = self.output / "runs" / provider / name
        parent.mkdir(parents=True, exist_ok=True)
        attempt = parent / f"attempt-{len(list(parent.glob('attempt-*'))):02d}"
        attempt.mkdir()
        self.state["activeSteps"][key] = {"attempt": str(attempt.relative_to(self.output)), "startedUtc": utc()}
        self.persist()
        seed = self.seed_path(provider, workload)
        if provider == "postgresql":
            shutil.copytree(seed / "pgdata", attempt / "pgdata")
        else:
            for path in seed.glob("database.sqlite*"):
                shutil.copy2(path, attempt / path.name)
        plan = self.dbplan(provider, attempt)
        result_path = attempt / "result.json"
        item = {"name": "client", "phase": name, "timeoutSeconds": seconds + 150,
                "cpus": ",".join(map(str, self.request["admission"]["clientCpus"])),
                "argv": self.argv(provider, route, seconds, concurrency, rate, result_path, native_contract,
                                  workload=workload, construction_mode=construction_mode, case_ids=context["cases"])}
        if diagnostic == "same-process-recovery":
            settings = self.request["diagnostics"]["sameProcessRecovery"]
            item["argv"] += ["--recovery-checks", "--recovery-rate", str(self.state["fixedRates"][provider]),
                "--recovery-seconds", str(settings["seconds"]), "--overload-rate", str(settings["overloadRate"]),
                "--overload-seconds", str(settings["overloadSeconds"]), "--queue-capacity", str(self.settings["queuePerWorker"] * concurrency)]
        elif diagnostic == "native-allocation":
            settings = self.request["diagnostics"]["nativeAllocation"]
            require(route in settings["routes"], "Allocator profiling route is not frozen in the request")
            library = self.bundle / "native/libdb-perf-alloc.so"
            require(library.is_file(), "Prepared native allocation profiler is missing")
            item["argv"] = ["env", "LD_PRELOAD=" + str(library), "DB_PERF_ALLOC_PROFILE_LIBRARY=" + str(library),
                            "DB_PERF_ALLOC_CAPACITY=" + str(settings["tableCapacity"]),
                            *item["argv"], "--allocation-profile"]
        elif diagnostic == "query-plan":
            settings = self.request["diagnostics"]["queryPlans"]
            require(provider in PROVIDERS and route in ROUTES and concurrency == 1 and rate is None
                    and workload == "primary" and construction_mode == settings["constructionMode"]
                    and context["cases"] == self.cases and seconds == 0,
                    "Query plans require the complete frozen C1 workload outside timing lanes")
            item["timeoutSeconds"] = settings["maximumCommandSeconds"]
            item["argv"] += ["--warmup", str(settings["warmupOperations"]), "--operations", str(settings["operations"]),
                             "--idle-seconds", "0"]
            if route == "direct_cpp":
                item["argv"] += ["--explain"]
            if provider == "postgresql":
                library = self.bundle / "auto-explain/auto_explain.so"
                require(library.is_file(), "Frozen auto_explain diagnostic library is absent")
                plan["postgres"]["queryPlans"] = {"library": str(library), "sha256": sha(library)}
        else:
            require(diagnostic is None, "Unknown diagnostic lane")
        plan["commands"] = [item]
        write_json(attempt / "plan.json", plan)
        write_json(attempt / "runner.json", {"requestSha256": sha(self.bundle / "request.json"),
            "bundleManifestSha256": sha(self.bundle / "bundle.json"), "environmentIdentity": self.identity,
            "controllerSources": {name: sha(self.bundle / "harness" / name) for name in ("run.py", "campaign.py", "campaign_build.py", "plan_diagnostics.py")}})
        with database(plan, attempt) as (connection, server):
            command = execute(item, connection, server, attempt, self.settings["sampleSeconds"], check=False,
                              server_cpu_group=plan["postgres"].get("activeCpuGroup"))
        cpu_owner = self.verify_server_accounting(attempt) if provider == "postgresql" else None
        require(result_path.exists() and command["exitCode"] in (0, 1) and not command["observerErrors"]
                and not command["timedOut"], "Measured command failed; preserved attempt cannot earn credit")
        raw = json.loads(result_path.read_text())
        require(get(raw, "executableSha256") == sha(self.executable(provider, route)), "Executed application differs from frozen bundle")
        validate_provider_identity(raw, provider, route)
        require(get(raw, "mode") == construction_mode
                and get(raw, "concurrency") == concurrency and get(raw, "emptyDriver", default=False if diagnostic == "same-process-recovery" else None) is False,
                "Executed provider/construction/concurrency/driver contract differs")
        if route.startswith("korda_"):
            expected = self.request[route.removeprefix("korda_")]["providers"][provider]
            require(get(raw, "loadedNativeSha256") == expected["nativeSha256"] and get(raw, "packageVersion") == expected["version"]
                    and get(raw, "nativeAot") is True, "Loaded provider/package/AOT differs from frozen input")
        elif route == "direct_cpp":
            native_measurements = [get(phase, "measurement") for phase in get(raw, "phases", default=[])] if diagnostic == "same-process-recovery" else [raw]
            require(native_measurements and all(get(measurement, "physicalPreparation") == native_contract["physicalPrepare"]
                    and get(measurement, "protocolConfirmation") == native_contract["protocolConfirmation"]
                    and get(measurement, "accountingReconciles") is True for measurement in native_measurements),
                    "Native baseline/layered preparation or accounting contract differs")
            require(get(raw, "inputImmutable") is True
                    and get(raw, "manifestSha256") == get(raw, "manifestSha256After") == context["sha256"],
                    "Native comparator rejected input immutability, workload or operation accounting")
            require(get(raw, "loadedSqliteSha256") == sha(self.bundle / self.bundle_manifest["sqliteLibrary"])
                    and get(raw, "buildReceiptSha256") == sha(self.bundle / "native/build-receipt.json"), "Native comparator loaded unexpected build/SQLite inputs")
        else:
            receipt = json.loads((self.executable(provider, route).parents[1] / "build-receipt.json").read_text())
            require(receipt["workloadManifestSha256"] == self.request["workload"]["sha256"] and get(raw, "nativeAot") is True,
                    "Independent ORM is not the prepared AOT workload")
            if provider == "sqlite":
                require(get(raw, "loadedNativeSha256") == sha(self.bundle / self.bundle_manifest["sqliteLibrary"]),
                        "Independent ORM did not load the exact shared SQLite engine")
        if diagnostic == "same-process-recovery":
            measurement = normalized_recovery(raw, context["cases"], self.request["diagnostics"]["sameProcessRecovery"], self.state["fixedRates"][provider])
            resources = {"phases": [{"name": phase["name"], "resources": summarize_resources(attempt / "client.resources.jsonl", phase["measurement"])}
                                     for phase in measurement["phases"]]}
            slo = recovery_meets_slo(measurement, self.settings)
        else:
            measurement = normalized_result(raw, context["cases"], seconds, rate, require_coverage=require_coverage)
            resources = ({"scope": "Instrumented bounded query-plan diagnostic; raw process samples are retained without timing/CPU/memory comparison credit"}
                         if diagnostic == "query-plan" else summarize_resources(attempt / "client.resources.jsonl", measurement))
            slo = meets_slo(measurement, self.settings)
        if diagnostic == "native-allocation":
            profile = get(raw, "nativeAllocationProfile")
            validate_native_profile(profile, sha(self.bundle / "native/libdb-perf-alloc.so"), self.request["diagnostics"]["nativeAllocation"]["tableCapacity"])
            measurement["nativeAllocationProfile"] = profile
            slo = measurement["counts"]["failed"] == 0
        elif diagnostic == "query-plan":
            require(measurement["counts"]["succeeded"] == self.request["diagnostics"]["queryPlans"]["operations"]
                    and measurement["counts"]["failed"] == 0, "Query-plan diagnostic did not execute exactly one complete successful business cycle")
            evidence = (collect_postgres(attempt / "postgres-query-plans.json", context["manifest"], route, raw)
                        if provider == "postgresql" else replay_sqlite(self.bundle / self.bundle_manifest["sqliteLibrary"],
                            attempt / "database.sqlite", context["manifest"], route, raw))
            write_json(attempt / "query-plan-evidence.json", evidence)
            measurement["queryPlanEvidence"] = {"path": str((attempt / "query-plan-evidence.json").relative_to(self.output)),
                                              "sha256": sha(attempt / "query-plan-evidence.json"), "passed": evidence["passed"]}
            slo = evidence["passed"]
        elif diagnostic is None:
            validate_uninstrumented_timing(raw)
        if workload != "primary" and measurement["counts"]["failed"] == 0:
            require(measurement["counts"]["rows"] == measurement["counts"]["succeeded"] * context["manifest"]["queries"][0]["expectedRows"],
                    "Heavy lane did not consume every expected row of every successful operation")
        if case_ids is not None and measurement["counts"]["failed"] == 0:
            validate_subset_consumption(raw, context["manifest"], context["cases"])
        require(command["exitCode"] == 0 or measurement["counts"]["failed"] > 0,
                "A nonzero application exit without counted business failures cannot qualify")
        if measurement["counts"]["failed"] == 0:
            # These are our disposable clones of the preserved immutable seed.
            # Retirement follows consumer drain + PostgreSQL owner join; failed
            # database attempts remain intact for investigation.
            roots = [attempt / "pgdata"] if provider == "postgresql" else list(attempt.glob("database.sqlite*"))
            files = [path for root in roots for path in (root.rglob("*") if root.is_dir() else [root]) if path.is_file()]
            retirement = {"schemaVersion": 1, "retiredUtc": utc(), "rawResultSha256": sha(result_path),
                "seedReceiptSha256": sha(seed / "seed.json"), "ownerJoined": True,
                "reason": "Successful workload fully validated every consumed result and restored/rolled back each write; retire reproducible private database clones outside measurement boundaries",
                "files": {str(path.relative_to(attempt)): {"sha256": sha(path), "bytes": path.stat().st_size} for path in files}}
            write_json(attempt / "database-retirement.json", retirement)
            for root in roots:
                require(root.parent == attempt, "Only an owned immediate database clone may be retired")
                if root.is_dir():
                    shutil.rmtree(root)
                else:
                    root.unlink()
        quality = resource_quality(resources, provider, self.request["admission"]) if diagnostic is None else None
        observed = {"provider": provider, "route": route, "name": name, "seconds": seconds, "concurrency": concurrency,
                    "constructionMode": construction_mode,
                    "workloadId": workload, "workloadSha256": context["sha256"], "workloadCaseIds": context["cases"],
                    "diagnostic": diagnostic, "timingComparisonEligible": diagnostic is None,
                    "resourceQualityPassed": quality["passed"] if quality is not None else None,
                    "resourceQualityReasons": quality["reasons"] if quality is not None else [],
                    "seedReceiptSha256": sha(seed / "seed.json"),
                    "serverCpuOwnerSha256": cpu_owner,
                    "rate": rate, "nativeContract": native_contract, "measurement": measurement, "resources": resources, "rawPath": str(result_path.relative_to(self.output)),
                    "rawSha256": sha(result_path), "commandSha256": sha(attempt / "client.command.json"),
                    "sampleSha256": sha(attempt / "client.resources.jsonl"), "sloPassed": slo,
                    "longRunAcceptance": False, "wholeBatchAccepted": False}
        observed["evidenceFiles"] = {str(path.relative_to(self.output)): sha(path) for path in attempt.iterdir()
            if path.is_file() and not path.name.startswith("database.sqlite")}
        write_json(attempt / "normalized.json", observed)
        self.state["completedSteps"][key] = {"path": str((attempt / "normalized.json").relative_to(self.output)), "sha256": sha(attempt / "normalized.json")}
        del self.state["activeSteps"][key]
        self.persist()
        require(quality is None or quality["passed"], "Timing cell failed environment quality; raw/normalized evidence is saved before any explicit reviewed retry")
        return observed

    def run(self, short_only=False, next_segment=False):
        environment_path = self.output / "environment.json"
        if environment_path.exists():
            environment = json.loads(environment_path.read_text())
            if "environmentAdmissionSha256" in self.state:
                require(sha(environment_path) == self.state["environmentAdmissionSha256"], "Admitted environment receipt changed")
            validate_admission(environment, self.request, self.identity)
            os.sched_setaffinity(0, self.request["admission"]["observerCpus"])
        else:
            environment = admission(self.request, self.output)
        dependencies_path = self.output / "runtime-dependencies.json"
        if dependencies_path.exists():
            require(sha(dependencies_path) == self.state.get("runtimeDependenciesSha256"), "Runtime dependency receipt changed")
            dependencies = json.loads(dependencies_path.read_text())
            for path, expected in dependencies["libraries"].items():
                require(sha(path) == expected["sha256"], "Measurement VM runtime library changed")
        else:
            write_json(dependencies_path, runtime_dependencies(self.bundle, self.bundle_manifest))
            self.state["runtimeDependenciesSha256"] = sha(dependencies_path)
            self.persist()
        for provider in PROVIDERS:
            self.initialize(provider)
            if "queryPlans" in self.request.get("diagnostics", {}):
                for route in ROUTES:
                    observed = self.run_cell(provider, route, "query-plans-" + route, 0, 1,
                        require_coverage=True, diagnostic="query-plan",
                        construction_mode=self.request["diagnostics"]["queryPlans"]["constructionMode"])
                    require(observed["sloPassed"], "Query-plan SQL/parameter/leg evidence remains incomplete; diagnostic results cannot earn timing credit")
                self.state.setdefault("queryPlans", {})[provider] = {"routes": list(ROUTES),
                    "scope": "PostgreSQL actual executor plans and logged parameters; SQLite source-bound exact-engine EXPLAIN QUERY PLAN replay; no instrumented performance credit"}
                self.persist()
            if self.request.get("shortQueries") is not None:
                short = self.request["shortQueries"]
                for mode in short["constructionModes"]:
                    for route in ROUTES:
                        observed = self.run_cell(provider, route, f"short-projections-{mode}-{route}", short["seconds"],
                            short["concurrency"], require_coverage=True, construction_mode=mode, case_ids=short["cases"])
                        require(observed["measurement"]["counts"]["succeeded"] > 0
                                and observed["measurement"]["counts"]["failed"] == 0, "Short projection lane has business failures")
                self.state.setdefault("shortQueries", {})[provider] = {"cases": short["cases"],
                    "constructionModes": short["constructionModes"], "secondsPerCell": short["seconds"],
                    "concurrency": short["concurrency"], "cells": 8,
                    "scope": "five fully consumed short projections; separate dynamic/prepared closed-loop comparisons on the same frozen fixture; no long credit"}
                self.persist()
            # Repeats are independent processes, interleaved with other routes;
            # no local before diagnostics are rebound to this remote VM.
            before_rates = []
            for repeat in range(self.settings["noiseReplicates"]):
                order = list(ROUTES) if repeat % 2 == 0 else list(reversed(ROUTES))
                for route in order:
                    observed = self.run_cell(provider, route, f"noise-{repeat}-{route}",
                        self.settings["calibrationSeconds"], 1)
                    if route == "korda_before":
                        before_rates.append(observed["measurement"]["successOpsPerSecond"])
            coefficient = statistics.stdev(before_rates) / statistics.mean(before_rates)
            noise = {"beforeSuccessOpsPerSecond": before_rates, "coefficientOfVariation": coefficient,
                     "allowedCoefficientOfVariation": self.request["admission"]["maximumBeforeCv"]}
            self.state["calibration"][provider] = {"noise": noise}
            self.persist()
            require(coefficient <= self.request["admission"]["maximumBeforeCv"], "Before-process noise exceeded the frozen admission limit")
            layers = []
            for index, contract in enumerate(self.request["nativeLayers"]):
                layers.append(self.run_cell(provider, "direct_cpp", "native-layer-" + str(index),
                    self.settings["calibrationSeconds"], self.settings["concurrency"], native_contract=contract))
            self.state["calibration"][provider]["nativeLayers"] = [{"nativeContract": point["nativeContract"],
                "measurement": point["measurement"], "scope": "separate attribution layer, not the primary native column"} for point in layers]
            capacities = {}
            for route in ROUTES:
                points = []
                for concurrency in self.settings["capacityConcurrency"]:
                    observed = self.run_cell(provider, route, f"capacity-{route}-{concurrency}",
                        self.settings["capacitySeconds"], concurrency)
                    points.append(observed)
                valid = [point for point in points if point["sloPassed"]]
                require(bool(valid), "No capacity point met common error/p99 limits for " + provider + "/" + route)
                capacities[route] = {"points": [{"concurrency": p["concurrency"], "measurement": p["measurement"],
                    "sloPassed": p["sloPassed"]} for p in points],
                    "maximumObservedSuccessfulOpsPerSecondUnderServiceSlo": max(p["measurement"]["successOpsPerSecond"] for p in valid),
                    "status": "closed-loop service-latency precalibration only; this is not scheduled-latency maximum throughput"}
            scheduled = {}
            for route in ROUTES:
                candidates = capacities[route]["points"]
                best = max((point for point in candidates if point["sloPassed"]), key=lambda p: p["measurement"]["successOpsPerSecond"])
                concurrencies = sorted({self.settings["concurrency"], best["concurrency"]})
                points, refinements, brackets = [], [], []
                for concurrency in concurrencies:
                    capacity = next(point["measurement"]["successOpsPerSecond"] for point in candidates if point["concurrency"] == concurrency)
                    for index, fraction in enumerate(self.settings["scheduledRateFractions"]):
                        rate = math.floor(capacity * fraction * 1000) / 1000
                        require(rate > 0, "Scheduled ladder has no positive offered load")
                        points.append(self.run_cell(provider, route, f"scheduled-capacity-{route}-{concurrency}-{index}",
                            self.settings["scheduledCapacitySeconds"], concurrency, rate))
                    for iteration in range(self.settings["capacityRefinementSteps"]):
                        selection = capacity_refinement(points, concurrency, self.settings["capacityRefinementMaximumRelativeGap"])
                        if not selection["execute"]:
                            break
                        observed = self.run_cell(provider, route, f"scheduled-refine-{route}-{concurrency}-{iteration}",
                            self.settings["capacityRefinementSeconds"], concurrency, selection["proposedRate"])
                        points.append(observed)
                        refinements.append({"iteration": iteration, "selection": selection,
                            "result": {key: observed[key] for key in ("name", "rate", "sloPassed", "rawPath", "rawSha256")}})
                    bracket = capacity_refinement(points, concurrency, self.settings["capacityRefinementMaximumRelativeGap"])
                    bracket["iterationLimitReached"] = bracket["execute"]
                    bracket["execute"] = False
                    brackets.append(bracket)
                valid = [point for point in points if point["sloPassed"]]
                at_frozen_concurrency = [point for point in valid if point["concurrency"] == self.settings["concurrency"]]
                require(at_frozen_concurrency, "No scheduled-rate point met the common SLO at frozen long concurrency: " + provider + "/" + route)
                scheduled[route] = {"points": [{"concurrency": p["concurrency"], "rate": p["rate"], "measurement": p["measurement"],
                    "sloPassed": p["sloPassed"]} for p in points],
                    "maximumObservedSuccessfulOpsPerSecondUnderScheduledSlo": max(p["measurement"]["successOpsPerSecond"] for p in valid),
                    "maximumQualifiedOfferedRateAtFrozenConcurrency": max(p["rate"] for p in at_frozen_concurrency),
                    "refinementSelections": refinements, "observedBrackets": brackets,
                    "refinementWindowSeconds": self.settings["capacityRefinementSeconds"],
                    "status": "best tested scheduled-rate result with retained finite grid and bounded halving/midpoint observations; short windows and observed brackets do not prove a global maximum"}
            # Freeze the common offered rate without using the optimized route to
            # lower its comparison load after seeing a regression.
            rate = math.floor(min(scheduled[route]["maximumQualifiedOfferedRateAtFrozenConcurrency"]
                for route in ("korda_before", "direct_cpp", "independent_csharp_orm")) * self.settings["sustainableFraction"] * 1000) / 1000
            require(rate > 0, "No positive sustainable common offered load")
            if provider in self.state["fixedRates"]:
                require(self.state["fixedRates"][provider] == rate, "A resumed campaign cannot silently change its frozen offered rate")
            self.state["fixedRates"][provider] = rate
            self.state["calibration"][provider]["capacities"] = capacities
            self.state["calibration"][provider]["scheduledCapacities"] = scheduled
            self.persist()
            for route in ROUTES:
                observed = self.run_cell(provider, route, "fixed-load-" + route, self.settings["fixedValidationSeconds"],
                    self.settings["concurrency"], rate)
                require(observed["sloPassed"], "Frozen offered load failed common SLO: " + provider + "/" + route)
            self.state["calibration"][provider]["recoveryScope"] = "capacity observes saturation; recovery uses the explicit same-process retained source/session/worker diagnostic; redundant fresh-process recovery is not scheduled"
            diagnostics = self.request.get("diagnostics", {})
            if "sameProcessRecovery" in diagnostics:
                settings = diagnostics["sameProcessRecovery"]
                for route in ROUTES:
                    observed = self.run_cell(provider, route, "same-process-recovery-" + route,
                        settings["seconds"] * 2 + settings["overloadSeconds"], self.settings["concurrency"], diagnostic="same-process-recovery")
                    require(observed["sloPassed"], "Same-process recovery failed accounting, owners, observed saturation, business errors or the reference/recovery tail limit")
                self.state["calibration"][provider]["sameProcessRecovery"] = "all four routes passed three phases on the same retained source/connections/workers; final tracked owners drained"
            if "nativeAllocation" in diagnostics:
                settings = diagnostics["nativeAllocation"]
                for concurrency in settings["concurrency"]:
                    for route in settings["routes"]:
                        observed = self.run_cell(provider, route, f"native-allocation-{concurrency}-{route}",
                            settings["seconds"], concurrency, diagnostic="native-allocation", require_coverage=True)
                        require(observed["sloPassed"], "Separate native allocation profile failed; timing runs cannot fill this obligation")
        if self.request.get("heavyWorkload") is not None:
            heavy = self.request["heavyWorkload"]
            for provider in PROVIDERS:
                self.initialize(provider, heavy["id"])
                for concurrency in heavy["concurrency"]:
                    for route in ROUTES:
                        observed = self.run_cell(provider, route, f"heavy-{heavy['id']}-{concurrency}-{route}",
                            heavy["seconds"], concurrency, workload=heavy["id"], require_coverage=True)
                        require(observed["measurement"]["counts"]["succeeded"] > 0
                                and observed["measurement"]["counts"]["failed"] == 0, "Heavy lane has business failures")
            self.state["heavyWorkload"] = {"id": heavy["id"], "sha256": heavy["sha256"], "secondsPerCell": heavy["seconds"],
                "concurrency": heavy["concurrency"], "cells": 16, "scope": "separate fully consumed 8192-row wide stream; closed-loop service latency; no long-segment credit"}
            self.persist()
        if not environment["qualified"]:
            environment.update(qualified=True, pending=[], qualificationUtc=utc(),
                qualificationScope="one dedicated-to-task hosted VM; guest CPU quotas/affinities and observed before noise/steal")
            write_json(environment_path, environment)
        self.state["environmentAdmissionSha256"] = sha(environment_path)
        self.persist()
        if short_only:
            self.state["state"] = "short_campaign_complete_long_pending"
            self.persist()
            return
        # Read-only projection runs cannot satisfy the requested mixed-write
        # long campaign. This invariant cannot be waived by a duration counter.
        write_cases = self.request["workload"]["mixedWriteCaseIds"]
        require(write_cases and set(write_cases) <= set(self.cases), "The frozen long workload still lacks mixed read/write cases")
        declared_writes = {query["name"] for query in self.workload["queries"] if query.get("writesDatabase") is True}
        require(set(write_cases) <= declared_writes, "A read-only manifest case cannot be relabeled as a mixed-write operation")
        for cell in self.settings["order"]:
            provider, route = cell.split("/")
            already_qualified = cell in self.state["qualifiedLongSegments"]
            observed = self.run_cell(provider, route, "long-" + route, self.settings["secondsPerSegment"],
                self.settings["concurrency"], self.state["fixedRates"][provider], require_coverage=True)
            if cell not in self.state["observedLongSegments"]:
                self.state["observedLongSegments"].append(cell)
                self.persist()
            require(observed["sloPassed"], "A long segment failed the common success/error/tail limits")
            require(observed["resources"]["hostCpu"]["stealFraction"] <= self.request["admission"]["maximumStealFraction"],
                    "Long-segment VM steal exceeded the frozen threshold")
            require(observed["resources"]["maximumIntervalStealFraction"] <= self.request["admission"].get("maximumIntervalStealFraction", 0.05)
                    and observed["resources"]["quotaStableAndAvailable"] and observed["resources"]["guestThrottleEvents"] == 0,
                    "Long-segment guest quota/throttling or interval steal was unstable")
            require(observed["resources"]["client"]["counterMonotonic"], "Client resource accounting is uncertain")
            if provider == "postgresql":
                require(observed["resources"]["server"]["counterMonotonic"], "Server resource accounting is uncertain")
            if cell not in self.state["qualifiedLongSegments"]:
                self.state["qualifiedLongSegments"].append(cell)
            self.persist()
            if next_segment and not already_qualified and len(self.state["qualifiedLongSegments"]) < 8:
                self.state["state"] = "long_campaign_in_progress"
                self.persist()
                return
        require(len(self.state["qualifiedLongSegments"]) == 8, "Long-campaign cells are incomplete")
        self.state["state"] = "long_campaign_observed_batch_acceptance_separate"
        self.state["finishedUtc"] = utc()
        self.state["wholeBatchAccepted"] = False
        self.persist()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="action", required=True)
    validate = sub.add_parser("validate-request")
    validate.add_argument("request", type=Path)
    validate.add_argument("--allow-disabled", action="store_true")
    build = sub.add_parser("prepare")
    build.add_argument("--request", type=Path, required=True)
    build.add_argument("--output", type=Path, required=True)
    build.add_argument("--repo", type=Path, required=True)
    measure = sub.add_parser("measure")
    measure.add_argument("--bundle", type=Path, required=True)
    measure.add_argument("--output", type=Path, required=True)
    measure.add_argument("--resume", action="store_true")
    measure.add_argument("--short-only", action="store_true")
    measure.add_argument("--next-segment", action="store_true")
    measure.add_argument("--retry-step", help="Explicitly retry one preserved, unqualified provider/step on the same live VM")
    measure.add_argument("--retry-reason", help="Retained reason for investigating and rerunning the failed observation")
    args = parser.parse_args()
    if args.action == "validate-request":
        record = request_record(args.request, enabled=not args.allow_disabled)
        print(json.dumps({"task": record["task"], "enabled": record["enabled"], "measurementExecuted": False,
                          "durationBudget": duration_budget(record) if "timeBudget" in record else None}))
    elif args.action == "prepare":
        prepare(args.request, args.output, args.repo)
    else:
        def interrupted(_signal, _frame):
            raise KeyboardInterrupt("Hosted campaign interrupted; preserving current attempt")
        signal.signal(signal.SIGTERM, interrupted)
        campaign = Campaign(args.bundle, args.output, args.resume)
        try:
            if args.retry_step:
                require(args.resume, "Explicit retries require matching same-VM resume")
                campaign.retry(args.retry_step, args.retry_reason)
            else:
                require(not args.retry_reason, "Retry reason needs a selected step")
            campaign.run(args.short_only, args.next_segment)
        except BaseException as error:
            campaign.state["state"] = "interrupted_or_failed"
            campaign.state["lastError"] = {"type": type(error).__name__, "message": str(error), "utc": utc()}
            campaign.persist()
            raise


if __name__ == "__main__":
    main()
