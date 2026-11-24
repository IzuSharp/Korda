#!/usr/bin/env python3
"""Run frozen commands with bounded failure and separate client/server evidence.

This observer does not admit an environment or grant long-campaign credit.
"""
from __future__ import annotations

import argparse
from contextlib import contextmanager
from datetime import datetime, timezone
import hashlib
import json
import math
import os
from pathlib import Path
import re
import signal
import socket
import subprocess
import time
import uuid


def utc():
    return datetime.now(timezone.utc).isoformat()


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write_json(path, value):
    path = Path(path)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(value, indent=2) + "\n")
    temporary.replace(path)


def process(pid):
    """Retain CPU identity when smaps is unavailable; unavailable RSS is not zero."""
    try:
        text = Path(f"/proc/{pid}/stat").read_text()
        fields = text[text.rfind(")") + 2:].split()
    except (FileNotFoundError, ProcessLookupError):
        return None
    result = {"pid": pid, "startTicks": int(fields[19]), "state": fields[0], "parentPid": int(fields[1]),
              "userTicks": int(fields[11]), "systemTicks": int(fields[12]),
              "reapedChildUserTicks": int(fields[13]), "reapedChildSystemTicks": int(fields[14]),
              "threads": int(fields[17]), "memoryBytes": {}, "memoryErrors": []}
    try:
        for line in Path(f"/proc/{pid}/smaps_rollup").read_text().splitlines():
            key, _, value = line.partition(":")
            if key in ("Rss", "Pss", "Private_Clean", "Private_Dirty"):
                result["memoryBytes"][key] = int(value.split()[0]) * 1024
    except OSError as error:
        result["memoryErrors"].append({"source": "smaps_rollup", "errno": error.errno})
    try:
        current = Path(f"/proc/{pid}/stat").read_text()
        current = current[current.rfind(")") + 2:].split()
        if int(current[19]) != result["startTicks"]:
            return None
    except (FileNotFoundError, ProcessLookupError):
        result["exitedDuringAcquisition"] = True
    return result


def tree(pid):
    result, pending, seen = [], [pid], set()
    while pending:
        current = pending.pop()
        if current in seen:
            continue
        seen.add(current)
        reading = process(current)
        if reading is not None:
            result.append(reading)
        try:
            for children in Path(f"/proc/{current}/task").glob("*/children"):
                pending.extend(int(value) for value in children.read_text().split())
        except (FileNotFoundError, ProcessLookupError):
            pass
    return result


def tree_totals(readings):
    # Do not accumulate per-PID maxima: after reaping, their CPU moves into the
    # parent's cutime/cstime. Snapshots are non-atomic; retain raw PID identities
    # and cross-check with final OS wait4 lifetime accounting.
    return {"ownAndReapedUserTicks": sum(s["userTicks"] + s["reapedChildUserTicks"] for s in readings),
            "ownAndReapedSystemTicks": sum(s["systemTicks"] + s["reapedChildSystemTicks"] for s in readings),
            "sampledRssBytes": sum(s["memoryBytes"].get("Rss", 0) for s in readings),
            "sampledPssBytes": sum(s["memoryBytes"].get("Pss", 0) for s in readings),
            "rssComplete": bool(readings) and all("Rss" in s["memoryBytes"] for s in readings),
            "pssComplete": bool(readings) and all("Pss" in s["memoryBytes"] for s in readings),
            "memoryAvailableProcessCount": sum(bool(s["memoryBytes"]) for s in readings),
            "processCount": len(readings), "treeSnapshotNonAtomic": True}


def host_cpu():
    fields = Path("/proc/stat").read_text().splitlines()[0].split()
    return dict(zip(("user", "nice", "system", "idle", "iowait", "irq", "softirq", "steal", "guest", "guestNice"), map(int, fields[1:])))


