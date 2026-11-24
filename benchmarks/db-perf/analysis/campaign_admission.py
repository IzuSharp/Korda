"""Recompute preflight dependencies without treating raw long cells as delivery acceptance."""

from __future__ import annotations

import math
import statistics

from identity_binding import PROVIDERS, ROUTES
from raw_metrics import compare_measurement, number_equal, timestamp
from resource_metrics import CPU_KEYS, compare_object, host_cpu, quota, tree
from strict_io import field, natural, need


def environment_admission(environment, request, records):
    settings = request["admission"]
    available = environment["availableCpus"]
    need(isinstance(available, list) and available and all(type(cpu) is int and cpu >= 0 for cpu in available)
         and len(set(available)) == len(available), "admission_available_cpus", "Admitted available CPU set is invalid")
    groups = [environment[key] for key in ("clientCpus", "serverCpus", "observerCpus")]
    need(all(group and len(group) == len(set(group)) for group in groups) and len(set(sum(groups, []))) == sum(map(len, groups))
         and set(sum(groups, [])) <= set(available), "admission_cpu_partition", "Client/server/observer counting resources overlap or are unavailable")
    need(environment["uname"][0] == "Linux" and environment["uname"][4] == "x86_64", "admission_platform", "Actual admitted VM is not Linux x64")
    limits = quota(environment["limits"])
    compare_object({"maximumCpuCapacity": limits["capacity"], "quotaSignature": limits["signature"], "localThrottleCounters": limits["throttles"]},
                   environment["quotaAssessment"], "admission quota assessment")
    need(limits["capacity"] is None or limits["capacity"] >= len(available), "admission_quota_capacity", "Visible CPU capacity is constrained by an ancestor quota")
    idle = environment["idle"]
    deltas = {key: natural(idle["delta"][key], "idle host CPU " + key) for key in CPU_KEYS}
    # These are the actual producer-recorded deltas. Do not fabricate two
    # cumulative endpoints, which were not serialized for this idle interval.
    derived_idle = host_cpu({key: 0 for key in CPU_KEYS}, deltas)
    compare_object(derived_idle, idle, "admission recorded idle delta arithmetic")
    accepted = idle["stealFraction"] <= settings["maximumStealFraction"] and idle["busyFraction"] <= settings["maximumIdleBusyFraction"]
    need(environment["idleAccepted"] is accepted, "admission_idle_verdict", "Idle admission verdict differs from actual reported tick deltas")
    samples = environment["idleSamples"]
    need(isinstance(samples, list) and len(samples) >= 2, "admission_idle_samples", "Initial idle raw series is absent")
    previous = None
    for sample in samples:
        start, finish = natural(sample["monotonicNs"], "idle sample start"), natural(sample["acquiredThroughMonotonicNs"], "idle sample end")
        need(start <= finish and (previous is None or previous["acquiredThroughMonotonicNs"] <= start), "admission_idle_sample_clock", "Initial idle sample intervals overlap or reverse")
        need(not sample["client"] and not sample["server"] and sample.get("serverCpuGroup") is None, "admission_idle_process_present", "Admission idle contains a measured client/server")
        for side in ("client", "server"):
            compare_object(tree(sample[side], None, side), sample[side + "Totals"], "idle process tree")
        need(quota(sample["guestQuota"])["signature"] == limits["signature"], "admission_idle_quota_changed", "Initial idle CPU quota differs from admission")
        if previous is not None:
            need(timestamp(sample["utc"]) >= timestamp(previous["utc"]), "admission_idle_utc_order", "Initial idle UTC reversed")
            host_cpu(previous["hostCpuTicks"], sample["hostCpuTicks"])
        previous = sample
    if records:
        need(samples[-1]["acquiredThroughMonotonicNs"] <= min(item["actual_command"]["startedMonotonicNs"] for item in records),
             "admission_after_measurement", "A business measurement began before actual initial idle observations completed")
    return {"idle_passed": accepted, "idle_raw_delta": deltas, "idle_recomputed": derived_idle,
        "configured_idle_seconds": settings["idleSeconds"], "idle_actual_endpoint_elapsed_seconds": None,
        "idle_interior_sample_seconds": (samples[-1]["acquiredThroughMonotonicNs"] - samples[0]["monotonicNs"]) / 1e9,
        "idle_sample_count": len(samples), "idle_boundary_scope": "source-bound reported host CPU delta; separate interior sample acquisitions; full idle elapsed/cumulative endpoints were not serialized",
        "quota": limits, "available_cpus": available, "physical_host_exclusivity_asserted": False}


