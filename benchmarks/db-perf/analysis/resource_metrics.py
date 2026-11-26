"""Recompute sampled memory and owned-cgroup CPU without inventing edges."""

from __future__ import annotations

from datetime import timedelta
from pathlib import PurePosixPath
import re
import statistics

from strict_io import EvidenceError, field, finite, natural, need
from raw_metrics import number_equal, timestamp


CPU_KEYS = ("user", "nice", "system", "idle", "iowait", "irq", "softirq", "steal")
GROUP_ID_KEYS = ("path", "deviceInode", "controls", "serverIdentity")


def compare_object(expected, actual, prefix="resource"):
    """Compare independently computed fields, allowing explanatory extras only."""
    need(isinstance(actual, dict), "derived_object_missing", prefix + " is absent")
    for key, value in expected.items():
        need(key in actual, "derived_field_missing", prefix + "." + key + " is absent")
        other = actual[key]
        if isinstance(value, dict):
            compare_object(value, other, prefix + "." + key)
        elif type(value) is float:
            number_equal(other, value, prefix + "." + key)
        else:
            need(type(other) is type(value) and other == value, "derived_resource_mismatch", prefix + "." + key + " differs from raw records")


def host_cpu(before, after):
    delta = {}
    for key in CPU_KEYS:
        a, b = natural(before.get(key), "host " + key), natural(after.get(key), "host " + key)
        need(b >= a, "host_cpu_decreased", "Host CPU cumulative ticks decreased")
        delta[key] = b - a
    total = sum(delta.values())
    need(total > 0, "empty_host_cpu", "Host CPU observation contains no ticks")
    return {"totalTicks": total, "stealFraction": delta["steal"] / total,
            "busyFraction": (total - delta["idle"] - delta["iowait"]) / total, "delta": delta}


def quota(snapshot):
    need(snapshot.get("available") is True and snapshot.get("mountRoot") == "/" and
         snapshot.get("sameCgroupNamespaceAsInit") is True and snapshot.get("hostInit") in ("systemd", "init"),
         "quota_unavailable", "Complete VM cgroup-v2 quota ancestry is unavailable", unproven=True)
    nodes = snapshot.get("ancestors")
    need(isinstance(nodes, list) and nodes, "quota_ancestry_missing", "Quota ancestry is empty")
    membership, mount_text = snapshot.get("membership"), snapshot.get("mountPoint")
    need(isinstance(membership, str) and membership.startswith("/") and isinstance(mount_text, str) and mount_text.startswith("/"),
         "quota_membership_missing", "Quota leaf membership and mount point must be explicitly recorded")
    mount, member = PurePosixPath(mount_text), PurePosixPath(membership)
    need(".." not in mount.parts and ".." not in member.parts and str(mount) == mount_text and str(member) == membership,
         "quota_membership_path", "Quota mount/membership is not a canonical absolute path")
    source = snapshot.get("membershipSource")
    need(isinstance(source, str) and [line[3:] for line in source.splitlines() if line.startswith("0::")] == [membership],
         "quota_membership_source", "Leaf membership differs from actual recorded cgroup-v2 membership text")
    current, expected_paths = mount / membership.lstrip("/"), []
    while True:
        expected_paths.append(str(current))
        if current == mount:
            break
        need(current.is_relative_to(mount) and current.parent != current, "quota_parent_escape", "Quota ancestry escapes its mount")
        current = current.parent
    need([node.get("path") for node in nodes] == expected_paths and [node.get("root") for node in nodes] == [False] * (len(nodes) - 1) + [True],
         "quota_incomplete_parent_chain", "Quota snapshots must include every ancestor from actual membership through mount root, in order")
    namespace = snapshot.get("namespaceObservation", {})
    need(namespace.get("sameAsInit") is True and namespace.get("selfBefore") == namespace.get("selfAfter") == namespace.get("init")
         and isinstance(namespace.get("init"), str) and re.fullmatch(r"cgroup:\[[0-9]+\]", namespace["init"]),
         "quota_namespace_identity", "Actual recorded namespace identities do not establish the host init namespace")
    signature, throttles, capacities, seen = [], {}, [], set()
    for node in nodes:
        path = node.get("path")
        need(isinstance(path, str) and path not in seen, "quota_duplicate_ancestor", "Repeated quota ancestor")
        seen.add(path)
        signature.append({key: node.get(key) for key in ("path", "root", "quotaStatus", "cpu.max", "cpuset.cpus.effective")})
        lines = node.get("cpu.stat", "").splitlines()
        pairs = [line.split() for line in lines]
        need(all(len(pair) == 2 for pair in pairs) and len({pair[0] for pair in pairs}) == len(pairs),
             "quota_stat_shape", "Malformed/duplicate quota CPU counter")
        try:
            stats = {key: int(value) for key, value in pairs}
        except ValueError as error:
            raise EvidenceError("quota_stat_type", "Quota counters are not integers") from error
        for key in ("usage_usec", "user_usec", "system_usec"):
            natural(stats.get(key), "quota " + key)
        if node.get("quotaStatus") == "read":
            parts = node.get("cpu.max", "").split()
            need(len(parts) == 2, "quota_limit_shape", "Malformed CPU bandwidth limit")
            try:
                period, maximum = int(parts[1]), None if parts[0] == "max" else int(parts[0])
            except ValueError as error:
                raise EvidenceError("quota_limit_type", "Noninteger CPU bandwidth limit") from error
            need(period > 0 and (maximum is None or maximum > 0), "quota_limit_value", "Nonpositive CPU bandwidth limit")
            if maximum is not None:
                capacities.append(maximum / period)
            throttles[path] = natural(stats.get("nr_throttled"), "quota throttle count")
        else:
            need(node.get("quotaStatus") in ("root_has_no_cpu_max", "cpu_controller_not_enabled_for_group"),
                 "quota_limit_unexplained", "CPU bandwidth absence is unexplained")
            need(isinstance(node.get("cpu.max"), dict) and node["cpu.max"].get("unavailableErrno") == 2,
                 "quota_limit_absence_mismatch", "Declared absent CPU controller differs from actual read status")
            if node["quotaStatus"] == "root_has_no_cpu_max":
                need(node["root"] is True, "quota_nonroot_root_exemption", "A non-root group cannot use the mount-root bandwidth exemption")
            else:
                need(isinstance(node.get("parentSubtreeControl"), str) and "cpu" not in node["parentSubtreeControl"].split(),
                     "quota_parent_controller_unverified", "Disabled child CPU controller lacks actual parent controller state")
    return {"signature": signature, "throttles": throttles, "capacity": min(capacities) if capacities else None}


