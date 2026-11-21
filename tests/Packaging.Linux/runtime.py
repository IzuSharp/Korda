#!/usr/bin/env python3
"""Execute an independently transferred directory without SDK or native build tools at runtime."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--receipt", type=Path, required=True)
    parser.add_argument("--require-tool-absence", action="store_true")
    args = parser.parse_args()
    args.directory = args.directory.resolve()
    absent = {tool: shutil.which(tool) is None for tool in ["dotnet", "cargo", "rustc", "rustup", "clang", "clang++", "clang-cl", "cmake", "ninja", "gcc", "g++", "cc", "c++"]}
    if args.require_tool_absence and not all(absent.values()):
        raise ValueError("This runtime-only acceptance environment contains an SDK or native toolchain")
    manifest = json.loads((args.directory / "manifest.json").read_text())
    rows = []
    for row in manifest["consumers"]:
        directory = args.directory / row["task"]
        executable = directory / row["assembly"]
        if digest(executable) != row["executable"]["sha256"]:
            raise ValueError("Transferred executable changed")
        for name, expected in row["native"].items():
            if digest(directory / name) != expected:
                raise ValueError("Transferred native asset changed")
        env = os.environ.copy()
        env.pop("LD_LIBRARY_PATH", None)
        run = subprocess.run([executable, "--require-aot"], cwd=args.directory, env=env, capture_output=True, timeout=120)
        if run.returncode:
            raise RuntimeError(run.stderr.decode(errors="replace")[-2000:])
        rows.append({"task": row["task"], "exit": run.returncode, "executable_sha256": digest(executable),
                     "native": row["native"], "stdout": run.stdout.decode(errors="replace"),
                     "stderr_sha256": hashlib.sha256(run.stderr).hexdigest()})
    args.receipt.parent.mkdir(parents=True, exist_ok=True)
    args.receipt.write_text(json.dumps({"passed": True, "tool_absence": absent, "rows": rows,
                                      "source_commit": manifest["source_commit"], "os_release": Path("/etc/os-release").read_text()}, indent=2) + "\n")


if __name__ == "__main__":
    main()
