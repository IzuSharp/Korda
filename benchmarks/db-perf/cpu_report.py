#!/usr/bin/env python3
"""Replay reports from one immutable CPU artifact; never record or run a workload."""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import subprocess
import sys
import time
import traceback
import zipfile
import zlib

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
SOURCE_COMMIT = "9acdc6a8cef25d6f51feaf69e553b36027ab3975"
HELPER_SHA = "77398b0a0c0781f336e47ee0c0cff4edcdc69808e43195ce8feb13b0c585126c"
ZIP_SHA = "9f23034ed6784fab5f8ae59ede4a8c1ed1085d44e57e47b858adb802cf304595"
PERF_SHA = "d5dcdbc7c08235061ad8686e60f91d2db7bc1ecd2eb025f96a1d0dd586c8e7db"
CELLS = ("after", "before", "native-product", "native-reuse", "dapper")
PREFIX = "db-perf-cpu/"
ERROR = re.compile(r"(?im)^\s*(?:error:|fatal:|unknown\s+--sort\s+key)|unknown\s+--sort\s+key")
EVENT = re.compile(r"^\s*(\d+)\s*/\s*(\d+)\s+(\d+)\.(\d{1,9}):\s+cpu-clock:u:\s*$")
REQUIRED_CELL_FILES = (
    "diagnostic.json", "result.json", "application-identity.json", "perf-start-attempts.json",
    "mapped-symbol-images.json", "postgres-owner.json", "sudo-task-pid.perf.data",
    *(name + suffix for name in ("steady-sample-ledger", "steady-stacks", "steady-report", "recorded-buildids")
      for suffix in (".command.json", ".stdout.log", ".stderr.log")),
)


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
    with path.open("x") as stream:
        stream.write(json.dumps(value, indent=2) + "\n")


def error_record(error):
    return {"type": type(error).__name__, "message": str(error)}


def get(value, name):
    keys = [key for key in value if key.lower() == name.lower()]
    require(len(keys) == 1, "Missing or ambiguous raw field: " + name)
    return value[keys[0]]


def validate_request(path):
    request = json.loads(Path(path).read_text())
    require(request.get("schemaVersion") == 1 and request.get("task") == "DB-Perf-01"
            and request.get("kind") == "offline-cpu-report-recovery" and request.get("enabled") is True,
            "Wrong or disabled offline recovery request")
    require(request["artifact"] == {"repository": "IzuSharp/Korda", "runId": 45473095059,
            "artifactId": 14959730098, "headCommit": SOURCE_COMMIT,
            "zipSha256": ZIP_SHA, "zipBytes": 65082845, "memberCount": 526,
            "expandedBytes": 251618976}, "Original artifact identity changed")
    require(request["helper"] == {"commit": SOURCE_COMMIT,
            "path": "benchmarks/db-perf/cpu_profile.py", "sha256": HELPER_SHA},
            "Only the fixed independently reviewed identity helper may be imported")
    require(request["originalProfileRequestSha256"] == "f5096e9f07058549652d6c66b4a6b1c0defbd6194d253be2ab57f7faf83f720c"
            and request["perfSha256"] == PERF_SHA and request["cells"] == list(CELLS),
            "Original profile, perf tool or five-cell sequence changed")
    require(request["reportSort"] == "comm,pid,dso,symbol"
            and request["maximumCommandSeconds"] == 120
            and request["recordNewSamples"] is False and request["executeApplications"] is False
            and request["acceptedLongSegments"] == 0 and request["wholeBatchAcceptance"] is False,
            "Offline reporting cannot run workloads or grant performance acceptance")
    return request


def command(argv, directory, name, *, timeout=120):
    """Retain one command and both streams, including zero-exit error output."""
    directory.mkdir(parents=True, exist_ok=True)
    out, err = directory / (name + ".stdout.log"), directory / (name + ".stderr.log")
    record = {"argv": list(map(str, argv)), "startedUtc": utc(), "timeoutSeconds": timeout}
    try:
        with out.open("xb") as stdout, err.open("xb") as stderr:
            result = subprocess.run(record["argv"], stdout=stdout, stderr=stderr,
                                    timeout=timeout, check=False)
        record["exitCode"] = result.returncode
    except BaseException as error:
        record["error"] = error_record(error)
        raise
    finally:
        record.update(finishedUtc=utc(), files={p.name: sha(p) for p in (out, err) if p.exists()})
        write(directory / (name + ".command.json"), record)
    require(result.returncode == 0, "Command failed; inspect " + name + ".command.json")
    return out, err