def tree(readings, root_identity, label):
    need(isinstance(readings, list), "process_tree_shape", "Process observations must be an array")
    identities, members = set(), {}
    for item in readings:
        pid, born = natural(item.get("pid"), label + " PID", 1), natural(item.get("startTicks"), label + " start ticks", 1)
        need(pid not in members and (pid, born) not in identities, "process_duplicate_identity", "Repeated sampled process")
        members[pid], identities = item, identities | {(pid, born)}
        for key in ("userTicks", "systemTicks", "reapedChildUserTicks", "reapedChildSystemTicks", "threads"):
            natural(item.get(key), label + " " + key)
        for key, value in item.get("memoryBytes", {}).items():
            natural(value, label + " memory " + key)
    if root_identity and readings:
        root = members.get(root_identity["pid"])
        need(root is not None and root["startTicks"] == root_identity["startTicks"], "process_root_identity", "Sampled tree has another root/PID reuse")
    return {"ownAndReapedUserTicks": sum(item["userTicks"] + item["reapedChildUserTicks"] for item in readings),
        "ownAndReapedSystemTicks": sum(item["systemTicks"] + item["reapedChildSystemTicks"] for item in readings),
        "sampledRssBytes": sum(item["memoryBytes"].get("Rss", 0) for item in readings),
        "sampledPssBytes": sum(item["memoryBytes"].get("Pss", 0) for item in readings),
        "rssComplete": bool(readings) and all("Rss" in item["memoryBytes"] for item in readings),
        "pssComplete": bool(readings) and all("Pss" in item["memoryBytes"] for item in readings),
        "memoryAvailableProcessCount": sum(bool(item["memoryBytes"]) for item in readings),
        "processCount": len(readings), "treeSnapshotNonAtomic": True}


