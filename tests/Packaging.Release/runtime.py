#!/usr/bin/env python3
"""Verify the A16 transferred executable and native hash in a runtime-only image."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--receipt", type=Path, required=True)
    parser.add_argument("--require-tool-absence", action="store_true")
    args = parser.parse_args()
    args.directory = args.directory.resolve()
    absent = {name: shutil.which(name) is None for name in ["dotnet", "cargo", "rustc", "rustup", "clang", "gcc", "cc"]}
    if args.require_tool_absence and not all(absent.values()):
        raise ValueError("The runtime-only environment contains a compiler or SDK")
    original = json.loads((args.directory / "receipt.json").read_text())
    executable = args.directory / "PackagingConsumer"
    native = args.directory / original["native_relative_path"]
    if sha(executable) != original["executable_sha256"] or sha(native) != original["native_sha256"]:
        raise ValueError("Transferred runtime bytes disagree with accepted package consumer")
    env = os.environ.copy()
    env.pop("LD_LIBRARY_PATH", None)
    result = subprocess.run([executable, "--require-aot"], cwd=args.directory, env=env, capture_output=True, timeout=60)
    if result.returncode:
        raise RuntimeError(result.stderr.decode(errors="replace"))
    args.receipt.parent.mkdir(parents=True, exist_ok=True)
    args.receipt.write_text(json.dumps({"passed": True, "tool_absence": absent, "source_commit": original["source_commit"],
          "executable_sha256": sha(executable), "native_sha256": sha(native), "stdout": result.stdout.decode(),
          "stderr_sha256": hashlib.sha256(result.stderr).hexdigest(), "os_release": Path("/etc/os-release").read_text()}, indent=2) + "\n")


if __name__ == "__main__":
    main()
