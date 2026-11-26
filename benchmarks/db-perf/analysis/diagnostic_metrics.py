"""Separate recovery/allocator diagnostics; never ordinary timing credit."""

from __future__ import annotations

from raw_metrics import (oracle, measurement, compare_measurement, client_metrics,
                         process_snapshot, cpu_difference, number_equal, timestamp, native_protocol)
from strict_io import field, finite, natural, need
from resource_metrics import compare_object


ALLOCATION_COUNTERS = ("mallocCalls", "callocCalls", "reallocCalls", "alignedCalls", "freeCalls", "nullFrees",
    "successfulAllocations", "failedAllocations", "deallocations", "allocatedRequestedBytes", "releasedRequestedBytes",
    "allocatedUsableBytes", "releasedUsableBytes")
COVERAGE_FAILURES = ("unknownFrees", "unknownReallocs", "droppedAllocations", "duplicateAddresses", "initializationFailures")


def allocation_profile(profile, library_sha256, capacity):
    need(isinstance(profile, dict), "native_profile_missing", "Separate native allocation lane lacks its actual profiler receipt")
    need(field(profile, "librarySha256") == library_sha256 and field(profile, "timingComparisonEligible") is False
         and field(profile, "fullAddressSpaceHeapPeakProven") is False, "native_profile_identity_scope", "Profiler library/scope differs from the separate instrumented contract")
    need(field(profile, "observedAllocationCoverageComplete") is True and field(profile, "requestedByteConservation") is True,
         "native_profile_claim_rejected", "Producer did not establish observed allocator coverage/conservation")
    natural(capacity, "profiler capacity", 256)
    need(capacity <= 4194304 and capacity & (capacity - 1) == 0, "native_profile_capacity", "Profiler table is not finitely bounded to its reviewed power-of-two capacity")
    snapshots = {}
    for label in ("before", "after", "afterDrain"):
        snapshot = field(profile, label)
        need(field(snapshot, "version") == 1 and field(snapshot, "tableCapacity") == capacity and field(snapshot, "tableBytes") == capacity * 24,
             "native_profile_layout", "Actual profiler ABI/capacity/table overhead differs")
        for key in COVERAGE_FAILURES:
            need(natural(field(snapshot, key), "profiler " + key) == 0, "native_profile_coverage_failure", "Raw allocator coverage failure cannot be hidden by a pass flag: " + key)
        for key in ALLOCATION_COUNTERS:
            natural(field(snapshot, key), "profiler " + key)
        live = natural(field(snapshot, "liveAllocations"), "live tracked allocations")
        need(live <= capacity and field(snapshot, "successfulAllocations") == field(snapshot, "deallocations") + live,
             "native_profile_live_count", "Successful/deallocated/live allocator counts do not conserve")
        for unit in ("Requested", "Usable"):
            need(field(snapshot, "allocated" + unit + "Bytes") - field(snapshot, "released" + unit + "Bytes") == natural(field(snapshot, "live" + unit + "Bytes"), "live tracked " + unit + " bytes"),
                 "native_profile_absolute_byte_conservation", "A raw allocator snapshot's lifetime allocated/released/live bytes do not conserve")
        for peak, current in (("peakLiveAllocations", "liveAllocations"), ("peakRequestedBytes", "liveRequestedBytes"), ("peakUsableBytes", "liveUsableBytes")):
            need(natural(field(snapshot, peak), peak) >= natural(field(snapshot, current), current), "native_profile_peak_below_live", "Tracked heap peak is below its actual live value")
        snapshots[label] = snapshot
    differences = {}
    for label, left, right in (("operations", snapshots["before"], snapshots["after"]), ("close", snapshots["after"], snapshots["afterDrain"])):
        delta = {}
        for key in ALLOCATION_COUNTERS:
            need(field(right, key) >= field(left, key), "native_profile_counter_decreased", "Cumulative allocator counter decreased")
            delta[key] = field(right, key) - field(left, key)
        for unit in ("Requested", "Usable"):
            need(field(left, "live" + unit + "Bytes") + delta["allocated" + unit + "Bytes"] == field(right, "live" + unit + "Bytes") + delta["released" + unit + "Bytes"],
                 "native_profile_byte_conservation", "Actual tracked allocator byte conservation failed")
        need(field(left, "liveAllocations") + delta["successfulAllocations"] == field(right, "liveAllocations") + delta["deallocations"],
             "native_profile_count_conservation", "Actual tracked allocator event conservation failed")
        for key in ("peakLiveAllocations", "peakRequestedBytes", "peakUsableBytes"):
            need(field(right, key) >= field(left, key), "native_profile_peak_reset", "Tracked allocator peak was reset inside the measurement/close boundary")
        differences[label] = delta
    mapping = {"allocationCalls": "successfulAllocations", "deallocationEvents": "deallocations", "allocatedRequestedBytes": "allocatedRequestedBytes",
        "releasedRequestedBytes": "releasedRequestedBytes", "allocatedUsableBytes": "allocatedUsableBytes", "releasedUsableBytes": "releasedUsableBytes"}
    for derived, raw in mapping.items():
        need(field(profile, derived) == differences["operations"][raw], "native_profile_derived_count", "Reported native allocation/free quantity differs from cumulative endpoints")
    for unit in ("Requested", "Usable"):
        need(field(profile, "tracked" + unit + "HeapPeakBytes") == field(snapshots["after"], "peak" + unit + "Bytes"),
             "native_profile_reported_peak", "Reported tracked heap peak differs from actual after snapshot")
    for name in ("usableByteConservation", "allocationCountConservation", "cumulativeCountersMonotonic", "libraryFileUnchanged", "passed", "preloadedGlobalAllocatorIdentityVerified"):
        value = field(profile, name, optional=True)
        need(value is None or value is True, "native_profile_extra_verdict", "A reported native profiler coverage/identity check failed")
    return {"library_sha256": library_sha256, "snapshots": snapshots, "deltas": differences,
        "table_capacity": capacity, "profiler_reserved_table_bytes": capacity * 24,
        "timing_comparison_eligible": False, "full_address_space_heap_peak_proven": False,
        "scope": "separate instrumented process; tracked public glibc allocation calls; excludes private mmap, managed object heap, allocator metadata, hidden calls and transient internal realloc overlap",
        "drain_scope": "after application disposal; retained runtime/harness/report storage is included and is not automatically a leak or hidden-provider-retirement proof"}