def point(record):
    return {"name": record["step"].split("/", 1)[1], "concurrency": record["concurrency"], "rate": record["rate"],
        "sloPassed": record["recomputed_business_slo_passed"], "rawPath": record["raw_path"], "rawSha256": record["raw_sha256"]}


def selection(points, concurrency, relative_limit):
    ordered = sorted((item for item in points if item["concurrency"] == concurrency), key=lambda item: item["rate"])
    names = ("name", "rate", "sloPassed", "rawPath", "rawSha256")
    result = {"concurrency": concurrency, "selectionInputs": [{key: item[key] for key in names} for item in ordered], "execute": False}
    passing = [item for item in ordered if item["sloPassed"]]
    def bound(item):
        return {key: item[key] for key in ("name", "rate", "rawSha256")}
    if not passing:
        if not ordered:
            return dict(result, status="no_observed_rate", lower=None, upper=None, relativeGap=None)
        upper = ordered[0]
        proposed = math.floor(upper["rate"] / 2 * 1000) / 1000
        result.update(lower=None, upper=bound(upper), relativeGap=None, proposedRate=proposed)
        allowed = 0 < proposed < upper["rate"] and all(item["rate"] != proposed for item in ordered)
        return dict(result, status="next_bounded_halving" if allowed else "rate_resolution_or_existing_observation_limit", execute=allowed)
    lower = max(passing, key=lambda item: item["rate"])
    result.update(lower=bound(lower), nonMonotonicSloObserved=any(not item["sloPassed"] and item["rate"] < lower["rate"] for item in ordered))
    failures = [item for item in ordered if not item["sloPassed"] and item["rate"] > lower["rate"]]
    if not failures:
        return dict(result, status="upper_failure_unobserved", upper=None, relativeGap=None)
    upper = min(failures, key=lambda item: item["rate"])
    gap = (upper["rate"] - lower["rate"]) / lower["rate"]
    proposed = math.floor((upper["rate"] + lower["rate"]) / 2 * 1000) / 1000
    result.update(upper=bound(upper), relativeGap=gap, proposedRate=proposed)
    if gap <= relative_limit:
        return dict(result, status="within_requested_bracket_precision")
    allowed = lower["rate"] < proposed < upper["rate"] and all(item["rate"] != proposed for item in ordered)
    return dict(result, status="next_bounded_midpoint" if allowed else "rate_resolution_or_existing_observation_limit", execute=allowed)


def verify_point(record, declared, *, offered):
    need(declared["concurrency"] == record["concurrency"] and declared["sloPassed"] is record["recomputed_business_slo_passed"],
         "calibration_point_identity", "Calibration summary changed a raw point's concurrency/SLO")
    if offered:
        number_equal(declared["rate"], record["rate"], "capacity point offered rate")
    compare_measurement(record["raw_measurement"], declared["measurement"])