def cgroup_snapshot():
    """Read the current v2 group and every visible ancestor, not just mount root.

    cpu.max exists only on non-root CPU-enabled groups; cpu.stat bandwidth
    counters are local, so a descendant alone cannot prove no ancestor throttle.
    https://docs.kernel.org/admin-guide/cgroup-v2.html#cpu-interface-files
    """
    def read(path):
        try:
            return Path(path).read_text().strip()
        except OSError as error:
            return {"unavailableErrno": error.errno}
    membership, mounts = read("/proc/self/cgroup"), read("/proc/self/mountinfo")
    result = {"schemaVersion": 2, "membershipSource": membership, "available": False, "ancestors": []}
    if not isinstance(membership, str) or not isinstance(mounts, str):
        return result
    groups = [line[3:] for line in membership.splitlines() if line.startswith("0::")]
    if len(groups) != 1 or not groups[0].startswith("/"):
        result["reason"] = "A single cgroup-v2 membership is required"
        return result
    def unescape(value):
        return re.sub(r"\\([0-7]{3})", lambda match: chr(int(match[1], 8)), value)
    candidates = []
    for line in mounts.splitlines():
        fields = line.split()
        if "-" not in fields or fields[fields.index("-") + 1] != "cgroup2":
            continue
        mount_root, mount = Path(unescape(fields[3])), Path(unescape(fields[4]))
        if Path(groups[0]).is_relative_to(mount_root):
            candidates.append((mount_root, mount))
    if not candidates:
        result["reason"] = "The current cgroup-v2 mount is unavailable"
        return result
    mount_root, mount = max(candidates, key=lambda pair: len(str(pair[0])))
    current = mount / Path(groups[0]).relative_to(mount_root)
    result.update(membership=groups[0], mountRoot=str(mount_root), mountPoint=str(mount),
                  hostInit=read("/proc/1/comm"))
    try:
        result["sameCgroupNamespaceAsInit"] = os.readlink("/proc/self/ns/cgroup") == os.readlink("/proc/1/ns/cgroup")
    except OSError as error:
        result["namespaceError"] = error.errno
    if not current.resolve().is_relative_to(mount.resolve()):
        result["reason"] = "Cgroup path escaped its mount"
        return result
    while True:
        node = {"path": str(current), "root": current == mount,
                "cpu.max": read(current / "cpu.max"), "cpu.stat": read(current / "cpu.stat"),
                "memory.max": read(current / "memory.max"), "cpuset.cpus.effective": read(current / "cpuset.cpus.effective"),
                "cgroup.subtree_control": read(current / "cgroup.subtree_control")}
        if isinstance(node["cpu.max"], str):
            node["quotaStatus"] = "read"
        elif node["cpu.max"].get("unavailableErrno") == 2 and node["root"] and mount_root == Path("/"):
            node["quotaStatus"] = "root_has_no_cpu_max"
        else:
            parent_enabled = read(current.parent / "cgroup.subtree_control")
            node["parentSubtreeControl"] = parent_enabled
            node["quotaStatus"] = "cpu_controller_not_enabled_for_group" if node["cpu.max"].get("unavailableErrno") == 2 and isinstance(parent_enabled, str) and "cpu" not in parent_enabled.split() else "unavailable"
        result["ancestors"].append(node)
        if current == mount:
            break
        current = current.parent
    result["available"] = all(isinstance(node["cpu.stat"], str) and node["quotaStatus"] != "unavailable" for node in result["ancestors"])
    return result


def cpu_group_controls(path):
    """Observe scheduling/limit settings; this observer never changes them."""
    values = {}
    for name in ("cpu.max", "cpu.weight", "cpuset.cpus.effective", "memory.max", "cgroup.subtree_control"):
        try:
            values[name] = (Path(path) / name).read_text().strip()
        except OSError as error:
            values[name] = {"unavailableErrno": error.errno}
    return values


def cpu_group_parent_controls(snapshot):
    if not snapshot.get("available") or not snapshot.get("ancestors"):
        raise ValueError("PostgreSQL accounting needs the observable current cgroup-v2 ancestry")
    return [{key: item[key] for key in ("path", "cpu.max", "memory.max", "cpuset.cpus.effective", "cgroup.subtree_control")}
            for item in snapshot["ancestors"]]


def parse_cpu_group_stat(text):
    values = {}
    for line in text.splitlines():
        key, value = line.split()
        if key in values or not value.isdigit():
            raise ValueError("Invalid/duplicate task cgroup CPU counter")
        values[key] = int(value)
    if not {"usage_usec", "user_usec", "system_usec"} <= values.keys():
        raise ValueError("Task cgroup lacks cumulative CPU usage/user/system counters")
    return values


