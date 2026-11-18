#!/usr/bin/env python3
"""Calibrate, freeze and execute explicit B22 load/drain/idle resource stages."""
from __future__ import annotations

import argparse
import concurrent.futures
import contextlib
import hashlib
import importlib.metadata
import json
import math
import os
from pathlib import Path
import platform
import random
import shutil
import signal
import socket
import ssl
import subprocess
import sys
import threading
import time
from urllib.parse import quote, urlsplit

from harness import (Database, HERE, REFERENCE, PostgresCluster, Service, canonical, configuration,
                     create, eventually, require, sha, write_json)

STAGES = {"steady-30m": 1800, "regression-24h": 86400, "release-72h": 259200}
# Runtime startup tracing buffers in memory unless streaming is explicit. The
# buffer size uses hexadecimal syntax; 0x40 is the intended finite 64 MiB.
TRACE_ENVIRONMENT = {
    "DOTNET_EnableEventPipe": "1",
    "DOTNET_EventPipeConfig": "Microsoft-Windows-DotNETRuntime:0x3:5,NativeWeave.B22:0xffffffffffffffff:5",
    "DOTNET_EventPipeCircularMB": "0x40",
    "DOTNET_EventPipeOutputStreaming": "1",
    "DOTNET_EventPipeBufferingMode": "0",
}
ZERO_GAUGES = tuple("nativeweave.server." + value for value in
    ("requests.active", "requests.queued", "requests.retiring", "payload.managed.bytes", "payload.native.bytes"))
COHORT_CONFIGURATION_SHA256 = "35d2415eec723a861e46e7221db92b94226b23bd6c6740ac943742093865fcf7"


def native_cohort_policy(provider):
    """Finite practical observation floors, separate from native hard admission."""
    paths = ("src/Reference.TaskService/ServiceConfiguration.cs",
             "src/Data.NativeHost/HostTransactionTransport.cs", "src/Data.NativeHost/DataSource.cs",
             "src/Query.Binding/Owners.cs", "native/database/host/src/runtime.rs",
             "native/database/host/src/session.rs", "native/database/host/src/operation.rs",
             "native/database/host/src/abi.rs")
    return {"connectionSlots": {"postgresql": 16, "sqlite": 2} if provider == "postgresql" else {"sqlite": 18},
            "sourceCounts": {"postgresql": 1, "sqlite": 1} if provider == "postgresql" else {"sqlite": 2},
            "configurationSha256": COHORT_CONFIGURATION_SHA256,
            "rolesPerSlot": {"session": 1, "transaction": 1, "query": 1, "plan": 1,
                             "cursor": 1, "operation": 3, "buffer": 3},
            "operationRoles": ["normal dispatch", "session-close control", "transaction-close control"],
            "bufferRoles": ["normal result or retained cursor batch", "session-close snapshot", "transaction-close snapshot"],
            "derivation": "max(fresh measured maximum plus idle range, populated source-cohort floor); sessions/cursors C, operations/buffers 3C, registry qualified permanent baseline +11C. Sources remain exact.",
            "interpretation": "Practical finite workload observation bands, not global object-lifetime bounds. Physical connection permits do not bound unreachable transaction wrappers. Qualified idle native baseline and final retirement remain separately mandatory.",
            "sourceEvidence": {path: sha(HERE.parents[1] / path) for path in paths}}


class Journal:
    def __init__(self, path):
        self.stream = path.open("x", buffering=1)
        self.lock = threading.Lock()
        self.records = 0

    def write(self, value):
        encoded = canonical(value).decode()
        require(len(encoded) <= 32768, "A resource journal record exceeded its fixed bound")
        with self.lock:
            self.stream.write(encoded); self.records += 1

    def close(self):
        self.stream.flush(); os.fsync(self.stream.fileno()); self.stream.close()


class Tail:
    def __init__(self, path):
        self.path, self.offset, self.latest = path, 0, None

    def read(self):
        values = []
        if not self.path.exists(): return values
        with self.path.open("rb") as stream:
            stream.seek(self.offset)
            while True:
                position = stream.tell(); line = stream.readline(32769)
                if not line: break
                require(len(line) <= 32768, "Observer record exceeded its bound")
                if not line.endswith(b"\n"): stream.seek(position); break
                value = json.loads(line); values.append(value); self.latest = value
            self.offset = stream.tell()
        return values


def process_sample(pid):
    root = Path("/proc") / str(pid)
    fields = {}
    for line in (root / "status").read_text().splitlines():
        key, _, value = line.partition(":")
        if key in ("VmRSS", "VmHWM", "VmSize", "Threads"): fields[key] = int(value.split()[0])
    stat = (root / "stat").read_text().rsplit(")", 1)[1].split()
    return {"rssBytes": fields["VmRSS"] * 1024, "peakRssBytes": fields["VmHWM"] * 1024,
        "virtualBytes": fields["VmSize"] * 1024, "threads": fields["Threads"],
        "fileDescriptors": sum(1 for _ in (root / "fd").iterdir()),
        "cpuTicks": int(stat[11]) + int(stat[12]), "cpuTicksPerSecond": os.sysconf("SC_CLK_TCK")}


def resource_vector(process, observation):
    values = {key: process[key] for key in ("rssBytes", "threads", "fileDescriptors")}
    for provider, counts in observation["databaseNativeOwners"].items():
        require(len(counts) == 6, "A native owner inventory is incomplete")
        for index, count in enumerate(counts): values[f"native.{provider}.{index}"] = count
    for key in ZERO_GAUGES: values[key] = observation["serverMeter"].get(key)
    require(all(isinstance(value, int) and value >= 0 for value in values.values()), "A required resource counter is unavailable")
    return values


