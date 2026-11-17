#!/usr/bin/env python3
"""Freeze the focused MCP field-preservation compiler/JIT/NativeAOT proof."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import time


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--sdk", default="dotnet")
    parser.add_argument("--native", type=Path, required=True, help="Existing maintainer asset required by the referenced Server project; this witness does not load it")
    parser.add_argument("--expected-native-sha256", required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[3]
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    native = args.native.resolve()
    if sha256(native) != args.expected_native_sha256:
        raise RuntimeError("The provided existing native asset does not match its frozen hash")
    frozen_native = output / "native/libnativeweave_server.so"
    frozen_native.parent.mkdir()
    shutil.copy2(native, frozen_native)
    commands = []

    def run(name, argv):
        log = output / (name + ".log")
        started = time.monotonic()
        with log.open("w") as stream:
            result = subprocess.run(argv, cwd=root, stdout=stream, stderr=subprocess.STDOUT, check=False)
        commands.append({"name": name, "argv": argv, "exit_code": result.returncode,
                         "seconds": round(time.monotonic() - started, 3), "log": log.name, "sha256": sha256(log)})
        (output / "commands.json").write_text(json.dumps(commands, indent=2) + "\n")
        print(f"{name}: {result.returncode}", flush=True)
        if result.returncode:
            raise RuntimeError(f"{name} failed: {log}")
        return log.read_text()

    version = run("sdk", [args.sdk, "--version"]).strip()
    if version != "11.0.100":
        raise RuntimeError(f"Expected pinned .NET SDK 11.0.100, got {version}")
    driver = "tests/Protocols.Mcp/Generator/Driver/Driver.csproj"
    witness = "tests/Protocols.Mcp/Generator/Witness/Witness.csproj"
    serial = ["-m:1", "-nr:false", "-p:UseSharedCompilation=false"]
    common = ["-c", "Release", "--no-restore", *serial, "-p:NativeWeaveServerAssetPath=" + str(frozen_native)]
    run("driver-restore", [args.sdk, "restore", driver, "--locked-mode", "--disable-parallel", "--ignore-failed-sources", *serial])
    run("driver-build", [args.sdk, "build", driver, *common, "--output", str(output / "driver")])
    compiler = run("driver-run", [args.sdk, str(output / "driver/NativeWeave.Server.Mcp.Generator.Tests.dll")])
    run("jit-restore", [args.sdk, "restore", witness, "--locked-mode", "--disable-parallel", "--ignore-failed-sources", *serial])
    run("jit-build", [args.sdk, "build", witness, *common, "--output", str(output / "jit")])
    jit = run("jit-run", [args.sdk, str(output / "jit/NativeWeave.Server.Mcp.Layout.Witness.dll")])
    run("aot-restore", [args.sdk, "restore", witness, "--locked-mode", "--disable-parallel", "--ignore-failed-sources", "-r", "linux-x64", "-p:PublishAot=true", *serial])
    run("aot-publish", [args.sdk, "publish", witness, *common, "-r", "linux-x64", "-p:PublishAot=true", "--output", str(output / "aot")])
    aot = run("aot-run", [str(output / "aot/NativeWeave.Server.Mcp.Layout.Witness")])
    if "assertions passed" not in compiler or "dynamic=True" not in jit or "dynamic=False" not in aot:
        raise RuntimeError("Expected actual compiler, JIT and NativeAOT witness results were absent")
    if sha256(native) != args.expected_native_sha256 or sha256(frozen_native) != args.expected_native_sha256:
        raise RuntimeError("Native asset changed during the compiler-only proof")

    generated = root / "tests/Protocols.Mcp/Generator/Witness/obj/generated/NativeWeave.Server.Mcp.Generator/NativeWeave.Server.Mcp.Generator.LayoutPreservationGenerator/NativeWeave.Mcp.Layout.g.cs"
    if not generated.is_file():
        raise RuntimeError("Generated preservation source is absent")
    (output / "NativeWeave.Mcp.Layout.g.cs").write_bytes(generated.read_bytes())
    sources = {}
    for directory in [root / "src/Protocols.Mcp/Generator", root / "tests/Protocols.Mcp/Generator"]:
        for path in sorted(directory.rglob("*")):
            relative = path.relative_to(directory)
            if path.is_file() and not {"bin", "obj", "__pycache__"}.intersection(relative.parts):
                sources[str(path.relative_to(root))] = sha256(path)
    artifacts = {}
    for path in sorted(output.rglob("*")):
        if path.is_file():
            artifacts[str(path.relative_to(output))] = sha256(path)
    result = {"status": "passed", "platform": "linux-x64", "sdk": version,
              "source_commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip(),
              "scope": "compiler-generated field preservation and static JSON witness; product MCP admission is verified separately",
              "native": {"provided_path": str(native), "sha256": args.expected_native_sha256,
                         "role": "unchanged Server project build input; no Rust build or native protocol execution"},
              "compiler": compiler.strip(), "jit": jit.strip(), "aot": aot.strip(),
              "sources": sources, "artifacts": artifacts, "commands": commands}
    (output / "result.json").write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps({"status": result["status"], "result": str(output / "result.json"), "sha256": sha256(output / "result.json")}), flush=True)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
