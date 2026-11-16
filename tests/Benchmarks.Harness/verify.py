#!/usr/bin/env python3
"""A21 reproducible Linux component verification; no downstream domain acceptance."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
SAMPLE = ROOT / "samples/api/benchmarks-harness"
spec = importlib.util.spec_from_file_location("bench", ROOT / "src/Benchmarks.Harness/nativeweave_bench.py")
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)
SDK_PACKAGES = ["microsoft.net.illink.tasks", "microsoft.dotnet.ilcompiler",
                "runtime.linux-x64.microsoft.dotnet.ilcompiler", "microsoft.netcore.app.runtime.linux-x64",
                "microsoft.netcore.app.runtime.nativeaot.linux-x64", "microsoft.aspnetcore.app.runtime.linux-x64"]


def source_snapshot():
    files = []
    for directory in [ROOT / "src/Benchmarks.Harness", ROOT / "tests/Benchmarks.Harness", SAMPLE]:
        files.extend(path for path in directory.rglob("*") if path.is_file() and
                     not any(part in {"bin", "obj", "__pycache__"} for part in path.parts))
    files.append(ROOT / "docs/guides/benchmarks-harness.md")
    return {str(path.relative_to(ROOT)): bench.sha256(path) for path in sorted(files)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--offline-packages", type=Path)
    args = parser.parse_args()
    if platform.system() != "Linux" or platform.machine() != "x86_64":
        parser.error("This verification row is Linux x64; other platforms remain deferred")
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=False)
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_CLI_USE_MSBUILD_SERVER="0")
    commands = []
    initial_sources = source_snapshot()

    def run(command, name, input_text=None):
        entry = {"name": name, "command": [str(x) for x in command], "cwd": str(ROOT)}
        commands.append(entry)
        started = time.monotonic_ns()
        result = subprocess.run(entry["command"], cwd=ROOT, env=env, text=True, input=input_text,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        (out / f"{name}.log").write_text(result.stdout)
        entry.update(exit_code=result.returncode, elapsed_ns=time.monotonic_ns() - started,
                     log_sha256=bench.sha256(out / f"{name}.log"))
        (out / "commands.json").write_text(json.dumps(commands, indent=2) + "\n")
        print(f"{name}: exit {result.returncode}", flush=True)
        if result.returncode:
            raise RuntimeError(f"{name} failed: {result.stdout[-3000:]}")
        return result.stdout

    config_args = []
    audit = "Normal repository NuGet policy"
    if args.offline_packages:
        feed = out / "sdk-feed"
        feed.mkdir()
        for package in SDK_PACKAGES:
            for archive in (args.offline_packages / package / "11.0.0").glob("*.nupkg"):
                shutil.copy2(archive, feed / archive.name)
        if len(list(feed.glob("*.nupkg"))) != len(SDK_PACKAGES):
            raise ValueError("The offline SDK feed is incomplete")
        configuration = ET.Element("configuration")
        sources = ET.SubElement(configuration, "packageSources")
        ET.SubElement(sources, "clear")
        ET.SubElement(sources, "add", key="local-sdk", value=str(feed))
        config = out / "NuGet.offline.config"
        ET.ElementTree(configuration).write(config, encoding="utf-8", xml_declaration=True)
        config_args = ["--configfile", config, "-p:NuGetAudit=false"]
        audit = "Unavailable offline; only cached Microsoft SDK packages restored; no audit-success claim"
    versions = {"dotnet": run(["dotnet", "--version"], "dotnet-version").strip(),
                "rustc": run(["rustc", "-Vv"], "rustc-version").strip(),
                "compiler_driver": run(["gcc", "--version"], "compiler-driver-version").splitlines()[0]}
    ld_lookup = run(["gcc", "-print-prog-name=ld.bfd"], "linker-lookup").strip()
    ld_path = Path(shutil.which(ld_lookup) or ld_lookup).resolve(strict=True)
    versions["linker"] = run([ld_path, "--version"], "linker-version").splitlines()[0]
    if versions["dotnet"] != "11.0.100" or not versions["rustc"].startswith("rustc 1.100.0 "):
        raise ValueError("Use the repository's pinned toolchains")
    run([sys.executable, "-m", "unittest", "discover", "-s", "tests/Benchmarks.Harness", "-v"], "unit-tests")
    run(["rustfmt", "--edition", "2024", "--check", SAMPLE / "native/operation.rs", SAMPLE / "native/main.rs"], "rustfmt")
    native = out / "libnativeweave_benchmark_fixture.so"
    direct = out / "direct-rust"
    rust_args = ["--edition=2024", "-C", "opt-level=3", "-C", "target-cpu=x86-64-v2", "-C", "linker=gcc",
                 "-C", "link-arg=-fuse-ld=bfd", "--print=link-args", "-D", "warnings"]
    run(["rustc", SAMPLE / "native/operation.rs", "--crate-type", "cdylib", *rust_args, "-o", native], "native-fixture")
    run(["rustc", SAMPLE / "native/main.rs", *rust_args, "-o", direct], "direct-rust")
    artifact_routes = [("direct-rust", direct, {str(direct): bench.sha256(direct)})]
    compiled_assemblies = []
    for kind, route in [("Managed", "managed-aot"), ("Native", "nativeweave-aot-rust")]:
        project = SAMPLE / kind / f"{kind}.csproj"
        props = [f"-p:A21NativeFixture={native}", "-p:UseSharedCompilation=false", "-p:CppCompilerAndLinker=gcc"]
        run(["dotnet", "restore", project, "--locked-mode", "-r", "linux-x64", "-p:PublishAot=true", *config_args, *props], kind + "-restore")
        run(["dotnet", "build", project, "-c", "Release", "--no-restore", "--disable-build-servers", *props], kind + "-build")
        assembly = SAMPLE / kind / "bin/Release/net11.0" / ("NativeWeave.Benchmark." + kind + ".dll")
        compiled_assemblies.append(assembly)
        ET.parse(assembly.with_suffix(".xml"))
        text = run(["dotnet", "run", "--project", project, "-c", "Release", "--no-build", "--no-restore", *props], kind + "-jit", "0\t1\tok\n")
        events = [json.loads(line) for line in text.splitlines() if line.startswith("{")]
        if not events[0]["dynamic_code_supported"] or events[-1]["value"] != bench.mix64(1):
            raise ValueError("JIT target did not execute actual fixture")
        destination = out / (kind + "-aot")
        run(["dotnet", "publish", project, "-c", "Release", "-r", "linux-x64", "--no-restore", "--disable-build-servers",
             "-p:PublishAot=true", "-p:IlcTreatWarningsAsErrors=true", "-v:normal", "-o", destination, *props], kind + "-publish")
        executable = destination / ("NativeWeave.Benchmark." + kind)
        text = run([executable], kind + "-aot-run", "0\t1\tok\n")
        events = [json.loads(line) for line in text.splitlines()]
        if events[0]["dynamic_code_supported"] is not False or events[-1]["value"] != bench.mix64(1):
            raise ValueError("Actual Native AOT fixture execution failed")
        artifacts = {str(executable): bench.sha256(executable)}
        if kind == "Native":
            packaged = destination / native.name
            if bench.sha256(packaged) != bench.sha256(native):
                raise ValueError("Native asset changed during consumer publication")
            artifacts[str(packaged)] = bench.sha256(packaged)
        artifact_routes.append((route, executable, artifacts))
    inventory = ROOT / "tests/Benchmarks.Harness/AssemblyInventory/AssemblyInventory.csproj"
    run(["dotnet", "restore", inventory, *config_args], "inventory-restore")
    run(["dotnet", "run", "--project", inventory, "-c", "Release", "--no-restore", "--disable-build-servers", "--", *compiled_assemblies], "compiled-public-api")
    locks = {str(file): bench.sha256(file) for file in [ROOT / "global.json", ROOT / "rust-toolchain.toml",
              SAMPLE / "Managed/packages.lock.json", SAMPLE / "Native/packages.lock.json",
              SAMPLE / "Managed/packages.linux-x64.aot.lock.json", SAMPLE / "Native/packages.linux-x64.aot.lock.json"]}
    commit = run(["git", "rev-parse", "HEAD"], "source-commit").strip()
    plan = {
        "schema_version": 1, "workload_id": "a21-scalar-adapter-smoke", "workload_kind": "harness-smoke",
        "source_commit": commit, "seed": 19021, "repetitions": 2,
        "timing": {"requests": 100, "period_ns": 5_000_000, "timeout_ns": 1_000_000_000,
                   "max_dispatch_lag_ns": 100_000_000, "max_in_flight": 64, "cleanup_timeout_ns": 1_000_000_000},
        "histogram": {"resolution_ns": 1000, "maximum_ns": 10_000_000_000},
        "settings": {"cache": "warm after five fixture requests", "connection_reuse": "one subprocess pipe per repetition",
                     "durability": "not applicable; scalar fixture", "tls": "not applicable; local IPC",
                     "serialization": "TSV requests, JSON admission/completion events", "allocator": "platform/default runtime",
                     "isa_profile": "x86-64-v2", "warmup_requests": 5, "features": [],
                     "instrumentation": "observer monotonic timestamps; target scalar allocation delta; sampled Linux procfs; all included in observed latency"},
        "engineering_cost": {"application_packages": [], "ffi_calls_per_scalar": {"direct-rust": 0, "nativeweave-aot-rust": 1, "managed-aot": 0},
                             "native_rebuild_for_new_value": False, "setup_steps": "Build targets once, pin plan hashes, run external Python controller",
                             "generated_code": "C# LibraryImport and System.Text.Json source generation; runtime reflection not required"},
        "regression": {"enabled": False, "baseline_id": "harness-smoke-no-reviewed-domain-baseline", "absolute_noise_ns": 0, "relative_noise": 0},
        "routes": [{"id": route, "command": [str(executable)], "artifacts": artifacts,
                    "toolchain": versions, "lock_hashes": locks} for route, executable, artifacts in artifact_routes],
    }
    plan_path = out / "workload.json"
    plan_path.write_text(json.dumps(plan, indent=2) + "\n")
    run([sys.executable, ROOT / "src/Benchmarks.Harness/nativeweave_bench.py", "run", plan_path, out / "runs"], "three-route-run")
    run([sys.executable, ROOT / "src/Benchmarks.Harness/nativeweave_bench.py", "omission"], "omission-control")
    final_sources = source_snapshot()
    if final_sources != initial_sources:
        (out / "source-change.json").write_text(json.dumps({"before": initial_sources, "after": final_sources}, indent=2) + "\n")
        raise RuntimeError("Source/lock files changed during verification; commit the final snapshot and rerun")
    report = {"task": "A21", "platform": "linux-x64", "status": "component_checks_passed",
              "source_commit": commit, "source_sha256": final_sources,
              "versions": versions, "nuget_audit": audit, "aot_executed": ["managed-aot", "nativeweave-aot-rust"],
              "linker": {"selection": "explicit GCC driver + BFD linker, actual Rust link args and AOT publish args retained",
                         "driver_path": str(Path(shutil.which("gcc")).resolve()), "driver_sha256": bench.sha256(Path(shutil.which("gcc")).resolve()),
                         "linker_path": str(ld_path), "linker_sha256": bench.sha256(ld_path)},
              "native_executed": "direct-rust", "repetitions": 2, "requests_per_repetition": 100,
              "artifact_sizes_bytes": {str(path.relative_to(out)): path.stat().st_size for path in out.rglob("*")
                                       if path.is_file() and "sdk-feed" not in path.parts},
              "public_application_api": "none; external tooling and internal executable fixtures",
              "package": "not distributed; no benchmark dependency enters product packages",
              "domain_performance_claim": False, "other_platforms": "not executed",
              "artifacts": {str(path.relative_to(out)): bench.sha256(path) for path in out.rglob("*") if path.is_file() and "sdk-feed" not in path.parts}}
    (out / "result.json").write_text(json.dumps(report, indent=2) + "\n")
    print("A21 Linux controller and three-route component checks passed.")


if __name__ == "__main__":
    main()