def group_snapshot(value, descriptor=None, *, empty=False):
    need(isinstance(value, dict), "cgroup_snapshot_missing", "Task-owned PostgreSQL cumulative CPU snapshot is missing", unproven=True)
    need(value.get("membersVerified") is True, "cgroup_membership_unverified", "Task-owned group membership was not verified")
    path = PurePosixPath(value.get("path", ""))
    need(path.is_absolute() and re.fullmatch(r"db-perf-01-pg-[0-9a-f]{24}", path.name), "cgroup_path_identity", "Unexpected task-owned group name/path")
    device = value.get("deviceInode")
    need(isinstance(device, list) and len(device) == 2, "cgroup_inode_missing", "Task-owned group inode is not recorded")
    for part in device:
        natural(part, "cgroup device/inode")
    if descriptor is not None:
        need(all(value.get(key) == descriptor.get(key) for key in GROUP_ID_KEYS), "cgroup_identity_changed", "Task-owned group path/inode/controls/owner changed")
    root, members, vanished = value.get("serverIdentity"), value.get("members"), value.get("exitedDuringMemberAcquisition")
    need(isinstance(members, list) and isinstance(vanished, list), "cgroup_members_missing", "Task-owned group acquisition membership is absent")
    if empty:
        need(not members and not vanished, "cgroup_owners_not_retired", "Task-owned group still has observed owners after join")
    else:
        need(isinstance(root, dict), "cgroup_owner_missing", "Task-owned group owner PID/start identity is absent")
        identities = {item.get("pid"): item for item in members}
        need(len(identities) == len(members), "cgroup_duplicate_member", "Duplicate group member")
        need(root.get("pid") in identities and identities[root["pid"]].get("startTicks") == root.get("startTicks"),
             "cgroup_owner_identity", "Recorded group no longer contains its original PostgreSQL owner")
        for item in members:
            for key in ("pid", "startTicks", "parentPid"):
                natural(item.get(key), "cgroup member " + key, 1)
            # Each extant member is captured after source-side ancestry validation.
            # A missing ancestor can exit during this non-atomic acquisition; do
            # not fabricate an offline parent record or require its persistence.
    for key, counter_value in value.get("cpu", {}).items():
        natural(counter_value, "cgroup " + key)
    for key in ("usage_usec", "user_usec", "system_usec"):
        natural(value.get("cpu", {}).get(key), "cgroup " + key)
    return None


def group_delta(before, after):
    need(all(before.get(key) == after.get(key) for key in GROUP_ID_KEYS), "cgroup_identity_changed", "Cumulative CPU group identity differs across boundaries")
    need(before.get("membersVerified") is True and after.get("membersVerified") is True,
         "cgroup_membership_unverified", "Cumulative CPU group membership was not verified")
    delta = {}
    for key in ("usage_usec", "user_usec", "system_usec"):
        first, last = natural(before["cpu"].get(key), key), natural(after["cpu"].get(key), key)
        need(last >= first, "cgroup_counter_decreased", "Cumulative task-owned PostgreSQL CPU moved backwards")
        delta[key] = last - first
    throttle = 0
    if "nr_throttled" in before["cpu"] or "nr_throttled" in after["cpu"]:
        first, last = natural(before["cpu"].get("nr_throttled"), "group throttles"), natural(after["cpu"].get("nr_throttled"), "group throttles")
        need(last >= first, "cgroup_throttles_decreased", "Task-owned throttle counter decreased")
        throttle = last - first
    else:
        need(not isinstance(before["controls"].get("cpu.max"), str), "cgroup_throttles_missing", "Enabled task-owned bandwidth accounting lacks throttle counts")
    return {"userCpuSeconds": delta["user_usec"] / 1e6, "systemCpuSeconds": delta["system_usec"] / 1e6,
        "usageCpuSeconds": delta["usage_usec"] / 1e6, "rawStartMicroseconds": before["cpu"], "rawEndMicroseconds": after["cpu"],
        "counterMonotonic": True, "cpuAccounting": "task_owned_cgroup_v2", "taskGroupThrottleEvents": throttle,
        "cpuGroupIdentity": {key: before[key] for key in ("path", "deviceInode", "serverIdentity")}}


