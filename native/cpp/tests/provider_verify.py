#!/usr/bin/env python3
"""Run native provider regressions against real, lifetime-owned PostgreSQL TLS servers."""

import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import time

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def certificates(directory):
    """Preserve the B02 trusted/untrusted/expired identity assertions."""
    directory.mkdir()
    now = datetime.datetime.now(datetime.timezone.utc)

    def authority(name):
        key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        subject = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, name)])
        cert = (
            x509.CertificateBuilder()
            .subject_name(subject)
            .issuer_name(subject)
            .public_key(key.public_key())
            .serial_number(x509.random_serial_number())
            .not_valid_before(now - datetime.timedelta(days=1))
            .not_valid_after(now + datetime.timedelta(days=10))
            .add_extension(x509.BasicConstraints(ca=True, path_length=0), True)
            .add_extension(x509.KeyUsage(False, False, False, False, False, True, True, False, False), True)
            .sign(key, hashes.SHA256())
        )
        return key, cert

    ca_key, root = authority("DB-CPP-01 test root")
    _, unrelated = authority("DB-CPP-01 unrelated root")
    for name, cert in [("root", root), ("untrusted", unrelated)]:
        (directory / f"{name}.pem").write_bytes(cert.public_bytes(serialization.Encoding.PEM))
    for name, expired in [("valid", False), ("expired", True)]:
        key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        cert = (
            x509.CertificateBuilder()
            .subject_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "localhost")]))
            .issuer_name(root.subject)
            .public_key(key.public_key())
            .serial_number(x509.random_serial_number())
            .not_valid_before(now - datetime.timedelta(days=3))
            .not_valid_after(now + datetime.timedelta(days=-1 if expired else 3))
            .add_extension(x509.BasicConstraints(ca=False, path_length=None), True)
            .add_extension(x509.SubjectAlternativeName([x509.DNSName("localhost")]), False)
            .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), False)
            .sign(ca_key, hashes.SHA256())
        )
        (directory / f"{name}.pem").write_bytes(cert.public_bytes(serialization.Encoding.PEM))
        private = directory / f"{name}.key"
        private.write_bytes(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
        private.chmod(0o600)
    return {name: digest(directory / f"{name}.pem") for name in ["root", "valid", "expired", "untrusted"]}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument("--postgres-bin", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    pg = args.postgres_bin.resolve()
    env = {key: value for key, value in os.environ.items() if not key.startswith("PG")}
    records = []
    servers = []
    status = "failed"

    def run(label, command, extra=None):
        began = time.monotonic()
        stdout = output / f"{label}.stdout.log"
        stderr = output / f"{label}.stderr.log"
        with stdout.open("w") as out, stderr.open("w") as err:
            result = subprocess.run([str(part) for part in command], env=env | (extra or {}), stdout=out, stderr=err)
        records.append({"label": label, "command": [str(part) for part in command], "exit_code": result.returncode,
                        "seconds": round(time.monotonic() - began, 3), "stdout_sha256": digest(stdout), "stderr_sha256": digest(stderr)})
        (output / "commands.json").write_text(json.dumps(records, indent=2) + "\n")
        print(f"{label}: {result.returncode}", flush=True)
        if result.returncode:
            raise RuntimeError(f"{label} failed; see {stderr}")

    try:
        certs = output / "certificates"
        (output / "tls-identities.json").write_text(json.dumps(certificates(certs), indent=2) + "\n")
        urls = {}
        for name, tls in [("valid", True), ("expired", True), ("plain", False)]:
            data = output / f"pg-{name}"
            run(f"init-{name}", [pg / "initdb", "-D", data, "-U", "nativeweave", "--auth=trust", "--no-locale", "--encoding=UTF8"])
            with socket.socket() as sock:
                sock.bind(("127.0.0.1", 0))
                port = sock.getsockname()[1]
            with (data / "postgresql.conf").open("a") as config:
                config.write("\nfsync=on\nssl=" + ("on" if tls else "off") + "\n")
                if tls:
                    config.write(f"ssl_cert_file='{certs / name}.pem'\nssl_key_file='{certs / name}.key'\n")
            log = (output / f"server-{name}.log").open("w")
            process = subprocess.Popen([str(pg / "postgres"), "-D", str(data), "-h", "127.0.0.1", "-p", str(port), "-k", ""], env=env, stdout=log, stderr=subprocess.STDOUT)
            servers.append((process, data, log))
            for _ in range(200):
                if process.poll() is not None:
                    raise RuntimeError(f"{name} server exited")
                if subprocess.run([str(pg / "pg_isready"), "-h", "127.0.0.1", "-p", str(port)], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0:
                    break
                time.sleep(0.05)
            else:
                raise RuntimeError("PostgreSQL startup deadline expired")
            urls[name] = f"postgresql://nativeweave@localhost:{port}/postgres"
        (output / "server-topology.json").write_text(json.dumps({"endpoints": urls, "postgres_sha256": digest(pg / "postgres"), "test_binary_sha256": digest(args.binary.resolve())}, indent=2) + "\n")
        run("provider", [args.binary.resolve()], {"NW_PG_URL": urls["valid"], "NW_PG_EXPIRED_URL": urls["expired"], "NW_PG_PLAIN_URL": urls["plain"],
                                                   "NW_PG_ROOT": str(certs / "root.pem"), "NW_PG_UNTRUSTED_ROOT": str(certs / "untrusted.pem")})
        status = "passed"
    except Exception as error:
        (output / "failure.txt").write_text(str(error) + "\n")
        print(str(error), flush=True)
    finally:
        for process, data, log in reversed(servers):
            subprocess.run([str(pg / "pg_ctl"), "-D", str(data), "-m", "fast", "stop"], env=env, stdout=log, stderr=subprocess.STDOUT)
            try:
                process.wait(timeout=20)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
            log.close()
            if status == "passed":
                shutil.rmtree(data)
        (output / "summary.json").write_text(json.dumps({"status": status, "commands": len(records), "servers_stopped": len(servers), "rust_required": False}, indent=2) + "\n")
    return 0 if status == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
