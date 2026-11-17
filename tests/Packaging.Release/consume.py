#!/usr/bin/env python3
"""Consume A16's exact local closure in an independent cache, optionally a fresh image.

The caller owns fresh-image provenance. --pristine-image is only valid inside the
named clean image launched by the existing Linux workflow, with no toolchain mount.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
PINNED_SDK_IMAGE = "mcr.microsoft.com/dotnet/sdk:11.0.100-resolute-amd64@sha256:7913685c883a29dd8ba79da96172f5c6b29ed9be706ebe95935e3ef45f19c9c1"


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write(path, data):
    Path(path).write_text(json.dumps(data, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepared", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--version", default="0.1.0-task04.1")
    parser.add_argument("--feed", type=Path)
    parser.add_argument("--offline-sdk-cache", type=Path)
    parser.add_argument("--pristine-image")
    parser.add_argument("--negative-cases", action="store_true")
    parser.add_argument("--expected-bootstrap-failure", action="store_true")
    args = parser.parse_args()
    prepared, out = args.prepared.resolve(), args.output.resolve()
    out.mkdir(parents=True, exist_ok=False)
    preparation = json.loads((prepared / "preparation.json").read_text())
    release_record = json.loads((prepared / ("closure-" + args.version + ".json")).read_text())
    feed = (args.feed or prepared / "feed").resolve()
    consumer = Path(tempfile.mkdtemp(prefix="nativeweave-a16-consumer-"))
    shutil.copytree(prepared / "consumer-template", consumer, dirs_exist_ok=True)
    env = os.environ.copy()
    env.update(NUGET_PACKAGES=str(consumer / "cache"), DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_CLI_USE_MSBUILD_SERVER="0", MSBUILDDISABLENODEREUSE="1")
    native_linker_environment = env.pop("LD_LIBRARY_PATH", None)
    absence = {name: shutil.which(name) is None for name in ["cargo", "rustc", "rustup"]}
    if args.pristine_image:
        if not all(absence.values()) or args.pristine_image != PINNED_SDK_IMAGE:
            raise ValueError("Clean-image receipt requires the exact pinned SDK image with no Rust executable")
        for directory in ["/root/.cargo/bin", "/usr/local/cargo/bin", "/opt/rust", "/usr/local/rustup"]:
            if Path(directory).exists():
                raise ValueError("A pristine consumer must not mount or install a Rust toolchain")
    trap = consumer / "rust-trap"
    trap.mkdir()
    marker = consumer / "unexpected-rust"
    for name in ["cargo", "rustc", "rustup"]:
        p = trap / name
        p.write_text(f'#!/bin/sh\necho unexpected-rust >> "{marker}"\nexit 99\n')
        p.chmod(0o755)
    env["PATH"] = str(trap) + os.pathsep + env["PATH"]
    options = ["-p:UseSharedCompilation=false", "-nr:false", "-m:1"]
    if args.offline_sdk_cache:
        options.append("-p:NuGetAudit=false")
    report = {"task": "A16", "passed": False, "rid": "linux-x64", "source_commit": preparation["source_commit"],
              "source_dirty": preparation["source_dirty"], "consumer_root": str(consumer), "outside_source_tree": not consumer.is_relative_to(ROOT),
              "commands": [], "cases": [], "image": args.pristine_image, "pristine_machine": bool(args.pristine_image),
              "os_release": Path("/etc/os-release").read_text(), "architecture": platform.machine(),
              "rust_absence_before_trap": absence, "public_publication": False, "unsigned_local_packages": True,
              "packages": {x["id"]: sha(feed / f"{x['id']}.{x['version']}.nupkg") for x in release_record["packages"]}}
    def config(path, package_feed=feed, offline=False):
        root = ET.Element("configuration")
        sources = ET.SubElement(root, "packageSources")
        ET.SubElement(sources, "clear")
        if not offline:
            ET.SubElement(sources, "add", key="validated-local-closure", value=str(package_feed))
            if args.offline_sdk_cache:
                sdk = consumer / "sdk-feed"
                sdk.mkdir(exist_ok=True)
                for package in args.offline_sdk_cache.glob("*/11.0.0/*.nupkg"):
                    if package.name.lower().startswith(("microsoft.", "runtime.")):
                        destination = sdk / package.name
                        if not destination.exists():
                            shutil.copy2(package, destination)
                ET.SubElement(sources, "add", key="sdk-tools", value=str(sdk))
            else:
                ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
        ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)

    def project(path, version=args.version, override=None):
        root = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
        props = ET.SubElement(root, "PropertyGroup")
        for key, value in {"TargetFramework": "net11.0", "LangVersion": "15.0", "OutputType": "Exe", "AssemblyName": "PackagingConsumer",
                           "Nullable": "enable", "ImplicitUsings": "enable", "TreatWarningsAsErrors": "true", "IsAotCompatible": "true",
                           "RestorePackagesWithLockFile": "true"}.items():
            ET.SubElement(props, key).text = value
        ET.SubElement(props, "NuGetLockFilePath", Condition="'$(RuntimeIdentifier)' != ''").text = "packages.$(RuntimeIdentifier).lock.json"
        items = ET.SubElement(root, "ItemGroup")
        ET.SubElement(items, "PackageReference", Include="NativeWeave.Conformance.Packaging", Version=f"[{version}]")
        if override:
            ET.SubElement(items, "PackageReference", Include="NativeWeave.Core", Version=f"[{override}]")
        ET.SubElement(items, "AdditionalFiles", Include="Bindings.nwmetadata")
        ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)

    def run(label, command, cwd=consumer, environment=env, expected=None):
        result = subprocess.run(list(map(str, command)), cwd=cwd, env=environment, capture_output=True, timeout=600)
        for suffix, data in [("stdout", result.stdout), ("stderr", result.stderr)]:
            (out / (label + "." + suffix)).write_bytes(data)
        report["commands"].append({"label": label, "argv": list(map(str, command)), "exit": result.returncode,
                                   "stdout_sha256": hashlib.sha256(result.stdout).hexdigest(), "stderr_sha256": hashlib.sha256(result.stderr).hexdigest()})
        write(out / "result.json", report)
        print(label, result.returncode, flush=True)
        text = (result.stdout + result.stderr).decode(errors="replace")
        if expected is None:
            if result.returncode:
                raise RuntimeError(text[-3500:])
        elif result.returncode == 0 or expected not in text:
            raise RuntimeError("Expected hard rejection " + expected + ": " + text[-3500:])
        return text

    try:
        if run("sdk", ["dotnet", "--version"]).strip() != "11.0.100":
            raise ValueError("Use the pinned SDK 11.0.100")
        cfg = consumer / "NuGet.Config"
        config(cfg)
        csproj = consumer / "Consumer.csproj"
        project(csproj)
        run("restore", ["dotnet", "restore", csproj, *options])
        run("build", ["dotnet", "build", csproj, "-c", "Release", "--no-restore", *options])
        dll = consumer / "bin/Release/net11.0/PackagingConsumer.dll"
        if args.expected_bootstrap_failure:
            run("incompatible-bootstrap", ["dotnet", dll], expected="incompatible known record/profile")
            report["cases"].append("native-ABI-mismatch-before-domain-owner")
            report["passed"] = True
            return
        report["jit"] = run("jit", ["dotnet", dll]).strip()
        run("missing-capability", ["dotnet", dll, "--missing-capability"])
        development_record = json.loads((dll.parent / "NativeWeave.conformance.packaging.assets.json").read_text())
        development_native = dll.parent / development_record["outputPath"]
        if sha(development_native) != preparation["native_sha256"]:
            raise ValueError("Loaded development native artifact differs")
        # The actual SDK transitive analyzer list must contain one of each tool.
        items = json.loads(run("compiler-items", ["dotnet", "msbuild", csproj, "-getItem:Analyzer", *options]))["Items"]["Analyzer"]
        for name in ["NativeWeave.Generators.CSharp.Metadata.dll", "NativeWeave.Analyzers.dll"]:
            if sum(Path(x["Identity"]).name == name for x in items) != 1:
                raise ValueError("Missing or duplicate transitive compiler package: " + name)
            if (dll.parent / name).exists():
                raise ValueError("Compiler tooling leaked into the runtime output")
        run("aot-restore", ["dotnet", "restore", csproj, "-r", "linux-x64", "-p:PublishAot=true", *options])
        initial_hash = sha(development_native)
        # All subsequent builds have zero sources and no restore. The typed C#
        # additions use existing native capabilities and the same native bytes.
        config(cfg, offline=True)
        code = (consumer / "Program.cs").read_text().replace("var entity =", 'if (AddedHandler.Run(new AddedEntity(7)).Number != 7) throw new Exception("Added typed handler failed.");\nvar entity =')
        (consumer / "Program.cs").write_text(code)
        (consumer / "AddedModels.cs").write_text('internal sealed record AddedEntity(int Number);\ninternal sealed record AddedDto(int Number);\ninternal static class AddedHandler { internal static AddedDto Run(AddedEntity value) => new(value.Number); }\n')
        run("offline-managed-edit-build", ["dotnet", "build", csproj, "-c", "Release", "--no-restore", *options])
        run("offline-managed-edit-run", ["dotnet", dll])
        publish_env = env.copy()
        if native_linker_environment:
            publish_env["LD_LIBRARY_PATH"] = native_linker_environment
        publish = out / "runtime-transfer"
        run("offline-aot-publish", ["dotnet", "publish", csproj, "-c", "Release", "-r", "linux-x64", "-p:PublishAot=true", "-p:IlcTreatWarningsAsErrors=true", "--no-restore", "-o", publish, *options], environment=publish_env)
        executable = publish / "PackagingConsumer"
        report["aot"] = run("aot-execute", [executable, "--require-aot"]).strip()
        if (publish / "PackagingConsumer.dll").exists():
            raise ValueError("An actual Native AOT executable is required")
        published_record = json.loads((publish / "NativeWeave.conformance.packaging.assets.json").read_text())
        native_file = publish / published_record["outputPath"]
        if sha(native_file) != initial_hash or sha(development_native) != initial_hash:
            raise ValueError("Managed edits or AOT publication changed native content")
        if published_record["outputPath"] == development_record["outputPath"]:
            raise ValueError("The SDK publish transform was not exercised")
        report.update(native_sha256=initial_hash, development_record=development_record, publish_record=published_record,
                      executable_sha256=sha(executable), offline_after_restore=True, managed_entity_dto_handler_additions=True,
                      transitive_generator_count=1, transitive_analyzer_count=1,
                      locks={p.name: sha(p) for p in consumer.glob("packages*.json")})
        if args.negative_cases:
            manifest = publish / "NativeWeave.conformance.packaging.assets.json"
            original_record, original_native = manifest.read_bytes(), native_file.read_bytes()
            try:
                manifest.unlink()
                run("missing-output-record", [executable], expected="NWPKG002")
                manifest.write_bytes(original_record)
                native_file.write_bytes(original_native[:-1] + bytes([original_native[-1] ^ 1]))
                run("tampered-native", [executable], expected="do not match the package SHA-256")
            finally:
                manifest.write_bytes(original_record)
                native_file.write_bytes(original_native)
            report["cases"].extend(["missing-output-record", "tampered-native", "missing-capability"])
            for name, suppressed in [("warnings-enabled", False), ("warnings-suppressed", True)]:
                negative = Path(tempfile.mkdtemp(prefix="nativeweave-a16-override-"))
                shutil.copytree(prepared / "consumer-template", negative, dirs_exist_ok=True)
                config(negative / "NuGet.Config")
                project(negative / "Consumer.csproj", override="0.1.0-task04.2")
                neg_env = env.copy()
                neg_env["NUGET_PACKAGES"] = str(negative / "cache")
                negative_options = [*options, "-p:TreatWarningsAsErrors=false", *(["-p:NoWarn=NU1608%3BNU1605"] if suppressed else [])]
                restored = run(name + "-restore", ["dotnet", "restore", negative / "Consumer.csproj", *negative_options], cwd=negative, environment=neg_env)
                if not suppressed and "NU1608" not in restored:
                    raise ValueError("Expected direct override restore warning was not observed")
                run(name + "-hard-error", ["dotnet", "build", negative / "Consumer.csproj", "--no-restore", *negative_options], cwd=negative, environment=neg_env, expected="NWPKG001")
                report["cases"].append(name + "-owned-version-override")
        if marker.exists():
            raise ValueError("Consumer invoked Rust")
        report["rust_invocation_trap_fired"] = False
        report["passed"] = True
        write(out / "runtime-transfer/receipt.json", {"executable_sha256": report["executable_sha256"], "native_sha256": initial_hash,
              "native_relative_path": published_record["outputPath"], "source_commit": report["source_commit"], "rid": "linux-x64"})
    finally:
        write(out / "result.json", report)
        print("Consumer evidence:", out, flush=True)


if __name__ == "__main__":
    main()
