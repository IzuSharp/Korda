#!/usr/bin/env python3
"""Inspect the exact GNU x64 native payload before packaging or runtime transfer.

This is a maintainer tool, not an application loader. It never loads a library or
uses ldd on untrusted executable input. ELF metadata is inspected with readelf.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import platform
import re
import subprocess


class Rejected(ValueError):
    """A native package does not match the declared Linux profile."""


def require(condition, code, message):
    if not condition:
        raise Rejected(f"{code}: {message}")


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def command(*args):
    result = subprocess.run(list(map(str, args)), capture_output=True, text=True, timeout=30)
    require(result.returncode == 0, "NWPKG003", "ELF inspection failed: " + result.stderr[-1000:])
    return result.stdout


def header(path, executable=False):
    with Path(path).open("rb") as stream:
        value = stream.read(64)
    require(len(value) == 64 and value[:7] == b"\x7fELF\x02\x01\x01", "NWPKG003",
            "Expected a little-endian ELF64 artifact")
    require(int.from_bytes(value[18:20], "little") == 62, "NWPKG003", "Expected the x86-64 machine profile")
    kind = int.from_bytes(value[16:18], "little")
    require(kind in (2, 3) if executable else kind == 3, "NWPKG003", "Incorrect ELF artifact kind")


def inspect(path, *, allowed_dependencies, expected_exports=None, maximum_glibc="2.39", executable=False):
    """Return file-bound ELF facts; a CPU build claim additionally needs source/build evidence."""
    path = Path(path)
    header(path, executable)
    dynamic = command("readelf", "--wide", "--dynamic", path)
    versions = command("readelf", "--wide", "--version-info", path)
    symbols = command("readelf", "--wide", "--dyn-syms", path)
    notes = command("readelf", "--wide", "--notes", path)
    needed = sorted(re.findall(r"\(NEEDED\).*?\[([^\]]+)\]", dynamic))
    require(len(needed) == len(set(needed)), "NWPKG002", "Duplicate ELF dependency")
    require(set(needed) <= set(allowed_dependencies), "NWPKG003",
            "Undeclared native dependencies: " + ", ".join(sorted(set(needed) - set(allowed_dependencies))))
    require(not re.search(r"\((?:RPATH|RUNPATH)\)", dynamic), "NWPKG003",
            "This profile does not permit implicit ELF search paths")
    glibc = sorted({tuple(map(int, value.split("."))) for value in re.findall(r"\bGLIBC_(\d+(?:\.\d+)+)\b", versions)})
    limit = tuple(map(int, maximum_glibc.split(".")))
    require(not glibc or glibc[-1] <= limit, "NWPKG003",
            f"GLIBC requirement exceeds the Ubuntu 24.04 baseline ({maximum_glibc})")
    require("GLIBC_PRIVATE" not in versions, "NWPKG003", "Private glibc symbols are not a portable ABI")
    exports = sorted({parts[-1].split("@", 1)[0] for line in symbols.splitlines()
                      if len(parts := line.split()) >= 8 and parts[4] in ("GLOBAL", "WEAK")
                      and parts[5] == "DEFAULT" and parts[6] not in ("UND", "ABS")})
    if expected_exports is not None:
        require(exports == sorted(expected_exports), "NWPKG005", "Export inventory differs from the reviewed profile")
    require(not re.search(r"x86 ISA needed:.*x86-64-v[34]", notes), "NWPKG003",
            "ELF requires instructions beyond x86-64-v2")
    return {"filename": path.name, "sha256": sha(path), "bytes": path.stat().st_size,
            "machine": "x86_64", "format": "ELF64-little-endian", "needed": needed,
            "maximum_glibc": ".".join(map(str, glibc[-1])) if glibc else None,
            "exports": exports, "notes": notes.strip(), "executable": executable}


def host_profile():
    """Report the observed OS separately from whether it is the declared minimum row."""
    values = {}
    for line in Path("/etc/os-release").read_text().splitlines():
        if "=" in line:
            key, value = line.split("=", 1)
            values[key] = value.strip('"')
    return {"os_id": values.get("ID"), "os_version": values.get("VERSION_ID"),
            "architecture": platform.machine(), "glibc": platform.libc_ver(),
            "declared_baseline": values.get("ID") == "ubuntu" and values.get("VERSION_ID") == "24.04"
            and platform.machine() == "x86_64"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifact", type=Path)
    parser.add_argument("--profile", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    profile = json.loads(args.profile.read_text())
    result = inspect(args.artifact, **profile)
    result["host"] = host_profile()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
