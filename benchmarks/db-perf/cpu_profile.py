#!/usr/bin/env python3
"""Bounded, task-owned CPU sampling of an already frozen Native AOT runtime.

This is a separate diagnostic VM. It never changes campaign inputs, grants
long-run credit, uses a rebuilt executable, or broadens perf to system scope.
"""
from __future__ import annotations

import argparse
import gzip
import hashlib
import importlib
import json
import os
from pathlib import Path
import re
import select
import shutil
import signal
import stat
import struct
import subprocess
import sys
import tarfile
import time
import traceback
import zipfile
import zlib

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
HARNESS = "da153c22ef1093fccc3233ec76bfda96e6cc59df"
ROUTES = ("korda_after", "korda_before", "direct_cpp", "independent_csharp_orm")


def require(value, message):
    if not value:
        raise ValueError(message)


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def utc():
    from datetime import datetime, timezone
    return datetime.now(timezone.utc).isoformat()


def write(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + "\n")


def error_record(error):
    return {"type": type(error).__name__, "message": str(error)}


def checked(command, directory, name, *, timeout=120, cwd=None, env=None):
    """One attempt, with stdout/stderr and even timeout/failure retained."""
    directory.mkdir(parents=True, exist_ok=True)
    out, err = directory / (name + ".stdout.log"), directory / (name + ".stderr.log")
    require(not out.exists() and not err.exists(), "Existing command evidence cannot be replaced")
    record = {"argv": list(map(str, command)), "startedUtc": utc(), "timeoutSeconds": timeout}
    try:
        with out.open("xb") as stdout, err.open("xb") as stderr:
            result = subprocess.run(record["argv"], cwd=cwd, env=env, stdout=stdout, stderr=stderr,
                                    timeout=timeout, check=False)
        record["exitCode"] = result.returncode
    except BaseException as error:
        record["error"] = error_record(error)
        raise
    finally:
        record.update(finishedUtc=utc(), files={p.name: sha(p) for p in (out, err) if p.exists()})
        write(directory / (name + ".command.json"), record)
    require(result.returncode == 0, "Command failed; inspect " + str(directory / (name + ".command.json")))
    return out


def validate_request(path):
    value = json.loads(Path(path).read_text())
    require(value.get("schemaVersion") == 1 and value.get("task") == "DB-Perf-01"
            and value.get("kind") == "cpu-hotspot-diagnostic" and value.get("enabled") is True,
            "Wrong or disabled CPU diagnostic request")
    runtime = value["runtime"]
    require(runtime["repository"] == "IzuSharp/Korda" and runtime["runId"] == 45461269267
            and runtime["artifactId"] == 14953201071 and runtime["harnessCommit"] == HARNESS,
            "Freeze the already observed runtime, not a later candidate")
    for name in ("zipSha256", "tarSha256", "manifestSha256", "requestSha256"):
        require(re.fullmatch("[0-9a-f]{64}", runtime[name]) is not None, "Missing runtime hash " + name)
    require(value["symbolsSourceCommit"] == HARNESS, "Symbol reconstruction source differs")
    require(value["profile"] == {"provider": "postgresql", "seconds": 30, "concurrency": 1,
            "constructionMode": "prepared", "frequencyHz": 99, "stackBytes": 16384,
            "maximumCommandSeconds": 180, "sampleSeconds": 0.5}, "CPU scope or bounds changed")
    require(value["cells"] == [
        {"name": "after", "route": "korda_after"},
        {"name": "before", "route": "korda_before"},
        {"name": "native-product", "route": "direct_cpp", "physicalPrepare": "per-operation", "protocolConfirmation": "product"},
        {"name": "native-reuse", "route": "direct_cpp", "physicalPrepare": "reuse", "protocolConfirmation": "drain-only"},
        {"name": "dapper", "route": "independent_csharp_orm"}], "Frozen diagnostic sequence changed")
    require(value["planDiagnostic"] == {"warmupOperations": 350, "operations": 35,
            "casesOfInterest": ["range", "offset", "early-stop"], "protocolConfirmation": "drain-only",
            "physicalPrepare": ["per-operation", "reuse"]}, "Plan isolation or single-factor pair changed")
    require(value["allowExistingSudoForTaskPidOnly"] is True
            and value["wholeBatchAcceptance"] is False and value["acceptedLongSegments"] == 0,
            "Diagnostics cannot grant performance acceptance")
    return value