def identity(args):
    artifact = args.artifact_directory.resolve()
    executable = artifact / ("NativeWeave.Reference.TaskService.dll" if args.mode == "jit" else "NativeWeave.Reference.TaskService")
    require(executable.is_file(), "Missing exact observed application artifact")
    manifest = artifact.parent.parent / "manifest.json"
    require(manifest.is_file(), "The instrumented consumer build manifest is required")
    metadata = json.loads(manifest.read_text())
    require(metadata.get("applicationSourceDelta", {}).get("bodyUnchanged") is True, "Observer entrypoint delta is not bound to an unchanged application body")
    require(metadata["referenceSources"]["ServiceConfiguration.cs"] == COHORT_CONFIGURATION_SHA256,
            "Reference connection budgets changed; independently review the native cohort policy")
    modules = {}
    for provider in ("sqlite", "postgresql") if args.provider == "postgresql" else ("sqlite",):
        paths = list(artifact.rglob("libnw_database_" + provider + ".so"))
        require(len(paths) == 1, "Expected one exact provider module")
        modules[provider] = str(paths[0])
    command = [args.dotnet, str(executable)] if args.mode == "jit" else [str(executable)]
    command = ["env", "NW_B22_DB_MODULES=" + json.dumps(modules, sort_keys=True), *command]
    dotnet = Path(shutil.which(args.dotnet) or args.dotnet).resolve()
    require(dotnet.is_file(), "The actual .NET analysis/JIT host is unavailable")
    runtime = json.loads(subprocess.check_output([str(dotnet), str(args.trace_reader.resolve()), "--runtime-identity"], text=True, timeout=30))
    require(runtime["dynamicCodeSupported"] is True, "TraceReader must run as the separately identified JIT analyzer")
    runtime_directory = Path(runtime["runtimeDirectory"]).resolve()
    reader_directory = args.trace_reader.resolve().parent
    packages = {}
    for name in ("httpx", "httpcore", "h11", "h2", "hpack", "hyperframe", "anyio", "certifi", "idna", "sniffio", "typing_extensions", "brotli", "brotlicffi", "zstandard"):
        try: distribution = importlib.metadata.distribution(name)
        except importlib.metadata.PackageNotFoundError:
            packages[name] = None; continue
        packages[name] = {"version": distribution.version, "files": {str(relative): sha(Path(distribution.locate_file(relative)))
            for relative in distribution.files or () if Path(distribution.locate_file(relative)).is_file() and
            "__pycache__" not in relative.parts and Path(relative).suffix != ".pyc"}}
    record = {"mode": args.mode, "provider": args.provider, "executableSha256": sha(executable),
        "observerBuildManifestSha256": sha(manifest),
        "nativePeakPolicy": native_cohort_policy(args.provider),
        "traceEnvironment": TRACE_ENVIRONMENT,
        "artifacts": {str(p.relative_to(artifact)): sha(p) for p in sorted(artifact.rglob("*")) if p.is_file()},
        "controller": {name: sha(HERE / name) for name in ("soak.py", "harness.py")},
        "importedReferenceHelpers": {name: sha(REFERENCE / name) for name in ("run.py", "golden.py", "postgres_oracles.py")},
        "traceReaderFiles": {str(p.relative_to(reader_directory)): sha(p) for p in sorted(reader_directory.rglob("*")) if p.is_file()},
        "dotnetHost": {"path": str(dotnet), "sha256": sha(dotnet), "selectedRuntime": runtime,
            "runtimeFiles": {str(p.relative_to(runtime_directory)): sha(p) for p in sorted(runtime_directory.rglob("*")) if p.is_file()}},
        "certificateSha256": sha(args.certificate), "privateKeySha256": sha(args.private_key),
        "python": {"version": sys.version, "executableSha256": sha(Path(sys.executable).resolve()), "packages": packages},
        "kernel": platform.release(), "machine": platform.machine(),
        "processorCountEnvironment": os.environ.get("DOTNET_PROCESSOR_COUNT"),
        "cpuAffinity": sorted(os.sched_getaffinity(0)),
        "cgroupCpuMax": Path("/sys/fs/cgroup/cpu.max").read_text().strip() if Path("/sys/fs/cgroup/cpu.max").exists() else None}
    if args.provider == "postgresql":
        require(args.postgres_bin is not None, "PostgreSQL requires explicit pinned binaries")
        record["postgresLifecycleBinaries"] = {name: sha(args.postgres_bin / name) for name in ("postgres", "psql", "initdb", "pg_ctl")}
    return command, record, set(modules)


def revalidate_inputs(args, expected):
    # Verify the analyzer/host/runtime bytes before even launching its identity
    # command. Then recheck the complete transitive controller/product inventory.
    reader = args.trace_reader.resolve().parent
    require({str(path.relative_to(reader)): sha(path) for path in sorted(reader.rglob("*")) if path.is_file()} == expected["traceReaderFiles"],
        "Pinned TraceReader inputs changed during the attempt")
    host = Path(shutil.which(args.dotnet) or args.dotnet).resolve()
    require(str(host) == expected["dotnetHost"]["path"] and sha(host) == expected["dotnetHost"]["sha256"], "Pinned .NET host changed during the attempt")
    runtime = Path(expected["dotnetHost"]["selectedRuntime"]["runtimeDirectory"]).resolve()
    require({str(path.relative_to(runtime)): sha(path) for path in sorted(runtime.rglob("*")) if path.is_file()} == expected["dotnetHost"]["runtimeFiles"],
        "Pinned analysis/JIT runtime changed during the attempt")
    _, actual, _ = identity(args)
    require(actual == expected, "Pinned directly executed inputs changed during the attempt")


