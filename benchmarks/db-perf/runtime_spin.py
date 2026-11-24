#!/usr/bin/env python3
"""One bounded spin-limit sensitivity experiment on the original Native AOT ELF.

No runtime build, perf sampling, default-policy change or long-run credit. The
frozen acquisition helper and Campaign.run_cell remain byte-for-byte unchanged.
The only launch adaptation is a recorded, same-PID exec with two selected
environment keys removed, and (for spin0) one of them set to the text "0".
"""
from __future__ import annotations

import argparse
import copy
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import platform
import signal
import statistics
import subprocess
import sys
import tempfile
import time
import traceback

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
# Canonical JSON of the complete reviewed request except its boolean activation
# flag. Altering workload, order, identities, bounds or claims needs a new review.
REQUEST_CONTRACT_SHA256 = "83a3a7037ff7f68e073258cdd9de55ac7e988ff0fdd62058fdd530ce69988497"
REFERENCE_SHA256 = "f5096e9f07058549652d6c66b4a6b1c0defbd6194d253be2ab57f7faf83f720c"
HELPER_SHA256 = "77398b0a0c0781f336e47ee0c0cff4edcdc69808e43195ce8feb13b0c585126c"
SPIN = "DOTNET_ThreadPool_UnfairSemaphoreSpinLimit"
LEGACY = "COMPlus_ThreadPool_UnfairSemaphoreSpinLimit"
OTHER_KNOBS = tuple(prefix + name for prefix in ("DOTNET_", "COMPlus_") for name in (
    "ThreadPool_SpuriousDispatchNoSpinThreshold", "ThreadPool_UnfairSemaphoreWakeCooldown"))
SELECTED_KEYS = (SPIN, LEGACY, *OTHER_KNOBS)
TOKEN_KEYS = ("GH_TOKEN", "GITHUB_TOKEN", "NUGET_API_KEY", "ACTIONS_RUNTIME_TOKEN", "ACTIONS_ID_TOKEN_REQUEST_TOKEN")


def require(value, message):
    if not value:
        raise ValueError(message)


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def utc():
    return datetime.now(timezone.utc).isoformat()


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "Duplicate JSON property")
        result[key] = value
    return result


def load(path):
    def invalid_constant(_):
        raise ValueError("Non-finite JSON number")
    return json.loads(Path(path).read_text(encoding="utf-8"), object_pairs_hook=unique_object,
                      parse_constant=invalid_constant)


def write(path, value, *, exclusive=False):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    data = json.dumps(value, indent=2, allow_nan=False) + "\n"
    if exclusive:
        with path.open("x", encoding="utf-8") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
    else:
        temporary = None
        try:
            # A unique name also permits the final failure receipt if a signal
            # lands between file creation and assignment of its Python owner.
            with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent,
                    prefix=path.name + ".", suffix=".pending", delete=False) as stream:
                temporary = Path(stream.name)
                stream.write(data)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, path)
        except BaseException:
            if temporary is not None:
                try:
                    temporary.unlink(missing_ok=True)
                except OSError:
                    pass  # Preserve the original failure; never delete another attempt's file.
            raise


def validate_value(value):
    require(isinstance(value, dict) and type(value.get("enabled")) is bool, "Request activation must be explicit")
    contract = dict(value)
    del contract["enabled"]
    require(hashlib.sha256(canonical(contract)).hexdigest() == REQUEST_CONTRACT_SHA256,
            "Runtime-spin request differs from the complete reviewed single-factor contract")
    return value


def validate_request(path):
    value = validate_value(load(path))
    for name, expected_hash in (("referenceRequest", REFERENCE_SHA256), ("acquireHelper", HELPER_SHA256)):
        entry = value[name]
        source = ROOT / entry["path"]
        require(source.is_file() and source.stat().st_size == entry["bytes"]
                and sha(source) == expected_hash == entry["sha256"], "Frozen " + name + " source differs")
    original = load(ROOT / value["referenceRequest"]["path"])
    require(value["runtime"] == original["runtime"] and value["applications"] == original["applications"],
            "Runtime or any of the eight original applications changed")
    for name, digest in value["frozenHarnessFiles"].items():
        require(sha(HERE / name) == digest, "Local reference harness source changed: " + name)
    return value


def scrub_credentials():
    for name in TOKEN_KEYS:
        os.environ.pop(name, None)


def condition_environment(inherited, condition):
    require(condition in ("default", "spin0"), "Unknown spin condition")
    require(not any(name in inherited for name in TOKEN_KEYS), "Download credentials reached the child launch boundary")
    result = dict(inherited)
    result.pop(SPIN, None)
    result.pop(LEGACY, None)
    if condition == "spin0":
        result[SPIN] = "0"
    require(all(result.get(key) == value for key, value in inherited.items() if key not in (SPIN, LEGACY)),
            "A setting outside the two spin-limit aliases changed")
    return result


def selected_environment(environment):
    # Never serialize arbitrary environment keys or their values. These are the
    # six reviewed integer runtime knobs only; reject unexpected text silently.
    result = {}
    for key in SELECTED_KEYS:
        value = environment.get(key)
        require(value is None or (len(value) <= 16 and all(c in "0123456789abcdefABCDEFxX+-" for c in value)),
                "A selected runtime knob contains unexpected nonnumeric text")
        result[key] = value
    return result