def owner_counts(value, native, *, opaque=False):
    keys = ("cancellationOwners", "connections", "pgResults", "sqliteStatements") if native else ("sources", "connections", "operations", "cursors", "buffers", "owners")
    result = {}
    for key in keys:
        count = field(value, key)
        if opaque and key != "connections":
            need(count is None, "recovery_orm_owner_visibility", "This frozen ORM producer cannot observe internal provider ownership")
            result[key] = None
        else:
            result[key] = natural(count, "recovery owner " + key)
    if not native:
        for key in ("sqlExecutions", "protocolSubmissions"):
            if opaque:
                need(field(value, key) is None, "recovery_orm_activity_visibility", "This frozen ORM producer cannot observe internal SQL/protocol counters")
            else:
                natural(field(value, key), "Korda recovery " + key)
    natural(result["connections"], "recovery connections")
    return result


def closed_idle(value, native):
    if value is None:
        return None
    before, after = process_snapshot(field(value, "before"), "final idle before"), process_snapshot(field(value, "after"), "final idle after")
    cpu = cpu_difference(before, after, "final closed idle")
    for key, delta in cpu.items():
        number_equal(field(value, key), delta, "final idle " + key)
    duration, requested = finite(field(value, "observedSeconds"), "final idle duration", 0), finite(field(value, "requestedSeconds"), "final idle requested", 0)
    need(duration >= requested - 0.01, "recovery_idle_short", "Final idle ended before its requested observation")
    wall = (timestamp(field(value, "finishedUtc")) - timestamp(field(value, "startedUtc"))).total_seconds()
    need(abs(wall - duration) < 0.05, "recovery_idle_clock", "Final idle UTC and elapsed duration differ")
    if native:
        start, end = natural(field(value, "startedMonotonicNs"), "idle monotonic start"), natural(field(value, "finishedMonotonicNs"), "idle monotonic end")
        need(end >= start, "recovery_idle_monotonic", "Final idle monotonic interval is reversed")
        number_equal((end - start) / 1e9, duration, "final native idle elapsed")
    return {"cpu": cpu, "before": before, "after": after, "observed_seconds": duration, "scope": field(value, "scope")}


