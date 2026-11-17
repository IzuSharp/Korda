#!/usr/bin/env python3
"""Run packaged Core composition on Linux; prior component acceptance is reused.

This adds a different check: copied consumers reference the actual nupkg bytes
outside the repository. It does not replace component receipts with these runs.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/Packaging.Linux"))
from linux_profile import host_profile, inspect, sha
from packages import pack

COMPONENTS = {
    "A01": ("Interop.Abi", "NativeWeave.Interop.Abi.Tests", "nw_abi_conformance"),
    "A03": ("Interop.Lifetime", "NativeWeave.Interop.Lifetime.Tests", "nativeweave_interop_lifetime"),
    "A04": ("Interop.Memory", "NativeWeave.Interop.Memory.Tests", "nativeweave_interop_memory"),
    "A05": ("Interop.Errors", "NativeWeave.Interop.Errors.Tests", "nativeweave_errors_fixture"),
    "A06": ("Interop.Callbacks", "NativeWeave.Interop.Callbacks.Tests", "nativeweave_interop_callbacks"),
    "A07": ("Interop.Async", "NativeWeave.Interop.Async.Tests", "nativeweave_interop_async"),
}
SYSTEM_DEPENDENCIES = ["libc.so.6", "libgcc_s.so.1", "libm.so.6", "libdl.so.2", "libpthread.so.0", "librt.so.1", "ld-linux-x86-64.so.2"]


def write(path, value):
    Path(path).write_text(json.dumps(value, indent=2) + "\n")


def copy_sources(folder, output):
    """Only task-owned C# source files; no ProjectReference or source-tree imports."""
    files = []
    for path in sorted(folder.rglob("*.cs")):
        relative = path.relative_to(folder)
        if any(part in {"bin", "obj", "ApiCheck", "InvalidUco"} for part in relative.parts):
            continue
        target = output / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)
        files.append({"path": str(path.relative_to(ROOT)), "sha256": sha(path)})
    return files


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--components", nargs="+", choices=COMPONENTS, default=list(COMPONENTS))
    parser.add_argument("--require-baseline", action="store_true")
    parser.add_argument("--offline-sdk-cache", type=Path)
    args = parser.parse_args()
    output = (args.output or ROOT / "artifacts/task04/A14" / time.strftime("run-%Y%m%dT%H%M%SZ", time.gmtime())).resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = {"task": "A14", "passed": False, "platform": "linux-x64", "host": host_profile(),
              "components": args.components, "complete_core_closure": set(args.components) == set(COMPONENTS),
              "commands": [], "native": [], "consumers": [], "pristine_machine": False,
              "runtime_only_transfer": "separate runtime.py receipt required", "public_release": False}
    environment = os.environ.copy()
    environment.update(DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_CLI_USE_MSBUILD_SERVER="0", MSBUILDDISABLENODEREUSE="1")
    native_dir = output / "native"
    native_dir.mkdir()
    cargo_target = output / "cargo-target"
    environment["CARGO_TARGET_DIR"] = str(cargo_target)
    environment["RUSTFLAGS"] = "-Ctarget-cpu=x86-64-v2"
    options = ["-p:UseSharedCompilation=false", "-nr:false", "-m:1"]

    def run(label, argv, *, cwd=ROOT, env=environment, expected=0):
        result = subprocess.run(list(map(str, argv)), cwd=cwd, env=env, capture_output=True, timeout=600)
        for suffix, data in [("stdout", result.stdout), ("stderr", result.stderr)]:
            (output / f"{label}.{suffix}").write_bytes(data)
        report["commands"].append({"label": label, "argv": list(map(str, argv)), "exit": result.returncode,
                                   "stdout_sha256": hashlib.sha256(result.stdout).hexdigest(),
                                   "stderr_sha256": hashlib.sha256(result.stderr).hexdigest()})
        write(output / "result.json", report)
        print(label, result.returncode, flush=True)
        if (expected == 0 and result.returncode != 0) or (expected != 0 and result.returncode == 0):
            raise RuntimeError(result.stdout.decode(errors="replace")[-2000:] + result.stderr.decode(errors="replace")[-2000:])
        return result.stdout.decode(errors="replace") + result.stderr.decode(errors="replace")

    try:
        if args.require_baseline and not report["host"]["declared_baseline"]:
            raise ValueError("The declared minimum-distro row must execute on Ubuntu 24.04 x64")
        report["commit"] = run("commit", ["git", "rev-parse", "HEAD"]).strip()
        report["dirty"] = bool(run("dirty", ["git", "status", "--porcelain"]).strip())
        if run("sdk", ["dotnet", "--version"]).strip() != "11.0.100":
            raise ValueError("Use SDK 11.0.100")
        if not run("rustc", ["rustc", "-vV"]).startswith("rustc 1.100.0 "):
            raise ValueError("Use Rust 1.100.0")
        report["linker"] = run("linker", ["clang", "--version"])
        report["source_locks"] = {name: sha(ROOT / name) for name in ["global.json", "Cargo.lock", "Cargo.toml", "rust-toolchain.toml"]}
        for task in args.components:
            module, _, library = COMPONENTS[task]
            if not (ROOT / "tests" / module).is_dir():
                raise ValueError(f"{task} is not integrated; incomplete closure cannot be accepted")
            if task == "A01":
                abi = native_dir / "libnativeweave_interop_abi.rlib"
                run("abi-rlib", ["rustc", "--edition=2024", "-Ctarget-cpu=x86-64-v2", "-Copt-level=3", "--crate-name", "nativeweave_interop_abi", "--crate-type", "rlib", ROOT / "native/interop/abi/src/lib.rs", "-o", abi])
                run("abi-native", ["rustc", "--edition=2024", "-Ctarget-cpu=x86-64-v2", "-Copt-level=3", "--extern", f"nativeweave_interop_abi={abi}", "--crate-type", "cdylib", ROOT / "tests/Interop.Abi/native/fixture.rs", "-o", native_dir / f"lib{library}.so"])
            else:
                crate = library.replace("_", "-")
                run(task + "-native", ["cargo", "build", "--release", "--locked", "-p", crate])
                shutil.copy2(cargo_target / "release" / f"lib{library}.so", native_dir)
            report["native"].append(inspect(native_dir / f"lib{library}.so", allowed_dependencies=SYSTEM_DEPENDENCIES))
        run("core-restore", ["dotnet", "restore", ROOT / "src/NativeWeave.Core/NativeWeave.Core.csproj", "--locked-mode", *options])
        run("core-build", ["dotnet", "build", ROOT / "src/NativeWeave.Core/NativeWeave.Core.csproj", "-c", "Release", "--no-restore", *options])
        version = "0.1.0-task04.1"
        feed = output / "feed"
        entries = {"LICENSE": ROOT / "LICENSE"}
        for name in ["NativeWeave.Core", "NativeWeave.Interop.Abi"]:
            folder = ROOT / ("src/NativeWeave.Core" if name == "NativeWeave.Core" else "src/Interop.Abi") / "bin/Release/net11.0"
            for suffix in [".dll", ".xml"]:
                entries["lib/net11.0/" + name + suffix] = folder / (name + suffix)
        core = pack(feed, "NativeWeave.Core", version, entries)
        native_entries = {"runtimes/linux-x64/native/" + p.name: p for p in native_dir.glob("*.so")}
        native_entries["LICENSE"] = ROOT / "LICENSE"
        native_package = pack(feed, "NativeWeave.Conformance.Core.Native", version, native_entries)
        report["packages"] = {p.name: sha(p) for p in [core, native_package]}
        consumer_root = Path(tempfile.mkdtemp(prefix="nativeweave-a14-packaged-"))
        report["consumer_root"] = str(consumer_root)
        report["outside_source_tree"] = not consumer_root.is_relative_to(ROOT)
        shutil.copy2(ROOT / "global.json", consumer_root)
        config = ET.Element("configuration")
        sources = ET.SubElement(config, "packageSources")
        ET.SubElement(sources, "clear")
        ET.SubElement(sources, "add", key="components", value=str(feed))
        if args.offline_sdk_cache:
            sdk_feed = output / "sdk-feed"
            sdk_feed.mkdir()
            for p in args.offline_sdk_cache.glob("*/11.0.0/*.nupkg"):
                if p.name.lower().startswith(("microsoft.", "runtime.")):
                    shutil.copy2(p, sdk_feed / p.name)
            ET.SubElement(sources, "add", key="sdk", value=str(sdk_feed))
        else:
            ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
        ET.ElementTree(config).write(consumer_root / "NuGet.Config", encoding="utf-8", xml_declaration=True)
        consumer_env = environment.copy()
        consumer_env["NUGET_PACKAGES"] = str(consumer_root / "packages")
        # No source-native search path is inherited. The SDK must resolve package assets.
        consumer_env.pop("LD_LIBRARY_PATH", None)
        trap = consumer_root / "rust-invocation-trap"
        trap.mkdir()
        marker = consumer_root / "unexpected-rust"
        for name in ["cargo", "rustc", "rustup"]:
            p = trap / name
            p.write_text(f'#!/bin/sh\necho unexpected-rust >> "{marker}"\nexit 99\n')
            p.chmod(0o755)
        consumer_env["PATH"] = str(trap) + os.pathsep + environment["PATH"]
        for task in args.components:
            module, assembly, _ = COMPONENTS[task]
            project_dir = consumer_root / task
            project_dir.mkdir()
            source_files = copy_sources(ROOT / "tests" / module, project_dir)
            project = project_dir / "Consumer.csproj"
            project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework><LangVersion>15.0</LangVersion><OutputType>Exe</OutputType><AssemblyName>{assembly}</AssemblyName><AllowUnsafeBlocks>true</AllowUnsafeBlocks><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors><IsAotCompatible>true</IsAotCompatible><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile><NuGetLockFilePath Condition="'$(RuntimeIdentifier)' != ''">packages.$(RuntimeIdentifier).lock.json</NuGetLockFilePath></PropertyGroup><ItemGroup><PackageReference Include="NativeWeave.Core" Version="[{version}]"/><PackageReference Include="NativeWeave.Conformance.Core.Native" Version="[{version}]"/></ItemGroup></Project>''')
            local_options = [*options, *(["-p:NuGetAudit=false"] if args.offline_sdk_cache else [])]
            run(task + "-restore", ["dotnet", "restore", project, *local_options], cwd=consumer_root, env=consumer_env)
            run(task + "-build", ["dotnet", "build", project, "-c", "Release", "--no-restore", *local_options], cwd=consumer_root, env=consumer_env)
            jit = run(task + "-jit", ["dotnet", project_dir / "bin/Release/net11.0" / (assembly + ".dll")], cwd=consumer_root, env=consumer_env).strip()
            publish = output / "runtime-transfer" / task
            # Native linker dependencies are legitimate AOT publish prerequisites;
            # restore only that linker environment, never native search for execution.
            publish_env = consumer_env.copy()
            if environment.get("LD_LIBRARY_PATH"):
                publish_env["LD_LIBRARY_PATH"] = environment["LD_LIBRARY_PATH"]
            run(task + "-aot", ["dotnet", "publish", project, "-c", "Release", "-r", "linux-x64", "-p:PublishAot=true", "-p:IlcTreatWarningsAsErrors=true", "-o", publish, *local_options], cwd=consumer_root, env=publish_env)
            executable = publish / assembly
            aot = run(task + "-aot-run", [executable, "--require-aot"], cwd=consumer_root, env=consumer_env).strip()
            if (publish / (assembly + ".dll")).exists():
                raise ValueError("Native AOT output required")
            audit = inspect(executable, allowed_dependencies=SYSTEM_DEPENDENCIES, executable=True)
            report["consumers"].append({"task": task, "assembly": assembly, "source_files": source_files,
                                        "jit": jit, "aot": aot, "executable": audit,
                                        "locks": {p.name: sha(p) for p in project_dir.glob("packages*.json")},
                                        "native": {p.name: sha(p) for p in publish.glob("*.so")}})
        # Test the actual packaged output, not a source-tree fixture substitution.
        selected = report["consumers"][-1]
        directory = output / "runtime-transfer" / selected["task"]
        library = directory / ("lib" + COMPONENTS[selected["task"]][2] + ".so")
        hidden = library.with_suffix(".withheld")
        library.rename(hidden)
        try:
            failure = run("missing-native", [directory / selected["assembly"], "--require-aot"], cwd=consumer_root, env=consumer_env, expected=1)
            if "DllNotFoundException" not in failure:
                raise ValueError("Missing package asset did not produce the expected loader failure")
        finally:
            hidden.rename(library)
        if marker.exists():
            raise ValueError("Ordinary consumption invoked Rust")
        report["rust_invocation_trap_fired"] = False
        report["passed"] = True
        write(output / "runtime-transfer/manifest.json", {"schema_version": 1, "source_commit": report["commit"],
              "host": report["host"], "consumers": report["consumers"]})
    finally:
        write(output / "result.json", report)
        print("Evidence:", output, flush=True)


if __name__ == "__main__":
    main()