def process_identity():
    # /proc/stat command names may contain spaces/parentheses; fields after the
    # final ')' begin with field 3, so starttime (field 22) is index 19.
    fields = Path("/proc/self/stat").read_text().rsplit(")", 1)[1].split()
    return {"pid": os.getpid(), "startTicks": int(fields[19])}


def cell_by_id(request, cell_id):
    matches = [cell for cell in request["cells"] if cell["id"] == cell_id]
    require(len(matches) == 1, "Unknown or duplicate cell identity")
    return matches[0]


def verify_launch_argv(argv, planned, cell, request, bundle, receipt):
    require(argv and all(type(value) is str for value in argv), "Explicit original application argv required")
    identity = request["applications"][cell["provider"] + "/" + cell["route"]]
    target = bundle / identity["path"]
    require(argv[0] == str(target) and sha(target) == identity["sha256"], "Original application bytes/path changed")
    require(planned.count("--") == 1, "Launch must contain one transparent exec boundary")
    original = planned[planned.index("--") + 1:]
    require(len(argv) == len(original) and argv.count("--connection") == 1, "Original argv structure changed")
    connection_index = argv.index("--connection") + 1
    reconstructed = list(argv)
    reconstructed[connection_index] = "{connection}"
    require(reconstructed == original, "Actual application argv differs from the frozen runner plan")
    settings = request["profile"]
    required = {"--case": "mixed", "--mode": "prepared", "--session": "persistent",
                "--concurrency": str(settings["concurrency"]), "--warmup": str(settings["warmupOperationsPerWorker"]),
                "--seconds": str(settings["effectiveSecondsPerCell"]), "--idle-seconds": str(settings["idleSecondsPerCell"]),
                "--arrival-rate": str(settings["fixedRates"][cell["provider"]]),
                "--queue-capacity": str(settings["concurrency"] * settings["queuePerWorker"]),
                "--output": str(receipt.parent / "result.json")}
    frozen = load(bundle / "request.json")
    required["--cases"] = ",".join(frozen["workload"]["cases"])
    for flag, expected in required.items():
        require(argv.count(flag) == 1 and argv.index(flag) + 1 < len(argv)
                and argv[argv.index(flag) + 1] == expected, "Frozen launch argument changed: " + flag)
    return identity


def transfer_exec(argv, environment, receipt, evidence):
    """One environment transfer. A successful later raw result proves exec ran.

    Writing this pre-exec receipt alone never proves application execution. The
    controller must bind its PID/starttime, original ELF hash and result to the
    original runner's joined command receipt before accepting a cell.
    """
    write(receipt, evidence, exclusive=True)
    try:
        os.execvpe(argv[0], argv, environment)
    except BaseException as error:
        write(receipt.with_name("spin-exec-failure.json"), {
            "type": type(error).__name__, "message": str(error), "utc": utc(),
            "cellId": evidence.get("cellId"), "noAutomaticRetry": True}, exclusive=True)
        raise


def exec_cell(args):
    request = validate_request(args.request)
    require(request["enabled"], "Runtime-spin request is staged, not activated")
    require(args.controller_sha256 == sha(__file__), "Exec wrapper source changed after planning")
    bundle, receipt = args.bundle.resolve(), args.receipt.resolve()
    cell = cell_by_id(request, args.cell_id)
    require(receipt.name == "spin-environment.json" and receipt.parent.name == "attempt-00"
            and receipt.parent.parent.name == cell["id"] and receipt.parent.parent.parent.name == cell["provider"],
            "Environment receipt is outside this cell's only attempt")
    require(sha(bundle / "request.json") == request["runtime"]["requestSha256"]
            and sha(bundle / "bundle.json") == request["runtime"]["manifestSha256"], "Original runtime metadata changed")
    plan_path = receipt.parent / "plan.json"
    plan = load(plan_path)
    require(plan["provider"] == cell["provider"] and len(plan["commands"]) == 1, "Cell plan identity changed")
    argv = args.command[1:] if args.command[:1] == ["--"] else args.command
    identity = verify_launch_argv(argv, plan["commands"][0]["argv"], cell, request, bundle, receipt)
    inherited = selected_environment(os.environ)
    environment = condition_environment(os.environ, cell["condition"])
    selected = selected_environment(environment)
    evidence = {"schemaVersion": 1, "kind": "single-exec-environment-transfer", "cellId": cell["id"],
        "cell": cell, "observedUtc": utc(), "beforeExecMonotonicNs": time.monotonic_ns(),
        "processIdentity": process_identity(), "originalArgv": argv, "application": identity,
        "requestSha256": sha(args.request), "controllerSha256": sha(__file__), "planSha256": sha(plan_path),
        "inheritedSelectedEnvironment": inherited, "execSelectedEnvironment": selected,
        "otherEnvironmentEntriesUnchanged": True, "knownDownloadCredentialKeysAbsent": True,
        "scope": "These selected values are passed to one same-PID execvpe before runtime initialization; no private runtime effective field is read",
        "wholeBatchAcceptance": False, "acceptedLongSegments": 0}
    transfer_exec(argv, environment, receipt, evidence)