def provider_calibration(provider, indexed, campaign, request):
    settings = request["measurement"]
    stored = campaign.get("calibration", {}).get(provider, {})
    def get(name):
        return indexed.get(provider + "/" + name)
    def contract(record, seconds, concurrency, rate=None, expected_route=None):
        need(record is not None, "calibration_raw_missing", "Declared calibration prerequisite has no actual raw observation")
        need(record["diagnostic"] is None and record["workload_id"] == "primary" and record["workload_case_ids"] == request["workload"]["cases"]
             and record["construction_mode"] == settings["constructionMode"] and record["concurrency"] == concurrency
             and record["requested_seconds"] == seconds and record["rate"] == rate,
             "calibration_workload_contract", "Calibration point changes the fixed workload/mode/duration/concurrency/rate")
        need(record["identity"]["route"] == expected_route, "calibration_route_contract", "Named calibration point belongs to another actual route")
    noise_names = [f"noise-{repeat}-{route}" for repeat in range(settings["noiseReplicates"])
                   for route in (list(ROUTES) if repeat % 2 == 0 else list(reversed(ROUTES)))]
    noise_records = [get(name) for name in noise_names]
    noise = {"status": "pending", "expected_processes": len(noise_names), "observed_processes": sum(item is not None for item in noise_records)}
    if all(item is not None for item in noise_records):
        for item in noise_records:
            contract(item, settings["calibrationSeconds"], 1, expected_route=item["step"].split("-")[-1])
        need([item["step"].split("/", 1)[1] for item in sorted(noise_records, key=lambda item: item["actual_command"]["startedMonotonicNs"])] == noise_names,
             "noise_process_interleaving", "Independent noise processes were reordered instead of using frozen alternating route order")
        rates = [get(f"noise-{repeat}-korda_before")["raw_measurement"]["normalized"]["successOpsPerSecond"] for repeat in range(settings["noiseReplicates"])]
        coefficient = statistics.stdev(rates) / statistics.mean(rates)
        expected = {"beforeSuccessOpsPerSecond": rates, "coefficientOfVariation": coefficient, "allowedCoefficientOfVariation": request["admission"]["maximumBeforeCv"]}
        if "noise" in stored:
            compare_object(expected, stored["noise"], "independent-process noise")
        noise = dict(expected, status="verified", passed=coefficient <= request["admission"]["maximumBeforeCv"],
                     process_raw_hashes=[item["raw_sha256"] for item in noise_records])
    else:
        need("noise" not in stored, "noise_summary_without_processes", "Noise summary exists without all independent process records")
    result = {"noise": noise, "capacity_status": "pending", "scheduled_status": "pending", "fixed_rate": None}
    if "nativeLayers" in stored:
        need(len(stored["nativeLayers"]) == len(request["nativeLayers"]), "native_layer_summary_count", "Native attribution layer summary is incomplete")
        for index, declared in enumerate(stored["nativeLayers"]):
            item = get("native-layer-" + str(index))
            contract(item, settings["calibrationSeconds"], settings["concurrency"], expected_route="direct_cpp")
            need(declared["nativeContract"] == item["identity"]["native_contract"] == request["nativeLayers"][index],
                 "native_layer_summary_contract", "Layer summary switched its actual native preparation/protocol policy")
            compare_measurement(item["raw_measurement"], declared["measurement"])
    if "capacities" not in stored or "scheduledCapacities" not in stored:
        need(provider not in campaign.get("fixedRates", {}), "fixed_rate_without_calibration", "Frozen rate lacks the raw-bound capacity derivation")
        return result
    need(set(stored["capacities"]) == set(stored["scheduledCapacities"]) == set(ROUTES), "capacity_routes", "Capacity summary omitted a comparison route")
    capacities, scheduled = {}, {}
    for route in ROUTES:
        closed = [get(f"capacity-{route}-{count}") for count in settings["capacityConcurrency"]]
        declared = stored["capacities"][route]
        need(len(declared["points"]) == len(closed), "capacity_summary_count", "Closed-loop summary omits or duplicates a raw ramp point")
        for item, count, value in zip(closed, settings["capacityConcurrency"], declared["points"]):
            contract(item, settings["capacitySeconds"], count, expected_route=route)
            verify_point(item, value, offered=False)
        valid = [item for item in closed if item["recomputed_business_slo_passed"]]
        need(valid, "capacity_no_passing_point", "No closed-loop point meets the common actual SLO")
        best = max(valid, key=lambda item: item["raw_measurement"]["normalized"]["successOpsPerSecond"])
        maximum = best["raw_measurement"]["normalized"]["successOpsPerSecond"]
        number_equal(declared["maximumObservedSuccessfulOpsPerSecondUnderServiceSlo"], maximum, "closed-loop service maximum")
        capacities[route] = {"maximum_observed_success_ops_per_second": maximum, "best_concurrency": best["concurrency"],
            "scope": "tested closed-loop points under service SLO; not maximum offered-load capacity"}
        candidate_concurrencies = sorted({settings["concurrency"], best["concurrency"]})
        selected, refinement_records, brackets = [], [], []
        for count in candidate_concurrencies:
            basis = next(item["raw_measurement"]["normalized"]["successOpsPerSecond"] for item in closed if item["concurrency"] == count)
            for index, fraction in enumerate(settings["scheduledRateFractions"]):
                item = get(f"scheduled-capacity-{route}-{count}-{index}")
                contract(item, settings["scheduledCapacitySeconds"], count, math.floor(basis * fraction * 1000) / 1000, route)
                selected.append(item)
            for iteration in range(settings["capacityRefinementSteps"]):
                proposed = selection([point(item) for item in selected], count, settings["capacityRefinementMaximumRelativeGap"])
                if not proposed["execute"]:
                    break
                item = get(f"scheduled-refine-{route}-{count}-{iteration}")
                contract(item, settings["capacityRefinementSeconds"], count, proposed["proposedRate"], route)
                refinement_records.append({"iteration": iteration, "selection": proposed,
                    "result": {key: point(item)[key] for key in ("name", "rate", "sloPassed", "rawPath", "rawSha256")}})
                selected.append(item)
            bracket = selection([point(item) for item in selected], count, settings["capacityRefinementMaximumRelativeGap"])
            bracket["iterationLimitReached"], bracket["execute"] = bracket["execute"], False
            brackets.append(bracket)
        claimed = stored["scheduledCapacities"][route]
        actual_names = {record["step"].split("/", 1)[1] for record in indexed.values() if record["step"].startswith(provider + "/scheduled-capacity-" + route + "-")
                        or record["step"].startswith(provider + "/scheduled-refine-" + route + "-")}
        need(actual_names == {item["step"].split("/", 1)[1] for item in selected}, "scheduled_raw_point_omitted", "Capacity summary/selection omitted an actual observed scheduled point")
        need(len(claimed["points"]) == len(selected), "scheduled_summary_count", "Scheduled summary omits or duplicates a raw capacity point")
        for item, declared_point in zip(selected, claimed["points"]):
            verify_point(item, declared_point, offered=True)
        need(len(claimed["refinementSelections"]) == len(refinement_records) and len(claimed["observedBrackets"]) == len(brackets),
             "scheduled_selection_count", "Refinement inputs/brackets are incomplete")
        for expected, actual in zip(refinement_records, claimed["refinementSelections"]):
            compare_object(expected, actual, "raw-derived refinement")
        for expected, actual in zip(brackets, claimed["observedBrackets"]):
            compare_object(expected, actual, "raw-derived bracket")
        need(claimed["refinementWindowSeconds"] == settings["capacityRefinementSeconds"], "refinement_window_changed", "Capacity refinement used another window")
        valid = [item for item in selected if item["recomputed_business_slo_passed"]]
        frozen = [item for item in valid if item["concurrency"] == settings["concurrency"]]
        need(frozen, "no_sustainable_frozen_concurrency", "No scheduled point satisfies common SLO at fixed long concurrency")
        maximum = max(item["raw_measurement"]["normalized"]["successOpsPerSecond"] for item in valid)
        maximum_rate = max(item["rate"] for item in frozen)
        number_equal(claimed["maximumObservedSuccessfulOpsPerSecondUnderScheduledSlo"], maximum, "tested scheduled maximum success throughput")
        number_equal(claimed["maximumQualifiedOfferedRateAtFrozenConcurrency"], maximum_rate, "tested maximum offered rate at frozen concurrency")
        scheduled[route] = {"maximum_observed_success_ops_per_second": maximum, "maximum_qualified_offered_rate_at_frozen_concurrency": maximum_rate,
            "point_count": len(selected), "ordinary_slo_failures_retained": sum(not item["recomputed_business_slo_passed"] for item in selected),
            "raw_hashes": [item["raw_sha256"] for item in selected], "observed_brackets": brackets,
            "scope": "best tested finite grid/refinement observation; monotonic capacity and global maximum are unproven"}
    rates = {route: scheduled[route]["maximum_qualified_offered_rate_at_frozen_concurrency"] for route in ("korda_before", "direct_cpp", "independent_csharp_orm")}
    fixed = math.floor(min(rates.values()) * settings["sustainableFraction"] * 1000) / 1000
    need(fixed > 0, "no_positive_fixed_rate", "Calibration has no positive sustainable offered rate")
    number_equal(campaign["fixedRates"][provider], fixed, "independently derived fixed rate")
    validation = []
    for route in ROUTES:
        item = get("fixed-load-" + route)
        if item is not None:
            contract(item, settings["fixedValidationSeconds"], settings["concurrency"], fixed, route)
            need(item["recomputed_business_slo_passed"], "fixed_validation_slo", "Frozen validation load failed actual common SLO")
            validation.append(item["raw_sha256"])
    result.update(capacity_status="verified", scheduled_status="verified", capacities=capacities, scheduled=scheduled,
        fixed_rate=fixed, fixed_rate_inputs=rates, fixed_validation_raw_hashes=validation, fixed_validation_complete=len(validation) == 4)
    return result


