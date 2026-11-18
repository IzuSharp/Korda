"""Bounded external process/evidence helpers for the actual B22 composition.

No NativeWeave implementation is imported. The B21 controller supplies its
already-reviewed HTTP client and independent SQL adapter, not product outcomes.
"""
from __future__ import annotations

import base64
import contextlib
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import secrets
import signal
import socket
import subprocess
import sys
import threading
import time
from typing import Any, Callable

HERE = Path(__file__).resolve().parent
REFERENCE = HERE.parent / "Reference.TaskService"
sys.path.insert(0, str(REFERENCE))
_spec = importlib.util.spec_from_file_location("b22_reference_transport", REFERENCE / "run.py")
assert _spec and _spec.loader
reference = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(reference)
Database = reference.Database
create = reference.create
from postgres_oracles import PostgreSqlProxy, OracleFailure, _cstring  # noqa: E402


class Unavailable(RuntimeError):
    """An explicit real execution precondition is absent; never a passed case."""


def sha(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def canonical(value: Any) -> bytes:
    return (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".new")
    with temporary.open("wb") as stream:
        stream.write(canonical(value)); stream.flush(); os.fsync(stream.fileno())
    temporary.replace(path)
    descriptor = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def require(condition: bool, message: str) -> None:
    if not condition:
        raise OracleFailure(message)


def eventually(predicate: Callable[[], Any], description: str, timeout: float = 30) -> Any:
    deadline = time.monotonic() + timeout
    while True:
        value = predicate()
        if value:
            return value
        if time.monotonic() >= deadline:
            raise OracleFailure("Missing actual barrier: " + description)
        time.sleep(0.01)


class Request:
    """One independent external request with finite owned completion state."""
    def __init__(self, action: Callable[[], Any]):
        self.done = threading.Event()
        self.value = None
        self.error: BaseException | None = None
        def run():
            try:
                self.value = action()
            except BaseException as error:
                self.error = error
            finally:
                self.done.set()
        self.thread = threading.Thread(target=run, daemon=True)
        self.thread.start()

    def result(self, timeout: float = 60, *, allow_transport_error: bool = False):
        require(self.done.wait(timeout), "External request exceeded its finite controller deadline")
        if self.error is not None:
            if allow_transport_error:
                return {"transportError": type(self.error).__name__}
            raise self.error
        return self.value


class Service(reference.Service):
    """B21's real client/process owner with explicitly labelled crash behavior."""
    def __init__(self, *args, observed=False, **kwargs):
        super().__init__(*args, **kwargs)
        self.intentional_crash = False
        self.observed = observed

    def __enter__(self):
        if self.observed:
            self.command = ["env", "NW_B22_OBSERVATIONS=" + str(self.output.resolve() / "observations.jsonl"), *self.command]
        result = super().__enter__()
        if self.observed:
            def live_sample():
                path = self.output / "observations.jsonl"
                if not path.exists(): return False
                try:
                    rows = [json.loads(line) for line in path.read_text().splitlines()]
                except json.JSONDecodeError:
                    return False
                expected = {"sqlite", "postgresql"} if self.config["provider"] == "postgresql" else {"sqlite"}
                return any(set(row["databaseNativeOwners"]) == expected and
                    all(len(values) == 6 and values[0] > 0 for values in row["databaseNativeOwners"].values()) for row in rows)
            eventually(live_sample, "actual provider source visible in selected module", timeout=10)
        return result

    def crash(self):
        require(self.process is not None and self.process.poll() is None,
                "Crash barrier requires a live reference process")
        self.intentional_crash = True
        os.killpg(self.process.pid, signal.SIGKILL)
        self.process.wait(timeout=15)
        require(self.process.returncode == -signal.SIGKILL, "Reference process was not killed at the barrier")

    def __exit__(self, exc_type, exc, traceback):
        if self.intentional_crash:
            # Process exit is intentionally not reported as resource retirement.
            return super().__exit__(RuntimeError, RuntimeError("intentional process crash"), None)
        return super().__exit__(exc_type, exc, traceback)


class Evidence:
    def __init__(self, output: Path, identity: dict[str, Any]):
        self.output = output
        output.mkdir(parents=True, exist_ok=False)
        self.started = time.monotonic_ns()
        self.record: dict[str, Any] = {"schemaVersion": 1, "task": "B22", "platform": "linux-x64",
            "identity": identity, "startedUnixNs": time.time_ns(), "status": "running", "cases": []}
        self.timeline = (output / "timeline.jsonl").open("w", encoding="utf-8")
        self.current: dict[str, Any] | None = None
        self.save()

    def event(self, name: str, **fields):
        self.timeline.write(json.dumps({"monotonicNs": time.monotonic_ns(), "event": name, **fields}, sort_keys=True) + "\n")
        self.timeline.flush()

    def save(self):
        self.record["elapsedSeconds"] = (time.monotonic_ns() - self.started) / 1e9
        write_json(self.output / "receipt.json", self.record)

    def check(self, name: str, condition: bool, **observations):
        require(self.current is not None, "Assertion outside a case")
        item = {"id": name, "status": "passed" if condition else "failed", "observations": observations}
        self.current["assertions"].append(item)
        self.event("assertion", case=self.current["id"], **item)
        self.save()
        require(condition, name)

    @contextlib.contextmanager
    def case(self, name: str):
        require(self.current is None, "Cases must not overlap mutable evidence ownership")
        value = {"id": name, "status": "running", "assertions": [], "startedMonotonicNs": time.monotonic_ns()}
        self.record["cases"].append(value); self.current = value
        self.event("case-start", case=name); self.save()
        try:
            yield self.output / name
            require(bool(value["assertions"]), "A case without a nonvacuous observation cannot pass")
            value["status"] = "passed"
        except Unavailable as error:
            value["status"] = "unavailable"
            value["reason"] = str(error)
        except BaseException as error:
            value["status"] = "interrupted" if isinstance(error, (KeyboardInterrupt, SystemExit)) else "failed"
            value["failureType"] = type(error).__name__
            # SQL/provider exceptions may contain bound values. Only oracle-owned
            # assertions are copied into the public receipt.
            if isinstance(error, OracleFailure):
                value["failure"] = str(error)
            raise
        finally:
            value["elapsedSeconds"] = (time.monotonic_ns() - value["startedMonotonicNs"]) / 1e9
            self.event("case-end", case=name, status=value["status"])
            self.current = None; self.save()

    def close(self):
        cases = self.record["cases"]
        self.record["status"] = "passed" if cases and all(x["status"] == "passed" for x in cases) else "incomplete"
        self.record["longDurationAcceptance"] = {"steady30m": "not-run", "regression24h": "not-run", "release72h": "not-run"}
        self.save(); self.timeline.close()


class PostgresCluster:
    """Disposable PostgreSQL18 children owned by this controller invocation."""
    def __init__(self, binary: Path, output: Path):
        self.binary, self.output = binary.resolve(), output
        self.running = False

    def run(self, name, argv, timeout=90):
        with (self.output / (name + ".log")).open("ab") as log:
            completed = subprocess.run(list(map(str, argv)), stdout=log, stderr=subprocess.STDOUT, timeout=timeout)
        require(completed.returncode == 0, "PostgreSQL lifecycle command failed: " + name)

    def __enter__(self):
        self.output.mkdir(parents=True, exist_ok=False)
        version = subprocess.check_output([self.binary / "postgres", "--version"], text=True).strip()
        require(version.endswith(" 18.0"), "B22 requires the accepted PostgreSQL18.0 profile")
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0)); self.port = listener.getsockname()[1]
        self.data = self.output / "data"
        self.run("init", [self.binary / "initdb", "-D", self.data, "-U", "nativeweave", "--auth=trust", "--no-locale", "--encoding=UTF8"])
        self.uri = f"postgresql://nativeweave@127.0.0.1:{self.port}/postgres"
        self.start()
        write_json(self.output / "identity.json", {"version": version, "postgresSha256": sha(self.binary / "postgres"), "psqlSha256": sha(self.binary / "psql")})
        return self

    def start(self):
        self.run("start", [self.binary / "pg_ctl", "-D", self.data, "-l", self.output / "server.log", "-o", f"-h 127.0.0.1 -p {self.port} -k ''", "-w", "start"])
        self.running = True

    def stop(self, *, immediate=False):
        if self.running:
            self.run("immediate-stop" if immediate else "stop", [self.binary / "pg_ctl", "-D", self.data, "-m", "immediate" if immediate else "fast", "-w", "stop"])
            self.running = False

    def database(self):
        name = "b22_" + secrets.token_hex(8)
        Database("postgresql", self.uri, str(self.binary / "psql")).sql('CREATE DATABASE "' + name + '"')
        return self.uri.rsplit("/", 1)[0] + "/" + name

    def __exit__(self, *args):
        self.stop()