def load_acquire_helper(request):
    require(not any(name in sys.modules for name in ("campaign", "campaign_build", "plan_diagnostics", "run")),
            "A different harness was already imported")
    path = ROOT / request["acquireHelper"]["path"]
    require(sha(path) == HELPER_SHA256, "Frozen acquire helper changed")
    spec = importlib.util.spec_from_file_location("db_perf_spin_frozen_acquire", path)
    require(spec is not None and spec.loader is not None, "Cannot load the fixed acquisition helper")
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)
    return helper


def verify_imports(request, bundle):
    for filename, expected in request["frozenHarnessFiles"].items():
        source = bundle / "harness" / filename
        module = sys.modules.get(source.stem)
        require(module is not None and Path(module.__file__).resolve() == source.resolve()
                and sha(source) == expected, "Executed harness import differs: " + filename)


def spin_campaign_type(module, request_path, request, controller_sha):
    class SpinCampaign(module.Campaign):
        current_cell = None

        def argv(self, provider, route, seconds, concurrency, rate, output, native_contract=None, **keywords):
            require(self.current_cell is not None and self.current_cell["provider"] == provider
                    and self.current_cell["route"] == route, "Cell scope is not set before launch")
            original = super().argv(provider, route, seconds, concurrency, rate, output, native_contract, **keywords)
            return [sys.executable, "-I", "-S", "-B", str(Path(__file__).resolve()), "exec-cell",
                    "--request", str(request_path), "--bundle", str(self.bundle),
                    "--cell-id", self.current_cell["id"], "--receipt", str(output.parent / "spin-environment.json"),
                    "--controller-sha256", controller_sha, "--", *original]
    return SpinCampaign


def raw_get(record, name):
    matches = [value for key, value in record.items() if key.replace("_", "").lower() == name.replace("_", "").lower()]
    require(len(matches) == 1, "Missing or ambiguous raw field: " + name)
    return matches[0]


def nonnegative(value, name, *, integer=False):
    require(type(value) is int if integer else type(value) in (int, float), "Wrong numeric type: " + name)
    require(math.isfinite(value) and value >= 0, "Invalid nonnegative counter: " + name)
    return value


def cpu_interval(before, after):
    result = {}
    for name in ("UserCpuSeconds", "SystemCpuSeconds"):
        first = nonnegative(raw_get(before, name), name)
        last = nonnegative(raw_get(after, name), name)
        require(last >= first, "Process CPU counter regressed")
        result[name[0].lower() + name[1:]] = last - first
    return result


def raw_metrics(raw, observed):
    counts = observed["measurement"]["counts"]
    successes, rows = counts["succeeded"], counts["rows"]
    require(successes > 0 and rows > 0, "No successful operations/consumed rows for diagnostic metrics")
    first, last, allocated = (nonnegative(raw_get(raw, name), name, integer=True)
                              for name in ("AllocatedBefore", "AllocatedAfter", "AllocatedBytes"))
    require(last >= first and last - first == allocated, "Whole-process allocation endpoints do not reconcile")
    generations = {name: nonnegative(raw_get(raw, name), name, integer=True) for name in ("Gen0", "Gen1", "Gen2")}
    before, after = (raw_get(raw, name) for name in ("ProcessBefore", "ProcessAfter"))
    cpu = cpu_interval(before, after)
    for name, value in cpu.items():
        require(math.isclose(nonnegative(raw_get(raw, name), name), value, rel_tol=1e-9, abs_tol=1e-9),
                "Raw process CPU endpoints differ from their reported interval")
    total = cpu["userCpuSeconds"] + cpu["systemCpuSeconds"]
    require(math.isclose(nonnegative(raw_get(raw, "CpuSecondsPerSuccessfulOperation"), "CPU/op"),
                         total / successes, rel_tol=1e-9, abs_tol=1e-12), "Raw CPU/op denominator differs")
    idle = raw_get(raw, "Idle")
    require(isinstance(idle, dict) and raw_get(idle, "RequestedSeconds") == 5
            and nonnegative(raw_get(idle, "ObservedSeconds"), "idle seconds") >= 4.99, "Original five-second idle observation is missing")
    idle_cpu = cpu_interval(raw_get(idle, "Before"), raw_get(idle, "After"))
    for name, value in idle_cpu.items():
        require(math.isclose(nonnegative(raw_get(idle, name), name), value, rel_tol=1e-9, abs_tol=1e-9),
                "Idle CPU endpoints do not reconcile")
    for name in ("ManagedBytesBefore", "ManagedBytesAfter"):
        nonnegative(raw_get(idle, name), name, integer=True)
    for point in (before, after, raw_get(idle, "Before"), raw_get(idle, "After")):
        for name in ("RssBytes", "PeakRssBytes", "PssBytes"):
            value = raw_get(point, name)
            require(value is not None or name == "PssBytes", "Required process memory counter missing")
            if value is not None:
                nonnegative(value, name, integer=True)
    return {"counts": counts, "measurement": observed["measurement"],
        "allocation": {"allocatedBefore": first, "allocatedAfter": last, "allocatedBytes": allocated,
                       "bytesPerSuccessfulOperation": allocated / successes, "bytesPerRow": allocated / rows,
                       "gcCollectionDeltas": generations, "rawAttribution": raw_get(raw, "AllocationAttribution"),
                       "scope": "Cumulative whole-consumer managed allocation across all threads within the original warmed interval; framework/business split is not measured here; GC counts have no framework/business ownership"},
        "clientCpu": dict(cpu, totalCpuSeconds=total, userCpuSecondsPerSuccess=cpu["userCpuSeconds"] / successes,
                          systemCpuSecondsPerSuccess=cpu["systemCpuSeconds"] / successes,
                          totalCpuSecondsPerSuccess=total / successes, processBefore=before, processAfter=after,
                          scope="Original consumer process CPU endpoints for steady work plus drain; wrapper startup is outside these endpoints"),
        "resources": observed["resources"], "idle": idle,
        "idleMemoryScope": "ManagedBytesBefore/After are GetTotalMemory(false) retained/live estimates, not allocation counters; original RSS/PSS endpoints remain separate",
        "owners": {"before": raw_get(raw, "Before"), "after": raw_get(raw, "After"), "drained": raw_get(raw, "Drained"),
                   "scope": observed["measurement"]["ownershipCheckScope"]},
        "serverCpuOwnerSha256": observed["serverCpuOwnerSha256"],
        "nativeAllocationScope": "No allocator interposer or perf; RSS/PSS are retained, native heap allocation/free counts are not newly observed",
        "errorClassification": raw_get(raw, "Errors"), "sloPassed": observed["sloPassed"],
        "resourceQualityPassed": observed["resourceQualityPassed"],
        "resourceQualityReasons": observed["resourceQualityReasons"]}


