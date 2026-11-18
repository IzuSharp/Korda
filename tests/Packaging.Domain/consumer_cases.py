#!/usr/bin/env python3
"""Actual B24 package consumers, called by the existing release callback.

Every graph restores into its own cache outside the repository. The unchanged
accepted strict package validators check the new artifacts. This script is not
an alternative product runtime and never invokes Cargo or publishes a package.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(ROOT / "src/Packaging.Domain"))
from model import ENGINES, LEAVES, RID, load_manifest, module, relative_file, save, sha

SDK_IMAGE = "mcr.microsoft.com/dotnet/sdk:11.0.100-resolute-amd64@sha256:7913685c883a29dd8ba79da96172f5c6b29ed9be706ebe95935e3ef45f19c9c1"
FLAGS = ["-p:UseSharedCompilation=false", "-nr:false", "-m:1"]
# Exact existing A16 Linux executable allowlist, with the same glibc ceiling.
SYSTEM_DEPENDENCIES = ["libc.so.6", "libgcc_s.so.1", "libm.so.6", "libdl.so.2", "libpthread.so.0", "librt.so.1", "ld-linux-x86-64.so.2"]
GRAPHS = {
    "sqlite": (["NativeWeave.Database.Sqlite"], ["SQLITE"], ["sqlite"]),
    "postgresql": (["NativeWeave.Database.PostgreSql"], ["POSTGRES"], ["postgresql"]),
    "server": (["NativeWeave.Server"], ["SERVER"], ["none"]),
    "grpc": (["NativeWeave.Server.Grpc"], ["SERVER", "GRPC"], ["none"]),
    "mcp": (["NativeWeave.Server.Mcp"], ["SERVER", "MCP"], ["none"]),
    "combined": (["NativeWeave.Database.Sqlite", "NativeWeave.Database.PostgreSql",
                  "NativeWeave.Server.Grpc", "NativeWeave.Server.Mcp"],
                 ["SQLITE", "POSTGRES", "SERVER", "GRPC", "MCP"], ["sqlite", "postgresql"]),
}
compat = module("b24_consumer_compat", "src/Verification.Compatibility/compatibility.py")
profile = module("b24_consumer_linux_profile", "src/Packaging.Linux/linux_profile.py")


def compiler_diagnostics(text):
    """Bounded compiler/restore diagnostics, never arbitrary runtime output."""
    result = []
    pattern = re.compile(r"\b(?:error|warning)\s+((?:CS|IL|NU|NWPKG|NETSDK|MSB)\d+)\s*:\s*(.*)")
    for line in text.splitlines():
        match = pattern.search(line)
        if match is None:
            continue
        message = match[2]
        # URI credentials, PEM/key material and environment values have no
        # place in the safe compiler evidence; raw logs stay local by hash.
        if any(word in message.lower() for word in ("password", "private key", "bearer ", "connectionstring", "postgresql://", "postgres://")):
            message = "Diagnostic detail withheld; consult the local log identified by its receipt hash."
        else:
            message = re.sub(r"(?:https?|file)://\S+", "<source-uri>", message)
            message = re.sub(r"/(?:[^\s\[\]\"']+/)+[^\s\[\]\"']*", "<local-path>", message)
            message = message[:1200]
        diagnostic = {"code": match[1], "message": message}
        if diagnostic not in result:
            result.append(diagnostic)
        if len(result) == 24:
            break
    return result


def config(path, feed=None):
    root = ET.Element("configuration")
    sources = ET.SubElement(root, "packageSources")
    ET.SubElement(sources, "clear")
    if feed is not None:
        ET.SubElement(sources, "add", key="domain-local", value=str(feed))
        ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
        mappings = ET.SubElement(root, "packageSourceMapping")
        local = ET.SubElement(mappings, "packageSource", key="domain-local")
        ET.SubElement(local, "package", pattern="NativeWeave.*")
        external = ET.SubElement(mappings, "packageSource", key="nuget.org")
        ET.SubElement(external, "package", pattern="*")
    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)


def project(path, references, versions, symbols, *, override=None, edited=False):
    root = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    props = ET.SubElement(root, "PropertyGroup")
    for key, value in {
        "TargetFramework": "net11.0", "LangVersion": "15.0", "OutputType": "Exe",
        "AssemblyName": "B24Consumer", "Nullable": "enable", "ImplicitUsings": "enable",
        "TreatWarningsAsErrors": "true", "IsAotCompatible": "true",
        "JsonSerializerIsReflectionEnabledByDefault": "false",
        "RestorePackagesWithLockFile": "true",
        "DefineConstants": ";".join([*symbols, *(["MANAGED_EDIT"] if edited else [])]),
    }.items():
        ET.SubElement(props, key).text = value
    ET.SubElement(props, "NuGetLockFilePath", Condition="'$(PublishAot)' == 'true'").text = "packages.linux-x64.aot.lock.json"
    group = ET.SubElement(root, "ItemGroup")
    for identity in references:
        ET.SubElement(group, "PackageReference", Include=identity, Version="[" + versions[identity] + "]")
    if override:
        ET.SubElement(group, "PackageReference", Include=override[0], Version="[" + override[1] + "]")
    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)


def selected_record(record, roots):
    index = {item["id"]: item for item in record["packages"]}
    wanted = set()
    def visit(name):
        if name not in wanted:
            wanted.add(name)
            for dependency in index[name]["dependencies"]:
                visit(dependency)
    for name in roots:
        visit(name)
    result = copy.deepcopy(record)
    result["packages"] = [index[name] for name in sorted(wanted)]
    return result


def required_checks(graph, *, edited=False):
    _, symbols, _ = GRAPHS[graph]
    result = []
    if "SQLITE" in symbols or "POSTGRES" in symbols:
        result += ["typed-entity-materialization", "typed-native-database-roundtrip", "database-local-owners-retired"]
    if "SERVER" in symbols:
        result += ["actual-http-1.1-tls", "actual-http-2.0-tls", "server-owners-retired"]
    if "GRPC" in symbols:
        result.append("actual-grpc-http2-terminal-status-and-result")
    if "MCP" in symbols:
        result.append("actual-mcp-typed-result")
    if edited:
        result += ["ordinary-added-entity-dto-handler", "ordinary-added-http-handler"]
    return result


class Consumer:
    def __init__(self, manifest_path, closure, feed, output, mode):
        if mode == "full" and closure != "candidate":
            raise ValueError("The full assembled negative/upgrade gate belongs to the candidate closure")
        self.manifest_path = manifest_path
        self.manifest = load_manifest(manifest_path)
        self.closure = closure
        self.feed, self.output, self.mode = feed, output, mode
        self.output.mkdir(parents=True, exist_ok=False)
        self.evidence = output / "evidence"
        self.evidence.mkdir()
        self.work = Path(tempfile.mkdtemp(prefix="nativeweave-b24-consumer-"))
        self.release = json.loads(relative_file(manifest_path.parent, self.manifest[closure]["releaseRecord"]).read_text())
        self.items = {item["id"]: item for item in self.manifest[closure]["packages"]}
        self.packages = {identity: feed / f"{identity}.{item['version']}.nupkg" for identity, item in self.items.items()}
        compat.validate_closure(self.release, self.packages)
        if any(sha(path) != self.items[name]["sha256"] for name, path in self.packages.items()):
            raise ValueError("Destination differs from the frozen domain handoff")
        self.environment = os.environ.copy()
        self.linker_path = self.environment.pop("LD_LIBRARY_PATH", None)
        self.environment.update(DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_CLI_USE_MSBUILD_SERVER="0", MSBUILDDISABLENODEREUSE="1")
        absence = {tool: shutil.which(tool) is None for tool in ("cargo", "rustc", "rustup")}
        if os.environ.get("B24_PRISTINE_IMAGE") != SDK_IMAGE or not all(absence.values()):
            raise ValueError("B24 acceptance consumers require the existing pinned pristine SDK image without Rust")
        if any(Path(path).exists() for path in ("/root/.cargo/bin", "/usr/local/cargo/bin", "/opt/rust", "/usr/local/rustup")):
            raise ValueError("A Rust toolchain directory is present in the pristine consumer")
        if self.work.is_relative_to(ROOT):
            raise ValueError("Consumer must be outside the checkout")
        trap = self.work / "rust-trap"
        trap.mkdir()
        self.marker = self.work / "unexpected-rust"
        for tool in absence:
            path = trap / tool
            path.write_text('#!/bin/sh\nprintf unexpected-rust >> "' + str(self.marker) + '"\nexit 99\n')
            path.chmod(0o755)
        self.environment["PATH"] = str(trap) + os.pathsep + self.environment["PATH"]
        self.result = {"schemaVersion": 1, "task": "B24", "passed": False, "rid": RID,
            "source_commit": self.release["source_commit"], "packages": {name: sha(path) for name, path in self.packages.items()},
            "manifestSha256": sha(manifest_path), "closure": closure, "mode": mode,
            "pristine_machine": True, "image": SDK_IMAGE, "architecture": platform.machine(),
            "rust_absence_before_trap": absence, "outside_source_tree": True,
            "publicDomainReleaseAccepted": False, "newDomainSignerAuthenticated": False,
            "commands": [], "graphs": {}, "negativeCases": [],
            "sourceInputs": {name: sha(ROOT / name) for name in (
                "samples/api/packaging-domain/Program.cs", "samples/api/packaging-domain/AddedModels.cs.template",
                "src/Packaging.Domain/consume.py", "src/Packaging.Domain/model.py",
                "src/Packaging.Linux/linux_profile.py",
                "tests/Packaging.Domain/consumer_cases.py", "tests/Packaging.Domain/negative_cases.py",
                "tests/Packaging.Domain/runtime_verify.py")}}
        self.transfer = {"schemaVersion": 1, "task": "B24", "source_commit": self.release["source_commit"],
                         "rid": RID, "packages": self.result["packages"], "graphs": {}}
        self.flush()

    def flush(self):
        save(self.output / "receipt.json", self.result)
        save(self.evidence / "receipt.json", self.result)

    def run(self, label, argv, *, cwd=None, env=None, reject=None, timeout=600):
        argv = list(map(str, argv))
        result = subprocess.run(argv, cwd=cwd or self.work, env=env or self.environment,
                                capture_output=True, timeout=timeout)
        logs = self.output / "logs"
        logs.mkdir(exist_ok=True)
        row = {"label": label, "exitCode": result.returncode, "processExited": True}
        # Commands contain file locations/flags, never a database connection URI.
        row["argv"] = argv
        for name, value in (("stdout", result.stdout), ("stderr", result.stderr)):
            path = logs / (label + "." + name)
            path.write_bytes(value)
            row[name + "Sha256"] = hashlib.sha256(value).hexdigest()
        self.result["commands"].append(row)
        self.flush()
        text = (result.stdout + result.stderr).decode(errors="replace")
        if reject is None:
            passed = result.returncode == 0
        else:
            codes = [reject] if isinstance(reject, str) else reject
            passed = result.returncode != 0 and any(code in text for code in codes)
        print(label, result.returncode, flush=True)
        if not passed:
            if len(argv) > 1 and Path(argv[0]).name == "dotnet" and argv[1] in ("restore", "build", "publish", "msbuild"):
                row["failureDiagnostics"] = compiler_diagnostics(text)
                self.flush()
                for diagnostic in row["failureDiagnostics"]:
                    print(diagnostic["code"] + ": " + diagnostic["message"], flush=True)
            raise RuntimeError(label + " failed; inspect retained local stdout/stderr and their receipt hashes")
        return text

    def environment_for(self, directory):
        env = self.environment.copy()
        env["NUGET_PACKAGES"] = str(directory / "cache")
        return env

    def template(self, graph, directory, *, override=None):
        directory.mkdir(parents=True, exist_ok=False)
        for name in ("Directory.Build.props", "Directory.Build.targets"):
            (directory / name).write_text("<Project />\n")
        shutil.copyfile(ROOT / "global.json", directory / "global.json")
        shutil.copyfile(ROOT / "samples/api/packaging-domain/Program.cs", directory / "Program.cs")
        refs, symbols, _ = GRAPHS[graph]
        project(directory / "Consumer.csproj", refs, {name: item["version"] for name, item in self.items.items()}, symbols, override=override)
        config(directory / "NuGet.Config", self.feed)

    def validate_graph(self, graph, directory):
        record = selected_record(self.release, GRAPHS[graph][0])
        packages = {item["id"]: self.packages[item["id"]] for item in record["packages"]}
        assets = json.loads((directory / "obj/project.assets.json").read_text())
        compat.validate_closure(record, packages, assets)
        # The actual restored archive, not merely the feed, must have exact bytes.
        for item in record["packages"]:
            cached = directory / "cache" / item["id"].lower() / item["version"].lower() / (item["id"].lower() + "." + item["version"].lower() + ".nupkg")
            if sha(cached) != self.items[item["id"]]["sha256"]:
                raise ValueError("Restored package archive identity differs: " + item["id"])
        return {item["id"]: item["version"] for item in record["packages"]}

    def native_identity(self, graph, directory):
        selected = selected_record(self.release, GRAPHS[graph][0])
        expected = {LEAVES[item["id"]]: self.items[item["id"]]["native"]
                    for item in selected["packages"] if item["id"] in LEAVES}
        records = {}
        for engine, asset in expected.items():
            path = directory / ("NativeWeave." + engine + ".assets.json")
            value = json.loads(path.read_text())
            for field, wanted in {"schemaVersion": 1, "engineId": engine, "packageId": asset["facadePackageId"],
                    "packageVersion": asset["facadePackageVersion"], "rid": RID,
                    "sourcePath": asset["path"], "sha256": asset["sha256"]}.items():
                if value.get(field) != wanted:
                    raise ValueError("Native output identity mismatch: " + engine + "/" + field)
            native = relative_file(directory, value["outputPath"])
            if sha(native) != asset["sha256"]:
                raise ValueError("Actual native output differs: " + engine)
            records[engine] = {"record": path.name, "recordSha256": sha(path),
                               "relativePath": value["outputPath"], "sha256": sha(native),
                               "facade": value["packageId"], "facadeVersion": value["packageVersion"]}
        actual = {path.name for path in directory.glob("NativeWeave.*.assets.json")}
        if actual != {"NativeWeave." + engine + ".assets.json" for engine in expected}:
            raise ValueError("Missing or undeclared domain output record")
        for compiler in ("NativeWeave.Query.Generator.dll", "NativeWeave.Server.Generator.dll",
                         "NativeWeave.Server.Mcp.Generator.dll", "NativeWeave.Packaging.Tasks.dll"):
            if (directory / compiler).exists():
                raise ValueError("Compiler/package tooling leaked into runtime output: " + compiler)
        return records

    def execute(self, graph, directory, argv, label, *, aot=False, edited=False, providers=None):
        observed = []
        for provider in providers or GRAPHS[graph][2]:
            env = self.environment_for(directory)
            env["B24_PROVIDER"] = provider
            if provider == "postgresql":
                env["B24_DATABASE"] = os.environ.get("NW_TEST_POSTGRES") or ""
                if not env["B24_DATABASE"]:
                    raise ValueError("Real PostgreSQL requires explicit NW_TEST_POSTGRES")
            elif provider == "sqlite":
                env["B24_DATABASE"] = str(directory / (label + ".sqlite.db"))
            else:
                env.pop("B24_DATABASE", None)
            text = self.run(label + "-" + provider, [*argv, *(["--require-aot"] if aot else [])], cwd=directory, env=env)
            lines = [line.removeprefix("B24_RESULT ") for line in text.splitlines() if line.startswith("B24_RESULT ")]
            if len(lines) != 1:
                raise ValueError("Consumer did not produce exactly one terminal receipt")
            receipt = json.loads(lines[0])
            if receipt.get("task") != "B24" or receipt.get("provider") != provider:
                raise ValueError("Consumer receipt identity differs")
            if aot and receipt.get("dynamicCodeSupported") is not False:
                raise ValueError("Actual Native AOT witness missing")
            if not set(required_checks(graph, edited=edited)).issubset(receipt.get("checks", [])):
                raise ValueError("Actual consumer omitted required domain operations")
            observed.append(receipt)
        return observed

    def consume_graph(self, graph):
        directory = self.work / graph
        self.template(graph, directory)
        env = self.environment_for(directory)
        csproj = directory / "Consumer.csproj"
        self.run(graph + "-restore", ["dotnet", "restore", csproj, *FLAGS], cwd=directory, env=env)
        resolved = self.validate_graph(graph, directory)
        self.run(graph + "-build", ["dotnet", "build", csproj, "-c", "Release", "--no-restore", *FLAGS], cwd=directory, env=env)
        jit = directory / "bin/Release/net11.0"
        native_before = self.native_identity(graph, jit)
        row = {"selectedPackages": resolved, "nativeBefore": native_before,
               "initialProjectSha256": sha(csproj), "initialSourceSha256": sha(directory / "Program.cs"),
               "initialJit": self.execute(graph, directory, ["dotnet", jit / "B24Consumer.dll"], graph + "-jit")}
        self.result["graphs"][graph] = row
        self.flush()
        if self.mode == "smoke":
            return
        self.run(graph + "-aot-restore", ["dotnet", "restore", csproj, "-r", RID, "-p:PublishAot=true", *FLAGS], cwd=directory, env=env)
        self.validate_graph(graph, directory)
        config(directory / "NuGet.Config")
        edited = graph == "combined"
        if edited:
            shutil.copyfile(ROOT / "samples/api/packaging-domain/AddedModels.cs.template", directory / "AddedModels.cs")
            refs, symbols, _ = GRAPHS[graph]
            project(csproj, refs, {name: item["version"] for name, item in self.items.items()}, symbols, edited=True)
            self.run(graph + "-offline-managed-edit-build", ["dotnet", "build", csproj, "-c", "Release", "--no-restore", *FLAGS], cwd=directory, env=env)
            row["editedJit"] = self.execute(graph, directory, ["dotnet", jit / "B24Consumer.dll"], graph + "-edited-jit", edited=True)
        published = self.output / "runtime-transfer" / graph
        publish_env = env.copy()
        if self.linker_path:
            publish_env["LD_LIBRARY_PATH"] = self.linker_path
        self.run(graph + "-offline-aot-publish", ["dotnet", "publish", csproj, "-c", "Release", "-r", RID,
            "-p:PublishAot=true", "-p:IlcTreatWarningsAsErrors=true", "--no-restore", "-o", published, *FLAGS], cwd=directory, env=publish_env, timeout=900)
        if (published / "B24Consumer.dll").exists():
            raise ValueError("Published output is not Native AOT")
        aot_audit = profile.inspect(published / "B24Consumer", allowed_dependencies=SYSTEM_DEPENDENCIES,
                                    maximum_glibc="2.39", executable=True)
        native_after = self.native_identity(graph, published)
        native_jit = self.native_identity(graph, jit)
        expected_hashes = {engine: value["sha256"] for engine, value in native_before.items()}
        for after in (native_after, native_jit):
            if {engine: value["sha256"] for engine, value in after.items()} != expected_hashes:
                raise ValueError("Ordinary managed edit/AOT publish changed native bytes")
        row.update(aot=self.execute(graph, directory, [published / "B24Consumer"], graph + "-aot", aot=True, edited=edited),
            nativeAfter=native_after, executableSha256=sha(published / "B24Consumer"), aotElfAudit=aot_audit,
            offlineAfterRestore=True, addedEntityDtoHandler=edited,
            finalProjectSha256=sha(csproj),
            finalSourceSha256={path.name: sha(path) for path in directory.glob("*.cs")},
            locks={path.name: sha(path) for path in directory.glob("packages*.lock.json")})
        transfer_files = {path.relative_to(published).as_posix(): sha(path) for path in published.rglob("*")
                          if path.is_file() and path.suffix not in (".dbg", ".pdb", ".xml")}
        self.transfer["graphs"][graph] = {"executable": graph + "/B24Consumer", "files": transfer_files,
            "providers": GRAPHS[graph][2], "expectedChecks": required_checks(graph, edited=edited),
            "native": native_after, "selectedPackages": resolved, "aotElfAudit": aot_audit}
        save(self.output / "runtime-transfer/manifest.json", self.transfer)
        self.flush()
        if graph == "combined":
            from negative_cases import run_negatives
            run_negatives(self, directory, published)

    def finish(self):
        if self.run("sdk", ["dotnet", "--version"]).strip() != "11.0.100":
            raise ValueError("Use the pinned .NET SDK 11.0.100")
        for graph in (GRAPHS if self.mode == "full" else ("combined",)):
            self.consume_graph(graph)
        if self.marker.exists():
            raise ValueError("Consumer attempted a Rust command")
        # Re-read actual destination archives after all commands, rather than
        # treating initial validation as a final immutable readback.
        compat.validate_closure(self.release, self.packages)
        if {name: sha(path) for name, path in self.packages.items()} != self.result["packages"]:
            raise ValueError("Destination packages changed during consumption")
        self.result.update(passed=True, rust_invocation_trap_fired=False,
            historicalFaultSoakPerformanceRerun=False, runtimeOnlyTransferExecuted=False)
        self.flush()
        if self.mode == "full":
            save(self.evidence / "runtime-transfer-manifest.json", self.transfer)
        save(self.evidence / "index.json", {"files": {path.relative_to(self.evidence).as_posix(): sha(path)
            for path in sorted(self.evidence.rglob("*")) if path.is_file()},
            "scope": "Domain receipts and identity hashes only; no private keys, database, package or runtime payload."})
        print("PASS B24 actual " + self.mode + " domain package consumption", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--closure", choices=("previous", "candidate"), required=True)
    parser.add_argument("--feed", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--mode", choices=("smoke", "full"), required=True)
    args = parser.parse_args()
    consumer = Consumer(args.manifest.resolve(), args.closure, args.feed.resolve(), args.output.resolve(), args.mode)
    try:
        consumer.finish()
    except BaseException as error:
        consumer.result.update(passed=False, failureType=type(error).__name__)
        consumer.flush()
        raise


if __name__ == "__main__":
    main()