def campaign_prerequisites(archive, campaign, environment, binding, records):
    request, indexed = binding["request"], {item["step"]: item for item in records}
    actual_order = sorted(records, key=lambda item: item["actual_command"]["startedMonotonicNs"])
    seen = set()
    for index, record in enumerate(actual_order):
        command = record["actual_command"]
        identity = (command["clientIdentity"]["pid"], command["clientIdentity"]["startTicks"])
        need(identity not in seen, "measurement_process_reused", "Independent step records reuse one actual process identity")
        seen.add(identity)
        if index:
            need(actual_order[index - 1]["actual_command"]["finishedMonotonicNs"] <= command["startedMonotonicNs"],
                 "measurement_processes_overlap", "Independent observations overlapped the same client counting resources")
    admitted = environment_admission(environment, request, records)
    if "environmentAdmissionSha256" in campaign:
        archive.require_member_hash("environment.json", campaign["environmentAdmissionSha256"])
    quota_record = archive.json("quota-observation.json")
    need(quota_record["identity"] == environment["identity"] and quota_record["limits"] == environment["limits"]
         and quota_record["availableCpus"] == environment["availableCpus"], "admission_initial_quota_binding", "Pre-admission actual quota identity differs")
    calibrations = {provider: provider_calibration(provider, indexed, campaign, request) for provider in PROVIDERS}
    obligations = {}
    for provider in PROVIDERS:
        names = []
        names += ["query-plans-" + route for route in ROUTES]
        for mode in request["shortQueries"]["constructionModes"]:
            names += [f"short-projections-{mode}-{route}" for route in ROUTES]
        names += [f"native-layer-{index}" for index in range(len(request["nativeLayers"]))]
        names += ["same-process-recovery-" + route for route in ROUTES]
        names += [f"native-allocation-{count}-{route}" for count in request["diagnostics"]["nativeAllocation"]["concurrency"] for route in request["diagnostics"]["nativeAllocation"]["routes"]]
        names += [f"heavy-{request['heavyWorkload']['id']}-{count}-{route}" for count in request["heavyWorkload"]["concurrency"] for route in ROUTES]
        missing, found = [], []
        for name in names:
            record = indexed.get(provider + "/" + name)
            if record is None:
                missing.append(name)
                continue
            expected_workload, expected_cases = "primary", request["workload"]["cases"]
            expected_mode, expected_rate = request["measurement"]["constructionMode"], None
            if name.startswith("short-projections-"):
                expected_mode = name.split("-")[2]
                expected_cases = request["shortQueries"]["cases"]
                expected_seconds, expected_concurrency, expected_diagnostic = request["shortQueries"]["seconds"], request["shortQueries"]["concurrency"], None
            elif name.startswith("query-plans-"):
                expected_mode, expected_seconds, expected_concurrency, expected_diagnostic = request["diagnostics"]["queryPlans"]["constructionMode"], 0, 1, "query-plan"
            elif name.startswith("heavy-"):
                heavy = request["heavyWorkload"]
                expected_workload, expected_cases, expected_seconds, expected_concurrency, expected_diagnostic = heavy["id"], heavy["cases"], heavy["seconds"], int(name.split("-")[2]), None
            elif name.startswith("native-layer-"):
                expected_seconds, expected_concurrency, expected_diagnostic = request["measurement"]["calibrationSeconds"], request["measurement"]["concurrency"], None
            elif name.startswith("same-process-recovery-"):
                recovery = request["diagnostics"]["sameProcessRecovery"]
                expected_seconds, expected_concurrency, expected_diagnostic = 2 * recovery["seconds"] + recovery["overloadSeconds"], request["measurement"]["concurrency"], "same-process-recovery"
            else:
                expected_seconds, expected_concurrency, expected_diagnostic = request["diagnostics"]["nativeAllocation"]["seconds"], int(name.split("-")[2]), "native-allocation"
            expected_route = "direct_cpp" if name.startswith("native-layer-") else name.split("-")[-1]
            need(record["identity"]["route"] == expected_route and record["workload_id"] == expected_workload and record["workload_case_ids"] == expected_cases
                 and record["construction_mode"] == expected_mode and record["concurrency"] == expected_concurrency and record["requested_seconds"] == expected_seconds
                 and record["diagnostic"] == expected_diagnostic and record["rate"] == expected_rate,
                 "companion_input_contract", "A named companion changed the frozen route, dataset, selected cases, mode, duration, concurrency or diagnostic kind")
            # Ordinary saturation points may fail SLO; correctness companions
            # must preserve every result, while native-layer timing is retained
            # even if its service SLO is poor.
            if not name.startswith("native-layer-"):
                counts = record["raw_measurement"]["normalized"]["counts"]
                need(counts["failed"] == 0 and counts["succeeded"] > 0, "companion_business_failure", "A correctness/profile/recovery/heavy companion has a business failure")
                if record["diagnostic"] in ("same-process-recovery", "native-allocation", "query-plan"):
                    need(record["recomputed_business_slo_passed"], "companion_diagnostic_failed", "A required diagnostic did not pass its actual checks")
                if record["diagnostic"] != "same-process-recovery":
                    need(record["raw_measurement"]["normalized"]["fullCaseCoverage"] and all(item["succeeded"] > 0 for item in record["raw_measurement"]["by_case"]),
                         "companion_successful_case_coverage", "A required companion did not successfully consume every selected case")
            found.append({"step": record["step"], "raw_sha256": record["raw_sha256"]})
        obligations[provider] = {"complete": not missing, "missing": missing, "observed": found}
    timing_environment = all(item["resources"]["quality"]["passed"] for item in records if item["diagnostic"] is None)
    complete = (admitted["idle_passed"] and timing_environment and all(value["noise"].get("passed") is True and value.get("fixed_validation_complete") is True for value in calibrations.values())
                and all(value["complete"] for value in obligations.values()))
    if environment.get("qualified") is True:
        need(complete, "environment_qualified_without_raw_prerequisites", "Qualified environment lacks actual complete noise/calibration/companion dependencies")
    for item in records:
        if item["step"].split("/", 1)[1].startswith("long-"):
            need(complete and environment.get("qualified") is True and timestamp(environment["qualificationUtc"]) <= timestamp(item["actual_command"]["startedUtc"]),
                 "long_without_prior_admission", "Long workload started before complete source-bound raw preflight/admission")
    return {"environment": admitted, "providers": calibrations, "companion_raw_obligations": obligations, "raw_preflight_complete": complete,
        "all_uninstrumented_environment_observations_passed": timing_environment,
        "independent_processes_verified": len(seen),
        "scope": "raw admission/noise/capacity/fixed-rate/companion dependency arithmetic; query-plan semantic review, full-path allocation attribution and public delivery remain separate task gates"}