def process_cpu_identity(pid):
    try:
        raw = Path(f"/proc/{pid}/stat").read_text()
        fields = raw[raw.rfind(")") + 2:].split()
        return {"pid": pid, "parentPid": int(fields[1]), "startTicks": int(fields[19])}
    except (FileNotFoundError, ProcessLookupError):
        return None


def validate_server_group_members(root, members, lookup=process_cpu_identity):
    """Reject foreign/PID-reused owners, retaining exits during acquisition."""
    live, exited = [], []
    for pid in members:
        item = lookup(pid)
        if item is None:
            exited.append(pid)
            continue
        cursor, seen = item, set()
        while cursor["pid"] != root["pid"]:
            if cursor["pid"] in seen or cursor["parentPid"] <= 1:
                raise ValueError("A foreign process entered the task-owned PostgreSQL cgroup")
            seen.add(cursor["pid"])
            cursor = lookup(cursor["parentPid"])
            if cursor is None:
                raise ValueError("PostgreSQL cgroup member ancestry became unobservable")
        if cursor["startTicks"] != root["startTicks"]:
            raise ValueError("PostgreSQL cgroup root PID was reused")
        confirmed = lookup(pid)
        if confirmed is None:
            exited.append(pid)
        elif confirmed["startTicks"] != item["startTicks"]:
            raise ValueError("PostgreSQL cgroup member PID was reused")
        else:
            live.append(item)
    return live, exited


def server_cpu_group_snapshot(descriptor, *, empty=False):
    path = Path(descriptor["path"])
    identity = path.stat()
    if (path.resolve() != path or path.parent != Path(descriptor["parentPath"])
            or not re.fullmatch(r"db-perf-01-pg-[0-9a-f]{24}", path.name)
            or [identity.st_dev, identity.st_ino] != descriptor["deviceInode"]):
        raise ValueError("Task-owned PostgreSQL cgroup identity changed")
    controls = cpu_group_controls(path)
    if controls != descriptor["controls"] or any(child.is_dir() for child in path.iterdir()):
        raise ValueError("Task-owned PostgreSQL cgroup controls or child groups changed")
    members = sorted(int(value) for value in (path / "cgroup.procs").read_text().split())
    if empty:
        if members:
            raise ValueError("Task-owned PostgreSQL cgroup remains populated after owner join")
        live, exited = [], []
    else:
        root = descriptor["serverIdentity"]
        if root["pid"] not in members:
            raise ValueError("PostgreSQL root is outside its task-owned accounting group")
        live, exited = validate_server_group_members(root, members)
        if not any(item["pid"] == root["pid"] for item in live):
            raise ValueError("PostgreSQL root exited during CPU-group acquisition")
    return {"path": str(path), "deviceInode": descriptor["deviceInode"], "controls": controls,
            "serverIdentity": descriptor.get("serverIdentity"), "members": live, "exitedDuringMemberAcquisition": exited,
            "membersVerified": True, "cpu": parse_cpu_group_stat((path / "cpu.stat").read_text())}


def server_cpu_group_delta(first, last):
    for key in ("path", "deviceInode", "controls", "serverIdentity"):
        if first.get(key) != last.get(key):
            raise ValueError("PostgreSQL cumulative CPU accounting group changed within the interval")
    if not first.get("membersVerified") or not last.get("membersVerified"):
        raise ValueError("PostgreSQL CPU accounting membership was not verified")
    deltas = {}
    for key in ("usage_usec", "user_usec", "system_usec"):
        left, right = first["cpu"].get(key), last["cpu"].get(key)
        if type(left) is not int or type(right) is not int or not 0 <= left <= right:
            raise ValueError("PostgreSQL cumulative CPU counters were missing or moved backwards")
        deltas[key] = right - left
    throttle = 0
    if "nr_throttled" in first["cpu"] or "nr_throttled" in last["cpu"]:
        left, right = first["cpu"].get("nr_throttled"), last["cpu"].get("nr_throttled")
        if type(left) is not int or type(right) is not int or not 0 <= left <= right:
            raise ValueError("PostgreSQL task-group bandwidth counters were missing or moved backwards")
        throttle = right - left
    elif isinstance(first["controls"].get("cpu.max"), str):
        raise ValueError("Enabled PostgreSQL task-group bandwidth accounting is unavailable")
    return {"userCpuSeconds": deltas["user_usec"] / 1e6, "systemCpuSeconds": deltas["system_usec"] / 1e6,
            "usageCpuSeconds": deltas["usage_usec"] / 1e6, "rawStartMicroseconds": first["cpu"],
            "rawEndMicroseconds": last["cpu"], "counterMonotonic": True,
            "cpuAccounting": "task_owned_cgroup_v2", "taskGroupThrottleEvents": throttle,
            "cpuGroupIdentity": {key: first[key] for key in ("path", "deviceInode", "serverIdentity")}}