def acquire_runtime(request, request_path, output):
    frozen = request["runtime"]
    downloads = output / "download"
    metadata = checked(["gh", "api", f"repos/{frozen['repository']}/actions/artifacts/{frozen['artifactId']}"],
                       downloads, "artifact-metadata")
    info = json.loads(metadata.read_text())
    require(info["id"] == frozen["artifactId"] and info["workflow_run"]["id"] == frozen["runId"]
            and not info["expired"], "Runtime artifact identity/retention differs")
    archive = checked(["gh", "api", f"repos/{frozen['repository']}/actions/artifacts/{frozen['artifactId']}/zip"],
                      downloads, "runtime-zip", timeout=300)
    require(sha(archive) == frozen["zipSha256"], "Original runtime ZIP bytes differ")
    with zipfile.ZipFile(archive) as source:
        require(set(source.namelist()) == {"runtime-bundle.tar.gz", "preparation.json"}, "Unexpected ZIP member set")
        require(sum(i.file_size for i in source.infolist()) < 512 * 1024**2, "Unbounded ZIP")
        for name in source.namelist():
            with source.open(name) as incoming, (downloads / name).open("xb") as target:
                shutil.copyfileobj(incoming, target)
    tar = downloads / "runtime-bundle.tar.gz"
    require(sha(tar) == frozen["tarSha256"], "Original runtime tar bytes differ")
    prepared = json.loads((downloads / "preparation.json").read_text())
    require(prepared["sha256"] == frozen["tarSha256"]
            and prepared["bundleManifestSha256"] == frozen["manifestSha256"]
            and prepared["requestSha256"] == frozen["requestSha256"]
            and prepared["harnessCommit"] == HARNESS, "Preparation receipt differs")
    with tarfile.open(tar) as stream:
        members = stream.getmembers()
        require(len(members) < 10000 and sum(m.size for m in members) < 512 * 1024**2,
                "Unbounded runtime archive")
        stream.extractall(output / "runtime", filter="data")
    bundle = output / "runtime/bundle"
    require(sha(bundle / "bundle.json") == frozen["manifestSha256"]
            and sha(bundle / "request.json") == frozen["requestSha256"], "Frozen runtime metadata differs")
    sys.path.insert(0, str(bundle / "harness"))
    campaign = importlib.import_module("campaign")
    manifest = campaign.verify_bundle(bundle)
    require(manifest["harnessCommit"] == HARNESS, "Bundle harness source differs")
    actual = {key: {"path": path, "sha256": sha(bundle / path)}
              for key, path in manifest["applications"].items()}
    require(actual == request["applications"], "One of the eight original route ELF identities changed")
    write(output / "runtime-verification.json", {"runtime": frozen, "applications": actual,
          "filesVerified": len(manifest["files"]), "originalBytesUsedForExecution": True,
          "requestPath": str(request_path.resolve()), "requestSha256": sha(request_path),
          "timingComparisonEligible": False})
    return bundle, campaign


def elf_identity(path):
    """Read ELF64 little-endian metadata; never alter an executable to add symbols."""
    data = Path(path).read_bytes()
    require(len(data) >= 64 and data[:6] == b"\x7fELF\x02\x01", "Expected Linux ELF64 little-endian")
    header = struct.unpack_from("<16sHHIQQQIHHHHHH", data)
    require(header[2] == 62 and header[11] == 64 and 0 < header[12] < 65535,
            "Unexpected ELF machine/section table")
    sections = [struct.unpack_from("<IIQQQQIIQQ", data, header[6] + index * 64)
                for index in range(header[12])]
    strings = sections[header[13]]
    names = data[strings[4]:strings[4] + strings[5]]
    contents = {}
    for section in sections:
        name = names[section[0]:].split(b"\0", 1)[0].decode()
        require(section[1] == 8 or section[4] + section[5] <= len(data), "ELF section exceeds file")
        contents[name] = data[section[4]:section[4] + section[5]]
    build_id = None
    note = contents.get(".note.gnu.build-id", b"")
    offset = 0
    while offset + 12 <= len(note):
        nsize, dsize, kind = struct.unpack_from("<III", note, offset)
        offset += 12
        name = note[offset:offset + nsize].rstrip(b"\0")
        offset += (nsize + 3) & ~3
        value = note[offset:offset + dsize]
        offset += (dsize + 3) & ~3
        if name == b"GNU" and kind == 3:
            build_id = value.hex()
    link = contents.get(".gnu_debuglink")
    debuglink = None
    if link:
        filename = link.split(b"\0", 1)[0]
        start = (len(filename) + 4) & ~3
        debuglink = {"file": filename.decode(), "crc32": struct.unpack_from("<I", link, start)[0]}
    return {"sha256": hashlib.sha256(data).hexdigest(), "buildId": build_id,
            "debuglink": debuglink, "hasSymtab": ".symtab" in contents,
            "hasDebugInfo": ".debug_info" in contents}


def match_symbols(original, rebuilt, debug):
    left, right, symbols = map(elf_identity, (original, rebuilt, debug))
    require(left["sha256"] == right["sha256"], "Reconstructed ELF is not byte-identical to original")
    require(left["buildId"] and left["buildId"] == right["buildId"] == symbols["buildId"],
            "Original/reconstructed/symbol build IDs do not match")
    require(left["debuglink"] == right["debuglink"] and left["debuglink"] is not None,
            "Original ELF has no matching GNU debuglink")
    require(left["debuglink"]["file"] == debug.name
            and left["debuglink"]["crc32"] == zlib.crc32(debug.read_bytes()) & 0xffffffff
            and symbols["hasDebugInfo"], "Symbol filename/CRC/debug information differs")
    return {"original": left, "reconstructed": right, "symbols": symbols,
            "gnuDebuglinkCrcVerified": True, "rebuiltExecutableWillBeExecuted": False}


def put_symfs(path, symfs):
    target = symfs / str(path.resolve()).lstrip("/")
    target.parent.mkdir(parents=True, exist_ok=True)
    if target.exists():
        require(sha(path) == sha(target), "Existing symbol image differs")
    else:
        shutil.copy2(path, target)
    return target


