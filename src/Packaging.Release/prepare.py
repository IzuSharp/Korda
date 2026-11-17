#!/usr/bin/env python3
"""Stage actual Core/native/compiler packages for scoped A16 Linux acceptance.

The conformance facade exercises packaging without pretending unimplemented
Database/Server product APIs or publishing an additional public package.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/Packaging.Linux"))
from linux_profile import sha, inspect, host_profile
from packages import pack


def compatibility():
    spec = importlib.util.spec_from_file_location("nativeweave_compatibility", ROOT / "src/Verification.Compatibility/compatibility.py")
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


def write(path, data):
    Path(path).write_text(json.dumps(data, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--versions", nargs="+", default=["0.1.0-task04.1", "0.1.0-task04.2"])
    parser.add_argument("--wrong-abi", action="store_true")
    args = parser.parse_args()
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=False)
    logs = out / "logs"
    logs.mkdir()
    commands = []
    env = os.environ.copy()
    env.update(DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_CLI_USE_MSBUILD_SERVER="0", MSBUILDDISABLENODEREUSE="1")
    options = ["-p:UseSharedCompilation=false", "-nr:false", "-m:1"]
    def run(label, command, cwd=ROOT):
        result = subprocess.run(list(map(str, command)), cwd=cwd, env=env, capture_output=True, timeout=300)
        for suffix, data in [("stdout", result.stdout), ("stderr", result.stderr)]:
            (logs / (label + "." + suffix)).write_bytes(data)
        commands.append({"label": label, "argv": list(map(str, command)), "exit": result.returncode,
                         "stdout_sha256": hashlib.sha256(result.stdout).hexdigest(), "stderr_sha256": hashlib.sha256(result.stderr).hexdigest()})
        write(out / "commands.json", commands)
        print(label, result.returncode, flush=True)
        if result.returncode:
            raise RuntimeError(result.stdout.decode(errors="replace")[-2000:] + result.stderr.decode(errors="replace")[-2000:])
        return result.stdout.decode()
    commit = run("commit", ["git", "rev-parse", "HEAD"]).strip()
    dirty = bool(run("dirty", ["git", "status", "--porcelain"]).strip())
    if run("sdk", ["dotnet", "--version"]).strip() != "11.0.100":
        raise ValueError("SDK 11.0.100 is required")
    rustc = run("rustc", ["rustc", "-vV"])
    if not rustc.startswith("rustc 1.100.0 "):
        raise ValueError("Rust 1.100.0 is required")
    projects = {
        "core": "src/NativeWeave.Core/NativeWeave.Core.csproj",
        "tasks": "src/Packaging.Release/BuildTasks/NativeWeave.Packaging.Tasks.csproj",
        "metadata": "src/Generators.CSharp/Metadata/NativeWeave.Generators.CSharp.Metadata.csproj",
        "analyzers": "src/Analyzers/NativeWeave.Analyzers.csproj",
    }
    for name, project in projects.items():
        run(name + "-restore", ["dotnet", "restore", project, "--locked-mode", *options])
        run(name + "-build", ["dotnet", "build", project, "-c", "Release", "--no-restore", *options])
    rlib = out / "libnativeweave_interop_abi.rlib"
    native = out / "libnw_packaging.so"
    rustargs = ["--edition=2024", "-Dwarnings", "-Ctarget-cpu=x86-64-v2", "-Copt-level=3"]
    run("native-abi", ["rustc", *rustargs, "--crate-name", "nativeweave_interop_abi", "--crate-type", "rlib", "native/interop/abi/src/lib.rs", "-o", rlib])
    run("native", ["rustc", *rustargs, "--crate-type", "cdylib", "--extern", f"nativeweave_interop_abi={rlib}", "tests/Packaging.Release/native.rs", "-o", native, *(["--cfg", "wrong_abi"] if args.wrong_abi else [])])
    native_audit = inspect(native, allowed_dependencies=["libc.so.6", "libgcc_s.so.1", "ld-linux-x86-64.so.2"], expected_exports=["nw_packaging_get_abi_v1"])
    source_names = run("source-files", ["git", "ls-files", "--cached", "--others", "--exclude-standard", "--", "src", "tests/Packaging.Release", "samples/api/packaging-release", "native/interop/abi", "Cargo.lock", "global.json", "rust-toolchain.toml"]).splitlines()
    inventory = {"commit": commit, "dirty": dirty, "files": {name: sha(ROOT / name) for name in source_names if (ROOT / name).is_file()}}
    write(out / "source-inventory.json", inventory)
    metadata_path = ROOT / "samples/api/packaging-release/Consumer.nwmetadata"
    metadata = dict(line.split("\t", 1) for line in metadata_path.read_text().splitlines() if line.startswith(("module\t", "digest\t")))
    generated_type = "BindingMetadata_M_" + hashlib.sha256(metadata["module"].encode()).hexdigest()
    feed = out / "feed"
    feed.mkdir()
    compat = compatibility()
    records = []
    for version in args.versions:
        common = {"LICENSE": ROOT / "LICENSE"}
        core_entries = dict(common)
        for folder, assembly in [("src/NativeWeave.Core", "NativeWeave.Core"), ("src/Interop.Abi", "NativeWeave.Interop.Abi")]:
            for suffix in [".dll", ".xml"]:
                core_entries["lib/net11.0/" + assembly + suffix] = ROOT / folder / "bin/Release/net11.0" / (assembly + suffix)
        repository = ("https://github.com/IzuSharp/NativeWeave", commit)
        core_package = pack(feed, "NativeWeave.Core", version, core_entries, repository=repository)
        tool_packages = []
        for identity, folder, host, assembly in [
            ("NativeWeave.Generators.CSharp.Metadata", "src/Generators.CSharp/Metadata", "roslyn", "NativeWeave.Generators.CSharp.Metadata"),
            ("NativeWeave.Analyzers", "src/Analyzers", "netstandard2.0", "NativeWeave.Analyzers")]:
            entries = {**common, "tools/" + host + "/" + assembly + ".dll": ROOT / folder / "bin/Release/netstandard2.0" / (assembly + ".dll"),
                       "buildTransitive/" + identity + ".targets": ROOT / folder / "buildTransitive" / (identity + ".targets")}
            tool_packages.append(pack(feed, identity, version, entries, repository=repository))
        facade_id = "NativeWeave.Conformance.Packaging"
        native_id = facade_id + ".Native"
        native_path = "runtimes/linux-x64/native/libnw_packaging.so"
        descriptor = {"engineId": "conformance.packaging", "facadePackageId": facade_id, "facadePackageVersion": version,
                      "rid": "linux-x64", "sourcePath": native_path, "sha256": sha(native), "abiMajor": 1,
                      "abiMinor": 0, "capabilities": {"buffer.owned": 0}, "cpuBaseline": "x86-64-v2"}
        deps = {"NativeWeave.Core": version, native_id: version, "NativeWeave.Generators.CSharp.Metadata": version, "NativeWeave.Analyzers": version}
        closure = {"schemaVersion": 1, "packages": [
            {"id": "NativeWeave.Core", "version": version, "dependencies": {}},
            {"id": "NativeWeave.Generators.CSharp.Metadata", "version": version, "dependencies": {}},
            {"id": "NativeWeave.Analyzers", "version": version, "dependencies": {}},
            {"id": native_id, "version": version, "dependencies": {}, "nativeAssets": [descriptor]},
            {"id": facade_id, "version": version, "dependencies": deps},
        ]}
        native_manifest = {"schemaVersion": 1, "packageId": native_id, "packageVersion": version, "rid": "linux-x64",
                           "nativePath": native_path, "nativeSha256": sha(native), "abiMajor": 1, "sourceCommit": commit,
                           "sourceDirty": dirty, "features": [], "rustc": rustc, "cpuBaseline": "x86-64-v2",
                           "cargoLockSha256": sha(ROOT / "Cargo.lock"), "license": "MIT", "upstream": "NativeWeave product ABI writer"}
        native_package = pack(feed, native_id, version, {**common, native_path: native,
                              "nativeweave/manifest.json": json.dumps(native_manifest, sort_keys=True).encode(),
                              "nativeweave/source-inventory.json": json.dumps(inventory, sort_keys=True).encode(),
                              "NOTICE": b"Local conformance adapter over NativeWeave's MIT-licensed product ABI. No third-party engine is distributed by this package.\n"},
                              repository=repository)
        facade = out / ("facade-" + version)
        facade.mkdir()
        source = (ROOT / "samples/api/packaging-release/Facade.cs.in").read_text().replace("@VERSION@", version).replace("@NATIVE_SHA@", sha(native))
        (facade / "Facade.cs").write_text(source)
        root = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
        props = ET.SubElement(root, "PropertyGroup")
        for name, value in {"TargetFramework": "net11.0", "LangVersion": "15.0", "Nullable": "enable", "ImplicitUsings": "enable",
                            "AssemblyName": facade_id, "GenerateDocumentationFile": "true", "TreatWarningsAsErrors": "true"}.items():
            ET.SubElement(props, name).text = value
        group = ET.SubElement(root, "ItemGroup")
        for assembly in ["NativeWeave.Core", "NativeWeave.Interop.Abi"]:
            ET.SubElement(group, "Reference", Include=assembly, HintPath=str(ROOT / "src/NativeWeave.Core/bin/Release/net11.0" / (assembly + ".dll")), Private="false")
        ET.ElementTree(root).write(facade / "Facade.csproj", encoding="utf-8", xml_declaration=True)
        shutil.copy2(ROOT / "global.json", facade)
        run(version + "-facade", ["dotnet", "build", facade / "Facade.csproj", "-c", "Release", *options], cwd=facade)
        task_dir = ROOT / "src/Packaging.Release/BuildTasks/bin/Release/net11.0"
        entries = {**common,
                   "nativeweave/closure.json": json.dumps(closure, sort_keys=True).encode(),
                   "tools/net11.0/NativeWeave.Packaging.Tasks.dll": task_dir / "NativeWeave.Packaging.Tasks.dll",
                   "tools/net11.0/NativeWeave.Packaging.Tasks.xml": task_dir / "NativeWeave.Packaging.Tasks.xml",
                   "buildTransitive/" + facade_id + ".targets": ROOT / "src/Packaging.Release/buildTransitive/NativeWeave.Packaging.targets"}
        for suffix in [".dll", ".xml"]:
            entries["lib/net11.0/" + facade_id + suffix] = facade / "bin/Release/net11.0" / (facade_id + suffix)
        facade_package = pack(feed, facade_id, version, entries, deps, repository=repository)
        packages = [core_package, *tool_packages, native_package, facade_package]
        graph = {x["id"]: x for x in closure["packages"]}
        record = {"schema_version": 1, "source_commit": commit, "source_dirty": dirty, "rid": "linux-x64",
                  "builder_identity": "nativeweave-local-reviewed-staging", "packages": [], "local_unsigned_only": True}
        for package in packages:
            identity = next(i for i in graph if package.name == i + "." + version + ".nupkg")
            entry = {"id": identity, "version": version, "dependencies": graph[identity]["dependencies"],
                     "unsigned_entries": compat.package_entries(package), "upload_archive_sha256": sha(package)}
            if identity == native_id:
                entry.update(native={"path": native_path, "sha256": sha(native), "rid": "linux-x64", "abi_major": 1},
                             source_commit=commit, repository="https://github.com/IzuSharp/NativeWeave")
            record["packages"].append(entry)
        location = out / ("closure-" + version + ".json")
        write(location, record)
        write(out / ("build-closure-" + version + ".json"), closure)
        records.append({"version": version, "record": location.name, "sha256": sha(location), "packages": {p.name: sha(p) for p in packages}})
    consumer = out / "consumer-template"
    consumer.mkdir()
    shutil.copy2(ROOT / "global.json", consumer)
    shutil.copy2(metadata_path, consumer / "Bindings.nwmetadata")
    (consumer / "Program.cs").write_text((ROOT / "samples/api/packaging-release/Program.cs.in").read_text().replace("@METADATA_TYPE@", generated_type))
    write(out / "preparation.json", {"passed": True, "source_commit": commit, "source_dirty": dirty, "host": host_profile(),
          "native": native_audit, "native_sha256": sha(native), "records": records, "wrong_abi_negative": args.wrong_abi,
          "scope": "Actual implemented Core/native/compiler package composition; local unsigned sample, no domain or public release claim"})
    print("Prepared:", out, flush=True)


if __name__ == "__main__":
    main()
