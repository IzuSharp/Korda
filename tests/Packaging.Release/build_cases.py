#!/usr/bin/env python3
"""Exercise the shipped task in actual MSBuild with real restored package inputs."""
import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepared", type=Path, required=True)
    parser.add_argument("--consumer-receipt", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    receipt = json.loads(args.consumer_receipt.read_text())
    consumer = Path(receipt["consumer_root"])
    closure = json.loads((args.prepared / "build-closure-0.1.0-task04.1.json").read_text())
    native = consumer / "cache/nativeweave.conformance.packaging.native/0.1.0-task04.1/runtimes/linux-x64/native/libnw_packaging.so"
    assets = consumer / "obj/project.assets.json"
    task = ROOT / "src/Packaging.Release/BuildTasks/bin/Release/net11.0/NativeWeave.Packaging.Tasks.dll"
    cases = []
    def run(name, changed=None, expected=None, *, raw=None, rid="linux-x64", mode="validate", item_count=1, destination="libnw_packaging.so", tamper=False, changed_assets=None):
        directory = output / name
        directory.mkdir()
        record = directory / "closure.json"
        record.write_text(raw if raw is not None else json.dumps(changed or closure))
        resolved_assets = assets
        if changed_assets is not None:
            resolved_assets = directory / "project.assets.json"
            resolved_assets.write_text(json.dumps(changed_assets))
        root = ET.Element("Project")
        ET.SubElement(root, "UsingTask", TaskName="NativeWeave.Packaging.PackageClosure", AssemblyFile=str(task))
        group = ET.SubElement(root, "ItemGroup")
        selected = native
        if tamper:
            selected = directory / "runtimes/linux-x64/native/libnw_packaging.so"
            selected.parent.mkdir(parents=True)
            selected.write_bytes(native.read_bytes() + b"tampered")
        for _ in range(item_count):
            item = ET.SubElement(group, "SelectedNative", Include=str(selected))
            ET.SubElement(item, "NuGetPackageId").text = "NativeWeave.Conformance.Packaging.Native"
            ET.SubElement(item, "PathInPackage").text = "runtimes/linux-x64/native/libnw_packaging.so"
            ET.SubElement(item, "DestinationSubPath").text = destination
        target = ET.SubElement(root, "Target", Name="Check")
        ET.SubElement(target, "PackageClosure", ClosureFiles=str(record), AssetsFile=str(resolved_assets), RuntimeIdentifier=rid,
                      Mode=mode, OutputDirectory=str(directory / "output"), ResolvedFiles="@(SelectedNative)")
        project = directory / "Check.proj"
        ET.ElementTree(root).write(project, encoding="utf-8", xml_declaration=True)
        result = subprocess.run(["dotnet", "msbuild", project, "-t:Check", "-nologo", "-nr:false", "-v:minimal"], capture_output=True, timeout=60)
        (directory / "stdout").write_bytes(result.stdout)
        (directory / "stderr").write_bytes(result.stderr)
        text = (result.stdout + result.stderr).decode(errors="replace")
        passed = (result.returncode == 0) if expected is None else (result.returncode != 0 and expected in text)
        cases.append({"name": name, "passed": passed, "exit": result.returncode, "expected": expected,
                      "stdout_sha256": hashlib.sha256(result.stdout).hexdigest(), "stderr_sha256": hashlib.sha256(result.stderr).hexdigest()})
        (output / "result.json").write_text(json.dumps({"passed": all(x["passed"] for x in cases), "cases": cases,
            "source_commit": receipt["source_commit"], "task_sha256": hashlib.sha256(task.read_bytes()).hexdigest(),
            "closure_sha256": hashlib.sha256((args.prepared / "build-closure-0.1.0-task04.1.json").read_bytes()).hexdigest()}, indent=2) + "\n")
        print(name, result.returncode, flush=True)
        if not passed:
            raise RuntimeError(text)
    run("valid")
    changed = copy.deepcopy(closure)
    changed["schemaVersion"] = 2
    run("unknown-schema", changed, "NWPKG001")
    changed = copy.deepcopy(closure)
    changed["packages"].append(changed["packages"][0])
    run("duplicate-package", changed, "NWPKG001")
    run("duplicate-json", expected="NWPKG002", raw=json.dumps(closure).replace('"schemaVersion": 1', '"schemaVersion": 1, "schemaVersion": 1'))
    run("unsupported-rid", expected="NWPKG003", rid="linux-arm64")
    changed = copy.deepcopy(closure)
    next(x for x in changed["packages"] if "nativeAssets" in x)["nativeAssets"][0]["cpuBaseline"] = "x86-64-v4"
    run("unsupported-cpu-baseline", changed, "NWPKG003")
    relaxed_assets = json.loads(assets.read_text())
    for target in relaxed_assets["targets"].values():
        target["NativeWeave.Conformance.Packaging/0.1.0-task04.1"]["dependencies"]["NativeWeave.Core"] = "0.1.0-task04.1"
    run("nonexact-restored-edge", expected="NWPKG001", changed_assets=relaxed_assets)
    changed = copy.deepcopy(closure)
    leaf = next(x for x in changed["packages"] if "nativeAssets" in x)
    leaf["nativeAssets"][0]["sourcePath"] = "runtimes/linux-x64/native/../escape.so"
    run("source-path-traversal", changed, "NWPKG002")
    run("valid-record", mode="build")
    run("missing-sdk-native", expected="NWPKG002", mode="build", item_count=0)
    run("duplicate-sdk-native", expected="NWPKG002", mode="build", item_count=2)
    run("native-hash-drift", expected="NWPKG004", mode="build", tamper=True)
    run("native-hash-before-compile", expected="NWPKG004", tamper=True)
    run("output-path-traversal", expected="NWPKG002", mode="build", destination="../escape.so")
    changed = copy.deepcopy(closure)
    leaf = next(x for x in changed["packages"] if "nativeAssets" in x)
    other = copy.deepcopy(leaf["nativeAssets"][0])
    other["engineId"] = "conformance.second"
    leaf["nativeAssets"].append(other)
    run("duplicate-basename", changed, "NWPKG002")


if __name__ == "__main__":
    main()
