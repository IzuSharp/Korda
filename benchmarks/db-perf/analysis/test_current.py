#!/usr/bin/env python3
"""Actual-archive positive checks plus explicitly synthetic negative controls."""

from __future__ import annotations

import argparse
import copy
import json
from pathlib import Path

from archive_inputs import EvidenceZip, RuntimeBundle
from identity_binding import bind_runtime, bind_cell, PublicSource
from raw_metrics import measurement, client_metrics, compare_measurement
from resource_metrics import resources
from strict_io import EvidenceError, strict_json, sha_file, field
from verify_current import read_cell, analyze_cell, verified_long_cells, verify, schema_fingerprint
from campaign_admission import campaign_prerequisites


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("runtime-zip", "runtime-sha256", "observations-zip", "observations-sha256", "repository", "expected-run-id", "output"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--package-repository")
    parser.add_argument("--review-delta-only", action="store_true", help="Run only the three concrete review-002 regressions after actual archive verification")
    args = parser.parse_args()
    archive = EvidenceZip(args.observations_zip, args.observations_sha256)
    bundle = RuntimeBundle(args.runtime_zip, args.runtime_sha256)
    binding = bind_runtime(bundle, args.repository, args.package_repository)
    campaign = archive.json("campaign.json")
    baseline = verify(archive, bundle, binding, args.expected_run_id)
    cells = {key: read_cell(archive, key, ref) for key, ref in campaign["completedSteps"].items()}
    # Select by semantic shape, not a hand-picked count/elapsed result.
    managed = next(cell for cell in cells.values() if cell["normalized"]["route"] == "korda_before" and cell["normalized"]["rate"] is not None)
    native = next(cell for cell in cells.values() if cell["normalized"]["route"] == "direct_cpp" and cell["normalized"]["rate"] is not None)
    failed = next(cell for cell in cells.values() if cell["normalized"]["diagnostic"] is None and not cell["normalized"]["sloPassed"])
    results = []

    def reject(name, action):
        try:
            action()
        except (EvidenceError, KeyError, TypeError, ValueError) as error:
            results.append({"name": name, "passed": True, "rejection_code": getattr(error, "code", type(error).__name__)})
        else:
            results.append({"name": name, "passed": False, "rejection_code": None})

    def mutate_cell(name, mutation, original=managed):
        cell = copy.deepcopy(original)
        mutation(cell)
        reject(name, lambda: analyze_cell(cell, binding, campaign))

    def raw_update(name, values, original=managed):
        mutate_cell(name, lambda cell: cell["raw"].update(values), original)

    def extra_planner_setting(cell):
        cell["owner"]["argv"] += ["-c", "enable_seqscan=off"]
        cell["owner"]["launcherArgv"] += ["-c", "enable_seqscan=off"]
    mutate_cell("uncontracted_postgresql_planner_setting", extra_planner_setting)
    profiles = [cell for cell in cells.values() if cell["normalized"]["diagnostic"] == "native-allocation" and cell["normalized"]["route"] == "korda_after"]
    recoveries = [cell for cell in cells.values() if cell["normalized"]["diagnostic"] == "same-process-recovery" and cell["normalized"]["route"] == "korda_after"]
    if profiles and recoveries:
        def absolute_profile_offset(cell):
            profile = cell["raw"]["NativeAllocationProfile"]
            for label in ("before", "after", "afterDrain"):
                for key in ("live_requested_bytes", "live_usable_bytes", "peak_requested_bytes", "peak_usable_bytes"):
                    profile[label][key] += 1
            profile["trackedRequestedHeapPeakBytes"] += 1
            profile["trackedUsableHeapPeakBytes"] += 1
            cell["normalized"]["measurement"]["nativeAllocationProfile"] = copy.deepcopy(profile)
        mutate_cell("profile_absolute_live_bytes_offset_at_all_boundaries", absolute_profile_offset, profiles[0])
        def erase_korda_owners(cell):
            raw = cell["raw"]
            keys = ("Sources", "Operations", "Cursors", "Buffers", "Owners")
            for owners in [raw["WarmOwners"], raw["Drained"], *[phase[name] for phase in raw["Phases"] for name in ("BeforeOwners", "AfterOwners")]]:
                for key in keys:
                    owners[key] = None
            for owners in (cell["normalized"]["measurement"]["warmOwners"], cell["normalized"]["measurement"]["finalOwners"]):
                for key in keys:
                    owners[key[0].lower() + key[1:]] = None
        mutate_cell("korda_recovery_owner_visibility_erased", erase_korda_owners, recoveries[0])
    if args.review_delta_only:
        report = {"schema_version": 1, "task": "DB-Perf-01", "scope": "three review-002 synthetic contradictions, source revision 003; not performance measurements",
            "engine_sources": schema_fingerprint(), "test_source_sha256": sha_file(__file__), "input_archive_sha256": archive.sha256,
            "runtime_archive_sha256": bundle.sha256, "actual_positive_observations": baseline["observation_count"],
            "actual_positive_long_segments": baseline["long_measurements"]["count"], "controls": results,
            "all_passed": len(results) == 3 and all(item["passed"] for item in results)}
        output = Path(args.output)
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(report, indent=2, allow_nan=False) + "\n")
        print(json.dumps({"all_passed": report["all_passed"], "controls": len(results), "observations": baseline["observation_count"],
            "long_cells": baseline["long_measurements"]["count"], "output": str(output), "sha256": sha_file(output)}))
        return 0 if report["all_passed"] else 2

    reject("strict_duplicate_json", lambda: strict_json('{"x":1,"x":2}'))
    reject("strict_nan_json", lambda: strict_json('{"x":NaN}'))
    reject("strict_overflow_json", lambda: strict_json('{"x":1e309}'))
    raw_update("sticky_lifetime_failure_cannot_dilute", {"OracleFailures": 1, "FirstOracleFailure": {"case": "warmup"}})
    raw_update("sticky_lifetime_first_failure_required", {"OracleFailures": 0, "FirstOracleFailure": {"case": "old"}})
    raw_update("sticky_interval_reversed", {"OracleFailuresBefore": 1, "OracleFailuresAfter": 0})
    mutate_cell("sticky_missing_total", lambda c: c["raw"].pop("OracleFailures"))
    mutate_cell("sticky_missing_first", lambda c: c["raw"].pop("FirstOracleFailure"))
    mutate_cell("nested_oracle_interval_differs", lambda c: c["raw"]["OfferedLoad"].update(OracleFailuresAfter=1))
    mutate_cell("enclosing_start_counter_changed", lambda c: c["raw"].update(Started=c["raw"]["Started"] + 1))
    mutate_cell("arrivals_dropped", lambda c: c["raw"]["OfferedLoad"].update(Attempted=c["raw"]["OfferedLoad"]["Attempted"] - 1))
    mutate_cell("terminal_missing", lambda c: c["raw"]["OfferedLoad"].update(Completed=c["raw"]["OfferedLoad"]["Completed"] - 1))
    mutate_cell("failure_hidden_in_slo", lambda c: c["normalized"].update(sloPassed=True), failed)
    mutate_cell("failure_class_count_not_failed_total", lambda c: c["raw"]["OfferedLoad"]["ErrorClasses"].update({"invented-failure": 1}))
    mutate_cell("failure_class_negative", lambda c: c["raw"]["OfferedLoad"]["ErrorClasses"].update({"invalid-negative": -1}))
    mutate_cell("failure_text_without_failed_count", lambda c: (c["raw"].update(Errors=["lost failure"]), c["raw"]["OfferedLoad"].update(Errors=["lost failure"])))
    mutate_cell("histogram_population_changed", lambda c: c["raw"]["OfferedLoad"]["SuccessServiceHistogram"].__setitem__(0, 1))
    mutate_cell("histogram_negative_bin", lambda c: c["raw"]["OfferedLoad"]["SuccessScheduledHistogram"].__setitem__(0, -1))
    mutate_cell("histogram_missing_bins", lambda c: c["raw"]["OfferedLoad"]["SuccessScheduledHistogram"].pop())
    mutate_cell("planned_series_omission", lambda c: c["raw"]["OfferedLoad"]["Seconds"][0].update(Planned=0))
    mutate_cell("case_counter_rows_differ", lambda c: c["raw"]["Cases"][0].update(Rows=c["raw"]["Cases"][0]["Rows"] + 1))
    mutate_cell("case_counter_wrong_case", lambda c: c["raw"]["Cases"][0].update(Name="old-36-case"))
    mutate_cell("case_oracle_digest_differs", lambda c: c["raw"]["ExpectedResults"]["numeric"].update(Digest=1))
    mutate_cell("normalized_quantile_changed", lambda c: c["normalized"]["measurement"].update(p99MicrosecondsUpper=1))
    mutate_cell("normalized_monotonic_boundary_changed", lambda c: c["normalized"]["measurement"].update(steadyStartMonotonicNs=c["normalized"]["measurement"]["steadyStartMonotonicNs"] + 100000000))
    mutate_cell("raw_elapsed_changed", lambda c: c["raw"].update(EffectiveSeconds=1800))
    mutate_cell("raw_offered_timer_shortened", lambda c: c["raw"]["OfferedLoad"].update(DeadlineTicks=c["raw"]["OfferedLoad"]["StartedTicks"] + 1))
    mutate_cell("allocation_total_not_endpoint_delta", lambda c: c["raw"].update(AllocatedBytes=c["raw"]["AllocatedBytes"] + 1))
    mutate_cell("allocation_endpoint_reversed", lambda c: c["raw"].update(AllocatedAfter=c["raw"]["AllocatedBefore"] - 1))
    mutate_cell("allocation_offered_endpoint_outside", lambda c: c["raw"]["OfferedLoad"].update(AllocatedAtOfferedEnd=c["raw"]["AllocatedAfter"] + 1))
    mutate_cell("allocation_division_changed", lambda c: c["raw"].update(BytesPerRow=0))
    raw_update("gc_delta_negative", {"Gen1": -1})
    mutate_cell("cpu_endpoint_decreased", lambda c: c["raw"]["ProcessAfter"].update(UserCpuSeconds=c["raw"]["ProcessBefore"]["UserCpuSeconds"] - 0.000001))
    raw_update("cpu_reported_delta_changed", {"UserCpuSeconds": 0})
    mutate_cell("managed_idle_cpu_changed", lambda c: c["raw"]["Idle"].update(UserCpuSeconds=1000))
    raw_update("korda_owner_still_live", {"OwnersReturnedToZero": False})
    mutate_cell("native_owner_still_live", lambda c: c["raw"].update(drainedConnections=1), native)
    raw_update("raw_provider_swapped", {"Provider": "Sqlite"})
    raw_update("raw_route_swapped", {"Route": "direct-native-cpp"})
    raw_update("raw_elf_swapped", {"ExecutableSha256": binding["applications"]["sqlite/korda_before"]["sha256"]})
    raw_update("raw_loaded_library_swapped", {"LoadedNativeSha256": "f" * 64})
    mutate_cell("native_prepare_policy_changed", lambda c: c["raw"].update(physicalPreparation="per-operation"), native)
    mutate_cell("native_protocol_policy_changed", lambda c: c["raw"].update(protocolConfirmation="product"), native)
    mutate_cell("native_submission_counter_changed", lambda c: c["raw"]["protocol"].update(protocolSubmissions=c["raw"]["protocol"]["protocolSubmissions"] + 1), native)
    mutate_cell("native_confirmation_counter_negative", lambda c: c["raw"]["protocol"].update(confirmationSyncs=-1), native)
    mutate_cell("native_sql_legs_changed", lambda c: c["raw"]["protocol"].update(sqlExecutions=c["raw"]["protocol"]["sqlExecutions"] + 1, protocolSubmissions=c["raw"]["protocol"]["protocolSubmissions"] + 1), native)
    mutate_cell("raw_source_contract_swapped", lambda c: c["normalized"].update(workloadSha256="f" * 64))
    mutate_cell("client_affinity_swapped", lambda c: c["command"]["argv"].__setitem__(2, "2,3"))
    def change_cost(c, raw_key, option, value):
        c["raw"][raw_key] = value
        for argv in (c["command"]["argv"], c["plan"]["commands"][0]["argv"]):
            indices = [i for i, item in enumerate(argv) if item == option]
            argv[indices[-1] + 1] = str(value)
    for raw_key, option, value in (("BatchRows", "--batch-rows", 128), ("RequestedRows", "--rows", 64),
                                  ("Tenants", "--tenants", 16), ("OrdersPerTenant", "--orders-per-tenant", 2048),
                                  ("WarmupPerWorker", "--warmup", 1)):
        mutate_cell("contract_cost_" + raw_key, lambda c, key=raw_key, opt=option, val=value: change_cost(c, key, opt, val))
    def change_queue(c):
        replacement = c["raw"]["OfferedLoad"]["QueueCapacity"] * 2
        c["raw"]["OfferedLoad"]["QueueCapacity"] = replacement
        for argv in (c["command"]["argv"], c["plan"]["commands"][0]["argv"]):
            argv[argv.index("--queue-capacity") + 1] = str(replacement)
    mutate_cell("contract_queue_raw_and_actual_command_changed", change_queue)
    mutate_cell("actual_seed_workload_identity_changed", lambda c: c["seed"].update(workloadId="foreign", workloadSha256="f" * 64))
    mutate_cell("runner_controller_swapped", lambda c: c["runner"]["controllerSources"].update({"run.py": "f" * 64}))
    mutate_cell("runner_vm_boot_swapped", lambda c: c["runner"]["environmentIdentity"]["bootId"].update(value="another-vm"))
    mutate_cell("runner_bundle_swapped", lambda c: c["runner"].update(bundleManifestSha256="f" * 64))
    def server_cpu_swap(c):
        for name in ("argv", "launcherArgv"):
            index = c["owner"][name].index("taskset")
            c["owner"][name][index + 2] = "0,1"
    mutate_cell("actual_server_uses_client_cpus", server_cpu_swap)
    def server_image_swap(c):
        for name in ("argv", "launcherArgv"):
            index = c["owner"][name].index("taskset")
            c["owner"][name][index + 3] = "/other-postgres-install/bin/postgres"
    mutate_cell("actual_server_binary_swapped", server_image_swap)
    mutate_cell("command_starts_after_measurement", lambda c: c["command"].update(startedMonotonicNs=c["raw"]["SteadyStartMonotonicNs"] + 1000000000))
    mutate_cell("observer_reports_error", lambda c: c["command"].update(observerErrors=["incomplete sample"]))
    mutate_cell("resource_sum_tampered", lambda c: c["samples"][2]["clientTotals"].update(sampledRssBytes=1))
    mutate_cell("resource_pid_reuse", lambda c: c["samples"][2]["client"][0].update(startTicks=1))
    mutate_cell("resource_acquisition_reversed", lambda c: c["samples"][2].update(acquiredThroughMonotonicNs=c["samples"][2]["monotonicNs"] - 1))
    mutate_cell("resource_series_clock_shift", lambda c: c["samples"][2].update(monotonicNs=c["samples"][2]["monotonicNs"] + 1000000000))
    mutate_cell("cgroup_inode_changed", lambda c: c["samples"][2]["serverCpuGroup"]["deviceInode"].__setitem__(1, 1))
    mutate_cell("cgroup_middle_counter_decreased", lambda c: c["samples"][2]["serverCpuGroup"]["cpu"].update(user_usec=0))
    mutate_cell("cgroup_membership_missing", lambda c: c["samples"][2]["serverCpuGroup"].update(membersVerified=False))
    mutate_cell("cgroup_owner_not_member", lambda c: c["samples"][2]["serverCpuGroup"].update(members=[]))
    def quota_omit(c, index):
        for sample in c["samples"]:
            sample["guestQuota"]["ancestors"].pop(index)
        c["command"]["lastSample"] = copy.deepcopy(c["samples"][-1])
    mutate_cell("quota_middle_ancestor_omitted", lambda c: quota_omit(c, 1))
    mutate_cell("quota_root_ancestor_omitted", lambda c: quota_omit(c, -1))
    def quota_membership_omit(c):
        for sample in c["samples"]:
            sample["guestQuota"].pop("membership")
        c["command"]["lastSample"] = copy.deepcopy(c["samples"][-1])
    mutate_cell("quota_membership_missing", quota_membership_omit)
    mutate_cell("cgroup_final_sample_failed", lambda c: c["owner"].update(finalSampleError="unreadable cpu.stat"))
    mutate_cell("cgroup_not_removed", lambda c: c["owner"]["serverCpuGroup"].update(removed=False))
    mutate_cell("cgroup_final_member_remaining", lambda c: c["owner"]["serverCpuGroup"]["emptyAfterOwnerJoin"].update(members=[{"pid": 123}]))
    mutate_cell("cgroup_final_delta_tampered", lambda c: c["owner"]["serverCpuGroup"]["clientsThroughShutdownCpu"].update(userCpuSeconds=0))
    mutate_cell("normalized_cgroup_cpu_tampered", lambda c: c["normalized"]["resources"]["server"].update(userCpuSeconds=0))
    mutate_cell("normalized_sample_count_tampered", lambda c: c["normalized"]["resources"].update(samples=1))
    mutate_cell("native_managed_bytes_fabricated", lambda c: c["raw"].update(managedAllocatedBytes=0), native)
    first_ref = next(iter(campaign["completedSteps"].values()))
    reject("raw_archive_reference_hash_changed", lambda: archive.require_member_hash(first_ref["path"], "f" * 64))
    reject("actual_elf_bytes_hash_changed", lambda: bundle.require_elf(binding["applications"]["postgresql/korda_before"]["executable"], "f" * 64))
    reject("public_source_hash_changed", lambda: PublicSource(args.repository, binding["harness_commit"]).require("benchmarks/db-perf/run.py", "f" * 64))
    altered_campaign = copy.deepcopy(campaign)
    altered_campaign["qualifiedLongSegments"] = [provider + "/" + route for provider in ("postgresql", "sqlite") for route in ("direct_cpp", "korda_before", "korda_after", "independent_csharp_orm")]
    reject("eight_claimed_cells_without_raw_long", lambda: verified_long_cells(baseline["records"], altered_campaign, binding))
    pretend = copy.deepcopy(next(item for item in baseline["records"] if item["rate"] is not None and item["timing_observation_eligible"]))
    pretend["step"] = pretend["identity"]["provider"] + "/long-" + pretend["identity"]["route"]
    pretend["requested_seconds"] = 1800
    reject("short_actual_interval_wrapped_as_1800", lambda: verified_long_cells([pretend], campaign, binding))
    reject("archive_rebound_to_foreign_run", lambda: verify(archive, bundle, binding, "45461269267" if args.expected_run_id != "45461269267" else "45452087158"))
    diagnostic_cells = [cell for cell in cells.values() if cell["normalized"]["diagnostic"] == "same-process-recovery"]
    if diagnostic_cells:
        managed_recovery = next(cell for cell in diagnostic_cells if cell["normalized"]["route"] == "korda_after")
        native_recovery = next(cell for cell in diagnostic_cells if cell["normalized"]["route"] == "direct_cpp")
        mutate_cell("recovery_sticky_oracle_failure", lambda c: c["raw"].update(OracleFailures=1), managed_recovery)
        mutate_cell("recovery_pool_rebuilt", lambda c: c["raw"].update(PoolRebuiltBetweenPhases=True), managed_recovery)
        mutate_cell("recovery_phase_owner_not_warm", lambda c: c["raw"]["Phases"][1]["AfterOwners"].update(Operations=1), managed_recovery)
        mutate_cell("recovery_final_owner_live", lambda c: c["raw"]["Drained"].update(Connections=1), managed_recovery)
        mutate_cell("recovery_phase_allocated_bytes_inconsistent", lambda c: c["raw"]["Phases"][1].update(AllocatedBytes=0), managed_recovery)
        mutate_cell("recovery_phase_error_class_hidden", lambda c: c["raw"]["Phases"][1]["OfferedLoad"]["ErrorClasses"].update(unknown=1), managed_recovery)
        mutate_cell("recovery_phase_queue_budget_changed", lambda c: c["raw"]["Phases"][1]["OfferedLoad"].update(QueueCapacity=32), managed_recovery)
        mutate_cell("recovery_phase_duration_changed", lambda c: c["raw"]["Phases"][2]["OfferedLoad"].update(OfferedSeconds=3), managed_recovery)
        mutate_cell("recovery_normalized_total_lost_rejections", lambda c: c["normalized"]["measurement"]["counts"].update(rejected=0), managed_recovery)
        mutate_cell("recovery_native_connection_replaced", lambda c: c["raw"]["phases"][2]["measurement"]["connectionIds"].__setitem__(0, 1), native_recovery)
        mutate_cell("recovery_native_protocol_negative", lambda c: c["raw"]["phases"][1]["measurement"]["protocol"].update(confirmationSyncs=-1), native_recovery)
        mutate_cell("recovery_native_cumulative_cpu_reset", lambda c: c["raw"]["phases"][2]["measurement"]["processBefore"].update(userCpuSeconds=0), native_recovery)
        profiles = [cell for cell in cells.values() if cell["normalized"]["diagnostic"] == "native-allocation"]
        profile = next(cell for cell in profiles if cell["normalized"]["route"] == "korda_after")
        mutate_cell("profile_coverage_error_hidden", lambda c: c["raw"]["NativeAllocationProfile"]["after"].update(unknown_frees=1), profile)
        mutate_cell("profile_cumulative_bytes_inconsistent", lambda c: c["raw"]["NativeAllocationProfile"]["after"].update(allocated_requested_bytes=0), profile)
        mutate_cell("profile_derived_count_inconsistent", lambda c: c["raw"]["NativeAllocationProfile"].update(allocationCalls=0), profile)
        mutate_cell("profile_library_changed", lambda c: c["raw"]["NativeAllocationProfile"].update(librarySha256="f" * 64), profile)
        mutate_cell("profile_capacity_unbounded", lambda c: c["raw"]["NativeAllocationProfile"]["after"].update(table_capacity=2 ** 28), profile)
        mutate_cell("profile_failed_verdict_hidden", lambda c: c["raw"]["NativeAllocationProfile"].update(passed=False), profile)
        mutate_cell("profile_full_heap_scope_fabricated", lambda c: c["raw"]["NativeAllocationProfile"].update(fullAddressSpaceHeapPeakProven=True), profile)
        mutate_cell("profile_timing_credit_fabricated", lambda c: c["normalized"].update(timingComparisonEligible=True), profile)
        heavy = next(cell for cell in cells.values() if cell["normalized"]["workloadId"] != "primary" and cell["normalized"]["route"] == "korda_before")
        mutate_cell("heavy_seed_replaced_with_primary", lambda c: c["seed"].update(workloadId="primary", workloadSha256=binding["workloads"]["primary"]["sha256"]), heavy)
    environment = archive.json("environment.json")
    def mutate_campaign(name, mutation):
        candidate = copy.deepcopy(campaign)
        mutation(candidate)
        reject(name, lambda: campaign_prerequisites(archive, candidate, environment, binding, baseline["records"]))
    if campaign.get("fixedRates"):
        mutate_campaign("fixed_rate_changed_after_results", lambda c: c["fixedRates"].update(postgresql=c["fixedRates"]["postgresql"] + 1))
        mutate_campaign("noise_cv_rounded_to_zero", lambda c: c["calibration"]["postgresql"]["noise"].update(coefficientOfVariation=0))
        mutate_campaign("noise_raw_rate_list_changed", lambda c: c["calibration"]["postgresql"]["noise"]["beforeSuccessOpsPerSecond"].__setitem__(0, 1))
        mutate_campaign("capacity_maximum_fabricated", lambda c: c["calibration"]["postgresql"]["capacities"]["korda_before"].update(maximumObservedSuccessfulOpsPerSecondUnderServiceSlo=1))
        mutate_campaign("scheduled_slo_failure_hidden", lambda c: c["calibration"]["postgresql"]["scheduledCapacities"]["korda_before"]["points"][0].update(sloPassed=not c["calibration"]["postgresql"]["scheduledCapacities"]["korda_before"]["points"][0]["sloPassed"]))
        mutate_campaign("refinement_selection_input_missing", lambda c: c["calibration"]["postgresql"]["scheduledCapacities"]["korda_before"]["refinementSelections"][0]["selection"]["selectionInputs"].pop())
        mutate_campaign("upper_failure_bracket_changed", lambda c: c["calibration"]["postgresql"]["scheduledCapacities"]["korda_before"]["observedBrackets"][0].update(upper=None))
        missing = [item for item in baseline["records"] if item["step"] != "postgresql/noise-0-korda_before"]
        reject("qualified_environment_missing_noise_process", lambda: campaign_prerequisites(archive, campaign, environment, binding, missing))
        missing_profile = [item for item in baseline["records"] if item["step"] != "postgresql/native-allocation-4-korda_after"]
        reject("qualified_environment_missing_native_profile", lambda: campaign_prerequisites(archive, campaign, environment, binding, missing_profile))
    wrong_idle = copy.deepcopy(environment)
    wrong_idle["idle"]["busyFraction"] = 0
    reject("idle_admission_delta_arithmetic_changed", lambda: campaign_prerequisites(archive, campaign, wrong_idle, binding, baseline["records"]))
    wrong_partition = copy.deepcopy(environment)
    wrong_partition["serverCpus"] = wrong_partition["clientCpus"]
    reject("admission_cpu_partitions_overlap", lambda: campaign_prerequisites(archive, campaign, wrong_partition, binding, baseline["records"]))
    rejected_before = len(results)
    # A missing cumulative-GC endpoint is intentionally NOT a negative case:
    # this producer records true nonnegative collection deltas, and no false
    # total-allocation attribution is made from those deltas.
    report = {"schema_version": 1, "task": "DB-Perf-01", "scope": "synthetic mutations of immutable real preflight inputs; no mutation is performance evidence",
        "engine_sources": schema_fingerprint(), "test_source_sha256": sha_file(__file__),
        "actual_positive_observations": baseline["observation_count"], "actual_positive_long_segments": baseline["long_measurements"]["count"],
        "ordinary_slo_failures_retained": baseline["ordinary_slo_failure_observations_retained"],
        "controls": results, "negative_controls": rejected_before, "all_passed": all(item["passed"] for item in results),
        "gc_delta_only_schema_accepted": True, "input_archive_sha256": archive.sha256, "runtime_archive_sha256": bundle.sha256}
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2, allow_nan=False) + "\n")
    print(json.dumps({"all_passed": report["all_passed"], "negative_controls": len(results), "positive_observations": baseline["observation_count"],
        "failed_controls": [item["name"] for item in results if not item["passed"]], "output": str(output), "sha256": sha_file(output)}))
    return 0 if report["all_passed"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