class PostgresCpuGroup:
    """Own one fresh child of the observer's existing cgroup, with no limit edits.

    cpu.stat usage/user/system exist even with the CPU controller disabled:
    https://docs.kernel.org/admin-guide/cgroup-v2.html#cpu-interface-files
    """
    def __init__(self):
        self.descriptor = None
        self.created = False
        self.record = {"required": True, "created": False, "removed": False, "commands": [],
                       "scope": "Cumulative PostgreSQL task-group CPU includes exited children; launcher entry occurs before postgres exec/fork; sudo/tee finish outside the measured server work"}

    def privileged(self, argv):
        command = ["sudo", "-n", *map(str, argv)]
        result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=10)
        self.record["commands"].append({"argv": command, "exitCode": result.returncode,
                                        "stdout": result.stdout, "stderr": result.stderr})
        if result.returncode:
            raise RuntimeError("Task-owned PostgreSQL cgroup operation failed: " + result.stderr.strip())

    def create(self):
        before = cgroup_snapshot()
        self.record["parentControlsBefore"] = cpu_group_parent_controls(before)
        parent = Path(before["ancestors"][0]["path"]).resolve()
        path = parent / ("db-perf-01-pg-" + uuid.uuid4().hex[:24])
        self.record["requestedPath"] = str(path)
        if path.exists():
            raise ValueError("A task cgroup must be newly created, never reused")
        self.privileged(["mkdir", "--", path])
        self.created = self.record["created"] = True
        identity = path.stat()
        self.descriptor = {"path": str(path), "parentPath": str(parent), "deviceInode": [identity.st_dev, identity.st_ino],
                           "controls": cpu_group_controls(path)}
        self.record["descriptor"] = self.descriptor
        self.record["emptyBeforeLaunch"] = server_cpu_group_snapshot(self.descriptor, empty=True)
        self.check_parent()

    def check_parent(self):
        current = cpu_group_parent_controls(cgroup_snapshot())
        if current != self.record["parentControlsBefore"]:
            raise ValueError("Parent cgroup limits/controllers changed during task accounting")
        self.record["parentControlsUnchanged"] = True

    def launch_command(self, command):
        if not self.created or self.descriptor is None:
            raise ValueError("PostgreSQL cgroup must exist before constructing its launcher")
        # The pipeline must succeed before exec. Arguments are separate from this
        # constant script; postgres cannot fork any backend before group entry.
        script = 'set -eu\nprintf "%s\\n" "$$" | sudo -n tee -- "$1/cgroup.procs" >/dev/null\nshift\nexec "$@"'
        return ["/bin/sh", "-c", script, "db-perf-postgres-cgroup", self.descriptor["path"], *command]

    def attach_identity(self, identity):
        if identity is None:
            raise ValueError("PostgreSQL launcher identity is unavailable")
        self.descriptor["serverIdentity"] = {key: identity[key] for key in ("pid", "startTicks")}

    def close(self):
        if not self.created:
            return
        if self.descriptor is None:
            raise RuntimeError("Created PostgreSQL cgroup has no verified identity; retain it for investigation")
        self.record["emptyAfterOwnerJoin"] = server_cpu_group_snapshot(self.descriptor, empty=True)
        self.check_parent()
        self.privileged(["rmdir", "--", self.descriptor["path"]])
        self.record["removed"] = True