class Workload:
    """Finite controller state; durable state growth is measured independently."""
    def __init__(self, service, token, sql, config, seed, journal):
        self.service, self.token, self.sql, self.config = service, token, sql, config
        self.seed, self.journal = seed, journal
        self.lock = threading.Lock(); self.next_sequence = 0
        self.counts = {"created": 0, "attachments": 0, "completed": 0, "cancelledUploads": 0}
        self.items = []; self.item_locks = [threading.Lock() for _ in range(4)]
        self.versions = [1] * 4
        self.current_attachments = [None] * 4
        self.payloads = [(hashlib.sha256(f"b22-payload-{seed}-{i}".encode()).digest() * 2048) for i in range(4)]
        for index in range(4):
            status, body, _ = create(service, token, f"seed-{index}", f"B22 seed {index}")
            require(status == 201, "Seed create failed")
            self.items.append(json.loads(body)["itemId"]); self.counts["created"] += 1

    def reserve(self):
        with self.lock:
            value = self.next_sequence; self.next_sequence += 1; return value

    def request(self, *args, **kwargs):
        return self.service.request(*args, **kwargs, token=self.token)

    def perform(self, sequence, offered):
        started = time.monotonic_ns(); choice = random.Random(self.seed ^ sequence).randrange(100)
        name = "create-replay-recovery" if choice < 40 else "keyset-page" if choice < 75 else "attachment" if choice < 95 else "hash-conflict"
        try:
            if name == "create-replay-recovery":
                key, title = f"soak-create-{sequence}", f"B22 item {sequence}"
                status, body, _ = create(self.service, self.token, key, title)
                require(status == 201, "Soak create did not confirm")
                with self.lock: self.counts["created"] += 1
                replay = create(self.service, self.token, key, title)
                recovery = self.request("GET", "/v1/operations/" + json.loads(body)["operationId"])
                require(replay[0] == 201 and recovery[0] == 200 and body == replay[1] == recovery[1], "Soak stable response oracle failed")
            elif name == "keyset-page":
                status, body, _ = self.request("GET", "/v1/items?limit=25")
                require(status == 200, "Soak list failed")
                page = json.loads(body); rows = page["items"]
                order = [(row["createdAt"], row["itemId"]) for row in rows]
                require(len(rows) <= 25 and order == sorted(order) and len(order) == len(set(order)), "Soak page ordering failed")
                if page["nextCursor"]:
                    following = self.request("GET", "/v1/items?limit=25&after=" + quote(page["nextCursor"]))
                    require(following[0] == 200, "Soak scoped cursor failed")
                    later = json.loads(following[1])["items"]
                    require(not later or not order or (later[0]["createdAt"], later[0]["itemId"]) > order[-1], "Soak keyset boundary repeated")
            elif name == "attachment":
                slot = sequence % 4
                with self.item_locks[slot]:
                    route = "/v1/items/" + self.items[slot] + "/attachment"
                    status, response_bytes, _ = self.request("PUT", route, self.payloads[slot],
                        {"Content-Type": "application/octet-stream", "Idempotency-Key": f"soak-attachment-{sequence}", "If-Match": str(self.versions[slot])})
                    require(status == 200, "Soak attachment CAS failed")
                    self.versions[slot] += 1
                    expected_hash = hashlib.sha256(self.payloads[slot]).hexdigest()
                    response = json.loads(response_bytes)
                    require(type(response["responseVersion"]) is int and response["responseVersion"] == 1 and
                        response["itemId"] == self.items[slot] and response["version"] == self.versions[slot] and
                        response["contentHash"] == expected_hash and response["contentLength"] == len(self.payloads[slot]), "Attachment response did not reflect the expected version/content")
                    self.current_attachments[slot] = {"operationId": response["operationId"], "requestKey": f"soak-attachment-{sequence}",
                        "version": self.versions[slot], "contentHash": expected_hash, "contentLength": len(self.payloads[slot]), "responseBytes": response_bytes}
                    with self.lock: self.counts["attachments"] += 1
                    content = self.request("GET", route)
                    require(content[0] == 200 and content[1] == self.payloads[slot], "Soak attachment content changed")
            else:
                response = create(self.service, self.token, "seed-0", "different request hash")
                require(response[0] == 409, "Soak conflict changed its winning effect")
            with self.lock: self.counts["completed"] += 1
            self.journal.write({"sequence": sequence, "kind": name, "offeredNs": offered, "startedNs": started,
                "endedNs": time.monotonic_ns(), "status": "passed"})
        except BaseException as error:
            self.journal.write({"sequence": sequence, "kind": name, "offeredNs": offered, "startedNs": started,
                "endedNs": time.monotonic_ns(), "status": "failed", "failureType": type(error).__name__})
            raise

    def cancel_upload(self, sequence):
        # A fresh half-written HTTP/1 connection supplies a deterministic long-run
        # fault. The admitted file is observed before closing the actual socket.
        slot = 0
        with self.item_locks[slot]:
            parsed = urlsplit(self.service.url)
            context = ssl.create_default_context(cafile=str(self.service.certificate))
            staging = Path(self.config["blobDirectory"]) / "staging"
            key = f"soak-cancel-{sequence}"
            headers = (f"PUT /v1/items/{self.items[slot]}/attachment HTTP/1.1\r\nHost: {parsed.netloc}\r\n"
                f"Authorization: Bearer {self.token}\r\nIdempotency-Key: {key}\r\nIf-Match: {self.versions[slot]}\r\n"
                "Content-Type: application/octet-stream\r\nContent-Length: 65536\r\nConnection: close\r\n\r\n")
            with socket.create_connection((parsed.hostname, parsed.port), timeout=15) as plain:
                with context.wrap_socket(plain, server_hostname=parsed.hostname) as transport:
                    transport.sendall(headers.encode() + b"x" * 4096)
                    def matching_manifest():
                        matches = []
                        for index, manifest in enumerate(staging.glob("*.json")):
                            require(index < 8192, "Staging metadata exceeded its frozen inventory bound")
                            try:
                                with manifest.open("rb") as stream: encoded = stream.read(8193)
                            except FileNotFoundError:
                                continue
                            require(len(encoded) <= 8192, "Staging metadata exceeded its frozen byte bound")
                            value = json.loads(encoded)
                            if value["requestKey"] == key and manifest.with_suffix(".part").is_file(): matches.append(manifest.name)
                        return matches
                    admitted = eventually(matching_manifest,
                        "soak cancellation admitted upload", timeout=15)
                    require(len(admitted) == 1, "Cancellation barrier was ambiguous")
            self.journal.write({"kind": "admitted-upload-connection-close", "sequence": sequence, "monotonicNs": time.monotonic_ns(), "status": "injected"})
            with self.lock: self.counts["cancelledUploads"] += 1

    def reconcile(self, timeout=45):
        expected = self.counts.copy(); operations = expected["created"] + expected["attachments"]
        eventually(lambda: int(self.sql.scalar("SELECT count(*) FROM outbox WHERE delivery_state<>2")) == 0,
            "durable outbox drain", timeout=timeout)
        actual = {table: int(self.sql.scalar("SELECT count(*) FROM " + table)) for table in ("operations", "items", "outbox")}
        require(actual == {"operations": operations, "items": expected["created"], "outbox": operations}, "Independent long-run cardinality oracle failed")
        dangling = int(self.sql.scalar("SELECT count(*) FROM outbox o LEFT JOIN operations r ON r.operation_id=o.operation_id WHERE r.operation_id IS NULL"))
        dangling += int(self.sql.scalar("SELECT count(*) FROM items i LEFT JOIN operations r ON r.operation_id=i.operation_id WHERE r.operation_id IS NULL"))
        cancelled = int(self.sql.scalar("SELECT count(*) FROM operations WHERE request_key LIKE 'soak-cancel-%'"))
        require(dangling == 0 and cancelled == 0, "Atomicity or cancelled-upload durable oracle failed")
        consumer = Database("sqlite", self.config["consumerDatabase"], None)
        count, effects = consumer.sql("SELECT count(*),COALESCE(sum(effects),0) FROM receipts")[0]
        require(int(count) == int(effects) == operations, "Independent consumer effect count failed")
        attachments = []
        for slot, item in enumerate(self.items):
            literal = "'" + item.replace("'", "''") + "'"
            item_version = int(self.sql.scalar("SELECT version FROM items WHERE tenant_id='b22-tenant' AND item_id=" + literal))
            require(item_version == self.versions[slot], "Independent item expected-version oracle failed")
            rows = self.sql.sql("SELECT a.operation_id,a.version,a.content_hash,a.content_length,o.request_key,o.tenant_id,o.principal_id "
                "FROM attachments a LEFT JOIN operations o ON o.operation_id=a.operation_id WHERE a.tenant_id='b22-tenant' AND a.item_id=" + literal)
            wanted = self.current_attachments[slot]
            if wanted is None:
                require(not rows, "An untouched attachment slot acquired a reference")
                attachments.append({"slot": slot, "present": False, "itemVersion": item_version}); continue
            require(len(rows) == 1, "Current attachment reference is missing or duplicated")
            operation, version, content_hash, length, key, tenant, principal = rows[0]
            require(operation == wanted["operationId"] and int(version) == wanted["version"] and content_hash == wanted["contentHash"] and
                int(length) == wanted["contentLength"] and key == wanted["requestKey"] and tenant == "b22-tenant" and principal == "b22-actor",
                "Independent current attachment metadata/operation linkage failed")
            replay_version, replay_status, replay_bytes = self.sql.replay(operation)
            require(replay_version == 1 and replay_status == 200 and replay_bytes == wanted["responseBytes"], "Current attachment operation has different durable replay bytes")
            blob = Path(self.config["blobDirectory"]) / "blobs" / content_hash
            require(blob.is_file() and blob.stat().st_size == wanted["contentLength"] and sha(blob) == content_hash, "Current attachment reference targets missing or incorrect durable content")
            attachments.append({"slot": slot, "present": True, "itemVersion": item_version, "attachmentVersion": int(version), "contentHash": content_hash,
                "contentLength": int(length), "operationId": operation, "requestKey": key})
        return {"expected": expected, "observed": actual, "consumerReceipts": int(count), "consumerEffects": int(effects), "dangling": dangling,
            "cancelledOperations": cancelled, "currentAttachments": attachments}


