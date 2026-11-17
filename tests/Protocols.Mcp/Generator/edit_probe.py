#!/usr/bin/env python3
"""Prove an ordinary private ProjectReference DTO edit recomputes the MCP proof."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import xml.etree.ElementTree as ET


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--sdk", default="dotnet")
    parser.add_argument("--native", type=Path, required=True)
    parser.add_argument("--expected-native-sha256", required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[3]
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    native = args.native.resolve()
    if digest(native) != args.expected_native_sha256:
        raise RuntimeError("Frozen native asset hash mismatch")
    consumer = output / "consumer"
    consumer.mkdir()
    for file in ["Directory.Build.props", "NuGet.Config", "global.json"]:
        shutil.copy2(root / file, consumer / file)
    for name in ["External", "Witness"]:
        source = root / "tests/Protocols.Mcp/Generator" / name
        destination = consumer / name
        destination.mkdir()
        for path in source.iterdir():
            if path.is_file():
                shutil.copy2(path, destination / path.name)
        project = destination / (name + ".csproj")
        tree = ET.parse(project)
        for reference in tree.findall(".//ProjectReference"):
            include = reference.attrib["Include"]
            if include != "../External/External.csproj":
                reference.set("Include", str((source / include).resolve()))
        tree.write(project, encoding="unicode")

    commands = []
    environment = os.environ.copy()
    blockers = output / "no-rust"
    blockers.mkdir()
    for tool in ["cargo", "rustc"]:
        path = blockers / tool
        path.write_text("#!/bin/sh\necho 'unexpected Rust invocation' >&2\nexit 97\n")
        path.chmod(0o755)
    environment["PATH"] = str(blockers) + os.pathsep + environment["PATH"]

    def run(name, argv):
        log = output / (name + ".log")
        with log.open("w") as stream:
            result = subprocess.run(argv, cwd=consumer, env=environment, stdout=stream, stderr=subprocess.STDOUT, check=False)
        commands.append({"name": name, "argv": argv, "exit_code": result.returncode, "log": log.name, "sha256": digest(log)})
        (output / "commands.json").write_text(json.dumps(commands, indent=2) + "\n")
        print(f"{name}: {result.returncode}", flush=True)
        if result.returncode:
            raise RuntimeError(f"{name} failed: {log}")
        return log.read_text()

    project = str(consumer / "Witness/Witness.csproj")
    serial = ["-m:1", "-nr:false", "-p:UseSharedCompilation=false"]
    build = ["-c", "Release", "--no-restore", *serial, "-p:NativeWeaveServerAssetPath=" + str(native)]
    generated = consumer / "Witness/obj/generated/NativeWeave.Server.Mcp.Generator/NativeWeave.Server.Mcp.Generator.LayoutPreservationGenerator/NativeWeave.Mcp.Layout.g.cs"

    def freeze(name, text):
        destination = output / name
        destination.mkdir()
        shutil.copytree(consumer / "Witness/bin/Release/net11.0", destination / "jit")
        shutil.copy2(consumer / "External/Models.cs", destination / "Models.cs")
        shutil.copy2(generated, destination / "NativeWeave.Mcp.Layout.g.cs")
        shutil.copy2(consumer / "External/obj/Release/net11.0/ref/NativeWeave.Mcp.Layout.External.dll", destination / "External.ref.dll")
        match = re.search(r"definition == typeof\(global::ExternalModels.ExternalRoot\)\) return \((\d+), (\d+)\)", generated.read_text())
        if match is None:
            raise RuntimeError("External implementation proof is absent")
        return {"declared_bound": int(match[1]), "reflection_bound": int(match[2]), "run": text.strip(),
                "generated_sha256": digest(generated), "reference_sha256": digest(destination / "External.ref.dll"),
                "implementation_sha256": digest(destination / "jit/NativeWeave.Mcp.Layout.External.dll")}

    run("restore", [args.sdk, "restore", project, "--locked-mode", "--disable-parallel", "--ignore-failed-sources", *serial])
    run("baseline-build", [args.sdk, "build", project, *build])
    executable = str(consumer / "Witness/bin/Release/net11.0/NativeWeave.Server.Mcp.Layout.Witness.dll")
    baseline = freeze("baseline", run("baseline-run", [args.sdk, executable]))
    model = consumer / "External/Models.cs"
    source = model.read_text()
    old = "public sealed class ExternalRoot : ExternalBase<ExternalPadding>\n{"
    if source.count(old) != 1:
        raise RuntimeError("Private edit fixture anchor is not unique")
    model.write_text(source.replace(old, old + "\n    private long _ordinaryPrivateEdit;"))
    # No consumer source/project/timestamp change accompanies this private-only
    # implementation edit. The normal SDK incremental dependency must notice it.
    run("edited-build", [args.sdk, "build", project, *build])
    edited = freeze("edited", run("edited-run", [args.sdk, executable]))
    if edited["declared_bound"] != baseline["declared_bound"] + 1 or edited["reflection_bound"] != baseline["reflection_bound"] + 1:
        raise RuntimeError("Private implementation edit did not recompute both field bounds")
    if edited["reference_sha256"] != baseline["reference_sha256"]:
        raise RuntimeError("The private-only fixture unexpectedly changed the reference assembly")
    if edited["implementation_sha256"] == baseline["implementation_sha256"]:
        raise RuntimeError("The private-only implementation assembly did not change")
    run("aot-restore", [args.sdk, "restore", project, "--locked-mode", "--disable-parallel", "--ignore-failed-sources", "-r", "linux-x64", "-p:PublishAot=true", *serial])
    run("aot-publish", [args.sdk, "publish", project, *build, "-r", "linux-x64", "-p:PublishAot=true", "--output", str(output / "edited-aot")])
    aot = run("aot-run", [str(output / "edited-aot/NativeWeave.Server.Mcp.Layout.Witness")])
    if "dynamic=False" not in aot or digest(native) != args.expected_native_sha256:
        raise RuntimeError("Edited AOT did not execute or native asset changed")
    artifacts = {str(path.relative_to(output)): digest(path) for path in sorted(output.rglob("*")) if path.is_file() and "consumer" not in path.relative_to(output).parts}
    result = {"status": "passed", "scope": "ordinary source ProjectReference private implementation edit; no Rust build or native protocol execution",
              "source_commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip(),
              "native_sha256": args.expected_native_sha256, "baseline": baseline, "edited": edited,
              "edited_aot": aot.strip(), "artifacts": artifacts, "commands": commands}
    (output / "result.json").write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps({"status": "passed", "result": str(output / "result.json"), "sha256": digest(output / "result.json")}), flush=True)


if __name__ == "__main__":
    main()