def sample(client_pid=None, server_pid=None, server_cpu_group=None):
    start = time.monotonic_ns()
    client, server = tree(client_pid) if client_pid else [], tree(server_pid) if server_pid else []
    quota = cgroup_snapshot()
    host = host_cpu()
    group = server_cpu_group_snapshot(server_cpu_group) if server_cpu_group is not None else None
    acquired = time.monotonic_ns()
    return {"utc": utc(), "monotonicNs": start, "acquiredThroughMonotonicNs": acquired,
            "client": client, "server": server, "clientTotals": tree_totals(client),
            "serverTotals": tree_totals(server), "serverCpuGroup": group, "hostCpuTicks": host, "guestQuota": quota}


def reaped(child):
    if os.waitid(os.P_PID, child.pid, os.WEXITED | os.WNOHANG | os.WNOWAIT) is None:
        return None
    _, status, usage = os.wait4(child.pid, 0)
    child.returncode = os.waitstatus_to_exitcode(status)
    return usage


def stop_and_join(child, initial_signal=signal.SIGTERM, grace_seconds=10):
    usage = reaped(child)
    if usage is not None:
        return usage
    try:
        os.killpg(child.pid, initial_signal)
    except ProcessLookupError:
        pass
    deadline = time.monotonic() + grace_seconds
    while time.monotonic() < deadline:
        usage = reaped(child)
        if usage is not None:
            return usage
        time.sleep(0.05)
    try:
        os.killpg(child.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        usage = reaped(child)
        if usage is not None:
            return usage
        time.sleep(0.05)
    raise RuntimeError("Process owner did not join after bounded terminate and kill")


def usage_fields(usage):
    return {"processLifetimeUserCpuSeconds": usage.ru_utime if usage else None,
            "processLifetimeSystemCpuSeconds": usage.ru_stime if usage else None,
            "processLifetimeMaxRssBytes": usage.ru_maxrss * 1024 if usage else None}


def validate_plan(plan, output):
    if plan.get("provider") not in ("sqlite", "postgresql") or not plan.get("commands"):
        raise ValueError("A provider and nonempty command plan are required")
    names, outputs = set(), set()
    reserved = {output / name for name in ("plan.json", "runner.json", "postgres-owner.json", "postgres.log", "postgres-init.log", "postgres-settings.txt", "postgres-query-plans.log", "postgres-query-plans.json", "query-plan-evidence.json")}
    for item in plan["commands"]:
        if not isinstance(item.get("name"), str):
            raise ValueError("Every command needs a string filename component")
        for suffix in (".command.json", ".stdout.log", ".resources.jsonl"):
            reserved.add(output / (item["name"] + suffix))
    for item in plan["commands"]:
        name = item["name"]
        if not isinstance(name, str) or not name or Path(name).name != name or name in names or name in (".", ".."):
            raise ValueError("Every command needs a unique contained filename component")
        names.add(name)
        argv = item.get("argv")
        if not isinstance(argv, list) or not argv or not all(isinstance(value, str) for value in argv):
            raise ValueError("Commands must be nonempty argument arrays")
        if argv.count("--output") > 1:
            raise ValueError("Each command has at most one explicit result output")
        bound = item.get("timeoutSeconds", 300)
        if not isinstance(bound, (int, float)) or not math.isfinite(bound) or not 0 < bound <= 7200:
            raise ValueError("Every command needs a finite timeout of at most two hours")
        for index, value in enumerate(argv[:-1]):
            if value == "--output":
                target = Path(argv[index + 1].replace("{output}", str(output))).resolve()
                if not target.is_relative_to(output):
                    raise ValueError("Command output must stay inside its fresh evidence directory")
                if target in outputs or target in reserved or target.exists():
                    raise ValueError("A result output must be distinct, fresh and cannot replace observer evidence")
                outputs.add(target)


@contextmanager
def database(plan, output):
    if plan["provider"] != "postgresql":
        yield plan["connection"], None
        return
    settings = plan["postgres"]
    binary, data = Path(settings["bin"]).resolve(), Path(settings["data"]).resolve()
    max_connections = settings.get("maxConnections", 100)
    if type(max_connections) is not int or not settings.get("maxClientConcurrency", 1) + 8 <= max_connections <= 1024:
        raise ValueError("PostgreSQL must reserve eight connections beyond client concurrency")
    if not (data / "PG_VERSION").exists():
        with (output / "postgres-init.log").open("w") as log:
            subprocess.run([binary / "initdb", "-D", data, "-U", "korda", "-A", "trust", "--no-locale", "--encoding=UTF8"],
                           stdout=log, stderr=subprocess.STDOUT, timeout=60, check=True)
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        port = listener.getsockname()[1]
    command = [str(binary / "postgres"), "-D", str(data), "-h", "127.0.0.1", "-p", str(port),
               "-c", "unix_socket_directories=", "-c", f"max_connections={max_connections}", "-c", "fsync=on",
               "-c", "synchronous_commit=on", "-c", "full_page_writes=on", "-c", "timezone=UTC",
               "-c", "shared_buffers=128MB", "-c", "default_transaction_isolation=read committed"]
    query_plans = settings.get("queryPlans")
    if query_plans is not None:
        library = Path(query_plans["library"]).resolve()
        if set(query_plans) != {"library", "sha256"} or sha(library) != query_plans["sha256"]:
            raise ValueError("The diagnostic auto_explain library differs from its frozen input")
        if any(value in str(library) for value in ('"', ',', '\n')):
            raise ValueError("The diagnostic library path cannot be represented as a preload entry")
        command += ["-c", "session_preload_libraries=" + str(library),
                    "-c", "logging_collector=on", "-c", "log_destination=jsonlog",
                    "-c", "log_directory=" + str(output.resolve()), "-c", "log_filename=postgres-query-plans.log",
                    "-c", "log_rotation_age=0", "-c", "log_rotation_size=0",
                    "-c", "log_statement=all", "-c", "log_parameter_max_length=-1",
                    "-c", "auto_explain.log_min_duration=0", "-c", "auto_explain.log_parameter_max_length=-1",
                    "-c", "auto_explain.log_format=json", "-c", "auto_explain.log_analyze=off",
                    "-c", "auto_explain.log_timing=off", "-c", "auto_explain.log_settings=on",
                    "-c", "auto_explain.log_nested_statements=on", "-c", "auto_explain.sample_rate=1"]
    if settings.get("cpus"):
        command = ["taskset", "-c", settings["cpus"], *command]
    receipt = {"argv": command, "startedUtc": utc(), "startedMonotonicNs": time.monotonic_ns(),
               "accounting": "parent plus live/reaped children; sampled tree snapshots are non-atomic"}
    accounting = settings.get("cpuAccounting")
    if accounting not in (None, "task-owned-cgroup-v2"):
        raise ValueError("Unknown PostgreSQL CPU accounting contract")
    cpu_group = PostgresCpuGroup() if accounting else None
    server, body_error = None, None
    with (output / "postgres.log").open("w") as log:
        try:
            launch = command
            if cpu_group is not None:
                cpu_group.create()
                launch = cpu_group.launch_command(command)
                receipt.update(launcherArgv=launch, accounting="task-owned cgroup-v2 cumulative CPU; proc tree and wait4 retained separately")
            server = subprocess.Popen(launch, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            receipt["identity"] = process(server.pid)
            if cpu_group is not None:
                cpu_group.attach_identity(receipt["identity"])
            deadline = time.monotonic() + 30
            while subprocess.run([binary / "pg_isready", "-h", "127.0.0.1", "-p", str(port), "-U", "korda"],
                                 stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=5).returncode:
                if os.waitid(os.P_PID, server.pid, os.WEXITED | os.WNOHANG | os.WNOWAIT) or time.monotonic() > deadline:
                    raise RuntimeError("PostgreSQL failed to become ready; see postgres.log")
                time.sleep(0.05)
            if cpu_group is not None:
                cpu_group.record["beforeClients"] = server_cpu_group_snapshot(cpu_group.descriptor)
                settings["activeCpuGroup"] = cpu_group.descriptor
            base = [binary / "psql", "-h", "127.0.0.1", "-p", str(port), "-U", "korda", "-d", "postgres", "-At"]
            exists = subprocess.check_output([*base, "-c", "SELECT count(*) FROM pg_database WHERE datname='db_perf_01'"], text=True, timeout=10).strip()
            if exists == "0":
                subprocess.run([*base, "-c", "CREATE DATABASE db_perf_01"], stdout=subprocess.DEVNULL, timeout=15, check=True)
            connection = f"postgresql://korda@127.0.0.1:{port}/db_perf_01"
            settings_sql = ("SELECT current_database(); SELECT version(); SHOW fsync; SHOW synchronous_commit; SHOW full_page_writes; "
                            "SHOW shared_buffers; SHOW default_transaction_isolation; SHOW max_connections; "
                            "SHOW server_encoding; SHOW client_encoding; "
                            "SELECT datcollate, datctype FROM pg_database WHERE datname=current_database();")
            if query_plans is not None:
                settings_sql += (" SHOW session_preload_libraries; SHOW log_statement; SHOW log_parameter_max_length; "
                                 "SHOW auto_explain.log_min_duration; SHOW auto_explain.log_analyze; "
                                 "SHOW auto_explain.log_parameter_max_length; SHOW auto_explain.sample_rate;")
            (output / "postgres-settings.txt").write_text(subprocess.check_output(
                [binary / "psql", "--dbname", connection, "-At", "-c", settings_sql], text=True, timeout=10))
            yield connection, server.pid
        except BaseException as error:
            body_error = error
            receipt["error"] = {"type": type(error).__name__, "message": str(error)}
            raise
        finally:
            shutdown_error = None
            if server is not None:
                try:
                    receipt["finalBeforeShutdown"] = sample(server_pid=server.pid,
                        server_cpu_group=cpu_group.descriptor if cpu_group is not None and "serverIdentity" in (cpu_group.descriptor or {}) else None)
                except BaseException as error:
                    receipt["finalSampleError"] = {"type": type(error).__name__, "message": str(error)}
                    shutdown_error = error
                try:
                    receipt.update(usage_fields(stop_and_join(server, signal.SIGINT, grace_seconds=20)))
                    receipt["exitCode"] = server.returncode
                    if server.returncode != 0:
                        raise RuntimeError("PostgreSQL shutdown returned a nonzero exit code")
                except BaseException as error:
                    receipt["shutdownError"] = {"type": type(error).__name__, "message": str(error)}
                    shutdown_error = error
            if cpu_group is not None:
                try:
                    cpu_group.close()
                    if "beforeClients" in cpu_group.record:
                        cpu_group.record["clientsThroughShutdownCpu"] = server_cpu_group_delta(
                            cpu_group.record["beforeClients"], cpu_group.record["emptyAfterOwnerJoin"])
                    if receipt.get("processLifetimeUserCpuSeconds") is not None and cpu_group.descriptor is not None:
                        total = cpu_group.record["emptyAfterOwnerJoin"]["cpu"]
                        cpu_group.record["wait4CrossCheck"] = {
                            "groupLifetimeUserSeconds": total["user_usec"] / 1e6,
                            "groupLifetimeSystemSeconds": total["system_usec"] / 1e6,
                            "wait4ProcessLifetimeUserSeconds": receipt["processLifetimeUserCpuSeconds"],
                            "wait4ProcessLifetimeSystemSeconds": receipt["processLifetimeSystemCpuSeconds"],
                            "scope": "Raw lifetime cross-check, not forced equality: group begins at launcher entry; wait4 includes earlier launcher work and reaped descendants"}
                except BaseException as error:
                    receipt["cpuGroupError"] = {"type": type(error).__name__, "message": str(error)}
                    shutdown_error = error
                receipt["serverCpuGroup"] = cpu_group.record
            receipt.update(finishedUtc=utc(), finishedMonotonicNs=time.monotonic_ns())
            write_json(output / "postgres-owner.json", receipt)
            if shutdown_error is not None and body_error is None:
                raise RuntimeError("PostgreSQL owner did not retire cleanly; aborting subsequent measurements") from shutdown_error


def execute(item, connection, server_pid, output, interval, *, check=True, server_cpu_group=None):
    output = Path(output).resolve()
    validate_plan({"provider": "sqlite", "commands": [item]}, output)
    name = item["name"]
    paths = [output / (name + suffix) for suffix in (".stdout.log", ".resources.jsonl", ".command.json")]
    if any(path.exists() for path in paths):
        raise ValueError("Existing command evidence cannot be overwritten")
    argv = [value.replace("{connection}", connection).replace("{output}", str(output)) for value in item["argv"]]
    if item.get("cpus"):
        argv = ["taskset", "-c", item["cpus"], *argv]
    receipt = {"name": name, "phase": item.get("phase", "exploratory"), "argv": argv,
               "startedUtc": utc(), "startedMonotonicNs": time.monotonic_ns(),
               "timeoutSeconds": item.get("timeoutSeconds", 300), "resourceSamples": paths[1].name,
               "measurementScope": "wait4 CPU/maxRSS are process-lifetime totals; steady measurements use consumer monotonic boundaries",
               "longRunAcceptance": False, "timedOut": False, "observerErrors": []}
    if server_cpu_group is not None:
        receipt["serverCpuGroupIdentity"] = server_cpu_group
    child, usage, interrupted = None, None, None
    try:
        with paths[0].open("x") as log, paths[1].open("x") as samples:
            child = subprocess.Popen(argv, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            receipt["clientIdentity"] = process(child.pid)
            deadline = time.monotonic() + receipt["timeoutSeconds"]
            while True:
                record = sample(child.pid, server_pid, server_cpu_group)
                receipt["lastSample"] = record
                samples.write(json.dumps(record, separators=(",", ":")) + "\n")
                samples.flush()
                usage = reaped(child)
                if usage is not None:
                    break
                if time.monotonic() >= deadline:
                    receipt["timedOut"] = True
                    raise TimeoutError("Command exceeded its recorded finite timeout")
                time.sleep(interval)
    except BaseException as error:
        receipt["observerErrors"].append({"type": type(error).__name__, "message": str(error)})
        if isinstance(error, (KeyboardInterrupt, SystemExit)):
            interrupted = error
    finally:
        if child is not None and child.returncode is None:
            try:
                usage = stop_and_join(child)
            except BaseException as error:
                receipt["observerErrors"].append({"type": type(error).__name__, "message": str(error)})
        receipt.update(usage_fields(usage))
        receipt.update(exitCode=child.returncode if child else None, finishedUtc=utc(), finishedMonotonicNs=time.monotonic_ns())
        receipt["files"] = {path.name: sha(path) for path in paths[:2] if path.exists()}
        receipt["success"] = receipt["exitCode"] == 0 and not receipt["observerErrors"] and not receipt["timedOut"]
        write_json(paths[2], receipt)
        print(name, "exit", receipt["exitCode"], "observer-errors", len(receipt["observerErrors"]), flush=True)
    if interrupted is not None:
        raise interrupted
    if check and not receipt["success"]:
        raise RuntimeError(f"{name} failed; its command, last sample and error evidence were preserved")
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("plan", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--sample-seconds", type=float, default=0.25)
    args = parser.parse_args()
    if not math.isfinite(args.sample_seconds) or not 0.05 <= args.sample_seconds <= 5:
        raise ValueError("Sampling interval outside finite bounds")
    output = args.output.resolve()
    if output.exists() and any(output.iterdir()):
        raise ValueError("Use a fresh output directory; prior observations are immutable")
    plan = json.loads(args.plan.read_text())
    validate_plan(plan, output)
    output.mkdir(parents=True, exist_ok=True)
    (output / "plan.json").write_bytes(args.plan.read_bytes())
    write_json(output / "runner.json", {"runnerSha256": sha(__file__), "planSha256": sha(args.plan),
        "startedUtc": utc(), "cpuAffinity": sorted(os.sched_getaffinity(0)), "ticksPerSecond": os.sysconf("SC_CLK_TCK"),
        "hostQualifiedForLongRuns": False, "resourceAccounting": "client and PostgreSQL trees separate; SQLite is in client",
        "pss": "sampled smaps_rollup; unavailable memory is explicit; acquisition intervals and PID start ticks retained"})
    with database(plan, output) as (connection, server_pid):
        for item in plan["commands"]:
            execute(item, connection, server_pid, output, args.sample_seconds,
                    server_cpu_group=plan.get("postgres", {}).get("activeCpuGroup"))


if __name__ == "__main__":
    main()
