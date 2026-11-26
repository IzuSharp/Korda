#!/usr/bin/env python3
"""Offline raw arithmetic/identity verification of the sticky-oracle campaign.

This is a measurement engine, not a substitute for the delivery, review,
allocation-attribution, or public NuGet acceptance gates.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import statistics

from archive_inputs import EvidenceZip, RuntimeBundle
from identity_binding import bind_runtime, bind_cell, argv_options, PROVIDERS, ROUTES
from raw_metrics import measurement, compare_measurement, client_metrics, owner_metrics, timestamp, number_equal
from resource_metrics import resources, quota, server_owner
from diagnostic_metrics import allocation_profile, recovery
from campaign_admission import campaign_prerequisites
from resource_metrics import compare_object
from strict_io import EvidenceError, field, finite, natural, need, sha_file, strict_json, valid_sha


def schema_fingerprint():
    names = ("verify_current.py", "strict_io.py", "archive_inputs.py", "identity_binding.py", "raw_metrics.py", "resource_metrics.py", "diagnostic_metrics.py", "campaign_admission.py")
    return {name: sha_file(Path(__file__).parent / name) for name in names}


def stat_identity(path):
    item = Path(path).stat()
    return {"device": item.st_dev, "inode": item.st_ino, "bytes": item.st_size, "mtime_ns": item.st_mtime_ns}


def read_cell(archive, key, reference):
    normalized = strict_json(archive.require_member_hash(reference["path"], reference["sha256"]))
    need(key == normalized["provider"] + "/" + normalized["name"], "step_identity", "Completed-step key differs from its actual wrapper")
    prefix = reference["path"].rsplit("/", 1)[0]
    need(prefix.startswith("runs/" + normalized["provider"] + "/" + normalized["name"] + "/attempt-") and reference["path"] == prefix + "/normalized.json",
         "step_path_identity", "Completed-step wrapper belongs to another attempt")
    need(normalized["rawPath"] == prefix + "/result.json", "step_raw_path", "Wrapper points to another attempt's raw result")
    for path, digest in normalized["evidenceFiles"].items():
        need(path.startswith(prefix + "/") and "/" not in path[len(prefix) + 1:], "foreign_attempt_reference", "Attempt evidence refers to another step/subtree")
        archive.require_member_hash(path, digest)
    required = ("result.json", "client.command.json", "client.resources.jsonl", "runner.json", "plan.json", "client.stdout.log")
    need(all(prefix + "/" + name in normalized["evidenceFiles"] for name in required), "step_missing_evidence", "Step omits a primary raw evidence reference")
    for name, key_name in (("result.json", "rawSha256"), ("client.command.json", "commandSha256"), ("client.resources.jsonl", "sampleSha256")):
        archive.require_member_hash(prefix + "/" + name, normalized[key_name])
    result = {"normalized": normalized, "prefix": prefix, "raw": archive.json(prefix + "/result.json"),
        "command": archive.json(prefix + "/client.command.json"), "samples": archive.jsonl(prefix + "/client.resources.jsonl"),
        "runner": archive.json(prefix + "/runner.json"), "plan": archive.json(prefix + "/plan.json"), "owner": None}
    for name, digest in result["command"]["files"].items():
        archive.require_member_hash(prefix + "/" + name, digest)
    if normalized["provider"] == "postgresql":
        archive.require_member_hash(prefix + "/postgres-owner.json", normalized["serverCpuOwnerSha256"])
        result["owner"] = archive.json(prefix + "/postgres-owner.json")
    else:
        need(normalized["serverCpuOwnerSha256"] is None, "sqlite_server_owner", "SQLite observation claims an external server")
    seed_prefix = "seeds/" + ("" if normalized["workloadId"] == "primary" else normalized["workloadId"] + "/") + normalized["provider"]
    seed_path = seed_prefix + "/seed.json"
    archive.require_member_hash(seed_path, normalized["seedReceiptSha256"])
    result["seed"] = archive.json(seed_path)
    result["seed_prefix"] = seed_prefix
    result["seed_command"] = archive.json(seed_prefix + "/initialize.command.json")
    result["seed_stdout"] = archive.read(seed_prefix + "/initialize.stdout.log").decode("utf-8")
    for name, digest in result["seed_command"]["files"].items():
        archive.require_member_hash(seed_prefix + "/" + name, digest)
    if normalized["provider"] == "postgresql":
        archive.require_member_hash(seed_prefix + "/postgres-owner.json", result["seed"]["serverCpuOwnerSha256"])
        result["seed_owner"] = archive.json(seed_prefix + "/postgres-owner.json")
    else:
        result["seed_owner"] = None
    retirement_path = prefix + "/database-retirement.json"
    if retirement_path in archive.entries:
        retirement = archive.json(retirement_path)
        need(retirement["rawResultSha256"] == normalized["rawSha256"] and retirement["seedReceiptSha256"] == normalized["seedReceiptSha256"] and retirement["ownerJoined"] is True,
             "database_retirement_binding", "Private database retirement is not bound to this raw result/seed/owner")
        need(timestamp(retirement["retiredUtc"]) >= timestamp(result["owner"]["finishedUtc"] if result["owner"] else result["command"]["finishedUtc"]),
             "database_retired_while_busy", "Private database was retired before its owner finished")
    return result


def bind_seed(cell, binding, measured_identity):
    n, seed, command = cell["normalized"], cell["seed"], cell["seed_command"]
    workload = binding["workloads"][n["workloadId"]]
    data = workload["manifest"]["dataset"]
    need(seed.get("task") == "DB-Perf-01" and seed.get("workloadId") == n["workloadId"] and seed.get("workloadSha256") == workload["sha256"],
         "seed_workload_identity", "The actual initializer seed belongs to another primary/heavy workload")
    need(isinstance(seed.get("files"), dict) and seed["files"], "seed_inventory_missing", "Initialized seed file inventory is absent")
    for name, digest in seed["files"].items():
        need(not Path(name).is_absolute() and ".." not in Path(name).parts, "seed_file_path", "Seed inventory escapes its own private database directory")
        valid_sha(digest, "seed file hash")
    request = binding["request"]
    cpus = ",".join(str(value) for value in request["admission"]["clientCpus"])
    need(command.get("exitCode") == 0 and command.get("success") is True and command.get("timedOut") is False
         and command.get("observerErrors") == [] and command.get("phase") == "dataset-initialization-excluded", "seed_initialization_failed", "Dataset initialization did not finish cleanly outside the workload")
    argv = command["argv"]
    need(argv[:3] == ["taskset", "-c", cpus], "seed_client_affinity", "Actual initializer CPU partition differs")
    executable = binding["applications"][n["provider"] + "/korda_before"]["executable"]
    need(argv[3] == measured_identity["runtime_root_as_executed"] + executable, "seed_initializer_binary", "Initializer did not execute the frozen before-package AOT binary from the same runtime bundle")
    options = argv_options(argv[3:])
    need(options.get("--initialize") is True and int(options["--tenants"]) == data["tenants"]
         and int(options["--orders-per-tenant"]) == data["ordersPerTenant"], "seed_dataset_parameters", "Actual initializer CLI data cardinalities differ from the selected workload")
    initialized = [line.removeprefix("DB_PERF_INITIALIZED ") for line in cell["seed_stdout"].splitlines() if line.startswith("DB_PERF_INITIALIZED ")]
    need(len(initialized) == 1, "seed_initializer_output", "Actual initializer did not retain one complete initialized-data receipt")
    actual = strict_json(initialized[0])
    expected_provider = "PostgreSql" if n["provider"] == "postgresql" else "Sqlite"
    need(field(actual, "schemaVersion") == 1 and field(actual, "provider") == expected_provider
         and field(actual, "tenants") == data["tenants"] and field(actual, "ordersPerTenant") == data["ordersPerTenant"],
         "seed_actual_dataset_output", "Initializer's actual data receipt differs from workload/CLI")
    natural(field(actual, "lines"), "initialized order lines", 1)
    need(command["finishedMonotonicNs"] <= cell["command"]["startedMonotonicNs"] and timestamp(command["finishedUtc"]) <= timestamp(cell["command"]["startedUtc"]),
         "seed_not_ready_before_client", "Business measurement began before seed initialization completed")
    owner = cell["seed_owner"]
    if n["provider"] == "postgresql":
        proof = server_owner(owner)
        server_cpus = ",".join(str(value) for value in request["admission"]["serverCpus"])
        need(owner["argv"][:3] == ["taskset", "-c", server_cpus] and owner["argv"][3] == measured_identity["runtime_root_as_executed"] + binding["postgres_binary"]["path"],
             "seed_server_execution_binding", "Seed database used another server binary/CPU partition")
        need(command["serverCpuGroupIdentity"] == proof["descriptor"] and owner["startedMonotonicNs"] <= command["startedMonotonicNs"] < command["finishedMonotonicNs"] <= owner["finishedMonotonicNs"]
             <= cell["command"]["startedMonotonicNs"], "seed_server_retirement_boundary", "Seed server identity/lifetime did not close before the measured client")
    else:
        need(seed.get("serverCpuOwnerSha256") is None, "sqlite_seed_external_server", "SQLite seed claims an external server owner")
    return {"workload_id": seed["workloadId"], "workload_sha256": seed["workloadSha256"], "seed_receipt_sha256": n["seedReceiptSha256"],
        "initializer_executable_sha256": binding["applications"][n["provider"] + "/korda_before"]["sha256"], "actual_initializer_receipt": actual,
        "initializer_finished_monotonic_ns": command["finishedMonotonicNs"], "private_seed_files_recorded": len(seed["files"]),
        "file_inventory_scope": "raw file hashes captured by initializer; checkpoint may omit reproducible database bytes; no retained-file rehash is claimed for absent seed payloads"}


def slo(measured, settings):
    value = measured["normalized"]
    return (value["correctnessPassed"] is True and value["oracleFailures"] == 0 and value["p99MicrosecondsUpper"] is not None
            and value["errorAndRejectionRate"] <= settings["maximumErrorAndRejectionRate"]
            and value["p99MicrosecondsUpper"] <= settings["maximumP99Microseconds"])


def analyze_cell(cell, binding, campaign):
    n, raw = cell["normalized"], cell["raw"]
    identity = bind_cell(binding, n, raw, cell["command"], cell["plan"], cell["runner"], campaign, cell["owner"])
    seed = bind_seed(cell, binding, identity)
    diagnostic = n.get("diagnostic")
    need(diagnostic in (None, "query-plan", "same-process-recovery", "native-allocation"), "diagnostic_adapter_pending", "Unreviewed instrumented diagnostic schema: " + str(diagnostic), unproven=True)
    manifest = binding["workloads"][n["workloadId"]]["manifest"]
    common = {"step": n["provider"] + "/" + n["name"], "diagnostic": diagnostic, "identity": identity,
        "raw_sha256": n["rawSha256"], "raw_path": n["rawPath"], "requested_seconds": n["seconds"], "rate": n["rate"],
        "concurrency": n["concurrency"], "construction_mode": n["constructionMode"], "workload_id": n["workloadId"],
        "workload_case_ids": n["workloadCaseIds"], "seed": seed,
        "actual_command": {key: cell["command"][key] for key in ("startedMonotonicNs", "finishedMonotonicNs", "startedUtc", "finishedUtc", "clientIdentity", "exitCode")}}
    if diagnostic == "same-process-recovery":
        recovered = recovery(raw, n, manifest, binding["request"]["diagnostics"]["sameProcessRecovery"], campaign["fixedRates"][n["provider"]])
        compare_object({key: value for key, value in recovered["normalized"].items() if key != "phases"}, n["measurement"], "recovery normalized")
        recorded_phases, recorded_resources = n["measurement"]["phases"], n["resources"]["phases"]
        need([item["name"] for item in recorded_phases] == [item["name"] for item in recorded_resources] == ["reference", "overload", "recovery"],
             "recovery_normalized_phase_order", "Recorded recovery phases/resources changed order")
        observed_phases = []
        for phase, value, recorded, observed in zip(recovered["phases"], recovered.pop("raw_phase_payloads"), recorded_phases, recorded_resources):
            compare_measurement(phase["measurement"], recorded["measurement"])
            phase_spec = dict(n, rate=field(field(value, "offeredLoad"), "requestsPerSecond"), resources=observed["resources"])
            observed_phases.append({"name": phase["name"], "resources": resources(cell["samples"], cell["command"], value, phase["measurement"],
                phase_spec, binding["request"], cell["owner"], phase_context=n["route"] != "direct_cpp")})
        actual_slo = (recovered["normalized"]["consumerPassed"] and recovered["normalized"]["saturationObserved"]
                      and recovered["normalized"]["counts"]["failed"] == 0
                      and all(slo(recovered["phases"][i]["measurement"], binding["request"]["measurement"]) for i in (0, 2)))
        need(n["sloPassed"] is actual_slo and n["timingComparisonEligible"] is False, "recovery_slo_mismatch", "Recovery SLO/diagnostic credit differs from actual phase ledgers")
        return dict(common, raw_measurement={"normalized": recovered["normalized"]}, recovery=recovered, resources={"phases": observed_phases},
                    recomputed_business_slo_passed=actual_slo, recorded_slo_passed=n["sloPassed"], timing_observation_eligible=False,
                    comparison_status="diagnostic_only")
    measured = measurement(raw, n, manifest)
    compare_measurement(measured, n["measurement"])
    client, owners = client_metrics(raw, measured), owner_metrics(raw, measured, manifest)
    observed = resources(cell["samples"], cell["command"], raw, measured, n, binding["request"], cell["owner"])
    actual_slo = slo(measured, binding["request"]["measurement"])
    profile = None
    if diagnostic is None:
        need(n.get("sloPassed") is actual_slo and n.get("timingComparisonEligible") is True
             and field(raw, "nativeAllocationProfile", optional=True) is None,
             "slo_or_instrumentation_mismatch", "Timing/SLO claim differs from actual counters/histogram/instrumentation")
        eligible = actual_slo and observed["quality"]["passed"]
    elif diagnostic == "native-allocation":
        settings = binding["request"]["diagnostics"]["nativeAllocation"]
        need(n["seconds"] == settings["seconds"] and n["concurrency"] in settings["concurrency"] and n["route"] in settings["routes"]
             and n["workloadId"] == "primary", "native_profile_contract", "Separate profile duration/concurrency/route/workload differs")
        profile = allocation_profile(field(raw, "nativeAllocationProfile"), binding["allocator_profiler"]["sha256"], settings["tableCapacity"])
        need(field(field(raw, "nativeAllocationProfile"), "libraryPath") == identity["runtime_root_as_executed"] + binding["allocator_profiler"]["path"],
             "native_profile_loaded_path", "Actually profiled allocator library differs from the executed runtime")
        need(n["measurement"]["nativeAllocationProfile"] == field(raw, "nativeAllocationProfile"), "native_profile_wrapper_mismatch", "Wrapper substituted different profiler raw counters")
        if n["route"] == "direct_cpp":
            need(field(raw, "nativeAllocationCalls") == profile["deltas"]["operations"]["successfulAllocations"]
                 and field(raw, "nativeFreeCalls") == profile["deltas"]["operations"]["freeCalls"]
                 and field(raw, "nativeHeapPeakBytes") is None,
                 "native_profile_envelope_mismatch", "Native reported allocation/free/peak differs from actual allocator endpoints")
        else:
            need(field(raw, "timingComparisonEligible") is False, "managed_profile_timing_claim", "Managed profiled process claimed timing comparison credit")
        actual_slo, eligible = measured["normalized"]["counts"]["failed"] == 0, False
        need(n["sloPassed"] is actual_slo and measured["normalized"]["fullCaseCoverage"], "native_profile_workload_failure", "Separate profile has unaccounted failure or incomplete case coverage")
    else:
        eligible = False
        need(measured["normalized"]["counts"]["succeeded"] == binding["request"]["diagnostics"]["queryPlans"]["operations"] and measured["normalized"]["counts"]["failed"] == 0,
             "query_diagnostic_work_count", "Query-plan diagnostic did not execute its declared complete business cycle")
    return dict(common, raw_measurement=measured, client=client, owners=owners, native_allocation_profile=profile,
        resources=observed, recomputed_business_slo_passed=actual_slo, recorded_slo_passed=n["sloPassed"], timing_observation_eligible=eligible,
        comparison_status="diagnostic_only" if diagnostic else ("eligible_preflight_observation" if eligible else "retained_failed_slo_or_environment_observation"))


def verified_long_cells(records, campaign, binding):
    settings, result, raw_seen, intervals = binding["request"]["measurement"], {}, set(), []
    target = {provider + "/" + route for provider in PROVIDERS for route in ROUTES}
    need(set(settings["order"]) == target and len(settings["order"]) == 8, "long_matrix_contract", "Frozen request does not describe four routes by two providers")
    for record in records:
        provider, name = record["step"].split("/", 1)
        if not name.startswith("long-"):
            continue
        route = name.removeprefix("long-")
        cell = provider + "/" + route
        need(cell in target and record["identity"]["route"] == route and cell not in result, "long_cell_identity", "Repeated/foreign long matrix cell")
        need(record["requested_seconds"] >= 1800 and record["requested_seconds"] == settings["secondsPerSegment"], "long_duration_contract", "Long wrapper has fewer than 1800 requested seconds")
        measured = record["raw_measurement"]["normalized"]
        need(measured["offeredSeconds"] is not None and abs(measured["offeredSeconds"] - record["requested_seconds"]) <= 1e-8
             and measured["elapsedThroughDrainSeconds"] >= 1800, "long_actual_duration", "Raw actual offered/through-drain interval is not a full continuous 1800-second run")
        need(record["diagnostic"] is None and record["timing_observation_eligible"] and measured["fullCaseCoverage"]
             and record["workload_id"] == "primary" and record["workload_case_ids"] == binding["request"]["workload"]["cases"],
             "long_workload_or_slo", "Long observation is instrumented, incomplete, wrong mix, or fails common SLO/environment criteria")
        need(all(item["succeeded"] > 0 for item in record["raw_measurement"]["by_case"])
             and {item["name"] for item in record["raw_measurement"]["by_case"]} == set(binding["request"]["workload"]["cases"]),
             "long_successful_case_coverage", "Every selected long-run case needs an actual successful result, not only attempted starts")
        need(record["rate"] == campaign["fixedRates"].get(provider) and record["rate"] > 0
             and record["concurrency"] == settings["concurrency"] and record["construction_mode"] == settings["constructionMode"],
             "long_common_load", "Long observation differs from the frozen same-provider sustainable workload")
        need(record["raw_sha256"] not in raw_seen, "long_raw_reused", "One raw measurement cannot satisfy two long cells")
        raw_seen.add(record["raw_sha256"])
        intervals.append((measured["steadyStartMonotonicNs"], measured["drainedMonotonicNs"], cell))
        result[cell] = {"raw_path": record["raw_path"], "raw_sha256": record["raw_sha256"], "actual_offered_seconds": measured["offeredSeconds"]}
    for previous, following in zip(sorted(intervals), sorted(intervals)[1:]):
        need(previous[1] <= following[0], "long_measurements_overlap", "Long matrix cells used the shared counting resources concurrently")
    need([item[2] for item in sorted(intervals)] == settings["order"][:len(intervals)], "long_chronological_order", "Actual long measurement order differs from the frozen request prefix")
    claimed = campaign.get("qualifiedLongSegments", [])
    need(isinstance(claimed, list) and len(claimed) == len(set(claimed)) and set(claimed) <= set(result),
         "unbacked_long_claim", "Controller claimed long cells without independently validated actual raw segments")
    return {"verified_cells": result, "count": len(result), "matrix_complete": set(result) == target,
        "scope": "raw measurement/identity/elapsed/load/SLO/environment matrix only; remaining task review, companions, attribution and release gates are separate"}


def verify(archive, bundle, binding, expected_run_id, expected_run_attempt=None):
    inventory = archive.verify_all()
    campaign, environment = archive.json("campaign.json"), archive.json("environment.json")
    need(campaign.get("task") == "DB-Perf-01" and campaign.get("schemaVersion") == 1, "campaign_schema", "Unknown campaign schema")
    identity = campaign["environmentIdentity"]
    need(identity.get("githubRunId") == str(expected_run_id) and (expected_run_attempt is None or identity.get("githubRunAttempt") == str(expected_run_attempt)),
         "campaign_run_identity", "Actual measurement run/attempt differs from caller's explicit archive binding")
    need(identity == environment["identity"] and isinstance(identity.get("bootId", {}).get("value"), str) and bool(identity["bootId"]["value"])
         and identity.get("runnerEnvironment") == "github-hosted", "campaign_environment_identity", "Actual observations lack one consistent dedicated hosted-VM boot identity")
    need(campaign["requestSha256"] == binding["request_sha256"] and campaign["bundleManifestSha256"] == bundle.manifest_sha256,
         "campaign_runtime_identity", "Observation archive belongs to another request/runtime bundle")
    need(identity["imageOS"] == bundle.manifest["preparationImage"]["ImageOS"] and identity["imageVersion"] == bundle.manifest["preparationImage"]["ImageVersion"],
         "prepare_measure_image", "Actual prepare/measurement VM images differ")
    for key in ("clientCpus", "serverCpus", "observerCpus"):
        need(environment[key] == binding["request"]["admission"][key], "environment_affinity_contract", "Admitted CPU partition differs")
    quota(environment["limits"])
    dependencies = archive.json("runtime-dependencies.json")
    archive.require_member_hash("runtime-dependencies.json", campaign["runtimeDependenciesSha256"])
    for path, info in dependencies["images"].items():
        need(path in bundle.files and bundle.files[path]["sha256"] == info["sha256"] and "not found" not in info["ldd"],
             "runtime_dependency_image", "Actual measurement-VM ELF dependency observation identifies another bundled image")
    records, refs, raw_hashes = [], set(), set()
    for key, reference in campaign["completedSteps"].items():
        need(reference["path"] not in refs, "step_reference_reused", "One normalized result cannot satisfy multiple completed steps")
        refs.add(reference["path"])
        cell = read_cell(archive, key, reference)
        need(cell["normalized"]["rawSha256"] not in raw_hashes, "raw_measurement_reused", "One timed raw result was reused by multiple completed steps")
        raw_hashes.add(cell["normalized"]["rawSha256"])
        records.append(analyze_cell(cell, binding, campaign))
    prerequisites = campaign_prerequisites(archive, campaign, environment, binding, records)
    longs = verified_long_cells(records, campaign, binding)
    unselected_wrappers = sorted(name for name in archive.entries if name.startswith("runs/") and name.endswith("/normalized.json") and name not in refs)
    selected_raw_paths = {item["raw_path"] for item in records}
    unselected_raw = sorted(name for name in archive.entries if name.startswith("runs/") and name.endswith("/result.json") and name not in selected_raw_paths)
    return {"schema_version": 1, "task": "DB-Perf-01", "engine": "active35-sticky-oracle-independent-raw", "status": "verified_observations_partial_task",
        "engine_sources": schema_fingerprint(), "observation_archive": inventory, "runtime_archive": bundle.summary,
        "source_binding": {key: binding[key] for key in ("harness_commit", "schema_commit", "request_sha256", "producer_sources", "controller_sources", "packages", "applications")},
        "environment_identity": identity, "campaign_state_as_recorded": campaign["state"],
        "observation_count": len(records), "ordinary_slo_failure_observations_retained": sum(not item["recomputed_business_slo_passed"] for item in records if item["diagnostic"] is None),
        "long_measurements": longs, "prerequisites": prerequisites, "records": records,
        "unselected_attempt_evidence": {"normalized_paths": unselected_wrappers, "raw_paths": unselected_raw,
            "scope": "archive inventory retained; unselected attempts need separate retry/failure review and are not silently granted comparison credit"},
        "unproven_or_separate_scopes": [
            "Complete eight long segments are required before a final four-column comparison; preflight values retain their original run/VM identities.",
            "Framework-only managed bytes are unproven in mixed end-to-end records; no empty-driver subtraction or rounded-zero claim.",
            "GC generation fields are true source-bound deltas; cumulative generation endpoints were not serialized, so endpoint subtraction cannot be repeated.",
            "Client primary CPU is application cumulative user/system seconds. /proc own+reaped ticks are diagnostic; remote SC_CLK_TCK is not serialized.",
            "PostgreSQL primary sampled CPU is owned-cgroup cumulative microseconds over complete interior acquisition intervals, including exited children; unsampled edges are not extrapolated.",
            "Native pre-measurement warm-owner idle and post-drain closed-owner idle have distinct scopes; actual elapsed for the latter was not serialized.",
            "Actual taskset command/source binding is checked; per-thread Cpus_allowed_list was not serialized.",
            "Compiler/static native input archive closure and third-party ORM nupkg payload closure remain separate input reviews; build-reported identities are retained.",
            "Query-plan semantic interpretation, independent review/stopping justification, public merge/preview/NuGet/AOT delivery and allocation-path coverage are separate task gates."
        ]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime-zip", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--package-repository")
    parser.add_argument("--observations-zip")
    parser.add_argument("--observations-sha256")
    parser.add_argument("--expected-run-id")
    parser.add_argument("--expected-run-attempt")
    parser.add_argument("--require-eight-long", action="store_true")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    paths = [args.runtime_zip] + ([args.observations_zip] if args.observations_zip else [])
    initial_stats = {path: stat_identity(path) for path in paths}
    code = 0
    try:
        bundle = RuntimeBundle(args.runtime_zip, args.runtime_sha256)
        binding = bind_runtime(bundle, args.repository, args.package_repository)
        if args.observations_zip:
            need(args.observations_sha256 and args.expected_run_id, "observation_cli_binding", "An observation ZIP requires its expected digest and actual run ID")
            archive = EvidenceZip(args.observations_zip, args.observations_sha256)
            result = verify(archive, bundle, binding, args.expected_run_id, args.expected_run_attempt)
            if args.require_eight_long and not result["long_measurements"]["matrix_complete"]:
                result.update(status="unproven_incomplete_long_matrix")
                code = 2
        else:
            need(not args.require_eight_long, "long_missing_observations", "A runtime alone cannot establish measurement credit", unproven=True)
            result = {"schema_version": 1, "task": "DB-Perf-01", "status": "runtime_bytes_verified_no_measurements", "engine_sources": schema_fingerprint(),
                      "runtime_archive": bundle.summary, "binding": {key: value for key, value in binding.items() if key not in ("workloads", "request")}, "long_segments_credited": 0}
        final_stats = {path: stat_identity(path) for path in paths}
        need(initial_stats == final_stats, "input_changed_during_verification", "Input archive stat identity changed during read-only verification")
        need(sha_file(args.runtime_zip) == args.runtime_sha256 and (not args.observations_zip or sha_file(args.observations_zip) == args.observations_sha256),
             "input_bytes_changed_during_verification", "Input archive bytes changed between full verification and the final hash check")
        result["input_stat_identities"] = final_stats
    except (EvidenceError, KeyError, TypeError, ValueError, OSError) as error:
        code = 2
        result = {"schema_version": 1, "task": "DB-Perf-01", "status": "unproven" if getattr(error, "unproven", False) else "rejected",
                  "error": {"code": getattr(error, "code", type(error).__name__), "message": str(error)}, "engine_sources": schema_fingerprint(), "long_segments_credited": 0}
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2, allow_nan=False) + "\n")
    print(json.dumps({"status": result["status"], "output": str(output), "sha256": sha_file(output),
                      "observations": result.get("observation_count"), "long_cells": result.get("long_measurements", {}).get("count", 0), "error": result.get("error")}, allow_nan=False))
    return code


if __name__ == "__main__":
    raise SystemExit(main())