def recovery(raw, specification, manifest, settings, fixed_rate):
    lifetime = oracle(raw, interval=False)
    native = field(raw, "route") == "direct-native-cpp"
    opaque = field(raw, "route") == "dapper-aot-recovery"
    phases = field(raw, "phases")
    need(isinstance(phases, list) and [field(item, "name") for item in phases] == ["reference", "overload", "recovery"],
         "recovery_phase_sequence", "Recovery must preserve three ordered phases in the same process")
    if native:
        need(field(raw, "sameConnectionsThroughout") is True and field(raw, "sameWorkerObjectsThroughout") is True and field(raw, "phaseIdleSeconds") == 0,
             "recovery_native_rebuilt", "Native recovery rebuilt owners/workers or inserted uncontracted idle")
        phase_raw = [field(item, "measurement") for item in phases]
        warm = owner_counts(field(phase_raw[0], "beforeOwners"), True)
        connection_ids = field(phase_raw[0], "connectionIds")
        need(isinstance(connection_ids, list) and len(connection_ids) == field(raw, "concurrency") and len(set(connection_ids)) == len(connection_ids)
             and all(type(value) is int and value > 0 for value in connection_ids), "recovery_connection_ids", "Retained native connection identities are absent or duplicated")
        final = owner_counts(field(raw, "finalOwners"), True)
        need(field(raw, "ownersReturnedToZero") is True, "recovery_native_owners", "Final native owners did not drain")
    else:
        need(field(raw, "sameSourceInstance") is True and field(raw, "sameWorkerAndSessionInstances") is True and field(raw, "poolRebuiltBetweenPhases") is False,
             "recovery_managed_rebuilt", "Managed recovery rebuilt its source/session/worker/pool")
        phase_raw, connection_ids = phases, None
        warm, final = owner_counts(field(raw, "warmOwners"), False, opaque=opaque), owner_counts(field(raw, "drained"), False, opaque=opaque)
        need(field(raw, "trackedResourcesReturnedToZero") is True, "recovery_managed_owners", "Final managed observed owners did not drain")
        need(field(raw, "actualCaseIds") == specification["workloadCaseIds"], "recovery_case_order", "Recovery selected another case order")
    need(warm["connections"] == field(raw, "concurrency"), "recovery_warm_concurrency", "Warm connection count differs from concurrency")
    transient = ("cancellationOwners", "pgResults") if native else ("operations", "cursors", "buffers")
    need(all(warm[key] in (None, 0) for key in transient), "recovery_warm_active_owner", "Warm baseline includes unfinished owners")
    need(all(final[key] == (None if value is None else 0) for key, value in warm.items()), "recovery_final_owner_visibility", "Final owners did not retire or observability changed")
    results, totals, last_end = [], {key: 0 for key in ("planned", "attempted", "sent", "started", "completed", "succeeded", "failed", "rejected", "retries", "rows")}, 0
    for index, value in enumerate(phase_raw):
        name = ("reference", "overload", "recovery")[index]
        need(owner_counts(field(value, "beforeOwners"), native, opaque=opaque) == warm == owner_counts(field(value, "afterOwners"), native, opaque=opaque),
             "recovery_phase_owner_changed", "A recovery phase did not return to the same warm ownership state")
        if native:
            need(field(value, "connectionIds") == connection_ids and field(value, "sameWarmOwners") is True, "recovery_phase_connection_changed", "Native recovery changed its retained connection identities")
        else:
            need(field(value, "queueFullyDrained") is True and field(value, "returnedToWarmOwnerBaseline") is True,
                 "recovery_managed_drain_claim", "Managed phase did not fully drain admitted work to warm ownership")
            natural(field(value, "preparationAllocatedBytes"), "recovery scheduler preparation bytes")
            natural(field(value, "pendingThreadPoolWorkAfterDrain"), "recovery pending ThreadPool work")
        phase_spec = dict(specification, seconds=settings["overloadSeconds"] if index == 1 else settings["seconds"], rate=settings["overloadRate"] if index == 1 else fixed_rate)
        measured = measurement(value, phase_spec, manifest, context=raw if not native else None)
        need(measured["boundary"]["start_ns"] >= last_end, "recovery_phase_overlap", "Recovery phases overlap or changed monotonic clock")
        last_end = measured["boundary"]["drained_ns"]
        if index != 1:
            need(measured["normalized"]["fullCaseCoverage"], "recovery_case_coverage", "Reference/recovery did not execute all selected cases")
        need(field(value, "nativeAllocationProfile", optional=True) is None, "recovery_allocator_instrumented", "Recovery cannot use allocator-instrumented timing")
        for key in totals:
            totals[key] += measured["normalized"]["counts"][key]
        client = client_metrics(value, measured, context=raw if not native else None)
        activity = None
        if native:
            client["idle"] = None
            client["memory"]["retained_after_phase"] = client["memory"].pop("after_drain")
            client["memory"].pop("retained_after_idle")
            client["phase_memory_scope"] = "same warm owners retained after phase completion; no per-phase final owner disposal or intentional idle"
            activity = native_protocol(value, measured, manifest)
        else:
            client["managed"]["scheduler_preparation_bytes_separate"] = field(value, "preparationAllocatedBytes")
            activity = {}
            for key in ("sqlExecutions", "protocolSubmissions"):
                first, last = field(field(value, "beforeOwners"), key), field(field(value, "afterOwners"), key)
                need((first is None) == (last is None), "recovery_counter_visibility_changed", "Provider activity observability changed across a phase")
                if first is not None:
                    natural(first, key + " before")
                    need(natural(last, key + " after") >= first, "recovery_activity_decreased", "Provider cumulative activity decreased")
                activity[key] = {"before": first, "after": last, "delta": None if first is None else last - first}
        if results:
            cpu_difference(results[-1]["client"]["memory"]["after"], client["memory"]["before"], "between recovery phases")
            if not native:
                need(client["managed"]["allocated_before"] >= results[-1]["client"]["managed"]["allocated_after"],
                     "recovery_allocation_cumulative_reset", "Whole-process cumulative allocation reset between retained-owner phases")
        results.append({"name": name, "measurement": measured, "client": client,
            "activity": activity,
            "raw_phase_cpu": {"before": field(value, "processBefore"), "after": field(value, "processAfter")}})
    accepted = [item["measurement"]["normalized"]["counts"]["failed"] == 0
                and item["measurement"]["normalized"]["counts"]["rejected"] == 0
                and item["measurement"]["normalized"]["counts"]["succeeded"] == item["measurement"]["normalized"]["counts"]["planned"] for item in results]
    saturation = results[1]["measurement"]["normalized"]["counts"]["rejected"] > 0
    passed = accepted[0] and accepted[2] and saturation and totals["failed"] == 0
    need(field(raw, "passed") is passed, "recovery_pass_claim", "Consumer recovery verdict differs from actual admitted/failed/rejected/retired counts")
    if not native:
        need(field(raw, "referenceAccepted") is accepted[0] and field(raw, "recoveryAccepted") is accepted[2]
             and field(raw, "saturationObserved") is saturation and field(raw, "allPhasesReturnedToWarmOwnerBaseline") is True,
             "recovery_component_verdict", "Recovery component verdict differs from raw phases")
    normalized = {"counts": totals, "correctnessPassed": True, "oracleFailures": lifetime["total"],
        "phases": [{"name": item["name"], "measurement": item["measurement"]["normalized"]} for item in results],
        "sameSourceConnectionsAndWorkers": True, "ownersReturnedToZero": True, "consumerPassed": passed,
        "warmOwners": warm, "finalOwners": final, "connectionIds": connection_ids, "saturationObserved": saturation}
    # Phase histograms expose scheduled and service latency independently.
    first_p99 = results[0]["measurement"]["histograms"]["offered"]["p99_us_upper"]
    last_p99 = results[2]["measurement"]["histograms"]["offered"]["p99_us_upper"]
    ratio = last_p99 / first_p99 if first_p99 else None
    reported_ratio = field(raw, "recoveryP99Ratio" if native else "scheduledP99Ratio")
    if ratio is None:
        need(reported_ratio is None, "recovery_ratio_without_reference", "Recovery ratio exists without a successful reference p99")
    else:
        number_equal(reported_ratio, ratio, "recovery reported p99 ratio")
    allocation = None
    if not native:
        first, last = results[0]["client"]["managed"]["allocated_before"], results[-1]["client"]["managed"]["allocated_after"]
        phase_bytes = sum(item["client"]["managed"]["total_bytes"] for item in results)
        allocation = {"first_phase_allocated_before": first, "last_phase_allocated_after": last, "total_between_outer_endpoints": last - first,
            "sum_of_measured_phase_bytes": phase_bytes, "between_phase_bytes": last - first - phase_bytes,
            "scope": "actual cumulative process endpoints; between-phase scheduler/report preparation is kept separate, not subtracted as framework attribution"}
    return {"normalized": normalized, "phases": results, "raw_phase_payloads": phase_raw, "managed_allocation_sequence": allocation,
        "oracle": lifetime, "closed_idle": closed_idle(field(raw, "idle", optional=True), native),
        "scope": "same source/connection/worker objects; each accepted phase is completely drained before immediate next phase preparation; phase threads/scheduler state may restart; no continuously carried cross-phase queue is asserted"}