def trace_analyze(args, output):
    command = [args.dotnet, str(args.trace_reader.resolve()), str(output / "runtime.nettrace"),
        str(output / "trace-receipt.json"), str(output / "known-handles.jsonl")]
    with (output / "trace-analysis.log").open("wb") as log:
        result = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, timeout=180)
    require(result.returncode == 0, "Actual runtime handle trace or positive control failed")
    return json.loads((output / "trace-receipt.json").read_text())


def idle_trace_windows(path, controls):
    """Require one fresh actual GC anchor inside each exact diagnostic window."""
    require(0 < controls <= 1024, "Declared idle-window inventory is outside its finite bound")
    windows = {}
    with path.open() as stream:
        for line in stream:
            row = json.loads(line)
            phase = row.get("phase")
            if phase not in ("idle-before-finalizers", "idle-after-finalizers"): continue
            sequence = row.get("controlSequence")
            require(isinstance(sequence, int) and 1 <= sequence <= controls, "Unexpected idle control identity")
            phases = windows.setdefault(sequence, {})
            require(phase not in phases, "Repeated idle control phase")
            phases[phase] = row
    require(set(windows) == set(range(1, controls + 1)), "An idle GCHandle window is missing")
    roots = []
    previous_end = -1.
    for sequence in range(1, controls + 1):
        phases = windows[sequence]
        require(set(phases) == {"idle-before-finalizers", "idle-after-finalizers"}, "An idle control pair is incomplete")
        before, after = phases["idle-before-finalizers"], phases["idle-after-finalizers"]
        require(previous_end < before["milliseconds"] < after["milliseconds"], "Idle control phases are not strictly ordered")
        anchor = after.get("lastGcMilliseconds")
        require(isinstance(anchor, (int, float)) and before["milliseconds"] <= anchor <= after["milliseconds"], "Idle runtime root total came from a stale GC")
        require(isinstance(after.get("runtimeGcHandlesAtLastGc"), int), "An actual GCHeapStats root anchor is unavailable")
        previous_end = after["milliseconds"]; roots.append(after)
    return roots


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("probe", "calibrate", "run", "freeze"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--profile", type=Path)
    parser.add_argument("--review-note")
    parser.add_argument("--artifact-directory", type=Path)
    parser.add_argument("--trace-reader", type=Path)
    parser.add_argument("--mode", choices=("jit", "aot"), default="aot")
    parser.add_argument("--provider", choices=("sqlite", "postgresql"), default="sqlite")
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--postgres-bin", type=Path)
    parser.add_argument("--certificate", type=Path)
    parser.add_argument("--private-key", type=Path)
    parser.add_argument("--stage", choices=tuple(STAGES))
    parser.add_argument("--rate", type=float)
    parser.add_argument("--concurrency", type=int, default=4)
    parser.add_argument("--seed", type=int, default=2200261118)
    parser.add_argument("--predecessor", type=Path, help="Preserve an interrupted attempt's identity; never silently add its duration")
    args = parser.parse_args()
    output = args.output.resolve()
    if args.action == "freeze":
        require(args.profile and args.review_note, "Freeze requires a measured candidate and explicit review note")
        profile = json.loads(args.profile.read_text())
        require(profile["status"] == "candidate-from-passed-calibration", "Only a passed actual calibration can be frozen")
        require(sha(Path(profile["calibrationReceipt"])) == profile["calibrationReceiptSha256"], "The calibration receipt changed before freeze")
        profile.update(status="frozen", frozenUnixNs=time.time_ns(), reviewNote=args.review_note,
            candidateSha256=sha(args.profile))
        output.parent.mkdir(parents=True, exist_ok=True)
        with output.open("xb") as stream: stream.write(canonical(profile)); stream.flush(); os.fsync(stream.fileno())
        print(json.dumps({"status": "frozen", "profile": str(output), "sha256": sha(output)})); return 0
    require(all((args.artifact_directory, args.trace_reader, args.certificate, args.private_key)), "Exact artifacts, trace reader and TLS files are required")
    command, inputs, expected_providers = identity(args)
    if args.action == "run":
        require(args.profile is not None and args.stage is not None, "A long stage requires its frozen profile and exact stage identifier")
        profile = json.loads(args.profile.read_text())
        require(profile["status"] == "frozen" and profile["identity"] == inputs, "Frozen profile does not match the exact executed inputs")
        settings = profile["settings"]
    else:
        require(args.rate is not None and 0 < args.rate <= 1000 and 1 <= args.concurrency <= 64, "Calibration requires a finite explicit offered rate and concurrency")
        settings = {"rate": args.rate, "concurrency": args.concurrency, "seed": args.seed,
            "calibrationCycles": 3, "calibrationLoadSeconds": 240, "calibrationIdleSeconds": 60,
            "regressionLoadSeconds": 240, "regressionIdleSeconds": 60,
            "maxGeneratorLagSeconds": 1.0, "sampleSeconds": 1.1,
            "traceBudgetBytes": 8 * 1024**3, "journalBudgetBytes": 1024**3,
            "resourceJournalBudgetBytes": 512 * 1024**2, "observerJournalBudgetBytes": 512 * 1024**2,
            "serviceLogBudgetBytes": 128 * 1024**2,
            "minimumFreeDiskBytes": 2 * 1024**3, "faultEverySeconds": 300,
            "idleCollectionAttempts": 3, "idleCollectionBudgetSeconds": 5,
            "applicationBlobBudgetBytes": 256 * 1024**2,
            "workload": "40% create+same-key replay+authorized recovery,35% keyset page,20% streamed64KiB attachment+download,5% hash conflict; per-request connection churn; admitted half-written upload connection close every300s"}
        profile = None
        if args.action == "probe":
            settings.update(calibrationCycles=1, calibrationLoadSeconds=3, calibrationIdleSeconds=5)
    output.mkdir(parents=True, exist_ok=False)
    started = time.monotonic_ns(); stopping = threading.Event()
    previous_handlers = {sig: signal.signal(sig, lambda *_: stopping.set()) for sig in (signal.SIGTERM, signal.SIGINT)}
    required = STAGES[args.stage] if args.action == "run" else settings["calibrationCycles"] * (settings["calibrationLoadSeconds"] + settings["calibrationIdleSeconds"])
    receipt = {"schemaVersion": 1, "task": "B22", "status": "running", "kind": args.action, "stage": args.stage,
        "requiredStageSeconds": required, "startedUnixNs": time.time_ns(), "identity": inputs, "settings": settings,
        "profileSha256": sha(args.profile) if args.profile else None, "completedCycles": 0,
        "fullB22Acceptance": False, "interruptedPredecessor": None, "creditedPredecessorSeconds": 0,
        "instrumentedDiagnosticGc": True, "performanceMeasurement": False, "error": None,
        "unavailableObservations": {"nativeAllocatorLiveBytes": None, "nativeAllocatorRetainedBytes": None,
            "databasePoolWaiters": None, "sqlitePhysicalPoolConnections": None},
        "observationLimits": "Native sessions are owner counts, not physical pool connections; process GCHandle totals are not every managed graph root."}
    write_json(output / "identity.json", inputs)
    receipt["identitySha256"] = sha(output / "identity.json")
    if args.predecessor:
        predecessor = json.loads(args.predecessor.read_text())
        receipt["interruptedPredecessor"] = {"path": str(args.predecessor.resolve()), "sha256": sha(args.predecessor),
            "recordedElapsedSeconds": predecessor.get("stageElapsedSeconds", 0), "status": predecessor.get("status")}
    write_json(output / "progress.json", receipt)
    operations = Journal(output / "operations.jsonl"); resources = Journal(output / "resources.jsonl")
    all_max, idle_vectors, post_gc_native, sample_counts = {}, [], [], 0
    candidate = None
    stage_start = None; last_sample = 0.; service = workload = None
    control_sequence = 0; last_fault = None
    trace = output / "runtime.nettrace"
    observer_control = output / "observer-control.json"
    traced_command = ["env", *[key + "=" + value for key, value in TRACE_ENVIRONMENT.items()],
        "DOTNET_EventPipeOutputPath=" + str(trace), "NW_B22_HANDLE_CONTROL=1",
        "NW_B22_HANDLE_CONTROL_OUTPUT=" + str(output / "known-handles.jsonl"), "NW_B22_OBSERVER_CONTROL=" + str(observer_control), *command]

    def checkpoint():
        receipt.update(elapsedSeconds=(time.monotonic_ns() - started) / 1e9,
            stageElapsedSeconds=0 if stage_start is None else (receipt.get("stageObservedThroughNs", stage_start) - stage_start) / 1e9,
            sampleCount=sample_counts, observedMax=all_max, workloadCounts=workload.counts.copy() if workload else {})
        progress = dict(receipt)
        progress["identity"] = {"path": "identity.json", "sha256": receipt["identitySha256"]}
        write_json(output / "progress.json", progress)

    def sample(phase, force=False):
        nonlocal last_sample, sample_counts
        now = time.monotonic()
        if not force and now - last_sample < settings["sampleSeconds"]: return None
        last_sample = now
        require(service.process.poll() is None, "Soak service exited unexpectedly")
        tail.read(); observation = tail.latest
        require(observation is not None and observation["observerFailures"] == 0, "Soak observer failed or is unavailable")
        require(observation["dynamicCodeSupported"] is (args.mode == "jit"), "Actual observer runtime mode does not match the selected artifact row")
        if args.mode == "jit":
            require(Path(observation["runtimeDirectory"]).resolve() == Path(inputs["dotnetHost"]["selectedRuntime"]["runtimeDirectory"]).resolve() and
                observation["runtimeVersion"] == inputs["dotnetHost"]["selectedRuntime"]["runtimeVersion"], "JIT service selected a different unrecorded runtime")
        require(set(observation["databaseNativeOwners"]) == expected_providers, "Soak provider inventory is incomplete")
        require(time.time_ns() // 1000000 - observation["unixMilliseconds"] < 10000, "Soak observer became stale")
        process = process_sample(service.process.pid); vector = resource_vector(process, observation)
        for key, value in vector.items(): all_max[key] = max(all_max.get(key, 0), value)
        trace_bytes = trace.stat().st_size
        receipt["traceBytesObservedWhileRunning"] = trace_bytes
        resources.write({"monotonicNs": time.monotonic_ns(), "phase": phase, "process": process,
            "traceBytes": trace_bytes, "observationSequence": observation["sequence"],
            "controlSequence": observation["controlSequence"], "resources": vector})
        sample_counts += 1
        receipt["stageObservedThroughNs"] = time.monotonic_ns()
        require(trace.stat().st_size <= settings["traceBudgetBytes"], "Finite trace-file budget exhausted")
        require((output / "operations.jsonl").stat().st_size <= settings["journalBudgetBytes"], "Finite operation-journal budget exhausted")
        require((output / "resources.jsonl").stat().st_size <= settings["resourceJournalBudgetBytes"], "Finite resource-journal budget exhausted")
        require((output / "service/observations.jsonl").stat().st_size <= settings["observerJournalBudgetBytes"], "Finite observer-journal budget exhausted")
        require((output / "service/service.log").stat().st_size <= settings["serviceLogBudgetBytes"], "Finite service-log budget exhausted")
        disk = os.statvfs(output)
        require(disk.f_bavail * disk.f_frsize >= settings["minimumFreeDiskBytes"], "Declared free-disk safety reserve exhausted")
        if profile:
            for key, limit in profile["peakCaps"].items(): require(vector[key] <= limit, "Frozen peak resource cap exceeded: " + key)
        checkpoint(); return vector

    def load(seconds, executor):
        nonlocal last_fault
        begun = time.monotonic_ns(); end = begun + int(seconds * 1e9)
        interval = 1e9 / settings["rate"]; planned = math.ceil(seconds * settings["rate"])
        offered_count = 0; futures = set()
        try:
            while offered_count < planned:
                if stopping.is_set(): raise InterruptedError("Stage interrupted")
                ready = {future for future in futures if future.done()}
                for future in ready: future.result()
                futures -= ready
                offered = begun + int(offered_count * interval)
                sample("load")
                # Sampling includes durable progress fsync. Its time belongs to
                # generator lag rather than disappearing behind an old clock.
                now = time.monotonic_ns()
                if now < offered:
                    stopping.wait(min((offered - now) / 1e9, .05)); continue
                require((now - offered) / 1e9 <= settings["maxGeneratorLagSeconds"], "Generator lag invalidated the fixed-arrival workload")
                require(len(futures) < settings["concurrency"], "Generator outstanding bound exhausted; offered load was not sustained")
                sequence = workload.reserve()
                if last_fault is None or (now - last_fault) / 1e9 >= settings["faultEverySeconds"]:
                    futures.add(executor.submit(workload.cancel_upload, sequence)); last_fault = now
                else:
                    futures.add(executor.submit(workload.perform, sequence, int(offered)))
                offered_count += 1
            while time.monotonic_ns() < end:
                if stopping.is_set(): raise InterruptedError("Stage interrupted")
                sample("load"); stopping.wait(min((end - time.monotonic_ns()) / 1e9, .05))
        finally:
            for future in futures: future.result(timeout=90)
        operations.write({"kind": "load-window", "begunNs": begun, "endedNs": time.monotonic_ns(),
            "requiredLoadSeconds": seconds, "plannedArrivals": planned, "offeredArrivals": offered_count})

    def idle(seconds):
        nonlocal control_sequence
        begun = time.monotonic(); durable = workload.reconcile()
        control_sequence += 1
        sources = {provider: 2 if args.provider == "sqlite" else 1 for provider in expected_providers}
        control = {"sequence": control_sequence, "collect": True, "expectedSources": sources}
        if profile: control["nativeBaseline"] = profile["postGcNativeBaseline"]
        write_json(observer_control, control)
        phases = {}
        def observed():
            for value in tail.read():
                if value["controlSequence"] != control_sequence or value["phase"] not in ("idle-before-finalizers", "idle-after-finalizers"): continue
                require(value["phase"] not in phases, "Repeated native observer control phase")
                require(set(value["databaseNativeOwners"]) == expected_providers and
                    all(len(counts) == 6 and all(isinstance(count, int) and count >= 0 for count in counts) for counts in value["databaseNativeOwners"].values()),
                    "Native idle-control provider inventory is incomplete")
                phases[value["phase"]] = value
            if "idle-after-finalizers" not in phases: return None
            require(set(phases) == {"idle-before-finalizers", "idle-after-finalizers"} and
                phases["idle-before-finalizers"]["timestamp"] < phases["idle-after-finalizers"]["timestamp"], "Native observer control pair is incomplete or unordered")
            return phases["idle-after-finalizers"]
        row = eventually(observed, "explicit post-GC idle observation", timeout=15)
        require(row["observerFailures"] == 0, "Diagnostic collection failed")
        require(1 <= row["controlAttempt"] <= settings["idleCollectionAttempts"], "Idle collection exceeded its declared attempt bound")
        require(all(counts[0] == sources[provider] and counts[1:5] == [0, 0, 0, 0] for provider, counts in row["databaseNativeOwners"].items()),
            "No actual quiescent native owner witness was obtained")
        if profile:
            require(row["databaseNativeOwners"] == profile["postGcNativeBaseline"], "Qualified post-GC native ownership differs from the frozen baseline")
        post_gc_native.append(row["databaseNativeOwners"])
        resources.write({"phase": "qualified-native-idle", "controlSequence": control_sequence,
            "attempt": row["controlAttempt"], "databaseNativeOwners": row["databaseNativeOwners"]})
        idle_vector = None
        while time.monotonic() - begun < seconds:
            if stopping.is_set(): raise InterruptedError("Stage interrupted")
            vector = sample("idle", force=True)
            if all(vector[key] == 0 for key in ZERO_GAUGES): idle_vector = vector
            stopping.wait(min(settings["sampleSeconds"], max(0, seconds - (time.monotonic() - begun))))
        require(idle_vector is not None, "Server permits and byte owners did not retire in the frozen idle window")
        if profile:
            for key, limit in profile["idleCaps"].items(): require(idle_vector[key] <= limit, "Frozen idle resource cap exceeded: " + key)
        idle_vectors.append(idle_vector)
        resources.write({"phase": "cycle-reconciled", "controlSequence": control_sequence, "resources": idle_vector, "durable": durable})
        return durable

    try:
        cluster_context = PostgresCluster(args.postgres_bin, output / "postgres") if args.provider == "postgresql" else contextlib.nullcontext(None)
        with cluster_context as cluster:
            database = cluster.database() if cluster else str(output / "application.sqlite")
            config, token = configuration(args.provider, database, output, tls=(args.certificate.resolve(), args.private_key.resolve()))
            config.update(runWorkers=True, maxBlobBytes=settings["applicationBlobBudgetBytes"])
            sql = Database(args.provider, database, str(cluster.binary / "psql") if cluster else None)
            with Service(traced_command, config, output / "service", observed=True, http2=True, certificate=args.certificate) as service:
                receipt["executionConfigSha256"] = sha(output / "service/config.private.json")
                receipt["serviceProcessId"] = service.process.pid
                tail = Tail(output / "service/observations.jsonl"); tail.read()
                workload = Workload(service, token, sql, config, settings["seed"], operations)
                stage_start = time.monotonic_ns(); last_fault = stage_start - int(settings["faultEverySeconds"] * 1e9)
                with concurrent.futures.ThreadPoolExecutor(max_workers=settings["concurrency"]) as executor:
                    if args.action == "run" and args.stage == "steady-30m":
                        load(required, executor)
                        receipt["steadyLoadElapsedSeconds"] = (time.monotonic_ns() - stage_start) / 1e9
                        receipt["finalDurableOracle"] = idle(settings["regressionIdleSeconds"])
                        receipt["completedCycles"] = 1
                    else:
                        count = settings["calibrationCycles"] if args.action in ("probe", "calibrate") else required // (settings["regressionLoadSeconds"] + settings["regressionIdleSeconds"])
                        for cycle in range(count):
                            load(settings["calibrationLoadSeconds"] if args.action in ("probe", "calibrate") else settings["regressionLoadSeconds"], executor)
                            receipt["finalDurableOracle"] = idle(settings["calibrationIdleSeconds"] if args.action in ("probe", "calibrate") else settings["regressionIdleSeconds"])
                            receipt["completedCycles"] = cycle + 1; checkpoint()
                            print(json.dumps({"stage": args.stage or args.action, "cycle": cycle + 1, "requiredCycles": count, "elapsedSeconds": receipt["stageElapsedSeconds"]}), flush=True)
                require(service.protocols == {"HTTP/2"}, "Ordinary soak requests did not use the frozen HTTP2/TLS profile")
                receipt["observedOrdinaryHttpVersions"] = sorted(service.protocols)
                receipt["stageCompletedBeforeClosureNs"] = time.monotonic_ns()
            tail.read(); final = tail.latest
            require(final["phase"] == "observer-stop" and set(final["databaseNativeOwners"]) == expected_providers and
                all(len(values) == 6 and all(value == 0 for value in values) for values in final["databaseNativeOwners"].values()), "Final actual native owners did not retire")
            require(final["observerFailures"] == 0 and all(final["serverMeter"].get(key) == 0 for key in ZERO_GAUGES), "Final actual server owners did not retire")
            receipt["finalRetirement"] = final
        revalidate_inputs(args, inputs)
        receipt["inputRechecks"] = ["before-runtime-trace-analysis"]
        # File growth is observed while the service is alive, independently of
        # the final flush. Missing runtime streaming cannot masquerade as a
        # bounded target-process resource profile.
        require(receipt.get("traceBytesObservedWhileRunning", 0) > 1024,
            "Runtime trace did not stream event blocks during the observed process lifetime")
        trace_receipt = trace_analyze(args, output); receipt["traceObservation"] = trace_receipt
        roots = idle_trace_windows(output / "trace-receipt.json.samples.jsonl", control_sequence)
        root_values = [max(row["trackedLiveHandles"], row["runtimeGcHandlesAtLastGc"]) for row in roots]
        receipt["idleGcHandleObservations"] = roots
        receipt["qualifiedPostGcNativeObservations"] = post_gc_native
        if profile:
            require(all(value <= profile["idleGcHandleCap"] for value in root_values), "Frozen idle runtime handle cap exceeded")
        checkpoint()
        actual_duration = (receipt["stageCompletedBeforeClosureNs"] - stage_start) / 1e9
        require(actual_duration >= required, "The exact required elapsed duration has not completed")
        revalidate_inputs(args, inputs)
        receipt["inputRechecks"].append("before-final-acceptance")
        receipt.update(status="probe-passed" if args.action == "probe" else "calibration-passed" if args.action == "calibrate" else "stage-passed", completedStageSeconds=actual_duration)
        if args.action == "calibrate":
            require(len(idle_vectors) == settings["calibrationCycles"] >= 3, "The declared calibration cycles did not all finish")
            require(all(value == post_gc_native[0] for value in post_gc_native), "Qualified post-GC native baseline did not stabilize")
            # Freeze actual measured maximum plus actual observed spread. No
            # universal percentage or unmeasured claimed benefit is introduced.
            idle_caps = {key: max(row[key] for row in idle_vectors) + max(row[key] for row in idle_vectors) - min(row[key] for row in idle_vectors) for key in idle_vectors[0]}
            peak_caps = {key: value + max(row[key] for row in idle_vectors) - min(row[key] for row in idle_vectors) for key, value in all_max.items()}
            candidate = {"schemaVersion": 1, "task": "B22", "status": "candidate-from-passed-calibration", "identity": inputs, "settings": settings,
                "calibrationReceipt": str(output / "receipt.json"), "calibrationDirectory": str(output),
                "thresholdDerivation": "observed maximum + observed idle-cycle range; exact hard Server budgets retain their accepted values; no performance-improvement threshold",
                "peakCaps": peak_caps, "idleCaps": idle_caps, "postGcNativeBaseline": post_gc_native[0],
                "idleGcHandleCap": max(root_values) + max(root_values) - min(root_values),
                "requiredStages": STAGES, "durationPolicy": "Each named stage must reach its declared actual elapsed duration in this attempt. Interrupted attempts remain explicit; no silently aggregated process lifetime.",
                "limitations": ["Process GCHandle totals/tracked kinds do not count every managed object graph root or attribute all handles exclusively to NativeWeave.", "Native allocator live/retained bytes are unavailable; RSS is recorded separately and never substituted.", "Durable database growth is intentional and independently counted; a disk limit or generator lag makes the stage fail.", "Only the declared stage/workload is accepted; the other required stages retain their own status."]}
            candidate["peakCaps"].update({"nativeweave.server.requests.active": 256, "nativeweave.server.requests.queued": 128,
                "nativeweave.server.requests.retiring": 256, "nativeweave.server.payload.managed.bytes": 64 * 1024**2,
                "nativeweave.server.payload.native.bytes": 64 * 1024**2})
            policy = inputs["nativePeakPolicy"]
            applied_floors = {}
            for provider, slots in policy["connectionSlots"].items():
                prefix = "native." + provider + "."
                candidate["peakCaps"][prefix + "0"] = policy["sourceCounts"][provider]
                floors = {1: slots, 2: 3 * slots, 3: slots, 4: 3 * slots,
                          5: post_gc_native[0][provider][5] + 11 * slots}
                for kind, floor in floors.items():
                    key = prefix + str(kind)
                    candidate["peakCaps"][key] = max(candidate["peakCaps"][key], floor)
                    applied_floors[key] = floor
            candidate["nativePeakPolicy"] = {**policy, "appliedFloors": applied_floors}
            candidate["thresholdDerivation"] += "; native transient peaks also retain the independently reviewed populated source-cohort floors"
            for key in ZERO_GAUGES: candidate["idleCaps"][key] = 0
    except BaseException as error:
        receipt.update(status="interrupted" if isinstance(error, (InterruptedError, KeyboardInterrupt)) or stopping.is_set() else "failed",
            error={"type": type(error).__name__, "oracle": str(error) if isinstance(error, (AssertionError, InterruptedError)) or type(error).__name__ == "OracleFailure" else None})
        if trace.exists(): receipt["retainedTraceBytes"] = trace.stat().st_size
    finally:
        checkpoint(); operations.close(); resources.close()
        receipt["endedUnixNs"] = time.time_ns()
        receipt["rawHashes"] = {name: sha(output / name) for name in ("identity.json", "operations.jsonl", "resources.jsonl", "runtime.nettrace", "known-handles.jsonl",
            "service/observations.jsonl", "service/service.log", "postgres/identity.json", "postgres/server.log") if (output / name).exists()}
        write_json(output / "receipt.json", receipt)
        if candidate is not None and receipt["status"] == "calibration-passed":
            candidate["calibrationReceiptSha256"] = sha(output / "receipt.json")
            write_json(output / "profile-candidate.json", candidate)
        for sig, previous in previous_handlers.items(): signal.signal(sig, previous)
    print(json.dumps({"status": receipt["status"], "stage": args.stage, "elapsedSeconds": receipt.get("stageElapsedSeconds"), "receipt": str(output / "receipt.json"), "fullB22Acceptance": False}), flush=True)
    return 0 if receipt["status"] in ("probe-passed", "calibration-passed", "stage-passed") else 1


if __name__ == "__main__": sys.exit(main())
