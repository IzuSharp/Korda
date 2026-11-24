"""Independent arithmetic for the public sticky-oracle DB-Perf producer schema."""

from __future__ import annotations

from datetime import datetime
from fractions import Fraction
import math
import statistics

from strict_io import EvidenceError, field, finite, natural, need


def timestamp(value):
    need(isinstance(value, str), "timestamp_type", "Timestamp must be a recorded string")
    try:
        result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise EvidenceError("timestamp_format", str(error)) from error
    need(result.tzinfo is not None, "timestamp_timezone", "Timestamp must have a timezone")
    return result


def number_equal(actual, expected, name, absolute=1e-8, relative=1e-10):
    finite(actual, name)
    need(abs(actual - expected) <= max(absolute, abs(expected) * relative),
         "derived_number_mismatch", name + " differs from independently recomputed raw inputs")


def oracle(raw, interval=True):
    total = natural(field(raw, "oracleFailures"), "process-lifetime oracle failures")
    first = field(raw, "firstOracleFailure")
    before = after = None
    if interval:
        before = natural(field(raw, "oracleFailuresBefore"), "oracle failures before")
        after = natural(field(raw, "oracleFailuresAfter"), "oracle failures after")
        need(before <= after <= total, "oracle_counter_order", "Sticky oracle interval exceeds its cumulative lifetime counter")
    need(total == 0 and first is None, "oracle_violation", "A sticky result/state/cleanup violation invalidates this observation independently of the ordinary error-rate SLO")
    return {"before": before, "after": after, "total": total, "first": first}