def reconstruct_symbols(request, bundle, source, output):
    head = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    require(head == HARNESS, "Builder checkout is not the frozen source commit")
    subprocess.run(["git", "-C", str(source), "diff", "--exit-code", "HEAD", "--", "benchmarks/db-perf", "global.json"], check=True)
    campaign_request = json.loads((bundle / "request.json").read_text())
    manifest = json.loads((bundle / "bundle.json").read_text())
    symfs = output / "symbols/symfs"
    results = {}
    for route in ("korda_after", "korda_before", "independent_csharp_orm"):
        original = bundle / manifest["applications"]["postgresql/" + route]
        prior = json.loads((original.parents[1] / "build-receipt.json").read_text())
        target = Path(prior["command"][2]).parent.resolve()
        temporary = Path(os.environ["RUNNER_TEMP"]).resolve()
        require(target.is_absolute() and target.is_relative_to(temporary / "db-perf-prepared/build")
                and not target.exists(), "Symbol rebuild requires the same fresh original external application path")
        builder = source / "benchmarks/db-perf/managed" / ("build_orm.py" if route == "independent_csharp_orm" else "build.py")
        command = [sys.executable, str(builder), "--provider", "postgresql", "--output", str(target),
                   "--manifest", str(bundle / "workload.json")]
        if route == "independent_csharp_orm":
            command += ["--kind", "benchmark"]
        else:
            lane = route.removeprefix("korda_")
            command += ["--feed", str(bundle / "packages" / lane / "postgresql/feed"),
                        "--version", campaign_request[lane]["providers"]["postgresql"]["version"]]
        result = {"route": route, "builderSourceCommit": head, "builderSha256": sha(builder),
                  "originalElf": str(original), "originalSha256": sha(original), "matched": False}
        try:
            checked(command, output / "symbol-commands", route, timeout=900)
            rebuilt = target / "out" / original.name
            info = elf_identity(original)
            require(info["debuglink"], "Frozen AOT ELF lacks a debuglink")
            debug = rebuilt.parent / info["debuglink"]["file"]
            result.update(reconstructed=elf_identity(rebuilt), recoveredSymbols=elf_identity(debug))
            result.update(match_symbols(original, rebuilt, debug), matched=True)
            copy = put_symfs(original, symfs)
            shutil.copy2(debug, copy.parent / debug.name)
            result.update(symbolFile=str(copy.parent / debug.name), symbolSha256=sha(debug),
                          receiptSha256=sha(target / "build-receipt.json"))
        except Exception as error:
            result["error"] = error_record(error)
        finally:
            results[route] = result
            write(output / "symbols/identity.json", results)
            retained = output / "symbol-builds" / route
            retained.mkdir(parents=True, exist_ok=True)
            if target.is_dir():
                for path in target.iterdir():
                    if path.is_file() and path.suffix in (".cs", ".csproj", ".json", ".Config", ".log"):
                        shutil.copy2(path, retained / path.name)
                if (target / "generated").exists():
                    shutil.copytree(target / "generated", retained / "generated")
                if not result["matched"] and (target / "out").is_dir():
                    shutil.copytree(target / "out", retained / "unmatched-output")
        # Preserve source, generated files, restore graph, receipts, ELF and .dbg.
        # Only this reconstruction's reproducible intermediates are removed.
        removed = []
        for name in ("bin", "obj", "packages", "sdk-cache"):
            path = target / name
            if path.is_dir() and not path.is_symlink():
                shutil.rmtree(path)
                removed.append(name)
        write(output / "symbols" / (route + "-cleanup.json"), {"removedReproducibleDirectories": removed,
              "retainedOriginalSha256": sha(original), "originalUnchanged": sha(original) == result["originalSha256"]})
    for relative in manifest["files"]:
        path = bundle / relative
        if path.is_file() and (".so" in path.name or relative == "native/db-perf-native"):
            put_symfs(path, symfs)
    return results, symfs


def find_perf(output):
    candidates = []
    located = shutil.which("perf")
    if located:
        candidates.append(Path(located).resolve())
    candidates.extend(sorted(Path("/usr/lib/linux-tools").glob("*/perf")))
    attempts = []
    for path in dict.fromkeys(candidates):
        if not path.is_file() or path.read_bytes()[:4] != b"\x7fELF":
            continue
        result = subprocess.run([str(path), "version"], capture_output=True, text=True, timeout=10)
        row = {"path": str(path), "sha256": sha(path), "exitCode": result.returncode,
               "stdout": result.stdout, "stderr": result.stderr}
        attempts.append(row)
        if result.returncode == 0:
            row["rootOwnedNotGroupOrWorldWritable"] = path.stat().st_uid == 0 and not path.stat().st_mode & 0o022
            row["perfEventParanoid"] = Path("/proc/sys/kernel/perf_event_paranoid").read_text().strip()
            write(output / "perf-tool.json", {"selected": row, "observedCandidates": attempts,
                  "sysctlOrPermissionChanges": False, "scope": "task PID and inherited threads only"})
            return path, row
    write(output / "perf-tool.json", {"selected": None, "observedCandidates": attempts})
    raise RuntimeError("No usable native perf tool; no workload has been started")


def ns_text(value):
    return f"{value // 1_000_000_000}.{value % 1_000_000_000:09d}"


SAMPLE = re.compile(r"^\s*(\d+)(?:\s*/\s*|\s+)(\d+)\s+(\d+)\.(\d{1,9}):?\s+")


def sample_ledger(path, pid, first, last):
    observed, threads, lost = [], set(), []
    for line in path.read_text(errors="replace").splitlines():
        if "PERF_RECORD_LOST" in line or "LOST_SAMPLES" in line:
            lost.append(line)
        match = SAMPLE.match(line)
        if match is None:
            continue
        identity, tid = int(match[1]), int(match[2])
        stamp = int(match[3]) * 1_000_000_000 + int(match[4].ljust(9, "0"))
        require(identity == pid, "Perf sampled a process outside the held application PID")
        require(first <= stamp <= last, "Filtered sample falls outside the consumer steady window")
        observed.append(stamp)
        threads.add(tid)
    require(observed, "No verified application samples in the exact steady window")
    return {"sampleCount": len(observed), "sampledTids": sorted(threads), "pid": pid,
            "firstSampleMonotonicNs": min(observed), "lastSampleMonotonicNs": max(observed),
            "steadyStartMonotonicNs": first, "steadyEndMonotonicNs": last,
            "lostRecords": lost, "allObservedSamplesInsideWindowAndOwnedPid": True,
            "sampleCountIsCpuSamplingNotOperations": True}