def summarize_cell(request, request_path, cell, campaign, observed, controller_sha):
    raw_path = (campaign.output / observed["rawPath"]).resolve()
    attempt = raw_path.parent
    raw, command = load(raw_path), load(attempt / "client.command.json")
    environment_path = attempt / "spin-environment.json"
    evidence = load(environment_path)
    require(evidence["cell"] == cell and evidence["cellId"] == cell["id"]
            and evidence["requestSha256"] == sha(request_path) and evidence["controllerSha256"] == controller_sha
            and evidence["planSha256"] == sha(attempt / "plan.json"), "Exec receipt identity changed")
    require(evidence["processIdentity"] == {key: command["clientIdentity"][key] for key in ("pid", "startTicks")},
            "Exec receipt does not belong to the original runner's joined child")
    require(command["exitCode"] in (0, 1) and not command["timedOut"] and not command["observerErrors"],
            "Exec child lacks a successful joined command observation")
    identity = request["applications"][cell["provider"] + "/" + cell["route"]]
    require(evidence["application"] == identity and raw_get(raw, "ExecutableSha256") == identity["sha256"],
            "Raw result does not prove execution of the recorded original ELF")
    plan_argv = load(attempt / "plan.json")["commands"][0]["argv"]
    verify_launch_argv(evidence["originalArgv"], plan_argv, cell, request, campaign.bundle, environment_path)
    require(command["argv"][-len(evidence["originalArgv"]):] == evidence["originalArgv"],
            "Runner argv differs from the actual exec argv")
    selected = evidence["execSelectedEnvironment"]
    expected = dict(evidence["inheritedSelectedEnvironment"], **{SPIN: "0" if cell["condition"] == "spin0" else None, LEGACY: None})
    require(selected == expected and set(selected) == set(SELECTED_KEYS)
            and evidence["knownDownloadCredentialKeysAbsent"] is True and evidence["otherEnvironmentEntriesUnchanged"] is True,
            "Recorded single-variable environment transfer differs")
    result = {"schemaVersion": 1, "cell": cell, "environment": evidence, "metrics": raw_metrics(raw, observed),
        "bindings": {name: {"path": str((attempt / name).relative_to(campaign.output)), "sha256": sha(attempt / name)}
                     for name in ("result.json", "normalized.json", "client.command.json", "client.resources.jsonl",
                                  "plan.json", "runner.json", "spin-environment.json")},
        "wait4ProcessLifetime": {key: value for key, value in command.items() if key.startswith("processLifetime")},
        "wait4Scope": "Process-lifetime CPU/maxRSS includes transparent Python exec startup, warmup and idle; use original consumer endpoints for steady CPU/op",
        "performanceAcceptance": False, "acceptedLongSegments": 0}
    write(attempt / "spin-summary.json", result, exclusive=True)
    return result