def histogram(bins, count, name):
    natural(count, name + " population")
    need(isinstance(bins, list) and len(bins) == 8192, "histogram_shape", name + " must retain 8192 raw bins")
    need(all(type(value) is int and value >= 0 for value in bins), "histogram_counter", name + " has a noninteger/negative bin")
    need(sum(bins) == count, "histogram_population", name + " does not account for every member of its population")
    def quantile(percent):
        if not count:
            return None
        rank, cumulative = (count * percent + 99) // 100, 0
        for index, value in enumerate(bins):
            cumulative += value
            if cumulative >= rank:
                return float(Fraction((1 << (index // 128)) * (129 + index % 128), 128000))
        raise AssertionError("Validated histogram lost its population")
    return {"samples": count, "p50_us_upper": quantile(50), "p95_us_upper": quantile(95), "p99_us_upper": quantile(99)}


def latency_summary_check(record, recomputed, label):
    for p in (50, 95, 99):
        actual = field(record, f"p{p}MicrosecondsUpper")
        expected = recomputed[f"p{p}_us_upper"]
        if expected is None:
            need(actual in (0, None), "empty_latency_value", label + " has a nonempty quantile without successes")
        else:
            number_equal(actual, expected, label + f" p{p}")


def error_diagnostics(raw, counts, *, native, managed_phase=False):
    offered = field(raw, "offeredLoad", optional=True)
    detailed = offered if offered is not None else raw
    errors = field(detailed, "errors")
    need(isinstance(errors, list) and all(isinstance(value, str) for value in errors), "error_text_shape", "Original error diagnostics must remain a string array")
    if offered is not None and not managed_phase:
        need(field(raw, "errors") == errors, "error_text_envelope_mismatch", "Enclosing and offered error text differ")
    classes = None
    if native or offered is not None:
        classes = field(detailed, "errorClasses")
        need(isinstance(classes, dict) and all(isinstance(key, str) and key for key in classes), "error_class_shape", "Failure class counts must have explicit names")
        for key, value in classes.items():
            natural(value, "error class " + key)
        need(sum(classes.values()) == counts["failed"], "error_class_conservation", "Failure classifications do not account for every failed operation")
        if native and offered is not None:
            need(field(raw, "errorClasses") == classes, "error_class_envelope_mismatch", "Native enclosing/offered failure classifications differ")
    need(not errors or counts["failed"] > 0, "uncounted_error_diagnostic", "Error text exists without an accounted failed operation")
    need(counts["failed"] == 0 or bool(errors), "failed_error_text_missing", "A failed business operation has no retained original diagnostic")
    return {"failed_operations": counts["failed"], "rejected_arrivals": counts["rejected"], "class_counts": classes,
        "original_error_text": errors,
        "classification_scope": "all failed operations, source-defined class names" if classes is not None else "not serialized for managed closed-loop; only per-worker first error text is available",
        "text_scope": "bounded first error per worker (and producer diagnostic, if any); not a complete exception transcript"}


def measurement(raw, specification, manifest, *, context=None):
    """Return both raw boundary arithmetic and producer-normalized comparisons.

    Primary offered latency and throughput cover accepted requests through drain.
    No unrecorded cutoff-only population is synthesized.
    """
    failures = oracle(raw)
    selected = specification["workloadCaseIds"]
    contracted = {query["name"]: query for query in manifest["queries"]}
    need(isinstance(selected, list) and selected and len(selected) == len(set(selected)) and set(selected) <= set(contracted),
         "unknown_selected_case", "Selected workload cases do not match actual manifest descriptors")
    producer = context if context is not None else raw
    native = field(producer, "route") == "direct-native-cpp"
    managed_phase = context is not None and not native
    need(field(producer if managed_phase else raw, "declaredCaseIds", "actualCaseIds") == selected, "declared_case_order", "Raw case order differs from the frozen selected workload")
    start_ns = natural(field(raw, "steadyStartMonotonicNs"), "raw steady start", 1)
    end_ns = natural(field(raw, "steadyEndMonotonicNs"), "raw steady end", 1)
    need(end_ns > start_ns, "raw_clock_order", "Raw monotonic interval is empty or reversed")
    elapsed = finite(field(raw, "effectiveSeconds"), "effective elapsed", 0)
    need(abs((end_ns - start_ns) / 1e9 - elapsed) <= 0.05, "raw_elapsed_identity", "Raw elapsed and monotonic interval disagree")
    if not managed_phase:
        need(timestamp(field(raw, "finishedUtc")) >= timestamp(field(raw, "startedUtc")), "raw_utc_order", "Raw wall-clock interval is reversed")
    offered = field(raw, "offeredLoad", "offered", optional=True)
    expected_rate = specification.get("rate")
    histograms = {}
    if expected_rate is not None:
        need(isinstance(offered, dict), "offered_missing", "Fixed-rate operation lacks its full offered ledger")
        offered_oracle = oracle(offered)
        need(offered_oracle["before"] == failures["before"] and offered_oracle["after"] == failures["after"],
             "oracle_interval_mismatch", "Offered and enclosing oracle interval counters differ")
        counts = {key: natural(field(offered, key), key) for key in
            ("planned", "attempted", "sent", "started", "completed", "succeeded", "failed", "rejected", "retries", "rows")}
        rate = finite(field(offered, "requestsPerSecond"), "actual offered rate", 0)
        seconds = finite(field(offered, "offeredSeconds"), "actual offered seconds", 0)
        need(rate > 0 and seconds > 0, "empty_offered_workload", "Offered rate/duration must be positive")
        number_equal(rate, expected_rate, "bound offered rate")
        number_equal(seconds, specification["seconds"], "bound offered duration")
        need(counts["planned"] == math.ceil(rate * seconds) == counts["attempted"] == counts["sent"] + counts["rejected"],
             "arrival_conservation", "Planned/attempted/sent/rejected arrivals do not reconcile")
        need(counts["sent"] == counts["started"] == counts["completed"] == counts["succeeded"] + counts["failed"],
             "terminal_conservation", "Accepted operations did not all retire")
        frequency = natural(field(offered, "timerFrequency"), "timer frequency", 1)
        ticks = {key: natural(field(offered, key), key, 1) for key in ("startedTicks", "deadlineTicks", "offeredEndTicks", "drainedTicks")}
        need(ticks["startedTicks"] <= ticks["deadlineTicks"] <= ticks["offeredEndTicks"] <= ticks["drainedTicks"],
             "offered_clock_order", "Offered start/deadline/end/drain clocks are unordered")
        exact = {key: Fraction(value * 10**9, frequency) for key, value in ticks.items()}
        actual_window = (ticks["deadlineTicks"] - ticks["startedTicks"]) / frequency
        need(abs(actual_window - seconds) <= max(1 / frequency, 1e-9), "offered_timer_duration", "Actual timer window differs from the requested fixed interval")
        duration = (ticks["drainedTicks"] - ticks["startedTicks"]) / frequency
        need(abs(duration - elapsed) <= 0.05, "enclosing_drain_duration", "Enclosing elapsed and offered through-drain interval disagree")
        boundary = {"start_ns": int(exact["startedTicks"]), "planned_end_ns": int(exact["deadlineTicks"]),
                    "offered_end_ns": int(exact["offeredEndTicks"]), "drained_ns": int(exact["drainedTicks"])}
        for key, population, label in (("successScheduledHistogram", "succeeded", "offered"),
            ("successServiceHistogram", "succeeded", "service"), ("allCompletedScheduledHistogram", "completed", "all_terminal_offered"),
            ("startDelayHistogram", "started", "start_delay")):
            histograms[label] = histogram(field(offered, key), counts[population], label)
        series = field(offered, "seconds")
        series_mapping = [(key, key) for key in ("planned", "sent", "started", "succeeded", "failed", "rejected", "rows")]
        need((not native or field(offered, "accountingReconciles") is True) and field(offered, "timeSeriesTruncated") is False,
             "offered_producer_integrity", "Producer rejected offered counts or truncated its raw series")
        queue_capacity = natural(field(offered, "queueCapacity"), "queue capacity", 1)
        queue_peak = natural(field(offered, "queuePeak"), "queue peak") if native else None
        need(queue_peak is None or queue_peak <= queue_capacity, "queue_bound", "Observed queue exceeds its declared finite bound")
        queue = {"capacity": queue_capacity, "peak": queue_peak}
        # The managed envelope intentionally reports service latency, while
        # the native envelope reports scheduled latency in offered mode.
        envelope_histogram = histograms["offered" if native else "service"]
        primary = histograms["offered"]
    else:
        need(offered is None, "unexpected_offered_ledger", "Closed-loop specification has an offered-load payload")
        succeeded = natural(field(raw, "succeeded") if native else field(raw, "completed"), "successes")
        failed, started = natural(field(raw, "failed"), "failures"), natural(field(raw, "started"), "started")
        need(started == succeeded + failed, "terminal_conservation", "Closed-loop starts differ from successful/failed terminals")
        counts = {"planned": started, "attempted": started, "sent": started, "started": started,
            "completed": started, "succeeded": succeeded, "failed": failed, "rejected": 0,
            "retries": natural(field(raw, "retries"), "retries"), "rows": natural(field(raw, "rows"), "rows")}
        duration = elapsed
        need(duration >= finite(specification["seconds"], "requested duration", 0) - 0.05, "short_closed_measurement", "Closed-loop run stopped before its requested interval")
        boundary = {"start_ns": start_ns, "planned_end_ns": end_ns, "offered_end_ns": end_ns, "drained_ns": end_ns}
        actual_window, queue = None, None
        primary = histograms["service"] = histogram(field(field(raw, "latencies"), "histogram"), succeeded, "service")
        envelope_histogram = primary
        series = field(raw, "seconds")
        series_mapping = [(key, key) for key in ("started", "succeeded", "failed", "rows")] if native else [("completed", "succeeded"), ("failed", "failed"), ("rows", "rows")]
    need(duration > 0, "empty_measurement_interval", "Actual measurement duration must be positive")
    need(counts["retries"] == 0, "uncontracted_retry", "The frozen workload does not retry individual requests")
    if not managed_phase:
        for key in ("planned", "started", "failed", "retries", "rows"):
            need(natural(field(raw, key), "enclosing " + key) == counts[key], "enclosing_counter_mismatch", "Enclosing and detailed counter differ: " + key)
        need(natural(field(raw, "completed"), "enclosing completed") == counts["completed" if native else "succeeded"],
             "enclosing_completed_semantics", "Completed has the wrong producer-specific meaning")
        need(field(raw, "timeSeriesTruncated") is False, "series_truncated", "Raw time series was truncated")
    if native:
        need(field(raw, "accountingReconciles") is True and field(raw, "succeeded") == counts["succeeded"], "native_accounting", "Native producer rejected its accounting")
        for key in ("attempted", "sent", "rejected"):
            need(natural(field(raw, key), "native " + key) == counts[key], "native_envelope_counter", "Native enclosing arrival count differs")
        need(natural(field(raw, "notStarted"), "native not started") == counts["rejected"], "native_unstarted_accounting", "Native unstarted count differs from retained rejected arrivals")
    elif not managed_phase:
        expected_results = field(raw, "expectedResults")
        need(isinstance(expected_results, dict) and set(selected) <= set(expected_results), "expected_results_missing", "Managed consumer omitted expected case results")
        for name in selected:
            need(field(expected_results[name], "rows") == contracted[name]["expectedRows"] and
                 str(field(expected_results[name], "digest")) == contracted[name]["expectedDigest"],
                 "expected_result_contract", "Consumer oracle inputs differ from frozen workload: " + name)
    need(isinstance(series, list) and series and [field(item, "second") for item in series] == list(range(len(series))),
         "series_sequence", "Raw per-second sequence has holes or duplicates")
    for key, total_key in series_mapping:
        need(sum(natural(field(item, key), "second " + key) for item in series) == counts[total_key],
             "series_conservation", "Per-second counter differs from total: " + key)
    if expected_rate is not None:
        for second, item in enumerate(series):
            left = min(counts["planned"], math.ceil(second * rate))
            right = min(counts["planned"], math.ceil((second + 1) * rate))
            need(field(item, "planned") == right - left, "planned_series_timing", "Planned arrivals were shifted into another second")
    cases = field(raw, "cases", "caseCounts")
    if isinstance(cases, dict):
        cases = [dict(item, name=name) for name, item in cases.items()]
    need(isinstance(cases, list), "case_count_shape", "Actual per-case counters are required")
    case_names = [field(item, "name") for item in cases]
    need(len(case_names) == len(set(case_names)) and set(case_names) <= set(selected), "case_count_identity", "Duplicate/foreign case counter")
    totals, actual_cases, by_case = {key: 0 for key in ("started", "succeeded", "failed", "rows")}, set(), []
    for case in cases:
        name = field(case, "name")
        values = {key: natural(field(case, key), "case " + key) for key in totals}
        need(values["started"] == values["succeeded"] + values["failed"], "case_terminal_conservation", "Case terminals do not reconcile")
        need(values["rows"] == values["succeeded"] * contracted[name]["expectedRows"], "case_result_rows", "Successful case did not consume its expected equivalent result")
        if values["started"]:
            actual_cases.add(name)
        for key in totals:
            totals[key] += values[key]
        by_case.append({"name": name, **values})
    need(all(totals[key] == counts[key] for key in totals), "case_global_conservation", "Per-case and global counters differ")
    declared_actual = field(raw, "executedCaseIds", optional=True)
    if declared_actual is not None:
        need(len(declared_actual) == len(set(declared_actual)) and set(declared_actual) == actual_cases,
             "executed_case_identity", "Declared observed cases differ from actual case counts")
    missing = [name for name in selected if name not in actual_cases]
    if not managed_phase:
        latency_summary_check(field(raw, "latencies"), envelope_histogram, "raw envelope")
    if native:
        latency_summary_check(field(raw, "serviceLatencies"), histograms["service"], "native service")
    normalized = {"counts": counts, "correctnessPassed": True, "oracleFailures": failures["total"],
        "elapsedThroughDrainSeconds": duration, "offeredSeconds": actual_window,
        "steadyStartMonotonicNs": boundary["start_ns"], "plannedOfferedEndMonotonicNs": boundary["planned_end_ns"],
        "offeredEndMonotonicNs": boundary["offered_end_ns"], "drainedMonotonicNs": boundary["drained_ns"],
        "missingExecutedCaseIds": missing, "fullCaseCoverage": not missing,
        "successOpsPerSecond": counts["succeeded"] / duration, "rowsPerSecond": counts["rows"] / duration,
        "errorAndRejectionRate": (counts["failed"] + counts["rejected"]) / max(1, counts["planned"]),
        "p50MicrosecondsUpper": primary["p50_us_upper"], "p95MicrosecondsUpper": primary["p95_us_upper"], "p99MicrosecondsUpper": primary["p99_us_upper"],
        "latencyBoundary": "scheduled-to-completion" if expected_rate is not None else "service time; closed-loop capacity only"}
    errors = error_diagnostics(raw, counts, native=native, managed_phase=managed_phase)
    return {"normalized": normalized, "oracle": failures, "boundary": boundary, "histograms": histograms, "by_case": by_case, "errors": errors,
        "queue": queue, "raw_interval_ns": {"before": start_ns, "after": end_ns},
        "end_to_end_scope": "accepted operations from start through drain; rejected arrivals retained separately; no cutoff-only operation population inferred"}


def compare_measurement(actual, recorded):
    for key, expected in actual["normalized"].items():
        need(key in recorded, "normalized_field_missing", "Normalized record omitted " + key)
        value = recorded[key]
        if key.endswith("MonotonicNs"):
            need(type(value) is int and abs(value - expected) <= 2, "normalized_clock_mismatch", "Normalized timer conversion differs by more than two rounding nanoseconds")
        elif type(expected) is float:
            number_equal(value, expected, "normalized " + key)
        else:
            need(value == expected, "normalized_value_mismatch", "Normalized " + key + " differs from its original raw bytes")


def process_snapshot(value, name):
    result = {key: finite(field(value, key), name + " " + key, 0) for key in ("userCpuSeconds", "systemCpuSeconds")}
    for key in ("rssBytes", "peakRssBytes"):
        result[key] = natural(field(value, key), name + " " + key)
    pss = field(value, "pssBytes", optional=True)
    result["pssBytes"] = natural(pss, name + " PSS") if pss is not None else None
    return result


def cpu_difference(before, after, name):
    result = {}
    for key in ("userCpuSeconds", "systemCpuSeconds"):
        need(after[key] >= before[key], "cpu_cumulative_decreased", name + " cumulative CPU decreased")
        result[key] = after[key] - before[key]
    return result


def client_metrics(raw, measured, *, context=None):
    native = field(context if context is not None else raw, "route") == "direct-native-cpp"
    managed_phase = context is not None and not native
    successes, rows = measured["normalized"]["counts"]["succeeded"], measured["normalized"]["counts"]["rows"]
    before, after = process_snapshot(field(raw, "processBefore"), "process before"), process_snapshot(field(raw, "processAfter"), "process after")
    cpu = cpu_difference(before, after, "operation interval")
    for key, value in cpu.items():
        number_equal(field(raw, key), value, "reported " + key, absolute=2e-6)
    per_success = sum(cpu.values()) / successes if successes else 0
    if not managed_phase:
        number_equal(field(raw, "cpuSecondsPerSuccessfulOperation"), per_success, "reported CPU/success", absolute=2e-6 / max(1, successes))
    memory = {"before": before, "after": after, "process_peak_rss_bytes": after["peakRssBytes"],
              "process_peak_scope": "OS process-lifetime highwater sampled after the operation interval; not a resettable steady-only peak"}
    cpu["seconds_per_success"] = per_success
    cpu["scope"] = "actual application cumulative CPU endpoints bracketing steady operations through drain"
    if native:
        drained = process_snapshot(field(raw, "processAfterDrain"), "after owner drain")
        retained = process_snapshot(field(raw, "processAfterIdle"), "after post-drain idle")
        idle = {"closed_owner_cpu": cpu_difference(drained, retained, "post-drain idle"),
                "closed_owner_elapsed_seconds": None, "closed_owner_elapsed_scope": "actual post-drain duration not persisted; configured sleep is available from the command",
                "warm_owner_reported_cpu": {key: finite(field(raw, "idle" + key[0].upper() + key[1:]), "warm idle " + key, 0) for key in ("userCpuSeconds", "systemCpuSeconds")},
                "warm_owner_observed_seconds": finite(field(raw, "idleObservationSeconds"), "warm idle observed seconds", 0),
                "warm_owner_scope": "source-bound reported delta from the pre-measurement warm-owner idle; its two CPU endpoints are not serialized"}
        memory.update(after_drain=drained, retained_after_idle=retained)
        need(field(raw, "managedAllocatedBytes") is None and field(raw, "gcCounts") is None, "cpp_managed_metric", "Direct native managed counters must be not applicable")
        managed = {"status": "not_applicable"}
        native_memory = {key: field(raw, key, optional=True) for key in ("nativeHeapPeakBytes", "nativeHeapStatus", "nativeAllocationCalls", "nativeFreeCalls", "nativeAllocationStatus",
            "sqliteMemoryUsedBefore", "sqliteMemoryUsedAfter", "sqliteEngineHighwaterSinceWarmup", "sqliteMemoryAfterDrain", "sqliteMemoryStatus")}
        native_memory["glibc_snapshots"] = {label: {key: field(field(raw, raw_name), key, optional=True) for key in ("glibcAllocatedBlockBytes", "glibcReservedBytes")}
            for label, raw_name in (("before", "processBefore"), ("after", "processAfter"), ("after_drain", "processAfterDrain"), ("after_idle", "processAfterIdle"))}
    else:
        allocated_before, allocated_after = natural(field(raw, "allocatedBefore"), "allocated before"), natural(field(raw, "allocatedAfter"), "allocated after")
        total = natural(field(raw, "allocatedBytes"), "allocated total")
        need(allocated_after >= allocated_before and total == allocated_after - allocated_before, "allocation_cumulative_mismatch", "Raw managed allocation cumulative arithmetic differs")
        per_operation, per_row = total / successes if successes else 0, total / rows if rows else 0
        if not managed_phase:
            number_equal(field(raw, "bytesPerSuccessfulOperation"), per_operation, "managed bytes/success")
            number_equal(field(raw, "bytesPerRow"), per_row, "managed bytes/row")
        managed = {"status": "end_to_end_total", "allocated_before": allocated_before, "allocated_after": allocated_after,
            "total_bytes": total, "successful_operations": successes, "rows": rows, "bytes_per_success": per_operation, "bytes_per_row": per_row,
            "gc_delta_counts": [natural(field(raw, "gen" + str(i)), "generation delta") for i in range(3)],
            "gc_scope": "source-bound GC.CollectionCount(after)-before; raw deltas preserved; cumulative GC endpoints were not serialized",
            "attribution_as_reported": field(raw, "allocationAttribution", optional=managed_phase), "framework_only_bytes": None,
            "framework_attribution_status": "unproven; no empty-driver subtraction or per-component GC split"}
        offered_raw = field(raw, "offeredLoad", optional=True)
        if offered_raw is not None:
            offered_end_bytes = natural(field(offered_raw, "allocatedAtOfferedEnd"), "allocated at actual offered end")
            need(allocated_before <= offered_end_bytes <= allocated_after, "allocation_offered_boundary_order", "Actual offered-end allocation counter is outside complete-operation cumulative endpoints")
            managed["offered_and_drain_raw"] = {"allocated_at_actual_offered_end": offered_end_bytes,
                "before_to_actual_offered_end_bytes": offered_end_bytes - allocated_before,
                "actual_offered_end_to_after_bytes": allocated_after - offered_end_bytes,
                "gc_cumulative_at_actual_offered_end": [natural(field(offered_raw, f"gen{i}AtOfferedEnd"), "generation at actual offered end") for i in range(3)],
                "scope": "actual producer offered-end counter, not an invented exact-deadline counter; no per-phase B/op or GC delta inferred without matching operation/GC endpoints"}
        idle_raw = field(raw, "idle", optional=True)
        idle = None
        if idle_raw is not None:
            idle_before, idle_after = process_snapshot(field(idle_raw, "before"), "closed idle before"), process_snapshot(field(idle_raw, "after"), "closed idle after")
            idle_cpu = cpu_difference(idle_before, idle_after, "closed idle")
            for key, value in idle_cpu.items():
                number_equal(field(idle_raw, key), value, "closed idle " + key, absolute=2e-6)
            observed, requested = finite(field(idle_raw, "observedSeconds"), "observed idle seconds", 0), finite(field(idle_raw, "requestedSeconds"), "requested idle seconds", 0)
            need(observed >= requested - 0.01, "short_idle_observation", "Actual idle observation is shorter than requested")
            wall = (timestamp(field(idle_raw, "finishedUtc")) - timestamp(field(idle_raw, "startedUtc"))).total_seconds()
            need(abs(wall - observed) <= 0.05, "idle_clock_mismatch", "Idle UTC and monotonic elapsed differ")
            idle = {"closed_owner_cpu": idle_cpu, "closed_owner_observed_seconds": observed,
                "closed_owner_requested_seconds": requested, "started_utc": field(idle_raw, "startedUtc"), "finished_utc": field(idle_raw, "finishedUtc"),
                "managed_heap_before": natural(field(idle_raw, "managedBytesBefore"), "idle managed heap before"),
                "managed_heap_after": natural(field(idle_raw, "managedBytesAfter"), "idle managed heap after"),
                "managed_heap_scope": "GC.GetTotalMemory(false) live-heap estimate, not cumulative allocated bytes",
                "scope": field(idle_raw, "scope")}
            memory.update(after_drain=idle_before, retained_after_idle=idle_after)
        native_memory = {"status": "separate profile/engine-specific counters only; RSS is not relabeled as native heap"}
    return {"cpu": cpu, "memory": memory, "managed": managed, "idle": idle, "native_memory": native_memory,
        "boundary_alignment": {"application_monotonic_interval": measured["raw_interval_ns"], "offered_ledger_interval": measured["boundary"],
            "scope": "actual CPU/allocation endpoint values are primary; application steady timestamps and offered Snapshot drain timestamp are distinct source-ordered reads. No missing endpoint timestamps are synthesized, and snapshot aggregation may occur after phase CPU/allocation capture."}}


def owner_metrics(raw, measured=None, manifest=None):
    route = field(raw, "route")
    if route == "direct-native-cpp":
        counters = {key: natural(field(raw, key), key) for key in ("drainedConnections", "drainedSqliteStatements", "drainedPgResults")}
        need(field(raw, "ownersReturnedToZero") is True and not any(counters.values()), "native_owners_not_retired", "Native owner counters did not retire")
        protocol = native_protocol(raw, measured, manifest)
        return {"scope": "native tracked connection/statement/result/cancel owners", "drained": counters, "protocol": protocol,
                "physical_network_round_trips": field(raw, "physicalNetworkRoundTrips"), "physical_network_round_trips_status": field(raw, "physicalNetworkRoundTripsStatus")}
    drained = field(raw, "drained")
    if route == "korda-public-fluent":
        counters = {key: natural(field(drained, key), key) for key in ("sources", "sessions", "operations", "cursors", "buffers", "owners")}
        need(field(raw, "ownersReturnedToZero") is True and not any(counters.values()), "korda_owners_not_retired", "Korda native owners did not retire")
        before, after = field(raw, "before"), field(raw, "after")
        activity = {}
        for key in ("sqlExecutions", "protocolSubmissions"):
            first, last, final = natural(field(before, key), key), natural(field(after, key), key), natural(field(drained, key), key)
            need(first <= last <= final, "native_activity_counter_order", "Cumulative SQL/protocol diagnostic counter decreased")
            activity[key] = {"before": first, "after": last, "after_close": final,
                             "operation_interval_delta": last - first, "close_delta": final - last}
        return {"scope": "six Korda native counters", "before": before, "after": after, "drained": drained, "activity": activity,
                "physical_network_round_trips": None, "physical_network_round_trips_status": "not instrumented; submissions are not RTT"}
    need(field(raw, "applicationConnectionsReturnedToZero") is True and natural(field(drained, "applicationConnections"), "application connections") == 0,
         "orm_connections_not_retired", "ORM application connections did not retire")
    return {"scope": "application-owned logical connections only; provider internal owners not instrumented", "drained": drained}


def native_protocol(raw, measured, manifest):
    value = field(raw, "protocol")
    names = ("sqlExecutions", "protocolSubmissions", "physicalPrepareCalls", "describeCalls", "confirmationSyncs", "pollWaitCalls", "unmaterializedRowsDrained")
    counters = {key: natural(field(value, key), "native protocol " + key) for key in names}
    provider = field(raw, "provider")
    if provider == "postgresql":
        need(counters["protocolSubmissions"] == sum(counters[key] for key in ("sqlExecutions", "physicalPrepareCalls", "describeCalls", "confirmationSyncs")),
             "native_protocol_submission_conservation", "Native submission count differs from actual execute/prepare/describe/sync counters")
    else:
        need(provider == "sqlite" and all(counters[key] == 0 for key in ("protocolSubmissions", "describeCalls", "confirmationSyncs", "pollWaitCalls", "unmaterializedRowsDrained")),
             "sqlite_protocol_counter", "SQLite contains uncontracted network protocol counters")
    expected_legs = None
    if measured is not None and manifest is not None and measured["normalized"]["counts"]["failed"] == 0:
        queries = {query["name"]: query for query in manifest["queries"]}
        expected_legs = sum(item["succeeded"] * natural(queries[item["name"]]["queryExecutionsPerOperation"], "query execution legs", 1) for item in measured["by_case"])
        need(counters["sqlExecutions"] == expected_legs, "native_sql_case_leg_conservation", "Successful per-case SQL legs differ from actual native execution delta")
        if provider == "postgresql":
            need(counters["describeCalls"] == counters["physicalPrepareCalls"], "native_describe_prepare_conservation", "Successful PostgreSQL preparation/description counts differ")
            syncs = 2 * expected_legs if field(raw, "protocolConfirmation") == "product" else 0
            need(counters["confirmationSyncs"] == syncs, "native_protocol_confirmation_conservation", "Successful SQL legs differ from the selected protocol confirmation policy")
        if field(raw, "physicalPreparation") == "per-operation":
            need(counters["physicalPrepareCalls"] == expected_legs, "native_physical_prepare_conservation", "Per-operation physical preparations differ from successful SQL legs")
    return {"raw_deltas": counters, "expected_successful_sql_legs_if_zero_failures": expected_legs,
        "scope": "source-bound sum of per-worker after-minus-before counters; cumulative per-worker endpoints were not serialized; no endpoint values are fabricated",
        "failed_operation_sql_attribution": "not inferred when failures exist; failed requests can stop after a partial SQL sequence"}
