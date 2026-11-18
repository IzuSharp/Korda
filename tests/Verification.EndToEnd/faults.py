"""Actual reference-service failures with independent durable-data oracles."""
from __future__ import annotations

import contextlib
import hashlib
import http.client
import json
import os
from pathlib import Path
import signal
import socket
import sqlite3
import ssl
import subprocess
import time
from urllib.parse import urlsplit

from harness import (Database, Evidence, OneShot, Request, Service, Unavailable, admin,
                     configuration, create, eventually, require, write_json)
from protocol_proxy import FaultProxy


def quote(value):
    return "'" + value.replace("'", "''") + "'"


def events(path):
    if not path.exists(): return []
    lines = path.read_text().splitlines()
    return [json.loads(line) for line in lines if line.strip()]


class Faults:
    def __init__(self, command, provider, evidence: Evidence, *, cluster=None, tls=None, shim=None, observed=False):
        self.command, self.provider, self.evidence = command, provider, evidence
        self.cluster, self.tls, self.shim = cluster, tls, shim
        self.observed = observed

    def setup(self, path):
        path.mkdir(parents=True, exist_ok=False)
        database = self.cluster.database() if self.provider == "postgresql" else str(path / "application.sqlite")
        config, token = configuration(self.provider, database, path, tls=self.tls)
        sql = Database(self.provider, database, str(self.cluster.binary / "psql") if self.cluster else None)
        return config, token, sql

    def service(self, config, path, command=None, *, http2=False):
        return Service(command or self.command, config, path, http2=http2,
                       certificate=self.tls[0] if self.tls else None, observed=self.observed)

    def atomic(self, sql, key, count, *, response=None):
        rows = sql.sql("SELECT operation_id FROM operations WHERE request_key=" + quote(key))
        self.evidence.check("operation-cardinality", len(rows) == count, observed=len(rows), expected=count)
        for row in rows:
            operation = row[0]
            item = int(sql.scalar("SELECT count(*) FROM items WHERE operation_id=" + quote(operation)))
            outbox = int(sql.scalar("SELECT count(*) FROM outbox WHERE operation_id=" + quote(operation)))
            self.evidence.check("atomic-item-result-outbox", item == 1 and outbox == 1, items=item, outbox=outbox)
            if response is not None:
                version, status, stored = sql.replay(operation)
                self.evidence.check("stored-response-bytes", version == 1 and status == 201 and stored == response,
                                    bytes=len(stored), sha256=hashlib.sha256(stored).hexdigest())

    def no_attachment(self, sql, item):
        count = int(sql.scalar("SELECT count(*) FROM attachments WHERE item_id=" + quote(item)))
        version = int(sql.scalar("SELECT version FROM items WHERE item_id=" + quote(item)))
        self.evidence.check("no-incomplete-reference", count == 0 and version == 1, references=count, itemVersion=version)

    def preload(self, config, path, mode, target):
        if self.shim is None:
            raise Unavailable("No built B22-only filesystem fault shim was supplied")
        audit = path / "filesystem.jsonl"
        arm = path / "filesystem.arm"
        release = path / "filesystem.release"
        prefix = ["env", "LD_PRELOAD=" + str(self.shim), "NW_B22_IO_TARGET=" + str(target),
                  "NW_B22_IO_ARM=" + str(arm), "NW_B22_IO_AUDIT=" + str(audit),
                  "NW_B22_IO_RELEASE=" + str(release), "NW_B22_IO_MODE=" + mode]
        return [*prefix, *self.command], arm, audit, release

    def pg_disconnect(self, path, category):
        config, token, sql = self.setup(path)
        key = "fault-" + category
        with FaultProxy(config["database"], path / "proxy") as proxy:
            proxied = dict(config, database=proxy.database_uri)
            with self.service(proxied, path / "service") as service:
                proxy.arm_fault(category, "disconnect")
                result = Request(lambda: create(service, token, key, "interrupted write")).result(allow_transport_error=True)
                pid = proxy.wait()
                self.evidence.check("non-successful-faulted-request", isinstance(result, dict) or result[0] >= 500,
                                    result=result if isinstance(result, dict) else {"status": result[0]})
                eventually(lambda: int(sql.scalar(f"SELECT count(*) FROM pg_stat_activity WHERE pid={pid}")) == 0,
                           "disconnected selected backend retired")
                self.atomic(sql, key, 0)
                status, body, _ = create(service, token, key, "interrupted write")
                self.evidence.check("same-key-retry-after-actual-rollback", status == 201, status=status)
                self.atomic(sql, key, 1, response=body)
            self.evidence.check("proxy-has-no-controller-failure", not proxy.failures, count=len(proxy.failures))

    def pg_commit(self, path, *, acknowledgement_loss):
        config, token, sql = self.setup(path)
        key = "lost-ack" if acknowledgement_loss else "held-commit"
        with FaultProxy(config["database"], path / "proxy", timeout=60) as proxy:
            with self.service(dict(config, database=proxy.database_uri), path / "service") as service:
                proxy.arm_fault("commit", "discard" if acknowledgement_loss else "hold")
                request = Request(lambda: create(service, token, key, "uncertain commit"))
                pid = proxy.wait()
                if not acknowledgement_loss:
                    self.atomic(sql, key, 0)
                    proxy.disconnect_selected()
                else:
                    require(proxy.discarded.wait(30), "Actual COMMIT and idle ReadyForQuery were not discarded")
                status, body, _ = request.result()
                problem = json.loads(body)
                self.evidence.check("unknown-is-preserved", status == 503 and problem.get("code") == "outcome_unknown",
                                    status=status, code=problem.get("code"), upstreamCommitAcknowledged=acknowledgement_loss)
                operation = problem["operationId"]
                marker = Path(config["blobDirectory"]) / "recovery" / (operation + ".json")
                self.evidence.check("unknown-recovery-protected", marker.is_file())
                if not acknowledgement_loss:
                    eventually(lambda: int(sql.scalar(f"SELECT count(*) FROM pg_stat_activity WHERE pid={pid}")) == 0,
                               "selected uncommitted backend retirement")
                self.atomic(sql, key, int(acknowledgement_loss))
                recovery = service.request("GET", "/v1/operations/" + operation, token=token)
                self.evidence.check("authorized-recovery-reflects-durable-observation",
                    recovery[0] == (200 if acknowledgement_loss else 404), status=recovery[0])
                replay = create(service, token, key, "uncertain commit")
                self.evidence.check("protected-same-key-recovery", replay[0] == 201,
                                    status=replay[0], sameKeyProtocol=True)
                self.atomic(sql, key, 1, response=replay[1])
                if acknowledgement_loss:
                    self.evidence.check("recovery-and-replay-byte-equality", recovery[1] == replay[1])
                self.evidence.check("not-found-does-not-remove-unknown-marker", marker.is_file())
            self.evidence.check("proxy-has-no-controller-failure", not proxy.failures, count=len(proxy.failures))

    def pg_drain(self, path):
        config, token, sql = self.setup(path)
        with FaultProxy(config["database"], path / "proxy") as proxy:
            with self.service(dict(config, database=proxy.database_uri), path / "service") as service:
                # The first Sync belongs to TransactionDriver::commit's own
                # confirmation drain. The second is Session::request_close's
                # pool-return drain; only this later fault preserves Committed.
                proxy.arm_fault("drain_after_commit", "disconnect", match_number=2)
                status, body, _ = create(service, token, "drain", "committed before cleanup failure")
                pid = proxy.wait()
                self.evidence.check("committed-effect-survives-cleanup-failure", status == 201, status=status)
                self.atomic(sql, "drain", 1, response=body)
                eventually(lambda: int(sql.scalar(f"SELECT count(*) FROM pg_stat_activity WHERE pid={pid}")) == 0,
                           "failed protocol-drain backend physical retirement")
                retry_start = proxy._record("independent_retry_started", failed_backend_pid=pid)
                replay = create(service, token, "drain", "committed before cleanup failure")
                retry_claims = [value for value in proxy.snapshot() if value["sequence"] > retry_start and
                    value["event"] == "command" and value.get("category") == "operations_insert"]
                self.evidence.check("discarded-connection-is-not-reused", replay[0] == 201 and replay[1] == body and
                    len(retry_claims) == 1 and retry_claims[0].get("backend_pid") not in (None, pid),
                    failedBackendPid=pid, retryClaimCommands=retry_claims)
            self.evidence.check("proxy-has-no-controller-failure", not proxy.failures, count=len(proxy.failures))

    def pg_restart(self, path):
        config, token, sql = self.setup(path)
        with FaultProxy(config["database"], path / "proxy", timeout=60) as proxy:
            with self.service(dict(config, database=proxy.database_uri), path / "service") as service:
                proxy.arm_fault("commit", "hold")
                request = Request(lambda: create(service, token, "restart", "server restart"))
                proxy.wait()
                self.cluster.stop(immediate=True)
                proxy.disconnect_selected()
                self.cluster.start()
                result = request.result(allow_transport_error=True)
                self.evidence.check("restart-does-not-invent-confirmation", isinstance(result, dict) or result[0] >= 500,
                                    result=result if isinstance(result, dict) else {"status": result[0]})
                self.atomic(sql, "restart", 0)
                status, body, _ = create(service, token, "restart", "server restart")
                self.evidence.check("actual-postgres-restart-recovery", status == 201, status=status)
                self.atomic(sql, "restart", 1, response=body)

    def sqlite_busy(self, path):
        config, token, sql = self.setup(path)
        with self.service(config, path / "service") as service:
            with sqlite3.connect(config["database"], isolation_level=None) as writer:
                writer.execute("BEGIN IMMEDIATE")
                started = time.monotonic()
                status, _, _ = create(service, token, "busy", "external SQLite writer")
                self.evidence.check("real-busy-rejected", writer.in_transaction and status >= 500,
                                    status=status, elapsedSeconds=time.monotonic() - started)
                self.atomic(sql, "busy", 0)
                writer.execute("ROLLBACK")
            status, body, _ = create(service, token, "busy", "external SQLite writer")
            self.evidence.check("busy-release-restores-admission", status == 201, status=status)
            self.atomic(sql, "busy", 1, response=body)

    def sqlite_enospc(self, path):
        config, token, sql = self.setup(path)
        command, arm, audit, _ = self.preload(config, path, "enospc", config["database"] + "-wal")
        with self.service(config, path / "service", command) as service:
            arm.write_text("one exact WAL write\n")
            status, fault_body, _ = create(service, token, "disk-full", "injected libc I/O ENOSPC")
            observed = events(audit)
            self.evidence.check("actual-native-wal-write-was-faulted", len(observed) == 1 and observed[0]["event"] == "injected-libc-enospc",
                                injection=observed)
            self.evidence.check("disk-full-does-not-return-success", status >= 500, status=status)
            problem = json.loads(fault_body)
            self.evidence.check("commit-io-failure-preserves-unknown", status == 503 and problem.get("code") == "outcome_unknown",
                                status=status, code=problem.get("code"))
            protected = Path(config["blobDirectory"]) / "recovery" / (problem["operationId"] + ".json")
            self.evidence.check("disk-full-unknown-stays-protected", protected.is_file())
            self.atomic(sql, "disk-full", 0)
            status, body, _ = create(service, token, "disk-full", "injected libc I/O ENOSPC")
            self.evidence.check("one-shot-storage-fault-recovers", status == 201, status=status)
            self.atomic(sql, "disk-full", 1, response=body)
        self.evidence.check("sqlite-integrity-after-enospc", sql.scalar("PRAGMA integrity_check") == "ok")
        if not self.observed:
            raise Unavailable("Actual libc ENOSPC and durable rollback observed, but exact SQLITE_FULL providerCode13 still requires the instrumented public exception observer")
        observed = events(path / "service" / "observations.jsonl")
        self.evidence.check("actual-sqlite-full-provider-code", any(row.get("databaseFirstChanceCodes", {}).get("UnknownOutcome:13", 0) > 0 for row in observed),
                            providerCode="13", outcome="UnknownOutcome")

    def outbox_crash(self, path):
        config, token, sql = self.setup(path)
        with self.service(config, path / "initial") as service:
            status, _, _ = create(service, token, "outbox", "one consumer effect")
            self.evidence.check("outbox-fixture-committed", status == 201)
        event = sql.scalar("SELECT event_id FROM outbox")
        consumer = Database("sqlite", config["consumerDatabase"])
        if self.provider == "postgresql":
            with FaultProxy(config["database"], path / "proxy", timeout=60) as proxy:
                proxy.arm_fault("outbox_update", "hold", match_number=2)
                process = OneShot(self.command, dict(config, database=proxy.database_uri), "--outbox-once", path / "crash-worker")
                try:
                    proxy.wait()
                    self.evidence.check("consumer-durable-before-source-ack", consumer.sql("SELECT delivery_attempts,effects FROM receipts") == [(1, 1)])
                    process.crash(); proxy.disconnect_selected()
                finally:
                    process.close()
        else:
            command, arm, audit, release = self.preload(config, path, "after-sync", config["consumerDatabase"] + "-wal")
            arm.write_text("pause consumer WAL preparation to install source writer barrier\n")
            process = OneShot(command, config, "--outbox-once", path / "crash-worker")
            writer = None
            try:
                eventually(lambda: any(x["event"] == "completed-filesystem-call-held" for x in events(audit)), "actual consumer WAL sync barrier")
                # A WAL-header fsync does not yet prove publication. Hold the
                # independent source writer before releasing consumer progress;
                # then require the committed receipt from a separate connection.
                writer = sqlite3.connect(config["database"], isolation_level=None)
                writer.execute("BEGIN IMMEDIATE")
                release.write_text("source acknowledgment is now blocked by an actual SQLite writer\n")
                eventually(lambda: consumer.sql("SELECT delivery_attempts,effects FROM receipts") == [(1, 1)],
                           "independently visible committed consumer receipt", timeout=4)
                self.evidence.check("consumer-durable-before-source-ack", consumer.sql("SELECT delivery_attempts,effects FROM receipts") == [(1, 1)])
                process.crash()
            finally:
                if writer is not None: writer.execute("ROLLBACK"); writer.close()
                process.close()
        state, attempts, lease = sql.sql("SELECT delivery_state,attempt_count,lease_until FROM outbox")[0]
        self.evidence.check("source-ack-not-recorded", int(state) == 1 and int(attempts) == 1, state=int(state), attempts=int(attempts))
        deadline = int(lease) / 1000
        self.evidence.event("waiting-real-outbox-lease", leaseUntilUnixMs=int(lease))
        while time.time() < deadline + 0.05:
            time.sleep(min(0.5, max(0.01, deadline + 0.05 - time.time())))
        admin(self.command, config, "--outbox-once", path / "restarted-worker")
        receipts = consumer.sql("SELECT event_id,delivery_attempts,effects FROM receipts")
        self.evidence.check("at-least-once-one-effect", receipts == [(event, 2, 1)], attempts=receipts[0][1], effects=receipts[0][2])
        self.evidence.check("retry-ack-committed", int(sql.scalar("SELECT delivery_state FROM outbox")) == 2)

    def _raw(self, service):
        parsed = urlsplit(service.url)
        plain = socket.create_connection((parsed.hostname, parsed.port), timeout=15)
        if self.tls:
            context = ssl.create_default_context(cafile=str(self.tls[0]))
            context.minimum_version = ssl.TLSVersion.TLSv1_2
            return context.wrap_socket(plain, server_hostname=parsed.hostname)
        return plain

    def open_upload(self, service, token, item, length, initial, key):
        transport = self._raw(service)
        parsed = urlsplit(service.url)
        headers = (f"PUT /v1/items/{item}/attachment HTTP/1.1\r\nHost: {parsed.netloc}\r\n"
            f"Authorization: Bearer {token}\r\nIdempotency-Key: {key}\r\nIf-Match: 1\r\n"
            f"Content-Type: application/octet-stream\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n")
        transport.sendall(headers.encode() + initial)
        return transport

    def upload_crash(self, path, *, after_rename):
        config, token, sql = self.setup(path)
        data = b"B22 immutable attachment\n" * 4096
        digest = hashlib.sha256(data).hexdigest()
        proxy_context = FaultProxy(config["database"], path / "proxy", timeout=60) if self.provider == "postgresql" else contextlib.nullcontext(None)
        with proxy_context as proxy:
            runtime_config = dict(config, database=proxy.database_uri) if proxy else config
            with self.service(runtime_config, path / "crashed-service") as service:
                status, body, _ = create(service, token, "upload-parent", "attachment crash")
                require(status == 201, "Attachment fixture create did not commit")
                item = json.loads(body)["itemId"]
                writer = None
                if after_rename:
                    if proxy: proxy.arm_fault("begin", "hold")
                    else:
                        writer = sqlite3.connect(config["database"], isolation_level=None)
                        writer.execute("BEGIN IMMEDIATE")
                    request = Request(lambda: service.request("PUT", f"/v1/items/{item}/attachment", data,
                        {"Content-Type": "application/octet-stream", "Idempotency-Key": "upload-crash", "If-Match": "1"}, token))
                    if proxy: proxy.wait()
                    blob = Path(config["blobDirectory"]) / "blobs" / digest
                    eventually(lambda: blob.exists(), "actual immutable blob rename before binding")
                    self.evidence.check("renamed-content-complete", blob.read_bytes() == data)
                else:
                    transport = self.open_upload(service, token, item, len(data), data[:32768], "upload-crash")
                    eventually(lambda: any(p.exists() for p in (Path(config["blobDirectory"]) / "staging").glob("*.part")),
                               "actual incomplete staged upload")
                    sizes = [p.stat().st_size for p in (Path(config["blobDirectory"]) / "staging").glob("*.part")]
                    self.evidence.check("staged-upload-cannot-be-complete", len(sizes) == 1 and sizes[0] < len(data),
                                        stagedBytes=sizes[0], declaredBytes=len(data), clientSentBytes=32768)
                self.no_attachment(sql, item)
                service.crash()
                if proxy and after_rename: proxy.disconnect_selected()
                if writer is not None: writer.execute("ROLLBACK"); writer.close()
                if after_rename: request.result(allow_transport_error=True)
                else: transport.close()
        self.no_attachment(sql, item)
        with self.service(config, path / "restarted-service") as restarted:
            self.evidence.check("restart-has-no-false-successful-reference", restarted.request("GET", f"/v1/items/{item}/attachment", token=token)[0] == 404)
            status, _, _ = restarted.request("PUT", f"/v1/items/{item}/attachment", data,
                {"Content-Type": "application/octet-stream", "Idempotency-Key": "upload-crash", "If-Match": "1"}, token)
            self.evidence.check("same-key-upload-recovery", status == 200, status=status)
            downloaded = restarted.request("GET", f"/v1/items/{item}/attachment", token=token)
            self.evidence.check("reachable-content-is-complete", downloaded[0] == 200 and downloaded[1] == data,
                                bytes=len(downloaded[1]), sha256=hashlib.sha256(downloaded[1]).hexdigest())
        admin(self.command, config, "--maintenance-once", path / "reconcile")
        self.evidence.check("reconciled-reference-keeps-content", (Path(config["blobDirectory"]) / "blobs" / digest).read_bytes() == data)

    def gc_crash(self, path):
        config, token, sql = self.setup(path)
        content = b"referenced content must survive collection"
        retained_hash = hashlib.sha256(content).hexdigest()
        with self.service(config, path / "initial") as service:
            status, body, _ = create(service, token, "gc-parent", "referenced blob")
            require(status == 201, "GC fixture create failed")
            item = json.loads(body)["itemId"]
            status, _, _ = service.request("PUT", f"/v1/items/{item}/attachment", content,
                {"Content-Type": "application/octet-stream", "Idempotency-Key": "gc-attachment", "If-Match": "1"}, token)
            require(status == 200, "GC fixture attachment failed")
        blobs = Path(config["blobDirectory"]) / "blobs"
        orphans = []
        for index in range(8):
            payload = ("independent-old-orphan-" + str(index)).encode()
            blob = blobs / hashlib.sha256(payload).hexdigest()
            with blob.open("wb") as stream:
                stream.write(payload); stream.flush(); os.fsync(stream.fileno())
            os.utime(blob, (time.time() - 120, time.time() - 120)); orphans.append(blob)
        os.utime(blobs / retained_hash, (time.time() - 120, time.time() - 120))
        command, arm, audit, _ = self.preload(config, path, "after-unlink", str(blobs) + "/")
        arm.write_text("hold after actual eligible orphan unlink\n")
        process = OneShot(command, config, "--maintenance-once", path / "crashed-collector")
        try:
            eventually(lambda: any(x["event"] == "completed-filesystem-call-held" for x in events(audit)), "actual successful GC unlink")
            remaining = sum(p.exists() for p in orphans)
            self.evidence.check("crash-inside-actual-collection", 0 < remaining < len(orphans), remaining=remaining, seeded=len(orphans))
            process.crash()
        finally:
            process.close()
        self.evidence.check("reference-safe-across-collector-crash", (blobs / retained_hash).read_bytes() == content)
        admin(self.command, config, "--maintenance-once", path / "restarted-collector")
        self.evidence.check("orphan-collection-resumes", not any(p.exists() for p in orphans))
        with self.service(config, path / "final-service") as service:
            result = service.request("GET", f"/v1/items/{item}/attachment", token=token)
            self.evidence.check("authorized-reference-after-gc", result[0] == 200 and result[1] == content)

    def half_close(self, path):
        config, token, sql = self.setup(path)
        with self.service(config, path / "service") as service:
            status, body, _ = create(service, token, "half-close-parent", "HTTP half close")
            require(status == 201, "Half-close parent fixture failed")
            item = json.loads(body)["itemId"]
            transport = self.open_upload(service, token, item, 65536, b"x" * 4096, "half-close")
            transport.shutdown(socket.SHUT_WR)
            wire = bytearray()
            try:
                while True:
                    part = transport.recv(8192)
                    if not part: break
                    require(len(wire) + len(part) <= 65536, "Half-close response exceeded its bound")
                    wire.extend(part)
            except (ConnectionResetError, ssl.SSLError):
                pass
            finally:
                transport.close()
            self.evidence.check("half-close-did-not-complete-attachment", not bytes(wire).startswith(b"HTTP/1.1 200"), responseBytes=len(wire))
            self.no_attachment(sql, item)
            self.evidence.check("host-continues-after-half-close", service.request("GET", "/v1/items", token=token)[0] == 200)

    def http2_reset(self, path):
        if not self.tls:
            raise Unavailable("The selected HTTP2 reset profile requires the explicit TLS certificate")
        import h2.config
        import h2.connection
        import h2.events
        config, token, sql = self.setup(path)
        with self.service(config, path / "service", http2=True) as service:
            status, body, _ = create(service, token, "h2-parent", "HTTP2 reset")
            require(status == 201, "HTTP2 parent fixture failed")
            item = json.loads(body)["itemId"]
            parsed = urlsplit(service.url)
            context = ssl.create_default_context(cafile=str(self.tls[0]))
            context.minimum_version = ssl.TLSVersion.TLSv1_2
            context.set_alpn_protocols(["h2"])
            with socket.create_connection((parsed.hostname, parsed.port), timeout=15) as plain:
                with context.wrap_socket(plain, server_hostname=parsed.hostname) as transport:
                    self.evidence.check("actual-http2-alpn", transport.selected_alpn_protocol() == "h2")
                    protocol = h2.connection.H2Connection(config=h2.config.H2Configuration(client_side=True, header_encoding="utf-8"))
                    protocol.initiate_connection()
                    protocol.send_headers(1, [(":method", "PUT"), (":scheme", "https"), (":authority", parsed.netloc),
                        (":path", f"/v1/items/{item}/attachment"), ("authorization", "Bearer " + token),
                        ("idempotency-key", "h2-reset"), ("if-match", "1"), ("content-type", "application/octet-stream"),
                        ("content-length", "65536")], end_stream=False)
                    protocol.send_data(1, b"x" * 8192, end_stream=False)
                    transport.sendall(protocol.data_to_send())
                    eventually(lambda: any(p.exists() for p in (Path(config["blobDirectory"]) / "staging").glob("*.part")),
                               "native HTTP2 upload admitted before reset")
                    protocol.reset_stream(1, error_code=8); transport.sendall(protocol.data_to_send())
                    self.evidence.event("actual-http2-rst-stream-sent", stream=1, errorCode=8)
            self.no_attachment(sql, item)
            self.evidence.check("host-continues-after-http2-reset", service.request("GET", "/v1/items", token=token)[0] == 200)