def server_owner(owner):
    need(owner.get("exitCode") == 0 and not any(owner.get(key) for key in ("shutdownError", "cpuGroupError", "finalSampleError")),
         "server_retirement_failed", "PostgreSQL owner/accounting did not retire cleanly")
    group = owner.get("serverCpuGroup", {})
    need(all(group.get(key) is True for key in ("required", "created", "removed", "parentControlsUnchanged")),
         "server_group_lifecycle_unverified", "PostgreSQL group creation/cleanup/parent controls are unverified")
    descriptor = group["descriptor"]
    need(descriptor["serverIdentity"] == {key: owner["identity"][key] for key in ("pid", "startTicks")},
         "server_owner_identity", "PostgreSQL group belongs to another owner")
    need(PurePosixPath(descriptor["path"]).parent.as_posix() == descriptor.get("parentPath") and group.get("requestedPath") == descriptor["path"],
         "server_group_parent", "Task-owned group parent/requested path differs")
    script = 'set -eu\nprintf "%s\\n" "$$" | sudo -n tee -- "$1/cgroup.procs" >/dev/null\nshift\nexec "$@"'
    need(owner.get("launcherArgv") == ["/bin/sh", "-c", script, "db-perf-postgres-cgroup", descriptor["path"], *owner.get("argv", [])],
         "server_owner_launcher_identity", "Actual group-entry launcher differs from its recorded owner command")
    initial, final = group["beforeClients"], group["emptyAfterOwnerJoin"]
    group_snapshot(initial, descriptor)
    group_snapshot(final, descriptor, empty=True)
    launch = group["emptyBeforeLaunch"]
    group_snapshot(launch, empty=True)
    need(all(launch[key] == descriptor[key] for key in ("path", "deviceInode", "controls")) and launch["serverIdentity"] is None,
         "server_group_launch_identity", "Prelaunch group identity differs")
    need(all(launch["cpu"][key] == 0 for key in ("usage_usec", "user_usec", "system_usec")),
         "server_group_not_new", "Task group already contains CPU usage before owner launch")
    measured = group_delta(initial, final)
    compare_object(measured, group["clientsThroughShutdownCpu"], "server lifetime cumulative delta")
    cross = group.get("wait4CrossCheck", {})
    for key, counter in (("User", "user_usec"), ("System", "system_usec")):
        value = finite(owner.get("processLifetime" + key + "CpuSeconds"), "PostgreSQL wait4 " + key, 0)
        number_equal(cross.get("groupLifetime" + key + "Seconds"), final["cpu"][counter] / 1e6, "group lifetime " + key)
        number_equal(cross.get("wait4ProcessLifetime" + key + "Seconds"), value, "wait4 lifetime " + key)
    need(natural(owner.get("finishedMonotonicNs"), "owner finish") > natural(owner.get("startedMonotonicNs"), "owner start"),
         "server_owner_clock_order", "PostgreSQL owner lifetime interval is reversed")
    return {"descriptor": descriptor, "initial": initial, "final": final, "cpu": measured,
            "wait4": {key: owner[key] for key in ("processLifetimeUserCpuSeconds", "processLifetimeSystemCpuSeconds", "processLifetimeMaxRssBytes")},
            "scope": "owned group from before clients through shutdown; wait4 is a separate lifetime cross-check, not forced equality"}


def sampled_side(samples, side):
    readings = [item[side + "Totals"] for item in samples if item[side + "Totals"]["processCount"]]
    if not readings:
        return {"status": "not_present"}, {}
    rss, pss = ([item["sampledRssBytes"] for item in readings if item["rssComplete"]],
                [item["sampledPssBytes"] for item in readings if item["pssComplete"]])
    ticks = {key: readings[-1][key] - readings[0][key] for key in ("ownAndReapedUserTicks", "ownAndReapedSystemTicks")}
    result = {"status": "sampled_nonatomic", "counterMonotonic": all(value >= 0 for value in ticks.values()),
        "steadyMedianRssBytes": statistics.median(rss) if rss else None, "sampledPeakRssBytes": max(rss) if rss else None,
        "steadyMedianPssBytes": statistics.median(pss) if pss else None, "sampledPeakPssBytes": max(pss) if pss else None,
        "rssCompleteSamples": len(rss), "pssCompleteSamples": len(pss), "processTreeSnapshotsAreNonAtomic": True}
    return result, {"before": readings[0], "after": readings[-1], "tick_deltas": ticks,
        "tick_hz": None, "scope": "diagnostic raw own+reaped ticks; remote SC_CLK_TCK was not serialized, so no local-Hz conversion is asserted"}


