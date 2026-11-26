#!/usr/bin/env python3
"""Build a private SQLite shared image from the product's actual C compile recipe.

All product feature macros are retained. The only source-level addition exports
the public SQLite C ABI from the otherwise hidden-visibility PIC build. No data
is created and no SQL query is executed by this tool.
"""

import argparse
import ctypes
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import shlex
import subprocess
import sys


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--product-root", type=Path, required=True)
    parser.add_argument("--clang", type=Path, required=True)
    parser.add_argument("--sqlite-source", type=Path, required=True, help="Directory containing locked sqlite3.c/header files")
    parser.add_argument("--compile-commands", type=Path, required=True)
    parser.add_argument("--reference-fingerprint", type=Path, required=True,
                        help="Actual static-native observation with sqlite_runtime_source_id and compile_options")
    parser.add_argument("--output", type=Path, required=True, help="New private output directory")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    receipt = {"schema_version": 1, "status": "running", "performance_result": False,
               "started_utc": datetime.now(timezone.utc).isoformat(), "commands": [], "inputs": {},
               "scope": "shared-image build and actual runtime fingerprint only; no SQL or performance measurement"}

    def save():
        (output / "build-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")

    def record(path, expected=None):
        path = Path(path).resolve()
        digest = sha(path)
        if expected is not None and digest != expected:
            raise ValueError("Locked identity mismatch: " + str(path))
        receipt["inputs"][str(path)] = {"sha256": digest, "bytes": path.stat().st_size,
                                      "locked_hash_verified": expected is not None}
        return digest

    def run(label, command):
        log = output / (label + ".log")
        entry = {"label": label, "argv": list(map(str, command)), "cwd": str(output),
                 "started_utc": datetime.now(timezone.utc).isoformat(), "log": log.name}
        receipt["commands"].append(entry)
        save()
        with log.open("wb") as stream:
            result = subprocess.run(command, cwd=output, stdout=stream, stderr=subprocess.STDOUT)
        entry.update(exit_code=result.returncode, finished_utc=datetime.now(timezone.utc).isoformat(), log_sha256=sha(log))
        save()
        if result.returncode:
            raise RuntimeError("Build command failed: " + label)
        return log.read_text(errors="replace")

    save()
    try:
        lock_path = args.product_root.resolve() / "native/dependencies.lock.json"
        lock = json.loads(lock_path.read_text())
        sqlite = next(value for value in lock["dependencies"] if value["name"] == "sqlite")
        record(lock_path)
        clang = args.clang.resolve()
        record(clang, lock["toolchain"]["llvm"]["executables"]["clang"])
        source = args.sqlite_source.resolve()
        for name, digest in sqlite["sourceFiles"].items():
            record(source / name, digest)
        record(args.compile_commands)
        entries = [entry for entry in json.loads(args.compile_commands.read_text())
                   if Path(entry["file"]).name == "sqlite3.c"]
        if len(entries) != 1:
            raise ValueError("Expected one actual SQLite compilation entry")
        original = entries[0]
        original_argv = original.get("arguments") or shlex.split(original["command"])
        record(original_argv[0], lock["toolchain"]["llvm"]["executables"]["clang"])
        record(original["file"], sqlite["sourceFiles"]["sqlite3.c"])
        required = {"-O3", "-DNDEBUG", "-std=c17", "-fPIC", "-fvisibility=hidden"}
        if not required.issubset(original_argv):
            raise ValueError("Actual product recipe is not the frozen Release/PIC C17 profile")
        defines = sorted(argument[2:] for argument in original_argv if argument.startswith("-D"))
        essential = {"SQLITE_DEFAULT_FOREIGN_KEYS=1", "SQLITE_ENABLE_API_ARMOR=1",
                     "SQLITE_ENABLE_COLUMN_METADATA=1", "SQLITE_ENABLE_DBSTAT_VTAB=1", "SQLITE_ENABLE_FTS3=1",
                     "SQLITE_ENABLE_FTS3_PARENTHESIS=1", "SQLITE_ENABLE_FTS5=1", "SQLITE_ENABLE_MEMORY_MANAGEMENT=1",
                     "SQLITE_ENABLE_RTREE=1", "SQLITE_ENABLE_STAT4=1", "SQLITE_ENABLE_UNLOCK_NOTIFY=1",
                     "SQLITE_SOUNDEX=1", "SQLITE_THREADSAFE=1", "SQLITE_USE_URI=1"}
        normalized = {value if "=" in value else value + "=1" for value in defines}
        if not essential.issubset(normalized):
            raise ValueError("Missing product SQLite feature definitions: " + repr(sorted(essential - normalized)))
        flags = []
        index = 1
        while index < len(original_argv):
            value = original_argv[index]
            if value == "-o":
                index += 2
                continue
            if value == "-c":
                index += 1
                continue
            if Path(value).resolve() == Path(original["file"]).resolve():
                index += 1
                continue
            if value == "-isystem":
                if Path(original_argv[index + 1]).resolve() != Path(original["file"]).resolve().parent:
                    raise ValueError("Unexpected product include directory")
                flags += ["-isystem", str(source)]
                index += 2
                continue
            if not (value.startswith("-D") or value in required):
                raise ValueError("Unreviewed product SQLite compiler option: " + value)
            flags.append(value)
            index += 1
        export = '-DSQLITE_API=__attribute__((visibility("default")))'
        image = output / "libsqlite3.so"
        receipt["actual_product_compile_entry"] = original
        receipt["retained_product_definitions"] = defines
        receipt["explicit_differences"] = [
            "Link a shared image instead of a static archive for SQLitePCLRaw.",
            "Add SQLITE_API default visibility while retaining -fvisibility=hidden for other symbols.",
            "Public dynamic-library call relocation differs from the product's static linkage; feature-option equality does not make binaries identical."]
        receipt["additional_export_macro"] = export
        version = run("clang-version", [str(clang), "--version"])
        if "clang version " + lock["toolchain"]["llvm"]["version"] not in version:
            raise ValueError("Compiler version differs from lock")
        run("compile-shared", [str(clang), *flags, export, "-shared", str(source / "sqlite3.c"),
                               "-Wl,-soname,libsqlite3.so", "-Wl,-z,defs", "-o", str(image), "-lm", "-ldl", "-lpthread"])
        module = ctypes.CDLL(str(image))
        module.sqlite3_libversion.restype = ctypes.c_char_p
        module.sqlite3_sourceid.restype = ctypes.c_char_p
        module.sqlite3_compileoption_get.argtypes = [ctypes.c_int]
        module.sqlite3_compileoption_get.restype = ctypes.c_char_p
        options = []
        for option_index in range(1000):
            item = module.sqlite3_compileoption_get(option_index)
            if item is None:
                break
            options.append(item.decode())
        else:
            raise ValueError("Unexpected unbounded compile option list")
        actual = {"sqlite_version": module.sqlite3_libversion().decode(),
                  "sqlite_runtime_source_id": module.sqlite3_sourceid().decode(), "compile_options": sorted(options)}
        record(args.reference_fingerprint)
        reference = json.loads(args.reference_fingerprint.read_text())
        actual["source_id_matches_reference"] = actual["sqlite_runtime_source_id"] == reference["sqlite_runtime_source_id"]
        actual["compile_options_match_reference"] = actual["compile_options"] == sorted(reference["compile_options"])
        actual["missing_options"] = sorted(set(reference["compile_options"]) - set(options))
        actual["extra_options"] = sorted(set(options) - set(reference["compile_options"]))
        receipt["runtime_fingerprint"] = actual
        receipt["image"] = {"path": str(image), "bytes": image.stat().st_size, "sha256": sha(image)}
        if actual["sqlite_version"] != sqlite["version"] or not actual["source_id_matches_reference"] or not actual["compile_options_match_reference"]:
            raise ValueError("Rebuilt shared SQLite runtime fingerprint differs from the static native reference")
        run("elf-dynamic", ["readelf", "-d", str(image)])
        receipt["status"] = "shared-image-built-and-fingerprint-matched"
    except BaseException as error:
        receipt.update(status="failed", error=repr(error))
    finally:
        receipt["finished_utc"] = datetime.now(timezone.utc).isoformat()
        save()
    print(json.dumps({"status": receipt["status"], "receipt": str(output / "build-receipt.json"),
                      "image": receipt.get("image")}))
    return 1 if receipt["status"] == "failed" else 0


if __name__ == "__main__":
    sys.exit(main())
