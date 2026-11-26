#!/usr/bin/env python3
"""Build the direct-driver campaign adapter from locked, explicitly supplied inputs.

This command never starts a database or a load generator. Without --main it
produces an object for the campaign scheduler; --validate-only does not compile.
"""

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import platform
import re
import shlex
import shutil
import subprocess
import sys
import time


HERE = Path(__file__).resolve().parent


def sha256(path):
    result = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(block)
    return result.hexdigest()


def parse_arguments():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--product-root", type=Path, default=HERE.parents[3])
    parser.add_argument("--clang", type=Path, required=True)
    parser.add_argument("--llvm-root", type=Path)
    parser.add_argument("--sqlite-source", type=Path, required=True)
    parser.add_argument("--sqlite-archive", type=Path, required=True)
    parser.add_argument("--sqlite-build-dir", type=Path, required=True)
    parser.add_argument("--postgres-prefix", type=Path, required=True)
    parser.add_argument("--json-source", type=Path, required=True)
    parser.add_argument("--main", type=Path, help="Optional scheduler C++ translation unit to link")
    parser.add_argument("--output", type=Path, required=True, help="New output directory")
    parser.add_argument("--validate-only", action="store_true")
    return parser.parse_args()


def main(args):
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    receipt = {
        "schema_version": 1,
        "task": "DB-Perf-01",
        "route": "direct-native-cpp",
        "status": "running",
        "performance_result": False,
        "started_utc": datetime.now(timezone.utc).isoformat(),
        "language": "C++20",
        "configuration": "Release -O3 -DNDEBUG",
        "arguments": {key: str(value) if isinstance(value, Path) else value for key, value in vars(args).items()},
        "inputs": {},
        "sources": {},
        "commands": [],
        "outputs": {},
        "limitations": [
            "Reuses an existing SQLite archive with recorded build metadata; it is not independently rebuilt here.",
            "SQLite and patched libpq are statically linked. Loaded system libraries must also be identified at execution.",
            "Building an adapter or scheduler does not establish real-database correctness or performance acceptance.",
            "Native allocation/free and whole-process heap peak are not measured by the adapter.",
        ],
    }

    def save():
        (output / "build-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")

    def record(path, expected=None):
        path = Path(path).resolve()
        actual = sha256(path)
        if expected is not None and actual != expected:
            raise RuntimeError("Locked file hash differs: " + str(path))
        receipt["inputs"][str(path)] = {
            "sha256": actual,
            "size": path.stat().st_size,
            "locked_hash_verified": expected is not None,
        }
        return actual

    def run(label, command, timeout=600):
        log = output / (f"{len(receipt['commands']) + 1:02d}-" + label + ".log")
        entry = {"label": label, "argv": [str(value) for value in command], "cwd": str(output),
                 "started_utc": datetime.now(timezone.utc).isoformat(), "log": log.name}
        receipt["commands"].append(entry)
        save()
        started = time.monotonic()
        try:
            with log.open("w") as stream:
                result = subprocess.run(command, cwd=output, stdout=stream, stderr=subprocess.STDOUT,
                                        timeout=timeout, check=False)
            entry["exit_code"] = result.returncode
        except Exception as error:
            entry["execution_error"] = repr(error)
            raise
        finally:
            entry["elapsed_seconds"] = time.monotonic() - started
            if log.exists():
                entry["log_sha256"] = sha256(log)
            save()
        if result.returncode:
            raise RuntimeError("Build command failed: " + label + "; inspect " + str(log))
        return log.read_text()

    save()
    try:
        if sys.platform != "linux" or platform.machine() != "x86_64":
            raise RuntimeError("This build entry requires Linux x64")
        product = args.product_root.resolve()
        clang = args.clang.resolve()
        llvm = args.llvm_root.resolve() if args.llvm_root else clang.parent.parent
        sqlite_source = args.sqlite_source.resolve()
        sqlite_archive = args.sqlite_archive.resolve()
        postgres = args.postgres_prefix.resolve()
        json_source = args.json_source.resolve()
        lock_path = product / "native/dependencies.lock.json"
        lock = json.loads(lock_path.read_text())
        dependencies = {item["name"]: item for item in lock["dependencies"]}
        record(lock_path)
        compiler_hash = record(clang, lock["toolchain"]["llvm"]["executables"]["clang++"])
        receipt["compiler_sha256"] = compiler_hash
        receipt["compiler_version"] = lock["toolchain"]["llvm"]["version"]
        receipt["libcxx_version"] = lock["runtime"]["version"]
        receipt["dependency_lock_sha256"] = sha256(lock_path)

        for name, directory in [("sqlite", sqlite_source), ("nlohmann-json", json_source)]:
            for relative, expected in dependencies[name]["sourceFiles"].items():
                record(directory / relative, expected)

        commands_path = args.sqlite_build_dir.resolve() / "compile_commands.json"
        record(commands_path)
        compile_entries = [item for item in json.loads(commands_path.read_text())
                           if Path(item["file"]).name == "sqlite3.c"]
        if len(compile_entries) != 1:
            raise RuntimeError("Expected exactly one SQLite amalgamation compile entry")
        sqlite_compile = compile_entries[0]
        record(sqlite_compile["file"], dependencies["sqlite"]["sourceFiles"]["sqlite3.c"])
        sqlite_argv = sqlite_compile.get("arguments") or shlex.split(sqlite_compile["command"])
        record(sqlite_argv[0], compiler_hash)
        if "-O3" not in sqlite_argv or "-DNDEBUG" not in sqlite_argv:
            raise RuntimeError("Existing SQLite archive metadata is not the selected Release profile")
        required_defines = {
            "SQLITE_DEFAULT_FOREIGN_KEYS=1", "SQLITE_ENABLE_API_ARMOR=1", "SQLITE_ENABLE_COLUMN_METADATA=1",
            "SQLITE_ENABLE_DBSTAT_VTAB=1", "SQLITE_ENABLE_FTS3=1", "SQLITE_ENABLE_FTS3_PARENTHESIS=1",
            "SQLITE_ENABLE_FTS5=1", "SQLITE_ENABLE_MEMORY_MANAGEMENT=1", "SQLITE_ENABLE_RTREE=1",
            "SQLITE_ENABLE_STAT4=1", "SQLITE_ENABLE_UNLOCK_NOTIFY=1", "SQLITE_SOUNDEX=1",
            "SQLITE_THREADSAFE=1", "SQLITE_USE_URI=1",
        }
        actual_defines = {argument[2:] for argument in sqlite_argv if argument.startswith("-D")}
        normalized_defines = {value if "=" in value else value + "=1" for value in actual_defines}
        if not required_defines.issubset(normalized_defines):
            raise RuntimeError("Existing SQLite archive metadata lacks required production definitions: "
                               + repr(sorted(required_defines - normalized_defines)))
        record(sqlite_archive)
        receipt["sqlite_archive_provenance"] = {
            "archive_sha256": sha256(sqlite_archive), "existing_compile_command": sqlite_compile,
            "existing_compile_commands_sha256": sha256(commands_path), "independently_rebuilt": False,
            "recorded_definitions": sorted(actual_defines),
        }

        pg_receipt_path = postgres / "korda-postgresql-build.json"
        record(pg_receipt_path)
        built = json.loads(pg_receipt_path.read_text())
        pg_lock = dependencies["libpq"]
        if (built["version"] != pg_lock["version"] or built["compilerSha256"] != compiler_hash
                or built["archiveSha256"] != pg_lock["archiveSha256"]
                or built["patchSha256"] != pg_lock["patches"][0]["sha256"]):
            raise RuntimeError("Patched libpq source/compiler/version receipt differs from the product lock")
        record(product / pg_lock["patches"][0]["path"], pg_lock["patches"][0]["sha256"])
        pg_archives = [postgres / "lib" / name for name in
                       ["libpq.a", "libpgcommon_shlib.a", "libpgport_shlib.a"]]
        for path in pg_archives + [postgres / "include/libpq-fe.h"]:
            relative = "install/" + path.relative_to(postgres).as_posix()
            record(path, built["files"][relative])
        receipt["postgresql_provenance_sha256"] = sha256(pg_receipt_path)
        receipt["dependency_versions"] = {name: dependencies[name]["version"]
                                          for name in ["sqlite", "libpq", "nlohmann-json"]}
        runtime = llvm / "lib/x86_64-unknown-linux-gnu"
        runtime_archives = [runtime / name for name in ["libc++.a", "libc++abi.a"]]
        for path in runtime_archives:
            record(path)

        sources = [HERE / "query_adapter.hpp", HERE / "query_adapter.cpp", HERE / "build.py", HERE / "README.md"]
        if args.main:
            sources.append(args.main.resolve())
        snapshot = output / "sources"
        snapshot.mkdir()
        used_names = set()
        for source in sources:
            if source.name in used_names:
                raise RuntimeError("Duplicate source snapshot name: " + source.name)
            used_names.add(source.name)
            recorded = record(source)
            shutil.copy2(source, snapshot / source.name)
            copied = sha256(snapshot / source.name)
            if copied != recorded:
                raise RuntimeError("Source changed while taking the build snapshot: " + str(source))
            receipt["sources"][source.name] = copied
        receipt["compile_source_scope"] = "Frozen source snapshot; original paths and hashes remain recorded inputs"

        major, minor = [int(piece) for piece in pg_lock["version"].split(".")[:2]]
        pq_version_number = major * 10000 + minor if major >= 10 else major * 10000 + minor * 100
        flags = ["-std=c++20", "-O3", "-DNDEBUG", "-fno-omit-frame-pointer", "-Wall", "-Wextra", "-Werror",
                 "-stdlib=libc++", "-pthread", "-DDB_PERF_EXPECTED_PQ_VERSION=" + str(pq_version_number),
                 "-I" + str(HERE), "-I" + str(sqlite_source), "-I" + str(postgres / "include"),
                 "-I" + str(json_source / "include")]
        adapter_object = output / "query_adapter.o"
        commands = [[str(clang), *flags, "-c", str(snapshot / "query_adapter.cpp"), "-o", str(adapter_object)]]
        if args.main:
            main_object = output / "campaign_main.o"
            commands.append([str(clang), *flags, "-c", str(snapshot / args.main.name), "-o", str(main_object)])
            executable = output / "db-perf-native"
            commands.append([str(clang), "-stdlib=libc++", "-nostdlib++", str(main_object), str(adapter_object),
                             str(sqlite_archive), *map(str, pg_archives), *map(str, runtime_archives),
                             "-lssl", "-lcrypto", "-lz", "-lm", "-ldl", "-pthread", "-o", str(executable)])
        receipt["planned_commands"] = commands
        receipt["linkage"] = "static locked SQLite/libpq/libc++/libc++abi; system libc/OpenSSL/zlib"
        receipt["runtime_identity_required"] = True
        save()
        if args.validate_only:
            receipt["status"] = "inputs_validated_not_built"
        else:
            version = run("compiler-version", [str(clang), "--version"], timeout=30)
            if "clang version " + lock["toolchain"]["llvm"]["version"] not in version:
                raise RuntimeError("Compiler version output differs from the product lock")
            for index, command in enumerate(commands):
                run("compile" if index == 0 else "compile-main" if index == 1 else "link", command)
            for path in [adapter_object] + ([main_object, executable] if args.main else []):
                receipt["outputs"][path.name] = sha256(path)
            if args.main:
                linked = run("ldd", ["ldd", str(executable)], timeout=30)
                for filename in re.findall(r"(?:=>\s+|^\s*)(/[^\s()]+)", linked, flags=re.MULTILINE):
                    if Path(filename).is_file():
                        record(filename)
                run("elf-dynamic", ["readelf", "-d", str(executable)], timeout=30)
            receipt["status"] = "built_not_executed"
    except Exception as error:
        receipt["status"] = "failed"
        receipt["error"] = repr(error)
    finally:
        receipt["finished_utc"] = datetime.now(timezone.utc).isoformat()
        save()
    print(json.dumps({"status": receipt["status"], "receipt": str(output / "build-receipt.json"),
                      "outputs": receipt["outputs"]}))
    return 1 if receipt["status"] == "failed" else 0


if __name__ == "__main__":
    raise SystemExit(main(parse_arguments()))