def summarize(samples, *, require_two=True):
    if len(samples) < 2:
        need(not require_two, "resource_samples_insufficient", "Fewer than two complete interior resource samples", unproven=True)
        return {"samples": len(samples), "status": "insufficient_interior_samples"}, {}
    result = {"samples": len(samples), "firstMonotonicNs": samples[0]["monotonicNs"], "lastMonotonicNs": samples[-1]["acquiredThroughMonotonicNs"],
        "hostCpu": host_cpu(samples[0]["hostCpuTicks"], samples[-1]["hostCpuTicks"]),
        "maximumIntervalStealFraction": max(host_cpu(a["hostCpuTicks"], b["hostCpuTicks"])["stealFraction"] for a, b in zip(samples, samples[1:]))}
    quotas = [quota(item["guestQuota"]) for item in samples]
    stable = all(item["signature"] == quotas[0]["signature"] for item in quotas)
    need(all(set(item["throttles"]) == set(quotas[0]["throttles"]) for item in quotas), "quota_ancestry_changed", "Quota ancestry differs inside the interval")
    for before, after in zip(quotas, quotas[1:]):
        need(all(after["throttles"][key] >= value for key, value in before["throttles"].items()), "quota_throttles_decreased", "An ancestor throttle counter decreased")
    changes = {key: quotas[-1]["throttles"][key] - value for key, value in quotas[0]["throttles"].items()}
    result.update(quotaStableAndAvailable=stable, guestThrottleEvents=sum(changes.values()), guestThrottleEventsByAncestor=changes)
    raw_ticks = {}
    for side in ("client", "server"):
        result[side], raw_ticks[side] = sampled_side(samples, side)
    if result["server"]["status"] != "not_present":
        for before, after in zip(samples, samples[1:]):
            group_delta(before["serverCpuGroup"], after["serverCpuGroup"])
        result["server"].update(group_delta(samples[0]["serverCpuGroup"], samples[-1]["serverCpuGroup"]))
    return result, raw_ticks


def resource_quality(resources, provider, limits):
    reasons = []
    if resources.get("quotaStableAndAvailable") is not True:
        reasons.append("guest quota ancestry was unavailable or changed")
    if type(resources.get("guestThrottleEvents")) is not int or resources["guestThrottleEvents"] != 0:
        reasons.append("guest CPU throttling was observed or unmeasurable")
    for value, maximum, label in ((resources.get("hostCpu", {}).get("stealFraction"), limits["maximumStealFraction"], "overall VM steal"),
                                  (resources.get("maximumIntervalStealFraction"), limits.get("maximumIntervalStealFraction", 0.05), "interval VM steal")):
        if not 0 <= value <= maximum:
            reasons.append(label + " exceeded the frozen limit or was unavailable")
    for side in ("client", "server") if provider == "postgresql" else ("client",):
        if resources.get(side, {}).get("counterMonotonic") is not True:
            reasons.append(side + " CPU counters were uncertain")
    if provider == "postgresql":
        server = resources.get("server", {})
        if server.get("cpuAccounting") != "task_owned_cgroup_v2" or server.get("taskGroupThrottleEvents") != 0:
            reasons.append("PostgreSQL task-group cumulative CPU was unavailable or throttled")
    return {"passed": not reasons, "reasons": reasons}