def paired_summary(results):
    groups = {}
    for result in results:
        cell = result["cell"]
        key = (cell["provider"], cell["route"], cell["repeat"])
        group = groups.setdefault(key, {})
        require(cell["condition"] not in group, "Duplicate paired condition")
        group[cell["condition"]] = result
    require(len(groups) == 18 and all(set(value) == {"default", "spin0"} for value in groups.values()), "Incomplete paired experiment")
    pairs = []
    for (provider, route, repeat), group in sorted(groups.items()):
        values = {}
        for condition, result in group.items():
            metrics = result["metrics"]
            values[condition] = {"userCpuSecondsPerSuccess": metrics["clientCpu"]["userCpuSecondsPerSuccess"],
                "systemCpuSecondsPerSuccess": metrics["clientCpu"]["systemCpuSecondsPerSuccess"],
                "totalCpuSecondsPerSuccess": metrics["clientCpu"]["totalCpuSecondsPerSuccess"],
                "p99MicrosecondsUpper": metrics["measurement"]["p99MicrosecondsUpper"],
                "errorAndRejectionRate": metrics["measurement"]["errorAndRejectionRate"],
                "successOpsPerSecond": metrics["measurement"]["successOpsPerSecond"],
                "managedBytesPerSuccess": metrics["allocation"]["bytesPerSuccessfulOperation"]}
        pairs.append({"provider": provider, "route": route, "repeat": repeat, "values": values,
            "spin0MinusDefault": {key: values["spin0"][key] - value for key, value in values["default"].items()},
            "defaultCellId": group["default"]["cell"]["id"], "spin0CellId": group["spin0"]["cell"]["id"]})
    aggregates = []
    for provider in ("postgresql", "sqlite"):
        for route in ("korda_before", "korda_after", "independent_csharp_orm"):
            selected = [pair for pair in pairs if pair["provider"] == provider and pair["route"] == route]
            require(len(selected) == 3, "Missing independent repeat")
            aggregates.append({"provider": provider, "route": route, "independentPairs": 3,
                "medianPairedSpin0MinusDefault": {key: statistics.median(pair["spin0MinusDefault"][key] for pair in selected)
                                                   for key in selected[0]["spin0MinusDefault"]}})
    return {"pairs": pairs, "aggregates": aggregates, "performanceAcceptance": False, "acceptedLongSegments": 0,
        "scope": "All three independent pairs retained, including contrary effects; descriptive sensitivity only, no significance or production optimization claim. Full p50/p95, CPU, memory, idle, errors and ownership remain in every cell summary/raw receipt."}


class ActiveBudgetExpired(KeyboardInterrupt):
    pass


def run(args, request):
    require(request["enabled"], "Runtime-spin request is staged, not activated")
    output = args.output.resolve()
    require(not output.exists(), "Use a fresh diagnostic directory; no automatic resume/retry or overwrite")
    output.mkdir(parents=True)
    request_path = output / "runtime-spin-request.json"
    request_path.write_bytes(args.request.read_bytes())
    controller_sha = sha(__file__)
    started = time.monotonic()
    state = {"schemaVersion": 1, "task": "DB-Perf-01", "kind": request["kind"], "startedUtc": utc(), "status": "started",
        "requestSha256": sha(request_path), "controllerSha256": controller_sha, "expectedCells": request["cells"],
        "cells": [], "errors": [], "qualifiedLongSegments": [], "wholeBatchAccepted": False,
        "scope": request["scope"], "budget": request["profile"]}
    write(output / "diagnostic.json", state)
    old_handler = signal.getsignal(signal.SIGALRM)
    bundle = module = None
    results = []

    def expired(_signal, _frame):
        raise ActiveBudgetExpired("Active diagnostic budget exhausted; join existing owners and retain every attempt")

    signal.signal(signal.SIGALRM, expired)
    signal.setitimer(signal.ITIMER_REAL, request["profile"]["activeSeconds"])
    try:
        require(os.environ.get("GITHUB_ACTIONS") == "true" and os.environ.get("RUNNER_ENVIRONMENT") == "github-hosted"
                and platform.system() == "Linux" and platform.machine() == "x86_64", "Use its own hosted Linux x64 diagnostic VM")
        require(os.environ.get("ImageOS") == request["environment"]["imageOS"]
                and os.environ.get("ImageVersion") == request["environment"]["imageVersion"], "Frozen measurement image differs")
        helper = load_acquire_helper(request)
        try:
            bundle, module = helper.acquire_runtime(request, request_path, output)
        finally:
            # The exact read-only helper is the sole network stage. No token is
            # inherited by original AOT consumers or their exec wrappers.
            scrub_credentials()
        verify_imports(request, bundle)
        os.environ["LD_LIBRARY_PATH"] = str(bundle / "postgres/lib") + ":" + str(bundle / "native")
        campaign = spin_campaign_type(module, request_path, request, controller_sha)(bundle, output / "cells", False)
        campaign.deadline = min(campaign.deadline, started + request["profile"]["activeSeconds"])
        for key, value in {"concurrency": 4, "constructionMode": "prepared", "warmupOperations": 64,
                           "idleSeconds": 5, "sampleSeconds": 0.5, "queuePerWorker": 4}.items():
            require(campaign.settings[key] == value, "Frozen original measurement setting differs: " + key)
        write(output / "environment.json", {"identity": module.environment_identity(), "uname": list(platform.uname()),
            "affinityBefore": sorted(os.sched_getaffinity(0)), "selectedInheritedKnobs": selected_environment(os.environ),
            "runtimeDependencies": module.runtime_dependencies(bundle, campaign.bundle_manifest),
            "qualifiedForFormalTiming": False, "separateVmFromFormalCampaign": True,
            "sourceReview": request["sourceReview"], "sysctlOrPermissionChanges": False}, exclusive=True)
        # Reuse original CPU partitions, complete cgroup quota ancestry, host
        # idle/steal admission and all resource-quality thresholds unchanged.
        module.admission(campaign.request, campaign.output)
        for provider in request["profile"]["providers"]:
            campaign.initialize(provider)
        seen_processes = set()
        common_other_knobs = None
        for cell in request["cells"]:
            require(sha(__file__) == controller_sha and sha(request_path) == state["requestSha256"], "Controller/request changed during run")
            entry = {"cell": cell, "status": "started", "startedUtc": utc(), "attempts": 1}
            state["cells"].append(entry)
            write(output / "diagnostic.json", state)
            campaign.current_cell = cell
            print("Runtime spin diagnostic " + cell["id"], flush=True)
            try:
                observed = campaign.run_cell(cell["provider"], cell["route"], cell["id"],
                    request["profile"]["effectiveSecondsPerCell"], request["profile"]["concurrency"],
                    rate=request["profile"]["fixedRates"][cell["provider"]],
                    require_coverage=True, construction_mode="prepared", diagnostic=None)
                result = summarize_cell(request, request_path, cell, campaign, observed, controller_sha)
                process = result["environment"]["processIdentity"]
                identity = (process["pid"], process["startTicks"])
                require(identity not in seen_processes, "Independent cell reused a live process identity")
                seen_processes.add(identity)
                other = {key: result["environment"]["execSelectedEnvironment"][key] for key in OTHER_KNOBS}
                require(common_other_knobs is None or common_other_knobs == other, "A second reviewed runtime knob varied between cells")
                common_other_knobs = other
                results.append(result)
                entry.update(rawPath=observed["rawPath"], rawSha256=observed["rawSha256"],
                             summaryPath=str((Path(observed["rawPath"]).parent / "spin-summary.json")),
                             summarySha256=sha(campaign.output / Path(observed["rawPath"]).parent / "spin-summary.json"))
                require(observed["sloPassed"] and observed["resourceQualityPassed"],
                        "Original SLO/resource gate failed; retain this observation, stop without retry")
                entry["status"] = "completed"
            except BaseException as error:
                entry.update(status="failed", error={"type": type(error).__name__, "message": str(error)})
                raise
            finally:
                entry["finishedUtc"] = utc()
                write(output / "diagnostic.json", state)
        require(len(results) == 36, "All frozen cells must complete; no subset can be selected")
        write(output / "comparison.json", paired_summary(results), exclusive=True)
        state["status"] = "completed"
    except BaseException as error:
        state["status"] = "failed"
        state["errors"].append({"type": type(error).__name__, "message": str(error)})
        state["traceback"] = traceback.format_exc()
    finally:
        signal.setitimer(signal.ITIMER_REAL, 0)
        signal.signal(signal.SIGALRM, old_handler)
        scrub_credentials()
        if bundle is not None and module is not None:
            try:
                module.verify_bundle(bundle)
                verify_imports(request, bundle)
                state["originalRuntimeInventoryUnchangedAfterDiagnostics"] = True
            except BaseException as error:
                state["status"] = "failed"
                state["errors"].append({"type": type(error).__name__, "message": str(error)})
        state.update(finishedUtc=utc(), elapsedSeconds=time.monotonic() - started,
                     completedCells=sum(cell["status"] == "completed" for cell in state["cells"]),
                     notStartedCellIds=[cell["id"] for cell in request["cells"]
                                        if cell["id"] not in {item["cell"]["id"] for item in state["cells"]}])
        write(output / "diagnostic.json", state)
        print(json.dumps({"status": state["status"], "completedCells": state["completedCells"],
                          "output": str(output), "acceptedLongSegments": 0}), flush=True)
    return 0 if state["status"] == "completed" else 1