def load_helper(source):
    source = source.resolve()
    actual = subprocess.run(["git", "-C", str(source), "rev-parse", "HEAD"],
                            capture_output=True, text=True, check=True).stdout.strip()
    require(actual == SOURCE_COMMIT, "Helper checkout commit differs")
    path = source / "benchmarks/db-perf/cpu_profile.py"
    require(path.is_file() and not path.is_symlink() and sha(path) == HELPER_SHA,
            "Fixed helper bytes differ")
    spec = importlib.util.spec_from_file_location("db_perf_fixed_profile_identity", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def source_name(name):
    path = PurePosixPath(name)
    require(name and not name.startswith("/") and "\\" not in name
            and ".." not in path.parts and "." not in path.parts
            and str(path) == name, "Noncanonical artifact member path")
    return path


def selected_member(name):
    if name.startswith(PREFIX + "symbols/symfs/"):
        return True
    if name in {PREFIX + x for x in ("diagnostic.json", "cpu-profile-request.json",
                                    "runtime-verification.json", "symbols/identity.json",
                                    "perf-tool.json", "environment.json")}:
        return True
    return any(name == PREFIX + "cpu-cells/" + cell + "/" + suffix
               for cell in CELLS for suffix in REQUIRED_CELL_FILES)


def acquire(request, output):
    download = output / "download"
    metadata, _ = command(["gh", "api", "repos/IzuSharp/Korda/actions/artifacts/14959730098"],
                          download, "artifact-metadata")
    info = json.loads(metadata.read_text())
    require(info["id"] == 14959730098 and info["workflow_run"]["id"] == 45473095059
            and info["workflow_run"]["head_sha"] == SOURCE_COMMIT and not info["expired"]
            and info["size_in_bytes"] == request["artifact"]["zipBytes"]
            and info["digest"] == "sha256:" + ZIP_SHA, "Remote artifact identity changed")
    archive, _ = command(["gh", "api", "repos/IzuSharp/Korda/actions/artifacts/14959730098/zip"],
                         download, "source-zip", timeout=300)
    # No credential is inherited by perf, helper imports or any subsequent command.
    removed = []
    for key in ("GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN"):
        if key in os.environ:
            del os.environ[key]
            removed.append(key)
    require(archive.stat().st_size == request["artifact"]["zipBytes"] and sha(archive) == ZIP_SHA,
            "Original ZIP bytes differ")
    original = output / "original"
    inventory = []
    with zipfile.ZipFile(archive) as zipped:
        members = zipped.infolist()
        require(len(members) == 526 and len({i.filename for i in members}) == 526
                and sum(i.file_size for i in members) == 251618976,
                "Original ZIP member inventory changed")
        for member in members:
            source_name(member.filename)
            require(not member.is_dir() and not member.flag_bits & 1
                    and not stat.S_ISLNK(member.external_attr >> 16), "Nonregular original ZIP member")
            keep = selected_member(member.filename)
            target = original / member.filename if keep else None
            if keep:
                target.parent.mkdir(parents=True, exist_ok=True)
                stream = target.open("xb")
            else:
                stream = None
            digest, size = hashlib.sha256(), 0
            try:
                with zipped.open(member) as incoming:
                    while block := incoming.read(1024 * 1024):
                        digest.update(block)
                        size += len(block)
                        if stream:
                            stream.write(block)
            finally:
                if stream:
                    stream.close()
            require(size == member.file_size, "Member length changed")
            inventory.append({"path": member.filename, "bytes": size,
                              "sha256": digest.hexdigest(), "zipCrc32": member.CRC,
                              "extracted": keep})
    write(output / "source-member-inventory.json", {"archiveSha256": ZIP_SHA, "members": inventory,
          "memberCount": 526, "expandedBytes": 251618976,
          "allMembersStreamReadAndZipCrcChecked": True, "onlyRawProfileAndSymbolsExtracted": True})
    write(output / "source-artifact.json", {"artifact": request["artifact"],
          "remoteMetadataSha256": sha(metadata), "removedCredentialVariableNames": removed,
          "originalArchiveSourcesExecuted": False, "newSamplesRecorded": False})
    return original, {row["path"]: row for row in inventory}


def select_perf(output):
    observed = []
    for candidate in sorted(Path("/usr/lib/linux-tools").glob("*/perf")):
        status = candidate.stat()
        if not stat.S_ISREG(status.st_mode):
            continue
        digest = sha(candidate)
        observed.append({"path": str(candidate), "sha256": digest})
        if digest != PERF_SHA:
            continue
        require(status.st_uid == 0 and status.st_mode & 0o022 == 0, "Unexpected perf tool ownership")
        stdout, stderr = command([candidate, "--version"], output / "tool", "perf-version")
        require(stdout.read_text().strip() == "perf version 6.8.12" and not stderr.read_bytes(),
                "Original perf version differs")
        write(output / "perf-tool.json", {"path": str(candidate), "sha256": digest,
              "version": stdout.read_text().strip(), "observedCandidates": observed,
              "rootOwnedNotGroupOrWorldWritable": True, "privilegeEscalationUsed": False,
              "scope": "offline script/report/buildid-list only"})
        return candidate
    write(output / "perf-tool-failure.json", {"expectedSha256": PERF_SHA, "observedCandidates": observed})
    raise ValueError("The exact original perf executable is unavailable")


def check_symbols(original, inventory, helper):
    root = original / PREFIX
    identities = json.loads((root / "symbols/identity.json").read_text())
    checked = []
    for route, record in identities.items():
        executable = root / "symbols/symfs" / record["originalElf"].lstrip("/")
        debug = executable.with_name(executable.name + ".dbg")
        elf, symbols = helper.elf_identity(executable), helper.elf_identity(debug)
        require(elf["sha256"] == record["originalSha256"] == record["reconstructed"]["sha256"]
                and symbols["sha256"] == record["symbolSha256"]
                and elf["buildId"] == symbols["buildId"] == record["original"]["buildId"]
                and elf["debuglink"] == {"file": debug.name, "crc32": zlib.crc32(debug.read_bytes())}
                and symbols["hasDebugInfo"], "Original AOT ELF/debug identities changed")
        checked.append({"route": route, "elf": elf, "symbols": symbols,
                        "originalRebuildReceiptIsOnlyHistoricalEvidence": True})
    require(set(identities) == {"korda_after", "korda_before", "independent_csharp_orm"},
            "Original three AOT symbol sets changed")
    return checked


def events(text, pid, first, last):
    rows = []
    for line in text.splitlines():
        if not line.strip():
            continue
        match = EVENT.fullmatch(line)
        require(match is not None, "Unparsed or lost record in filtered event ledger")
        stamp = int(match[3]) * 1_000_000_000 + int(match[4].ljust(9, "0"))
        require(int(match[1]) == pid and first <= stamp <= last,
                "Filtered event has a foreign PID or lies outside the original steady window")
        rows.append((int(match[2]), stamp))
    require(rows, "Empty filtered event ledger")
    return rows


def valid_report(stdout, stderr):
    require(not ERROR.search(stderr) and not ERROR.search(stdout), "Perf emitted an error despite its exit code")
    require(stdout.strip() and re.search(r"(?m)^#\s*Samples:", stdout)
            and "cpu-clock:u" in stdout, "Perf report is empty or lacks its event/sample header")
    rows = [line for line in stdout.splitlines() if re.match(r"^\s*\d+(?:\.\d+)?%\s+", line)]
    require(rows, "Perf report has no sample-attribution rows")
    return {"attributionRowCount": len(rows), "stderrEmpty": not stderr.strip(),
            "rowPreview": rows[:20], "percentagesArePerfPeriodWeighted": True}


def replay_cell(cell, request, original, inventory, output, perf, helper):
    source = original / PREFIX / "cpu-cells" / cell
    directory = output / "reports" / cell
    record = {"cell": cell, "startedUtc": utc(), "status": "started",
              "newSamplesRecorded": False, "newWorkloadExecuted": False,
              "timingComparisonEligible": False, "acceptedLongSegments": 0}
    try:
        old = json.loads((source / "diagnostic.json").read_text())
        raw = json.loads((source / "result.json").read_text())
        identity = json.loads((source / "application-identity.json").read_text())
        pid, ticks = identity["applicationIdentity"]["pid"], identity["applicationIdentity"]["startTicks"]
        require((pid, ticks) == (identity["launcherIdentity"]["pid"], identity["launcherIdentity"]["startTicks"])
                and identity["execRetainedPid"] and identity["collectorIsNotClient"], "Original exec identity differs")
        first, last = get(raw, "steadyStartMonotonicNs"), get(raw, "steadyEndMonotonicNs")
        require(type(first) is int and type(last) is int and last > first, "Missing original steady window")
        require(old["exitCode"] == 0 and old["unsafeUnjoinedOwner"] is False
                and old["postgresOwnerRetirementVerified"] is True, "Original process retirement failed")
        record.update(originalPid=pid, originalStartTicks=ticks, steadyStartMonotonicNs=first,
                      steadyEndMonotonicNs=last, originalRecordedStatus=old["status"],
                      originalBusinessCounts=old["correctnessAndOwners"]["counts"])
        for name in REQUIRED_CELL_FILES:
            path = source / name
            item = inventory[PREFIX + "cpu-cells/" + cell + "/" + name]
            require(path.is_file() and sha(path) == item["sha256"], "Extracted raw evidence changed")
            if name in old["evidenceFiles"]:
                require(item["sha256"] == old["evidenceFiles"][name], "Original nested evidence hash differs")
        attempts = json.loads((source / "perf-start-attempts.json").read_text())
        require(len(attempts) == 2 and attempts[1]["targetPid"] == pid
                and attempts[1]["targetStartTicks"] == ticks and attempts[1]["enabledAck"]
                and attempts[1]["exitCode"] == 0, "Original collector identity is unverified")
        data = source / "sudo-task-pid.perf.data"
        require(old["profileDataPath"] == data.name and sha(data) == old["profileDataSha256"]
                == attempts[1]["dataVerification"]["sha256"], "Original perf data changed")
        symfs = original / PREFIX / "symbols/symfs"
        executable = symfs / identity["exe"].lstrip("/")
        require(sha(executable) == identity["sha256"] == old["originalElfSha256"], "Original ELF changed")
        mapped = json.loads((source / "mapped-symbol-images.json").read_text())
        for name, item in mapped.items():
            if item["status"] == "copied":
                require(sha(symfs / name.lstrip("/")) == item["sha256"], "Mapped symbol image changed")
            else:
                require(item["status"] == "unknown" and item["otherProcessLookupAttempted"] is False,
                        "Unknown mapping has an unexpected origin")
        original_events = events((source / "steady-sample-ledger.stdout.log").read_text(), pid, first, last)
        require(helper.sample_ledger(source / "steady-sample-ledger.stdout.log", pid, first, last)
                == old["samplingCoverage"], "Original sample coverage differs")
        original_command = json.loads((source / "steady-report.command.json").read_text())
        require(original_command["exitCode"] == 0 and not (source / "steady-report.stdout.log").read_bytes()
                and "Unknown --sort key" in (source / "steady-report.stderr.log").read_text(),
                "This recovery must retain the actual original zero-exit report error")
        record["originalInvalidReport"] = {"exitCode": 0, "valid": False,
              "files": {name: inventory[PREFIX + "cpu-cells/" + cell + "/" + name]
                        for name in ("steady-report.command.json", "steady-report.stdout.log", "steady-report.stderr.log")}}
        span = helper.ns_text(first) + "," + helper.ns_text(last)
        common = [perf, "script", "-i", data, "--ns", "--time", span, "--symfs", symfs]
        ledger, err = command([*common, "--show-lost-events", "-F", "pid,tid,time,event"],
                              directory, "steady-sample-ledger")
        require(not err.read_bytes() and events(ledger.read_text(), pid, first, last) == original_events,
                "Replayed ledger differs from the original event sequence")
        coverage = helper.sample_ledger(ledger, pid, first, last)
        require(coverage == old["samplingCoverage"] and not coverage["lostRecords"], "Coverage differs or contains lost records")
        stacks, err = command([*common, "-F", "comm,pid,tid,time,event,ip,sym,dso"], directory, "steady-stacks")
        require(stacks.stat().st_size > 0 and not err.read_bytes(), "Missing replayed symbolized stacks")
        stdout, stderr = command([perf, "report", "--stdio", "--no-children", "--percent-limit", "0",
                                  "--sort", request["reportSort"], "--time", span, "--symfs", symfs,
                                  "-i", data], directory, "steady-report")
        checked = valid_report(stdout.read_text(), stderr.read_text())
        buildids, err = command([perf, "buildid-list", "-i", data], directory, "recorded-buildids")
        require(buildids.stat().st_size > 0 and not err.read_bytes(), "Missing recorded build IDs")
        build_records = []
        for line in buildids.read_text().splitlines():
            build_id, path = line.split(maxsplit=1)
            if path.startswith("/"):
                symbol = symfs / path.lstrip("/")
                require(symbol.is_file() and helper.elf_identity(symbol)["buildId"] == build_id,
                        "Recorded build ID is not backed by the original copied symbol image")
                build_records.append({"path": path, "buildId": build_id, "verified": True})
            else:
                require(path == "[vdso]", "Unknown nonfile build-ID source")
                build_records.append({"path": path, "buildId": build_id, "verified": False,
                                      "reason": "Original process vDSO was not archived as a file"})
        record.update(status="completed", samplingCoverage=coverage, validReport=checked,
                      originalPerfDataSha256=sha(data), originalElfSha256=sha(executable),
                      recordedBuildIds=build_records, originalOwnersReplayed=False,
                      ownerEvidenceScope="Original retirement receipts retained; no live owners created here")
    except Exception as error:
        record.update(status="failed", error=error_record(error), traceback=traceback.format_exc())
    finally:
        record.update(finishedUtc=utc(), files={p.name: sha(p) for p in directory.iterdir() if p.is_file()}
                      if directory.exists() else {})
        write(directory / "recovery.json", record)
    return record


def self_test():
    count = 0
    good = "# Samples: 2 of event 'cpu-clock:u'\n# Overhead Command Pid Shared Object Symbol\n 100.00% app 22 app [.] work\n"
    require(valid_report(good, "")["attributionRowCount"] == 1, "Valid report rejected")
    count += 1
    for stdout, stderr in (("", ""), ("", "Error:\nUnknown --sort key: tid\n"),
                           (good, "Error: failed"), (good, "Unknown --sort key: tid"),
                           ("# Samples: 2 of event 'cpu-clock:u'\n", ""),
                           (good.replace("cpu-clock:u", "cycles"), "")):
        try:
            valid_report(stdout, stderr)
        except ValueError:
            count += 1
        else:
            raise AssertionError("Invalid zero-exit report accepted")
    require(events("22/23 1.000000001: cpu-clock:u:\n", 22, 1_000_000_000, 2_000_000_000)
            == [(23, 1_000_000_001)], "Valid sample rejected")
    count += 1
    for data in ("23/23 1.000000001: cpu-clock:u:\n", "22/23 0.999999999: cpu-clock:u:\n",
                 "22/23 2.000000001: cpu-clock:u:\n", "PERF_RECORD_LOST 1\n", ""):
        try:
            events(data, 22, 1_000_000_000, 2_000_000_000)
        except ValueError:
            count += 1
        else:
            raise AssertionError("Invalid sample accepted")
    for name in ("/absolute", "a/../b", "a\\b", "a//b"):
        try:
            source_name(name)
        except ValueError:
            count += 1
        else:
            raise AssertionError("Invalid artifact path accepted")
    require(not selected_member(PREFIX + "symbol-builds/korda_after/Program.cs")
            and selected_member(PREFIX + "cpu-cells/after/sudo-task-pid.perf.data"),
            "Downloaded source must not be extracted or executed")
    count += 1
    print(json.dumps({"passed": True, "controls": count, "workloadsOrPerfExecuted": False}))


def run(args):
    request = validate_request(args.request)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    record = {"schemaVersion": 1, "task": "DB-Perf-01", "kind": "offline-cpu-report-recovery",
              "startedUtc": utc(), "status": "started", "requestSha256": sha(args.request),
              "controllerSha256": sha(__file__), "sourceCommit": SOURCE_COMMIT,
              "helperSha256": HELPER_SHA, "artifact": request["artifact"], "cells": [],
              "newSamplesRecorded": False, "applicationsExecuted": False, "databaseStarted": False,
              "aotBuildPerformed": False, "privilegedPerfExecuted": False,
              "timingComparisonEligible": False, "qualifiedLongSegments": [], "wholeBatchAccepted": False}
    try:
        write(output / "request.json", request)
        original, inventory = acquire(request, output)
        helper = load_helper(args.source)
        require(sha(original / PREFIX / "cpu-profile-request.json") == request["originalProfileRequestSha256"],
                "Original profile request changed")
        top = json.loads((original / PREFIX / "diagnostic.json").read_text())
        require(top["controllerSha256"] == HELPER_SHA and top["originalRuntimeInventoryUnchangedAfterDiagnostics"]
                and top["wholeBatchAccepted"] is False, "Original controller/runtime scope differs")
        record["symbols"] = check_symbols(original, inventory, helper)
        perf = select_perf(output)
        for cell in CELLS:
            outcome = replay_cell(cell, request, original, inventory, output, perf, helper)
            record["cells"].append({"cell": cell, "status": outcome["status"],
                                    "details": "reports/" + cell + "/recovery.json"})
        record["status"] = "completed" if all(c["status"] == "completed" for c in record["cells"]) else "failed"
    except Exception as error:
        record.update(status="failed", error=error_record(error), traceback=traceback.format_exc())
    finally:
        record["finishedUtc"] = utc()
        write(output / "recovery.json", record)
    return 0 if record["status"] == "completed" else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("validate", "self-test", "run"))
    parser.add_argument("--request", type=Path, default=HERE / "cpu-report-request.json")
    parser.add_argument("--source", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.action == "validate":
        validate_request(args.request)
        print(json.dumps({"valid": True, "requestSha256": sha(args.request)}))
    elif args.action == "self-test":
        self_test()
    else:
        require(args.source is not None and args.output is not None, "Run requires fixed helper source and a new output directory")
        return run(args)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