def resources(samples, command, raw, measured, normalized, request, owner=None, *, phase_context=False):
    need(isinstance(samples, list) and samples, "resource_stream_empty", "Actual resource stream is absent")
    code = command.get("exitCode")
    need(code in (0, 1) and command.get("success") is (code == 0) and command.get("timedOut") is False
         and command.get("observerErrors") == [] and (code == 0 or measured["normalized"]["counts"]["failed"] > 0),
         "client_command_failed", "Client has an unknown/unaccounted terminal error or its observer did not finish cleanly")
    start, end = natural(command.get("startedMonotonicNs"), "command start", 1), natural(command.get("finishedMonotonicNs"), "command end", 1)
    boundary = measured["boundary"]
    need(start <= boundary["start_ns"] < boundary["drained_ns"] <= end, "runner_measurement_interval", "Raw measurement is outside the actual process lifetime")
    if not phase_context:
        need(timestamp(command["startedUtc"]) <= timestamp(field(raw, "startedUtc")) <= timestamp(field(raw, "finishedUtc")) <= timestamp(command["finishedUtc"]),
             "runner_wall_interval", "Raw wall-clock interval is outside command lifetime")
    owned = server_owner(owner) if normalized["provider"] == "postgresql" else None
    descriptor = owned["descriptor"] if owned else None
    if owned:
        need(command.get("serverCpuGroupIdentity") == descriptor, "command_cgroup_identity", "Command refers to another PostgreSQL accounting group")
        need(owner["startedMonotonicNs"] <= start < end <= owner["finishedMonotonicNs"], "owner_command_interval", "Client is outside its independently owned PostgreSQL lifetime")
    previous_end, previous_wall = start, timestamp(command["startedUtc"])
    previous_group = owned["initial"] if owned else None
    for sample in samples:
        begin, finish = natural(sample.get("monotonicNs"), "sample start", 1), natural(sample.get("acquiredThroughMonotonicNs"), "sample finish", 1)
        need(previous_end <= begin <= finish <= end, "sample_clock_order", "Resource acquisitions overlap, reverse, or escape the command lifetime")
        wall = timestamp(sample.get("utc"))
        need(previous_wall <= wall <= timestamp(command["finishedUtc"]), "sample_wall_order", "Resource UTC order differs from command lifetime")
        previous_end, previous_wall = finish, wall
        for side, identity in (("client", command["clientIdentity"]), ("server", descriptor["serverIdentity"] if descriptor else None)):
            totals = tree(sample.get(side), identity, side)
            compare_object(totals, sample.get(side + "Totals"), side + " raw tree totals")
        if owned:
            group_snapshot(sample.get("serverCpuGroup"), descriptor)
            group_delta(previous_group, sample["serverCpuGroup"])
            previous_group = sample["serverCpuGroup"]
        else:
            need(sample.get("serverCpuGroup") is None and not sample["server"], "sqlite_external_server", "SQLite observation unexpectedly includes a database server")
    need(command.get("lastSample") == samples[-1], "observer_last_sample_mismatch", "Command's last sample differs from its raw stream")
    if owned:
        group_delta(previous_group, owned["final"])
    selected = [item for item in samples if item["monotonicNs"] >= boundary["start_ns"] and item["acquiredThroughMonotonicNs"] <= boundary["drained_ns"]]
    diagnostic = normalized.get("diagnostic") is not None
    bounded_plan = normalized.get("diagnostic") == "query-plan"
    result, tick_details = summarize(selected, require_two=not bounded_plan)
    if not bounded_plan:
        compare_object(result, normalized["resources"], "normalized resources")
    if not diagnostic:
        quality = resource_quality(result, normalized["provider"], request["admission"])
        need(normalized.get("resourceQualityPassed") is quality["passed"] and normalized.get("resourceQualityReasons") == quality["reasons"],
             "resource_quality_mismatch", "Recorded resource eligibility differs from raw resource metrics")
    else:
        need(normalized.get("resourceQualityPassed") is None and normalized.get("timingComparisonEligible") is False,
             "diagnostic_comparison_claim", "Instrumented diagnostic claimed timing/resource comparison credit")
        quality = {"passed": None, "reasons": ["instrumented diagnostic has no resource comparison credit"]}
    phases = {}
    if normalized.get("rate") is not None:
        for label, left, right in (("offered_window_interior", boundary["start_ns"], boundary["planned_end_ns"]),
                                   ("drain_interior", boundary["planned_end_ns"], boundary["drained_ns"])):
            values = [item for item in samples if item["monotonicNs"] >= left and item["acquiredThroughMonotonicNs"] <= right]
            phases[label] = summarize(values, require_two=False)[0]
    idle = field(raw, "idle", optional=True)
    if isinstance(idle, dict):
        left, right = timestamp(field(idle, "startedUtc")), timestamp(field(idle, "finishedUtc"))
        values = [item for item in samples if timestamp(item["utc"]) - timedelta(microseconds=(item["acquiredThroughMonotonicNs"] - item["monotonicNs"]) / 1000) >= left
                  and timestamp(item["utc"]) <= right]
        phases["managed_closed_idle_interior"] = summarize(values, require_two=False)[0]
        phases["managed_closed_idle_interior"]["alignment_scope"] = "recorded idle UTC boundaries; sample UTC at acquisition end minus recorded acquisition duration; no edge extrapolation"
    return {"scope": "whole acquisition intervals inside the recorded client steady+drain boundary; no inferred cutoff-only operation counts",
        "sample_count_total": len(samples), "steady_and_drain": result, "raw_tree_tick_diagnostics": tick_details,
        "raw_tree_reported_seconds_scope": "original normalized values retained in source receipt only; remote SC_CLK_TCK not serialized",
        "quality": quality, "phases": phases, "server_owner": owned}