def self_test():
    """Finite stdlib process/control tests. Never obtains or runs the AOT bundle."""
    request = validate_request(HERE / "runtime-spin-request.json")
    controls = []

    def rejects(name, action):
        try:
            action()
        except (ValueError, FileExistsError):
            controls.append(name)
        else:
            raise AssertionError("Negative control did not reject: " + name)

    mutations = [(("runtime", "artifactId"), 1), (("applications", "postgresql/korda_after", "sha256"), "0" * 64),
        (("profile", "concurrency"), 1), (("profile", "effectiveSecondsPerCell"), 30),
        (("profile", "fixedRates", "postgresql"), 180.628), (("profile", "idleSecondsPerCell"), 0),
        (("profile", "maximumAttemptsPerCell"), 2), (("profile", "independentProcessesPerCondition"), True),
        (("acquireHelper", "sha256"), "0" * 64), (("acceptedLongSegments",), 1), (("perfSampling",), True),
        (("aotBuilds",), True), (("wholeBatchAcceptance",), True), (("enabled",), 1)]
    for keys, value in mutations:
        changed = copy.deepcopy(request)
        target = changed
        for key in keys[:-1]:
            target = target[key]
        target[keys[-1]] = value
        rejects("contract:" + "/".join(keys), lambda changed=changed: validate_value(changed))
    for name, cells in (("missing", request["cells"][:-1]), ("duplicate", [request["cells"][0]] + request["cells"][:-1]),
                        ("reordered", list(reversed(request["cells"])) )):
        rejects("cell-order:" + name, lambda cells=cells: validate_value(dict(request, cells=cells)))
    active = dict(request, enabled=True)
    validate_value(active)
    require(len(request["cells"]) == 36, "Unexpected cell count")
    inherited = {"PATH": os.defpath, "UNCHANGED": "kept", SPIN: "400", LEGACY: "200", OTHER_KNOBS[0]: "4"}
    for condition in ("default", "spin0"):
        result = condition_environment(inherited, condition)
        require(inherited[SPIN] == "400" and result.get(SPIN) == ("0" if condition == "spin0" else None)
                and LEGACY not in result and result["UNCHANGED"] == "kept" and result[OTHER_KNOBS[0]] == "4",
                "Single-factor environment map differs or mutated its input")
        require(set(selected_environment(result)) == set(SELECTED_KEYS), "Environment evidence includes unreviewed keys")
    rejects("credentials-not-inherited", lambda: condition_environment(dict(inherited, GH_TOKEN="synthetic"), "default"))
    rejects("unknown-condition", lambda: condition_environment(inherited, "spin1"))
    rejects("unexpected-selected-text", lambda: selected_environment({SPIN: "unexpected-secret-like-text"}))
    with tempfile.TemporaryDirectory(prefix="db-perf-spin-self-test-") as directory:
        root = Path(directory)
        duplicate = root / "duplicate.json"
        duplicate.write_text('{"enabled":false,"enabled":true}')
        rejects("duplicate-json-key", lambda: load(duplicate))
        duplicate.write_text('{"number":NaN}')
        rejects("nonfinite-json", lambda: load(duplicate))
        marker = root / "exclusive.json"
        write(marker, {"first": True}, exclusive=True)
        rejects("existing-evidence", lambda: write(marker, {"second": True}, exclusive=True))
        require(load(marker) == {"first": True}, "Existing evidence was changed")
        state_path = root / "diagnostic.json"
        write(state_path, {"status": "started"})
        unrelated = state_path.with_name(state_path.name + ".pending")
        unrelated.write_bytes(b"pre-existing evidence must remain untouched")
        original_fsync = os.fsync
        interruption = ActiveBudgetExpired("synthetic budget interruption at fsync")

        def interrupted_fsync(_descriptor):
            raise interruption

        try:
            os.fsync = interrupted_fsync
            try:
                write(state_path, {"status": "in_progress"})
            except ActiveBudgetExpired as error:
                require(error is interruption, "Atomic-write cleanup replaced the original interruption")
            else:
                raise AssertionError("Atomic-write interruption control did not fire")
        finally:
            os.fsync = original_fsync
        require(load(state_path) == {"status": "started"} and not list(root.glob("diagnostic.json.*.pending")),
                "Interrupted atomic write replaced the committed state or retained its owned temporary file")
        write(state_path, {"status": "failed", "error": type(interruption).__name__})
        require(load(state_path) == {"status": "failed", "error": "ActiveBudgetExpired"}
                and unrelated.read_bytes() == b"pre-existing evidence must remain untouched",
                "Final failure state is blocked or unrelated pending evidence was deleted")
        controls.append("interrupted-atomic-state-final-failure")
        # Explicit synthetic bytes exercise the exec boundary's path/hash and
        # frozen original argv checks; they are never executed as an ELF.
        bundle = root / "synthetic-bundle"
        target = bundle / request["applications"]["postgresql/korda_before"]["path"]
        target.parent.mkdir(parents=True)
        target.write_bytes(b"synthetic identity fixture, not an executable")
        write(bundle / "request.json", {"workload": {"cases": ["fixture"]}}, exclusive=True)
        synthetic_request = copy.deepcopy(request)
        synthetic_request["applications"]["postgresql/korda_before"]["sha256"] = sha(target)
        cell = request["cells"][0]
        receipt = root / "attempt-00" / "spin-environment.json"
        original = [str(target), "--connection", "{connection}", "--case", "mixed", "--cases", "fixture",
                    "--mode", "prepared", "--session", "persistent", "--concurrency", "4", "--warmup", "64",
                    "--seconds", "15", "--idle-seconds", "5", "--arrival-rate", "180.627", "--queue-capacity", "16",
                    "--output", str(receipt.parent / "result.json")]
        argv = ["synthetic-local-connection" if value == "{connection}" else value for value in original]
        planned = ["synthetic-wrapper", "--", *original]
        verify_launch_argv(argv, planned, cell, synthetic_request, bundle, receipt)
        controls.append("launch-positive-original-argv")
        altered = list(argv)
        altered[altered.index("--seconds") + 1] = "16"
        rejects("launch-actual-versus-plan", lambda: verify_launch_argv(altered, planned, cell, synthetic_request, bundle, receipt))
        planned_altered = ["synthetic-wrapper", "--", *["{connection}" if value == "synthetic-local-connection" else value for value in altered]]
        rejects("launch-frozen-duration", lambda: verify_launch_argv(altered, planned_altered, cell, synthetic_request, bundle, receipt))
        target.write_bytes(b"different synthetic identity fixture")
        rejects("launch-executable-bytes", lambda: verify_launch_argv(argv, planned, cell, synthetic_request, bundle, receipt))

        point = {"UserCpuSeconds": 10.0, "SystemCpuSeconds": 5.0, "RssBytes": 1000, "PeakRssBytes": 2000, "PssBytes": None}
        after = dict(point, UserCpuSeconds=12.0, SystemCpuSeconds=6.0)
        raw = {"AllocatedBefore": 1000, "AllocatedAfter": 1500, "AllocatedBytes": 500,
               "Gen0": 1, "Gen1": 0, "Gen2": 0, "ProcessBefore": point, "ProcessAfter": after,
               "UserCpuSeconds": 2.0, "SystemCpuSeconds": 1.0, "CpuSecondsPerSuccessfulOperation": 0.3,
               "AllocationAttribution": "synthetic full-process raw totals", "Before": {}, "After": {}, "Drained": {}, "Errors": [],
               "Idle": {"RequestedSeconds": 5, "ObservedSeconds": 5.01, "Before": after, "After": after,
                        "UserCpuSeconds": 0.0, "SystemCpuSeconds": 0.0, "ManagedBytesBefore": 200, "ManagedBytesAfter": 240}}
        observation = {"measurement": {"counts": {"succeeded": 10, "rows": 100}, "ownershipCheckScope": "synthetic fixture"},
                       "resources": {}, "serverCpuOwnerSha256": None, "sloPassed": True,
                       "resourceQualityPassed": True, "resourceQualityReasons": []}
        metrics = raw_metrics(raw, observation)
        require(metrics["allocation"]["allocatedBytes"] == 500 and metrics["allocation"]["bytesPerRow"] == 5
                and metrics["clientCpu"]["totalCpuSecondsPerSuccess"] == 0.3
                and metrics["clientCpu"]["processAfter"]["PssBytes"] is None, "Synthetic raw endpoint reduction differs")
        controls.append("raw-cpu-allocation-null-pss-positive")
        for key, value in (("AllocatedAfter", 1501), ("AllocatedBefore", True), ("Gen0", -1),
                           ("UserCpuSeconds", float("inf")), ("CpuSecondsPerSuccessfulOperation", 0.6)):
            rejects("raw-endpoint:" + key, lambda key=key, value=value: raw_metrics(dict(raw, **{key: value}), observation))
        rejects("raw-idle-truncated", lambda: raw_metrics(dict(raw, Idle=dict(raw["Idle"], ObservedSeconds=1)), observation))
        # Real exec transfers (no runtime bundle, network, AOT or database). The
        # synthetic child reports only the selected keys, marker and PID. This
        # checks actual inheritance and same-PID exec, not merely a mock call.
        for condition in ("default", "spin0"):
            receipt = root / (condition + "-exec.json")
            observed_path = root / (condition + "-observed.json")
            environment = condition_environment(inherited, condition)
            code = ("import json,os;from pathlib import Path;"
                    "Path(" + repr(str(observed_path)) + ").write_text(json.dumps({"
                    "'pid':os.getpid(),'spin':os.environ.get(" + repr(SPIN) + "),"
                    "'legacy':os.environ.get(" + repr(LEGACY) + "),'unchanged':os.environ['UNCHANGED']}))")
            child = os.fork()
            if child == 0:
                try:
                    transfer_exec([sys.executable, "-I", "-S", "-c", code], environment, receipt,
                                  {"cellId": "synthetic-" + condition, "processIdentity": process_identity(), "testOnly": True})
                except BaseException:
                    os._exit(1)
            deadline = time.monotonic() + 10
            while True:
                done, status = os.waitpid(child, os.WNOHANG)
                if done:
                    break
                if time.monotonic() > deadline:
                    os.kill(child, signal.SIGKILL)
                    os.waitpid(child, 0)
                    raise AssertionError("Synthetic exec child exceeded ten-second bound")
                time.sleep(0.01)
            require(os.waitstatus_to_exitcode(status) == 0, "Synthetic exec transfer failed")
            observed = load(observed_path)
            require(observed["pid"] == child == load(receipt)["processIdentity"]["pid"]
                    and observed["spin"] == ("0" if condition == "spin0" else None)
                    and observed["legacy"] is None and observed["unchanged"] == "kept", "Actual exec inheritance differs")
            controls.append("real-stdlib-exec:" + condition)
    print(json.dumps({"passed": True, "controls": controls, "controlCount": len(controls), "cellsFrozen": 36,
                      "aotExecutions": 0, "databaseExecutions": 0, "downloads": 0, "acceptedLongSegments": 0}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="action", required=True)
    sub.add_parser("self-test")
    validate = sub.add_parser("validate")
    validate.add_argument("--request", type=Path, default=HERE / "runtime-spin-request.json")
    validate.add_argument("--github-output", type=Path)
    execute = sub.add_parser("run")
    execute.add_argument("--request", type=Path, default=HERE / "runtime-spin-request.json")
    execute.add_argument("--output", type=Path, required=True)
    child = sub.add_parser("exec-cell")
    child.add_argument("--request", type=Path, required=True)
    child.add_argument("--bundle", type=Path, required=True)
    child.add_argument("--cell-id", required=True)
    child.add_argument("--receipt", type=Path, required=True)
    child.add_argument("--controller-sha256", required=True)
    child.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if args.action == "self-test":
        self_test()
        return 0
    if args.action == "exec-cell":
        exec_cell(args)
        raise AssertionError("exec must transfer or raise")
    request = validate_request(args.request)
    if args.action == "validate":
        if args.github_output:
            with args.github_output.open("a", encoding="utf-8") as stream:
                stream.write("enabled=" + str(request["enabled"]).lower() + "\n")
        print(json.dumps({"valid": True, "enabled": request["enabled"], "cells": len(request["cells"]),
                          "requestSha256": sha(args.request), "measurementExecuted": False, "acceptedLongSegments": 0}))
        return 0
    return run(args, request)


if __name__ == "__main__":
    raise SystemExit(main())
