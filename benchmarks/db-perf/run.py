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


def sample(client_pid=None, server_pid=None):
    start = time.monotonic_ns()
    client, server = tree(client_pid) if client_pid else [], tree(server_pid) if server_pid else []
    quota = cgroup_snapshot()
    host = host_cpu()
    acquired = time.monotonic_ns()
    return {"utc": utc(), "monotonicNs": start, "acquiredThroughMonotonicNs": acquired,
            "client": client, "server": server, "clientTotals": tree_totals(client),
            "serverTotals": tree_totals(server), "hostCpuTicks": host, "guestQuota": quota}


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
    reserved = {output / name for name in ("plan.json", "runner.json", "postgres-owner.json", "postgres.log", "postgres-init.log", "postgres-settings.txt")}
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
            subprocess.run([binary / "initdb", "-D", data, "-U", "korda", "-A", "trust", "--no-locale"],
                           stdout=log, stderr=subprocess.STDOUT, timeout=60, check=True)
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        port = listener.getsockname()[1]
    command = [str(binary / "postgres"), "-D", str(data), "-h", "127.0.0.1", "-p", str(port),
               "-c", "unix_socket_directories=", "-c", f"max_connections={max_connections}", "-c", "fsync=on",
               "-c", "synchronous_commit=on", "-c", "full_page_writes=on", "-c", "timezone=UTC",
               "-c", "shared_buffers=128MB", "-c", "default_transaction_isolation=read committed"]
    if settings.get("cpus"):
        command = ["taskset", "-c", settings["cpus"], *command]
    receipt = {"argv": command, "startedUtc": utc(), "startedMonotonicNs": time.monotonic_ns(),
               "accounting": "parent plus live/reaped children; sampled tree snapshots are non-atomic"}
    server, body_error = None, None
    with (output / "postgres.log").open("w") as log:
        try:
            server = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            receipt["identity"] = process(server.pid)
            deadline = time.monotonic() + 30
            while subprocess.run([binary / "pg_isready", "-h", "127.0.0.1", "-p", str(port), "-U", "korda"],
                                 stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=5).returncode:
                if os.waitid(os.P_PID, server.pid, os.WEXITED | os.WNOHANG | os.WNOWAIT) or time.monotonic() > deadline:
                    raise RuntimeError("PostgreSQL failed to become ready; see postgres.log")
                time.sleep(0.05)
            base = [binary / "psql", "-h", "127.0.0.1", "-p", str(port), "-U", "korda", "-d", "postgres", "-At"]
            exists = subprocess.check_output([*base, "-c", "SELECT count(*) FROM pg_database WHERE datname='db_perf_01'"], text=True, timeout=10).strip()
            if exists == "0":
                subprocess.run([*base, "-c", "CREATE DATABASE db_perf_01"], stdout=subprocess.DEVNULL, timeout=15, check=True)
            connection = f"postgresql://korda@127.0.0.1:{port}/db_perf_01"
            settings_sql = ("SELECT version(); SHOW fsync; SHOW synchronous_commit; SHOW full_page_writes; "
                            "SHOW shared_buffers; SHOW default_transaction_isolation; SHOW max_connections;")
            (output / "postgres-settings.txt").write_text(subprocess.check_output([*base, "-c", settings_sql], text=True, timeout=10))
            yield connection, server.pid
        except BaseException as error:
            body_error = error
            receipt["error"] = {"type": type(error).__name__, "message": str(error)}
            raise
        finally:
            shutdown_error = None
            if server is not None:
                try:
                    receipt["finalBeforeShutdown"] = sample(server_pid=server.pid)
                except BaseException as error:
                    receipt["finalSampleError"] = {"type": type(error).__name__, "message": str(error)}
                try:
                    receipt.update(usage_fields(stop_and_join(server, signal.SIGINT, grace_seconds=20)))
                    receipt["exitCode"] = server.returncode
                    if server.returncode != 0:
                        raise RuntimeError("PostgreSQL shutdown returned a nonzero exit code")
                except BaseException as error:
                    receipt["shutdownError"] = {"type": type(error).__name__, "message": str(error)}
                    shutdown_error = error
            receipt.update(finishedUtc=utc(), finishedMonotonicNs=time.monotonic_ns())
            write_json(output / "postgres-owner.json", receipt)
            if shutdown_error is not None and body_error is None:
                raise RuntimeError("PostgreSQL owner did not retire cleanly; aborting subsequent measurements") from shutdown_error


def execute(item, connection, server_pid, output, interval, *, check=True):
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
    child, usage, interrupted = None, None, None
    try:
        with paths[0].open("x") as log, paths[1].open("x") as samples:
            child = subprocess.Popen(argv, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            receipt["clientIdentity"] = process(child.pid)
            deadline = time.monotonic() + receipt["timeoutSeconds"]
            while True:
                record = sample(child.pid, server_pid)
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
            execute(item, connection, server_pid, output, args.sample_seconds)


if __name__ == "__main__":
    main()