def configuration(provider: str, database: str, output: Path, *, tls: tuple[Path, Path] | None = None):
    token = secrets.token_urlsafe(32)
    config = {"provider": provider, "database": database, "allowLocalDatabaseTlsDisable": True,
        "blobDirectory": str(output / "blobs"), "consumerDatabase": str(output / "consumer.sqlite"),
        "cursorKey": base64.b64encode(secrets.token_bytes(32)).decode(), "runWorkers": False,
        "orphanGraceSeconds": 60, "requestTimeoutSeconds": 30,
        "principals": [{"token": token, "tenant": "b22-tenant", "principal": "b22-actor", "canWrite": True}]}
    if tls:
        config.update(tlsCertificatePem=str(tls[0]), tlsPrivateKeyPem=str(tls[1]))
    return config, token


def admin(command: list[str], config: dict, option: str, output: Path, timeout=90):
    output.mkdir(parents=True, exist_ok=False)
    private = output / "config.private.json"
    write_json(private, config); private.chmod(0o600)
    with (output / "process.log").open("wb") as log:
        result = subprocess.run([*command, "--config", str(private), option], stdout=log, stderr=subprocess.STDOUT, timeout=timeout)
    require(result.returncode == 0, "Administrative reference process failed: " + option)
    return (output / "process.log").read_text()


class OneShot:
    """An externally controlled actual migration/outbox/maintenance process."""
    def __init__(self, command: list[str], config: dict, option: str, output: Path, observed=False):
        output.mkdir(parents=True, exist_ok=False)
        private = output / "config.private.json"
        write_json(private, config); private.chmod(0o600)
        self.log = (output / "process.log").open("wb")
        if observed: command = ["env", "NW_B22_OBSERVATIONS=" + str(output.resolve() / "observations.jsonl"), *command]
        self.process = subprocess.Popen([*command, "--config", str(private), option],
            stdout=self.log, stderr=subprocess.STDOUT, start_new_session=True)
        self.crashed = False

    def crash(self):
        require(self.process.poll() is None, "Fault requires a still-live administrative process")
        os.killpg(self.process.pid, signal.SIGKILL); self.process.wait(timeout=15)
        require(self.process.returncode == -signal.SIGKILL, "Administrative process crash was not observed")
        self.crashed = True

    def close(self):
        if self.process.poll() is None:
            os.killpg(self.process.pid, signal.SIGKILL); self.process.wait(timeout=15)
        self.log.close()
