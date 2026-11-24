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

from campaign_build import PROVIDERS, ROUTES, prepare, require, request_record
from run import cgroup_snapshot, database, execute, host_cpu, process, sample, sha, utc, write_json


def get(value, *keys, default=None):
    lowered = {str(k).replace("_", "").lower(): v for k, v in value.items()}
    for key in keys:
        key = key.replace("_", "").lower()
        if key in lowered:
            return lowered[key]
    return default


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


def normalized_result(raw, expected_cases, seconds, rate=None, *, require_coverage=True):
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
    owner_scope = "tracked native/framework owners" if get(raw, "ownersReturnedToZero") is not None else "application connections only; independent ORM internal owners are not asserted"
    require(get(raw, "ownersReturnedToZero", "applicationConnectionsReturnedToZero") is True, "Tracked owners did not drain")
    require(not get(raw, "timeSeriesTruncated", default=False), "Time series was truncated")
    require(get(raw, "accountingReconciles", default=True) is True, "Consumer rejected its own operation accounting")
    offered = get(raw, "offeredLoad", "offered")
    if rate is not None:
        require(isinstance(offered, dict), "An open-loop run requires its complete offered-load ledger")
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
    return {"counts": counts, "elapsedThroughDrainSeconds": duration, "offeredSeconds": seconds if rate else None,
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
    return result


def meets_slo(value, settings):
    return (value["p99MicrosecondsUpper"] is not None and value["errorAndRejectionRate"] <= settings["maximumErrorAndRejectionRate"]
            and value["p99MicrosecondsUpper"] <= settings["maximumP99Microseconds"])


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
        self.workload = json.loads((self.bundle / "workload.json").read_text())
        self.cases = self.request["workload"]["cases"]
        require(self.cases and len(self.cases) == len(set(self.cases)), "Freeze a nonempty unique case sequence")
        require(set(self.cases) <= {q["name"] for q in self.workload["queries"]}, "Selected case is absent from workload contract")
        require(sha(self.bundle / "workload.json") == self.request["workload"]["sha256"], "Workload bytes changed")
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
        self.deadline = self.state["campaignStartedMonotonicNs"] / 1e9 + 330 * 60

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
            require(not observation["sloPassed"] or name.startswith("long-"), "A successful calibration observation cannot be replaced by a preferred sample")
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
                require(owner.get("exitCode") == 0 and not owner.get("shutdownError"), "PostgreSQL owner did not retire cleanly")
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
                    "maxConnections": max(32, max(self.settings["capacityConcurrency"]) + 8),
                    "maxClientConcurrency": max(self.settings["capacityConcurrency"])}}

    def initialize(self, provider):
        target = self.output / "seeds" / provider
        receipt = target / "seed.json"
        if receipt.exists():
            for name, digest in json.loads(receipt.read_text())["files"].items():
                require(sha(target / name) == digest, "Frozen seed was changed")
            return
        require(not target.exists(), "Interrupted seed initialization must be retained and investigated before retry")
        target.mkdir(parents=True)
        plan = self.dbplan(provider, target)
        data = self.workload["dataset"]
        item = {"name": "initialize", "phase": "dataset-initialization-excluded", "timeoutSeconds": 300,
                "cpus": ",".join(map(str, self.request["admission"]["clientCpus"])),
                "argv": [str(self.executable(provider, "korda_before")), "--connection", "{connection}", "--initialize",
                         "--tenants", str(data["tenants"]), "--orders-per-tenant", str(data["ordersPerTenant"])]}
        with database(plan, target) as (connection, server):
            execute(item, connection, server, target, 1)
        files = {str(p.relative_to(target)): sha(p) for p in target.rglob("*") if p.is_file() and
                 (p.is_relative_to(target / "pgdata") or p.name.startswith("database.sqlite"))}
        require(bool(files), "Dataset initialization produced no snapshot")
        write_json(receipt, {"task": "DB-Perf-01", "workloadSha256": self.request["workload"]["sha256"], "files": files})

    def argv(self, provider, route, seconds, concurrency, rate, output, native_contract=None):
        argv = [str(self.executable(provider, route)), "--connection", "{connection}", "--case", "mixed", "--cases", ",".join(self.cases),
                "--mode", self.settings["constructionMode"], "--session", "persistent", "--concurrency", str(concurrency),
                "--warmup", str(self.settings["warmupOperations"]), "--seconds", str(seconds), "--idle-seconds", str(self.settings["idleSeconds"]),
                "--output", str(output)]
        if route == "direct_cpp":
            native_contract = native_contract or self.request["nativeBaseline"]
            argv += ["--provider", provider, "--manifest", str(self.bundle / "workload.json"),
                     "--physical-prepare", native_contract["physicalPrepare"], "--protocol-confirmation", native_contract["protocolConfirmation"]]
        else:
            data = self.workload["dataset"]
            argv += ["--rows", str(data.get("requestedRows", 32)), "--batch-rows", str(data.get("batchRows", 32)),
                     "--tenants", str(data["tenants"]), "--orders-per-tenant", str(data["ordersPerTenant"])]
        substitutions = {"{manifest}": str(self.bundle / "workload.json"), "{sqliteLibrary}": str(self.bundle / self.bundle_manifest["sqliteLibrary"]), "{provider}": provider}
        for argument in self.request.get("extraArguments", {}).get(route, []):
            for old, new in substitutions.items():
                argument = argument.replace(old, new)
            argv.append(argument)
        if rate is not None:
            argv += ["--arrival-rate", str(rate), "--queue-capacity", str(self.settings["queuePerWorker"] * concurrency)]
        return argv

    def run_cell(self, provider, route, name, seconds, concurrency, rate=None, *, require_coverage=False, native_contract=None):
        key = provider + "/" + name
        native_contract = (native_contract or self.request["nativeBaseline"]) if route == "direct_cpp" else None
        if key in self.state["completedSteps"]:
            stored = self.state["completedSteps"][key]
            observed = verify_observation(self.output, stored)
            require(all(observed[field] == value for field, value in (("provider", provider), ("route", route),
                    ("name", name), ("seconds", seconds), ("concurrency", concurrency), ("rate", rate),
                    ("nativeContract", native_contract))), "Resumed cell arguments differ")
            require(not require_coverage or observed["measurement"]["fullCaseCoverage"], "Cached cell did not execute the complete workload")
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
        seed = self.output / "seeds" / provider
        if provider == "postgresql":
            shutil.copytree(seed / "pgdata", attempt / "pgdata")
        else:
            for path in seed.glob("database.sqlite*"):
                shutil.copy2(path, attempt / path.name)
        plan = self.dbplan(provider, attempt)
        result_path = attempt / "result.json"
        item = {"name": "client", "phase": name, "timeoutSeconds": seconds + 150,
                "cpus": ",".join(map(str, self.request["admission"]["clientCpus"])),
                "argv": self.argv(provider, route, seconds, concurrency, rate, result_path, native_contract)}
        plan["commands"] = [item]
        write_json(attempt / "plan.json", plan)
        write_json(attempt / "runner.json", {"requestSha256": sha(self.bundle / "request.json"),
            "bundleManifestSha256": sha(self.bundle / "bundle.json"), "environmentIdentity": self.identity,
            "controllerSources": {name: sha(self.bundle / "harness" / name) for name in ("run.py", "campaign.py", "campaign_build.py")}})
        with database(plan, attempt) as (connection, server):
            command = execute(item, connection, server, attempt, self.settings["sampleSeconds"], check=False)
        require(result_path.exists() and command["exitCode"] in (0, 1) and not command["observerErrors"]
                and not command["timedOut"], "Measured command failed; preserved attempt cannot earn credit")
        raw = json.loads(result_path.read_text())
        require(get(raw, "executableSha256") == sha(self.executable(provider, route)), "Executed application differs from frozen bundle")
        require(get(raw, "provider") == provider and get(raw, "mode") == self.settings["constructionMode"]
                and get(raw, "concurrency") == concurrency and get(raw, "emptyDriver") is False,
                "Executed provider/construction/concurrency/driver contract differs")
        if route.startswith("korda_"):
            expected = self.request[route.removeprefix("korda_")]["providers"][provider]
            require(get(raw, "loadedNativeSha256") == expected["nativeSha256"] and get(raw, "packageVersion") == expected["version"]
                    and get(raw, "nativeAot") is True, "Loaded provider/package/AOT differs from frozen input")
        elif route == "direct_cpp":
            require(get(raw, "physicalPreparation") == native_contract["physicalPrepare"]
                    and get(raw, "protocolConfirmation") == native_contract["protocolConfirmation"], "Native baseline/layered preparation contract differs")
            require(get(raw, "inputImmutable") is True and get(raw, "accountingReconciles") is True
                    and get(raw, "manifestSha256") == get(raw, "manifestSha256After") == self.request["workload"]["sha256"],
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
        measurement = normalized_result(raw, self.cases, seconds, rate, require_coverage=require_coverage)
        require(command["exitCode"] == 0 or measurement["counts"]["failed"] > 0,
                "A nonzero application exit without counted business failures cannot qualify")
        resources = summarize_resources(attempt / "client.resources.jsonl", measurement)
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
        observed = {"provider": provider, "route": route, "name": name, "seconds": seconds, "concurrency": concurrency,
                    "rate": rate, "nativeContract": native_contract, "measurement": measurement, "resources": resources, "rawPath": str(result_path.relative_to(self.output)),
                    "rawSha256": sha(result_path), "commandSha256": sha(attempt / "client.command.json"),
                    "sampleSha256": sha(attempt / "client.resources.jsonl"), "sloPassed": meets_slo(measurement, self.settings),
                    "longRunAcceptance": False, "wholeBatchAccepted": False}
        observed["evidenceFiles"] = {str(path.relative_to(self.output)): sha(path) for path in attempt.iterdir()
            if path.is_file() and not path.name.startswith("database.sqlite")}
        write_json(attempt / "normalized.json", observed)
        self.state["completedSteps"][key] = {"path": str((attempt / "normalized.json").relative_to(self.output)), "sha256": sha(attempt / "normalized.json")}
        del self.state["activeSteps"][key]
        self.persist()
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
                points = []
                for concurrency in concurrencies:
                    capacity = next(point["measurement"]["successOpsPerSecond"] for point in candidates if point["concurrency"] == concurrency)
                    for index, fraction in enumerate(self.settings["scheduledRateFractions"]):
                        rate = math.floor(capacity * fraction * 1000) / 1000
                        require(rate > 0, "Scheduled ladder has no positive offered load")
                        points.append(self.run_cell(provider, route, f"scheduled-capacity-{route}-{concurrency}-{index}",
                            self.settings["scheduledCapacitySeconds"], concurrency, rate))
                valid = [point for point in points if point["sloPassed"]]
                at_frozen_concurrency = [point for point in valid if point["concurrency"] == self.settings["concurrency"]]
                require(at_frozen_concurrency, "No scheduled-rate point met the common SLO at frozen long concurrency: " + provider + "/" + route)
                scheduled[route] = {"points": [{"concurrency": p["concurrency"], "rate": p["rate"], "measurement": p["measurement"],
                    "sloPassed": p["sloPassed"]} for p in points],
                    "maximumObservedSuccessfulOpsPerSecondUnderScheduledSlo": max(p["measurement"]["successOpsPerSecond"] for p in valid),
                    "maximumQualifiedOfferedRateAtFrozenConcurrency": max(p["rate"] for p in at_frozen_concurrency),
                    "status": "finite scheduled-rate grid at frozen concurrency and best precalibrated concurrency; not a global maximum proof"}
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
                # This is explicitly fresh-process recovery. It does not prove
                # that the same saturated pool recovers without process restart.
                self.run_cell(provider, route, "overload-" + route, self.settings["overloadSeconds"],
                    self.settings["concurrency"], rate * self.settings["overloadFactor"])
                recovered = self.run_cell(provider, route, "recovery-" + route, self.settings["fixedValidationSeconds"],
                    self.settings["concurrency"], rate)
                require(recovered["sloPassed"], "Post-overload recovery failed common SLO")
            self.state["calibration"][provider]["recoveryScope"] = "fresh process after overload; same-process pool recovery remains a separate obligation"
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
        print(json.dumps({"task": record["task"], "enabled": record["enabled"], "measurementExecuted": False}))
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
