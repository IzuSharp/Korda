#!/usr/bin/env python3
"""Build only the pinned PostgreSQL auto_explain diagnostic outside its install.

No make install, dependency replacement, product-input modification or timing
credit. A missing source tar may be downloaded from the already locked URL;
an existing mismatching tar is rejected and preserved.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import urllib.request

PG_VERSION = "18.0"
ARCHIVE_SHA = "0d5b903b1e5fe361bca7aa9507519933773eb34266b1357c4e7780fdee6d6078"
ARCHIVE_URL = "https://ftp.postgresql.org/pub/source/v18.0/postgresql-18.0.tar.bz2"
CLANG_SHA = "2033aa32d783adec2a3d30a5d9229b60d9a7594ad73e19e87c3fd2b5ed21a9c2"
SOURCE_MEMBERS = ("postgresql-18.0/contrib/auto_explain/auto_explain.c", "postgresql-18.0/contrib/auto_explain/Makefile")


def sha(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def _within(path: Path, root: Path) -> Path:
    resolved = path.resolve(strict=True)
    require(resolved.is_relative_to(root), "PostgreSQL build input is outside the exact prefix: " + str(path))
    return resolved


def _verify_install(prefix: Path, metadata: dict) -> dict:
    require(metadata.get("version") == PG_VERSION and metadata.get("archiveSha256") == ARCHIVE_SHA,
            "PostgreSQL producer identity does not match the pinned 18.0 source")
    require(metadata.get("compilerSha256") == CLANG_SHA, "PostgreSQL producer did not use pinned Clang 23.1.5")
    recorded = {}
    for name, digest in metadata["files"].items():
        if name.startswith("install/"):
            relative = Path(name).relative_to("install")
            path = _within(prefix / relative, prefix)
            require(sha(path) == digest, "PostgreSQL installed input hash mismatch: " + name)
            recorded[str(relative)] = digest
    require("bin/pg_config" in recorded and "include/server/postgres.h" in recorded,
            "PostgreSQL producer receipt lacks PGXS configuration/server headers")
    return recorded


def build(postgres_prefix, archive, output) -> dict:
    prefix, requested_archive, destination = Path(postgres_prefix).resolve(), Path(archive).resolve(), Path(output).resolve()
    require(not destination.exists() or not any(destination.iterdir()), "Diagnostic build output must be new or empty")
    require(not destination.is_relative_to(prefix), "Diagnostic output cannot modify the PostgreSQL install")
    destination.mkdir(parents=True, exist_ok=True)
    receipt = {"schemaVersion": 1, "task": "DB-Perf-01", "scope": "Separate auto_explain plan diagnostic; never enabled in timing lanes",
               "postgresVersion": PG_VERSION, "postgresPrefix": str(prefix), "requestedArchive": str(requested_archive),
               "archiveUrl": ARCHIVE_URL, "archiveExpectedSha256": ARCHIVE_SHA,
               "timingComparisonEligible": False, "passed": False}
    archive_path = requested_archive
    try:
        producer_path = prefix / "korda-postgresql-build.json"
        producer = json.loads(producer_path.read_text())
        before = _verify_install(prefix, producer)
        receipt["postgresProducer"] = {"path": str(producer_path), "sha256": sha(producer_path), "record": producer}
        receipt["installedInputHashes"] = before
        if not archive_path.exists():
            archive_path = destination / "download-postgresql-18.0.tar.bz2"
            # The URL and digest are the same source used by eng/postgres-tools.sh.
            # Network/permission errors propagate; no alternate mirror is tried.
            with urllib.request.urlopen(ARCHIVE_URL, timeout=60) as response, archive_path.open("xb") as target:
                shutil.copyfileobj(response, target, 1024 * 1024)
            receipt["archiveDownloaded"] = True
        else:
            receipt["archiveDownloaded"] = False
        require(archive_path.is_file() and sha(archive_path) == ARCHIVE_SHA,
                "Existing/downloaded PostgreSQL source archive differs from the locked SHA; retained without replacement")
        receipt["archiveSha256"] = sha(archive_path)
        source = destination / "source"
        source.mkdir()
        source_hashes = {}
        with tarfile.open(archive_path, "r:bz2") as package:
            for name in SOURCE_MEMBERS:
                member = package.getmember(name)
                require(member.isfile() and not member.issym() and not member.islnk(), "Expected a regular pinned auto_explain source")
                target = source / Path(name).name
                with package.extractfile(member) as data:
                    target.write_bytes(data.read())
                source_hashes[name] = {"sha256": sha(target), "bytes": target.stat().st_size}
        receipt["sourceInputs"] = source_hashes
        compiler = Path(os.environ.get("CC", producer["compiler"])).resolve(strict=True)
        require(sha(compiler) == CLANG_SHA, "Use the already installed pinned Clang 23.1.5 executable")
        receipt["compiler"] = {"path": str(compiler), "sha256": sha(compiler),
            "version": subprocess.check_output([str(compiler), "--version"], text=True)}
        pg_config = _within(prefix / "bin/pg_config", prefix)
        configuration = {}
        for option in ("version", "includedir", "includedir-server", "pkgincludedir", "pkglibdir", "pgxs", "configure", "cc", "cflags", "cppflags", "ldflags", "libs"):
            configuration[option] = subprocess.check_output([str(pg_config), "--" + option], text=True).strip()
        require(configuration["version"] == "PostgreSQL " + PG_VERSION, "pg_config version differs")
        for option in ("includedir", "includedir-server", "pkgincludedir", "pkglibdir", "pgxs"):
            _within(Path(configuration[option]), prefix)
        receipt["pgConfig"] = {"path": str(pg_config), "sha256": sha(pg_config), "values": configuration}
        receipt["serverHeaderHashes"] = {name: digest for name, digest in before.items() if name.startswith("include/")}
        receipt["pgxsInputHashes"] = {name: digest for name, digest in before.items() if "/pgxs/" in name or name.endswith("Makefile.global")}
        make = shutil.which("make")
        require(make is not None, "GNU make is required for PGXS")
        command = [make, "-j1", "USE_PGXS=1", "PG_CONFIG=" + str(pg_config), "CC=" + str(compiler), "all"]
        receipt["command"] = command
        with (destination / "build.stdout.log").open("wb") as stdout, (destination / "build.stderr.log").open("wb") as stderr:
            completed = subprocess.run(command, cwd=source, stdout=stdout, stderr=stderr, timeout=120, check=False)
        receipt["exitCode"] = completed.returncode
        require(completed.returncode == 0, "auto_explain PGXS build failed; raw logs retained")
        library = source / "auto_explain.so"
        require(library.is_file(), "PGXS did not produce auto_explain.so")
        target = destination / "auto_explain.so"
        shutil.copy2(library, target)
        require(target.read_bytes()[:5] == b"\x7fELF\x02", "Expected Linux ELF64 auto_explain library")
        receipt["library"] = {"path": "auto_explain.so", "sha256": sha(target), "bytes": target.stat().st_size}
        receipt["postgresInstallUnchanged"] = before == _verify_install(prefix, producer) and sha(producer_path) == receipt["postgresProducer"]["sha256"]
        require(receipt["postgresInstallUnchanged"], "Diagnostic build changed a PostgreSQL install input")
        require(all(sha(source / Path(name).name) == value["sha256"] for name, value in source_hashes.items()),
                "Pinned auto_explain source changed during compilation")
        receipt["passed"] = True
        # Only reproducible PGXS intermediates and a verified downloaded tar are
        # omitted from the bundle. Original source/Makefile/licenses/logs remain.
        for intermediate in (source / "auto_explain.o", source / "auto_explain.so", source / "auto_explain.bc"):
            if intermediate.is_file():
                intermediate.unlink()
        if receipt["archiveDownloaded"]:
            archive_path.unlink()
    except Exception as error:
        receipt["error"] = type(error).__name__ + ": " + str(error)
    for name in ("build.stdout.log", "build.stderr.log"):
        if (destination / name).is_file():
            receipt[name] = {"sha256": sha(destination / name), "bytes": (destination / name).stat().st_size}
    (destination / "build-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--postgres-prefix", required=True)
    parser.add_argument("--archive", required=True)
    parser.add_argument("--output", required=True)
    options = parser.parse_args()
    receipt = build(options.postgres_prefix, options.archive, options.output)
    print(json.dumps({"passed": receipt["passed"], "receipt": str(Path(options.output).resolve() / "build-receipt.json"),
                      "error": receipt.get("error"), "library": receipt.get("library")}))
    raise SystemExit(0 if receipt["passed"] else 1)


if __name__ == "__main__":
    main()