def perf_data_identity(status):
    return {"device": status.st_dev, "inode": status.st_ino, "uid": status.st_uid,
            "gid": status.st_gid, "mode": stat.S_IMODE(status.st_mode)}


def create_perf_data(path):
    # perf opens an existing empty output with O_TRUNC; it does not change its
    # ownership. Creating this private inode as the observer keeps root perf's
    # output readable without chmod, chown or a privileged file-copy command.
    require(path.parent.resolve() == path.parent, "Perf output directory must not contain symlinks")
    descriptor = os.open(path, os.O_CREAT | os.O_EXCL | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    try:
        status = os.fstat(descriptor)
        require(stat.S_ISREG(status.st_mode) and status.st_uid == os.geteuid()
                and stat.S_IMODE(status.st_mode) == 0o600 and status.st_size == 0,
                "Perf output is not an empty observer-owned private regular file")
        return perf_data_identity(status)
    finally:
        os.close(descriptor)


def verify_perf_data(path, expected):
    with os.fdopen(os.open(path, os.O_RDONLY | os.O_NOFOLLOW), "rb") as source:
        status = os.fstat(source.fileno())
        require(stat.S_ISREG(status.st_mode) and perf_data_identity(status) == expected,
                "Perf output inode, owner or mode changed")
        return {"identity": perf_data_identity(status), "size": status.st_size,
                "sha256": hashlib.file_digest(source, "sha256").hexdigest(),
                "ordinaryObserverReadVerified": True, "permissionOrOwnershipChanges": False}


def start_perf(perf, tool, child, identity, directory, request, runner):
    attempts = []
    for privileged in (False, True):
        if privileged:
            previous = attempts[-1]
            detail = (directory / previous["stderr"]).read_text(errors="replace")
            require(re.search(r"[Pp]ermission denied|[Oo]peration not permitted|perf_event_paranoid|[Aa]ccess.*restricted", detail),
                    "Only an observed permission denial permits the scoped sudo fallback")
            require(request["allowExistingSudoForTaskPidOnly"] and tool["rootOwnedNotGroupOrWorldWritable"]
                    and os.environ.get("GITHUB_ACTIONS") == "true", "Scoped existing sudo is unavailable")
        live = runner.process(child.pid)
        require(live is not None and live["startTicks"] == identity["startTicks"], "Held target PID changed")
        label = "sudo-task-pid" if privileged else "unprivileged-task-pid"
        control, ack = directory / (label + ".control"), directory / (label + ".ack")
        os.mkfifo(control, 0o600)
        os.mkfifo(ack, 0o600)
        control_fd = os.open(control, os.O_RDWR | os.O_NONBLOCK)
        ack_fd = os.open(ack, os.O_RDWR | os.O_NONBLOCK)
        data = directory / (label + ".perf.data")
        original_data_identity = create_perf_data(data)
        command = [str(perf), "record", "--clockid", "mono", "-e", "cpu-clock:u", "-F", "99",
                   "--call-graph", "dwarf,16384", "--inherit", "--no-buildid-cache", "-p", str(child.pid),
                   "--delay=-1", "--control=fifo:" + str(control) + "," + str(ack), "-o", str(data)]
        # The outer timeout is owned by the same privilege as perf, so even a
        # broken FIFO has a finite root-owned lifetime without changing sysctls.
        command = ["/usr/bin/timeout", "--signal=INT", "--kill-after=5s", "210s", *command]
        if privileged:
            command = ["/usr/bin/sudo", "-n", *command]
        stdout_path, stderr_path = directory / (label + ".stdout.log"), directory / (label + ".stderr.log")
        stdout, stderr = stdout_path.open("xb"), stderr_path.open("xb")
        process = subprocess.Popen(command, stdout=stdout, stderr=stderr, start_new_session=True)
        record = {"argv": command, "targetPid": child.pid, "targetStartTicks": identity["startTicks"],
                  "privileged": privileged, "stdout": stdout_path.name, "stderr": stderr_path.name,
                  "startedUtc": utc(), "startedMonotonicNs": time.monotonic_ns(),
                  "collectorPid": process.pid, "enabledAck": False,
                  "dataPath": str(data), "precreatedDataIdentity": original_data_identity}
        attempts.append(record)
        write(directory / "perf-start-attempts.json", attempts)
        deadline = time.monotonic() + 8
        os.write(control_fd, b"enable\n")
        while time.monotonic() < deadline and process.poll() is None:
            if select.select([ack_fd], [], [], 0.1)[0]:
                reply = os.read(ack_fd, 1024)
                record["ackBytes"] = reply.decode(errors="replace")
                if reply == b"ack\n":
                    record["enabledAck"] = True
                    record["enabledAckMonotonicNs"] = time.monotonic_ns()
                    write(directory / "perf-start-attempts.json", attempts)
                    return {"process": process, "control": control_fd, "ack": ack_fd,
                            "stdout": stdout, "stderr": stderr, "data": data,
                            "record": record, "attempts": attempts, "controlPath": control, "ackPath": ack}
                break
        os.write(control_fd, b"stop\n")
        try:
            process.wait(timeout=12)
        except subprocess.TimeoutExpired:
            # No workload has been released. The timeout remains task-scoped.
            process.wait(timeout=215)
        record.update(exitCode=process.returncode, finishedUtc=utc())
        stdout.close(); stderr.close()
        os.close(control_fd); os.close(ack_fd)
        control.unlink(); ack.unlink()
        try:
            record["dataVerification"] = verify_perf_data(data, original_data_identity)
        finally:
            write(directory / "perf-start-attempts.json", attempts)
        if privileged:
            raise RuntimeError("Scoped perf could not acknowledge enable; workload was not released")
    raise AssertionError("Unreachable perf startup")


def finish_perf(handle, directory):
    process = handle["process"]
    if process.poll() is None:
        os.write(handle["control"], b"stop\n")
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.wait(timeout=215)
    handle["record"].update(exitCode=process.returncode, finishedUtc=utc(), finishedMonotonicNs=time.monotonic_ns())
    handle["stdout"].close(); handle["stderr"].close()
    os.close(handle["control"]); os.close(handle["ack"])
    handle["controlPath"].unlink(); handle["ackPath"].unlink()
    try:
        verified = verify_perf_data(handle["data"], handle["record"]["precreatedDataIdentity"])
        handle["record"].update(dataVerification=verified, dataSha256=verified["sha256"])
    finally:
        write(directory / "perf-start-attempts.json", handle["attempts"])
    require(process.returncode == 0 and handle["data"].is_file(), "Perf did not finish successfully")


def retain_mapped_symbols(pid, directory, symfs, retained):
    """Copy only ELF files actually mapped executable by this owned client."""
    maps = Path(f"/proc/{pid}/maps").read_bytes()
    for line in maps.decode(errors="replace").splitlines():
        fields = line.split(maxsplit=5)
        if len(fields) != 6 or "x" not in fields[1] or not fields[5].startswith("/"):
            continue
        path = Path(fields[5])
        if str(path) in retained:
            continue
        row = {"path": str(path), "device": fields[3], "inode": int(fields[4]),
               "firstObservedMonotonicNs": time.monotonic_ns(),
               "source": "owned client's executable /proc mapping"}
        try:
            require(not fields[5].endswith(" (deleted)"), "An executable mapping lost its source file")
            status = path.stat()
            require(stat.S_ISREG(status.st_mode), "Executable mapping is not a regular file")
            device = ":".join(f"{part:x}" for part in (os.major(status.st_dev), os.minor(status.st_dev)))
            mapped_device = ":".join(f"{int(part, 16):x}" for part in fields[3].split(":"))
            require(status.st_ino == int(fields[4]) and device == mapped_device,
                    "An executable mapping no longer matches its file inode/device")
            with path.open("rb") as source:
                require(source.read(4) == b"\x7fELF", "Executable mapping is not an ELF image")
            copied = put_symfs(path, symfs)
            row.update(status="copied", resolvedPath=str(path.resolve()), sha256=sha(copied), symfsPath=str(copied))
        except (OSError, ValueError) as error:
            row.update(status="unknown", error=error_record(error), otherProcessLookupAttempted=False)
        retained[str(path)] = row
    write(directory / "mapped-symbol-images.json", retained)
    (directory / "application.maps").write_bytes(maps)


def observe_application(child, executable, identity, directory, server, group, request, runner, handle, symfs):
    deadline = time.monotonic() + request["profile"]["maximumCommandSeconds"]
    actual = None
    mapped_symbols = {}
    with (directory / "client.resources.jsonl").open("x") as samples:
        while True:
            row = runner.sample(child.pid, server, group)
            samples.write(json.dumps(row, separators=(",", ":")) + "\n")
            samples.flush()
            try:
                image = Path(f"/proc/{child.pid}/exe").resolve(strict=True)
                if image == executable.resolve() and actual is None:
                    actual = runner.process(child.pid)
                    require(actual is not None and actual["startTicks"] == identity["startTicks"], "Application PID changed at exec")
                    require(sha(image) == sha(executable), "Executed ELF differs")
                    write(directory / "application-identity.json", {"launcherIdentity": identity,
                          "applicationIdentity": actual, "exe": str(image), "sha256": sha(image),
                          "execRetainedPid": True, "collectorIsNotClient": True})
                if actual is not None:
                    retain_mapped_symbols(child.pid, directory, symfs, mapped_symbols)
            except FileNotFoundError:
                pass
            usage = runner.reaped(child)
            if usage is not None:
                require(actual is not None, "Actual application exec was not observed")
                return usage, actual
            require(handle["process"].poll() is None, "Perf exited while the original application was still running")
            require(time.monotonic() < deadline, "Profiled application exceeded its finite timeout")
            time.sleep(request["profile"]["sampleSeconds"])


def profile_cell(cell, campaign, request, perf, tool, symbols, symfs, output):
    import run as runner
    provider, route = "postgresql", cell["route"]
    directory = output / "cpu-cells" / cell["name"]
    directory.mkdir(parents=True)
    record = {"cell": cell, "startedUtc": utc(), "status": "started", "diagnostic": "cpu-sampling",
              "timingComparisonEligible": False, "longRunAcceptance": False, "errors": []}
    write(directory / "diagnostic.json", record)
    child = handle = usage = None
    gate_read = gate_write = None
    try:
        if route != "direct_cpp":
            require(symbols.get(route, {}).get("matched") is True, "Matched original AOT symbols are unavailable")
        seed = campaign.seed_path(provider)
        shutil.copytree(seed / "pgdata", directory / "pgdata")
        plan = campaign.dbplan(provider, directory)
        require("queryPlans" not in plan["postgres"], "CPU sampling cannot enable auto_explain")
        contract = {key: cell[key] for key in ("physicalPrepare", "protocolConfirmation")} if route == "direct_cpp" else None
        argv = campaign.argv(provider, route, 30, 1, None, directory / "result.json", contract)
        executable = campaign.executable(provider, route)
        write(directory / "plan.json", plan)
        with runner.database(plan, directory) as (connection, server):
            actual_argv = [value.replace("{connection}", connection) for value in argv]
            gate_read, gate_write = os.pipe()
            launch = ["taskset", "-c", ",".join(map(str, campaign.request["admission"]["clientCpus"])),
                      sys.executable, str(Path(__file__).resolve()), "exec-gated", "--fd", str(gate_read), "--", *actual_argv]
            with (directory / "client.stdout.log").open("xb") as log:
                child = subprocess.Popen(launch, stdout=log, stderr=subprocess.STDOUT,
                                         pass_fds=(gate_read,), start_new_session=True)
                os.close(gate_read); gate_read = None
                identity = runner.process(child.pid)
                require(identity is not None, "Held child identity unavailable")
                record.update(argv=actual_argv, launcherArgv=launch, launcherIdentity=identity)
                write(directory / "diagnostic.json", record)
                primary = None
                try:
                    handle = start_perf(perf, tool, child, identity, directory, request, runner)
                    require(runner.process(child.pid)["startTicks"] == identity["startTicks"], "Target PID was reused")
                    os.write(gate_write, b"G")
                    os.close(gate_write); gate_write = None
                    usage, actual = observe_application(child, executable, identity, directory, server,
                        plan["postgres"].get("activeCpuGroup"), request, runner, handle, symfs)
                    record.update(clientIdentity=actual, exitCode=child.returncode, **runner.usage_fields(usage))
                    require(child.returncode == 0, "Original profiled application failed")
                except BaseException as error:
                    primary = error
                    record["errors"].append({"phase": "client-or-profiler", **error_record(error)})
                    raise
                finally:
                    if gate_write is not None:
                        os.close(gate_write); gate_write = None
                    cleanup_errors = []
                    try:
                        if child is not None and child.returncode is None:
                            usage = runner.stop_and_join(child)
                    except Exception as error:
                        cleanup_errors.append(error)
                        record["errors"].append({"phase": "client-join", **error_record(error)})
                    try:
                        if handle is not None:
                            finish_perf(handle, directory)
                    except Exception as error:
                        cleanup_errors.append(error)
                        record["errors"].append({"phase": "perf-join", **error_record(error)})
                    if cleanup_errors and primary is None:
                        raise cleanup_errors[0]
        campaign.verify_server_accounting(directory)
        raw = json.loads((directory / "result.json").read_text())
        module = importlib.import_module("campaign")
        module.validate_provider_identity(raw, provider, route)
        require(module.get(raw, "executableSha256") == sha(executable), "Raw result executable SHA differs")
        require(module.get(raw, "mode") == "prepared" and module.get(raw, "concurrency") == 1
                and module.get(raw, "emptyDriver") is False, "Actual workload contract differs")
        if route.startswith("korda_"):
            expected = campaign.request[route.removeprefix("korda_")]["providers"][provider]
            require(module.get(raw, "nativeAot") is True
                    and module.get(raw, "loadedNativeSha256") == expected["nativeSha256"]
                    and module.get(raw, "packageVersion") == expected["version"], "Actual AOT/package/native identity differs")
        elif route == "direct_cpp":
            require(module.get(raw, "physicalPreparation") == contract["physicalPrepare"]
                    and module.get(raw, "protocolConfirmation") == contract["protocolConfirmation"]
                    and module.get(raw, "inputImmutable") is True
                    and module.get(raw, "manifestSha256") == module.get(raw, "manifestSha256After") == campaign.request["workload"]["sha256"]
                    and module.get(raw, "accountingReconciles") is True, "Native contract or manifest differs")
        else:
            require(module.get(raw, "nativeAot") is True, "Independent ORM must remain Native AOT")
        measurement = module.normalized_result(raw, campaign.cases, 30, None, require_coverage=True)
        require(measurement["counts"]["failed"] == 0 and measurement["counts"]["succeeded"] > 0,
                "Profiled business operations failed")
        first, last = module.get(raw, "steadyStartMonotonicNs"), module.get(raw, "steadyEndMonotonicNs")
        require(isinstance(first, int) and isinstance(last, int) and last > first, "Missing exact steady window")
        span = ns_text(first) + "," + ns_text(last)
        common = [str(perf), "script", "-i", str(handle["data"]), "--ns", "--time", span, "--symfs", str(symfs)]
        ledger = checked([*common, "--show-lost-events", "-F", "pid,tid,time,event"], directory, "steady-sample-ledger")
        coverage = sample_ledger(ledger, child.pid, first, last)
        checked([*common, "-F", "comm,pid,tid,time,event,ip,sym,dso"], directory, "steady-stacks")
        checked([str(perf), "report", "--stdio", "--no-children", "--percent-limit", "0",
                 "--sort", "comm,pid,tid,dso,symbol", "--time", span, "--symfs", str(symfs),
                 "-i", str(handle["data"])], directory, "steady-report")
        checked([str(perf), "buildid-list", "-i", str(handle["data"])], directory, "recorded-buildids")
        require(not coverage["lostRecords"], "Observed lost perf records; retain partial profile without complete coverage claim")
        record.update(status="completed", correctnessAndOwners=measurement, samplingCoverage=coverage,
                      originalElfSha256=sha(executable), rawResultSha256=sha(directory / "result.json"),
                      profileDataSha256=sha(handle["data"]), profileDataPath=handle["data"].name,
                      systemCpuStacksCollected=False, wallTimeWaitingExplained=False,
                      symbolIdentity=symbols.get(route, {"scope": "frozen direct native ELF symbol table; no AOT symbols needed"}))
    except Exception as error:
        record["status"] = "failed"
        record["errors"].append(error_record(error))
        record["traceback"] = traceback.format_exc()
    finally:
        if gate_read is not None:
            os.close(gate_read)
        if gate_write is not None:
            os.close(gate_write)
        if child is not None:
            record.update(exitCode=child.returncode, **runner.usage_fields(usage))
        record["unsafeUnjoinedOwner"] = child is not None and child.returncode is None
        attempts_path = directory / "perf-start-attempts.json"
        if attempts_path.exists():
            attempts = json.loads(attempts_path.read_text())
            record["unsafeUnjoinedOwner"] |= any(attempt.get("exitCode") is None for attempt in attempts)
        owner_path = directory / "postgres-owner.json"
        if owner_path.exists():
            try:
                importlib.import_module("campaign").server_owner_accounting(json.loads(owner_path.read_text()))
                record["postgresOwnerRetirementVerified"] = True
            except Exception as error:
                record["unsafeUnjoinedOwner"] = True
                record["errors"].append({"phase": "postgres-owner-final-check", **error_record(error)})
        record["finishedUtc"] = utc()
        record["evidenceFiles"] = {p.name: sha(p) for p in directory.iterdir() if p.is_file() and p.name != "diagnostic.json"}
        write(directory / "diagnostic.json", record)
    return record


def self_test():
    """Finite positive/negative controls of identity and sample attribution only."""
    import tempfile
    count = 0
    with tempfile.TemporaryDirectory(prefix="db-perf-cpu-controls-") as temporary:
        root = Path(temporary)
        output = root / "perf.data"
        before = create_perf_data(output)
        output.write_bytes(b"controlled output; no perf invocation")
        require(verify_perf_data(output, before)["sha256"] == sha(output), "Precreated data inode was not retained")
        count += 1
        output.rename(root / "original.perf.data")
        output.write_bytes(b"replacement")
        try:
            verify_perf_data(output, before)
        except ValueError:
            count += 1
        else:
            raise AssertionError("A replacement perf inode was accepted")
        output.unlink()
        output.symlink_to(root / "original.perf.data")
        try:
            verify_perf_data(output, before)
        except OSError:
            count += 1
        else:
            raise AssertionError("A symlink perf output was accepted")
        ledger = root / "samples.txt"
        ledger.write_text(" 123/124 10.100000001: cpu-clock:u:\n 123 125 10.200000001: cpu-clock:u:\n")
        require(sample_ledger(ledger, 123, 10_000_000_000, 11_000_000_000)["sampleCount"] == 2,
                "Valid PID/time sample control failed")
        count += 1
        for text in ("999/124 10.100000001: cpu-clock:u:\n", "123/124 9.100000001: cpu-clock:u:\n", ""):
            ledger.write_text(text)
            try:
                sample_ledger(ledger, 123, 10_000_000_000, 11_000_000_000)
            except ValueError:
                count += 1
            else:
                raise AssertionError("Unowned/out-of-window/empty samples were accepted")

        def fixture(path, extra):
            names = b"\0.shstrtab\0.note.gnu.build-id\0" + b"".join(name.encode() + b"\0" for name in extra)
            note = struct.pack("<III", 4, 4, 3) + b"GNU\0" + b"ABCD"
            payloads = {".shstrtab": names, ".note.gnu.build-id": note, **extra}
            data = bytearray(b"\0" * 64)
            entries = [(0,) * 10]
            for name, payload in payloads.items():
                entries.append((names.index(name.encode() + b"\0"), 1, 0, 0, len(data), len(payload), 0, 0, 1, 0))
                data.extend(payload)
            offset = len(data)
            for entry in entries:
                data.extend(struct.pack("<IIQQQQIIQQ", *entry))
            struct.pack_into("<16sHHIQQQIHHHHHH", data, 0, b"\x7fELF\x02\x01" + b"\0" * 10,
                             2, 62, 1, 0, 0, offset, 0, 64, 0, 0, 64, len(entries), 1)
            path.write_bytes(data)
        debug = root / "app.dbg"
        fixture(debug, {".debug_info": b"controlled metadata, never executable"})
        name = debug.name.encode() + b"\0"
        link = name + b"\0" * (-len(name) % 4) + struct.pack("<I", zlib.crc32(debug.read_bytes()) & 0xffffffff)
        original, rebuilt = root / "original", root / "rebuilt"
        fixture(original, {".gnu_debuglink": link})
        shutil.copyfile(original, rebuilt)
        require(match_symbols(original, rebuilt, debug)["gnuDebuglinkCrcVerified"], "Exact symbol control failed")
        count += 1
        for target in (rebuilt, debug):
            saved = target.read_bytes()
            target.write_bytes(saved + b"changed")
            try:
                match_symbols(original, rebuilt, debug)
            except ValueError:
                count += 1
            else:
                raise AssertionError("Altered executable/symbol bytes were accepted")
            target.write_bytes(saved)
    print(json.dumps({"controlsPassed": count, "databaseExecutions": 0, "perfExecutions": 0,
                      "scope": "pure sample/ELF identity controls; not actual profiling"}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="action", required=True)
    sub.add_parser("self-test")
    validate = sub.add_parser("validate")
    validate.add_argument("--request", type=Path, default=HERE / "cpu-profile-request.json")
    run = sub.add_parser("run")
    run.add_argument("--request", type=Path, default=HERE / "cpu-profile-request.json")
    run.add_argument("--source", type=Path, required=True)
    run.add_argument("--output", type=Path, required=True)
    gate = sub.add_parser("exec-gated")
    gate.add_argument("--fd", type=int, required=True)
    gate.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if args.action == "exec-gated":
        command = args.command[1:] if args.command[:1] == ["--"] else args.command
        require(command and Path(command[0]).is_absolute(), "Gated command needs an explicit original executable")
        require(os.read(args.fd, 1) == b"G", "Parent did not authorize release of this held child")
        os.close(args.fd)
        os.execvpe(command[0], command, os.environ)
    if args.action == "self-test":
        self_test()
        return
    request = validate_request(args.request)
    if args.action == "validate":
        print(json.dumps({"valid": True, "runtimeRun": request["runtime"]["runId"],
                          "cpuCells": len(request["cells"]), "nativePlanPairs": 2, "measurementExecuted": False}))
        return
    output = args.output.resolve()
    require(not output.exists(), "Use a fresh diagnostic directory; prior observations cannot be overwritten")
    output.mkdir(parents=True)
    state = {"schemaVersion": 1, "task": "DB-Perf-01", "kind": "cpu-hotspot-diagnostic",
             "startedUtc": utc(), "status": "started", "profileRequestSha256": sha(args.request),
             "controllerSha256": sha(__file__), "cells": [], "planCells": [], "errors": [],
             "timingComparisonEligible": False, "qualifiedLongSegments": [], "wholeBatchAccepted": False}
    write(output / "diagnostic.json", state)
    shutil.copyfile(args.request, output / "cpu-profile-request.json")
    try:
        require(os.environ.get("GITHUB_ACTIONS") == "true", "This diagnostic is assigned to its own Linux workflow VM")
        bundle, module = acquire_runtime(request, args.request, output)
        # The artifact token is no longer needed. Never propagate step-scoped
        # credentials into a process whose user stacks will be retained.
        for name in ("GH_TOKEN", "GITHUB_TOKEN", "NUGET_API_KEY", "ACTIONS_RUNTIME_TOKEN", "ACTIONS_ID_TOKEN_REQUEST_TOKEN"):
            os.environ.pop(name, None)
        perf, tool = find_perf(output)
        print("Reconstructing exact original AOT symbols", flush=True)
        symbols, symfs = reconstruct_symbols(request, bundle, args.source.resolve(), output)
        os.environ["LD_LIBRARY_PATH"] = str(bundle / "postgres/lib") + ":" + str(bundle / "native")
        os.environ["DEBUGINFOD_URLS"] = ""
        campaign = module.Campaign(bundle, output / "database-diagnostics", False)
        required = set(campaign.request["admission"]["clientCpus"] + campaign.request["admission"]["serverCpus"]
                       + campaign.request["admission"]["observerCpus"])
        require(required <= set(os.sched_getaffinity(0)), "Frozen logical CPU partition is unavailable")
        write(output / "environment.json", {"identity": module.environment_identity(),
              "uname": list(os.uname()), "cpuinfo": Path("/proc/cpuinfo").read_text(),
              "affinityBefore": sorted(os.sched_getaffinity(0)), "clientCpus": campaign.request["admission"]["clientCpus"],
              "serverCpus": campaign.request["admission"]["serverCpus"], "observerCpus": campaign.request["admission"]["observerCpus"],
              "qualifiedForFormalTiming": False, "separateVmFromFormalCampaign": True,
              "sysctlOrPermissionChanges": False, "perfTool": tool})
        os.sched_setaffinity(0, set(campaign.request["admission"]["observerCpus"]))
        campaign.initialize("postgresql")
        for cell in request["cells"]:
            print("CPU diagnostic " + cell["name"], flush=True)
            if cell["route"] == "independent_csharp_orm" and not symbols[cell["route"]]["matched"]:
                observed = {"cell": cell, "status": "skipped_optional_symbols_unavailable",
                            "symbolFailure": symbols[cell["route"]], "workloadExecutions": 0}
            else:
                observed = profile_cell(cell, campaign, request, perf, tool, symbols, symfs, output)
            state["cells"].append({"name": cell["name"], "status": observed["status"],
                                   "details": observed if observed["status"].startswith("skipped") else
                                   str((output / "cpu-cells" / cell["name"] / "diagnostic.json").relative_to(output))})
            write(output / "diagnostic.json", state)
            require(not observed.get("unsafeUnjoinedOwner", False), "An owned process was not proven retired; later cells are not started")
        for preparation in request["planDiagnostic"]["physicalPrepare"]:
            name = "native-plan-" + preparation + "-drain-only"
            print("Independent query plan diagnostic " + name, flush=True)
            try:
                observed = campaign.run_cell("postgresql", "direct_cpp", name, 0, 1,
                    native_contract={"physicalPrepare": preparation, "protocolConfirmation": "drain-only"},
                    diagnostic="query-plan", require_coverage=True)
                require(observed["sloPassed"] and not observed["timingComparisonEligible"], "Native plan diagnostic failed")
                state["planCells"].append({"name": name, "status": "completed", "rawPath": observed["rawPath"],
                     "rawSha256": observed["rawSha256"], "casesOfInterest": request["planDiagnostic"]["casesOfInterest"],
                     "fullWorkloadWarmupOperations": 350, "measuredOperations": 35,
                     "oneCaseOccursElevenTimesAcrossWarmupAndMeasuredCycle": True,
                     "perfEnabled": False, "onlyPhysicalPrepareVaries": True,
                     "genericCustomPlanModeProven": False})
            except Exception as error:
                state["planCells"].append({"name": name, "status": "failed", "error": error_record(error)})
            write(output / "diagnostic.json", state)
        module.verify_bundle(bundle)
        state["originalRuntimeInventoryUnchangedAfterDiagnostics"] = True
        require(all(c["status"] in ("completed", "skipped_optional_symbols_unavailable") for c in state["cells"])
                and all(c["status"] == "completed" for c in state["planCells"]), "Some frozen diagnostic cells failed; no automatic resampling")
        state["status"] = "completed"
    except BaseException as error:
        state["status"] = "failed"
        state["errors"].append(error_record(error))
        state["traceback"] = traceback.format_exc()
    finally:
        state["finishedUtc"] = utc()
        write(output / "diagnostic.json", state)
        print(json.dumps({"status": state["status"], "output": str(output), "qualifiedLongSegments": 0}), flush=True)
    if state["status"] != "completed":
        raise SystemExit(1)


if __name__ == "__main__":
    main()
