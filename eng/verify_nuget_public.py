#!/usr/bin/env python3
"""Consume the accepted database packages from NuGet.org in four fresh projects.

This is a public-registry delivery check, separate from the retained local-feed
acceptance gates. It neither produces nor publishes packages, invokes a native
product build, nor treats a local candidate as a public restore. NuGet repository
signing may change ZIP bytes; every unsigned entry must still match release.json.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import platform
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
SOURCE = "https://api.nuget.org/v3/index.json"
REPOSITORY = "https://github.com/IzuSharp/NativeWeave"
SAMPLE = "samples/api/packaging-domain/Program.cs"
RID = "linux-x64"
SDK = "11.0.100"
COMMON_VERSION = "0.1.0-db-cpp.1.1"
PROVIDER_VERSION = "0.1.0-db-cpp.1.2"
COMMON = {
    "NativeWeave.Interop.Abi": [],
    "NativeWeave.Core": ["NativeWeave.Interop.Abi"],
    "NativeWeave.Data": [],
    "NativeWeave.Database.Transactions": ["NativeWeave.Data"],
    "NativeWeave.Data.Streaming": ["NativeWeave.Data"],
    "NativeWeave.Query.Binding": ["NativeWeave.Data", "NativeWeave.Interop.Abi"],
    "NativeWeave.Query.Generator": [],
}
DEPENDENCIES = {
    **COMMON,
    "NativeWeave.Database": ["NativeWeave.Core", "NativeWeave.Data", "NativeWeave.Data.Streaming",
                            "NativeWeave.Database.Transactions", "NativeWeave.Query.Binding"],
    "NativeWeave.Database.Sqlite": ["NativeWeave.Core", "NativeWeave.Data", "NativeWeave.Database",
                                   "NativeWeave.Database.Sqlite.Native", "NativeWeave.Query.Generator"],
    "NativeWeave.Database.PostgreSql": ["NativeWeave.Core", "NativeWeave.Data", "NativeWeave.Database",
                                       "NativeWeave.Database.PostgreSql.Native", "NativeWeave.Query.Generator"],
    "NativeWeave.Database.Sqlite.Native": [],
    "NativeWeave.Database.PostgreSql.Native": [],
}
PROVIDERS = {
    "sqlite": ("NativeWeave.Database.Sqlite", "SQLITE", "libnw_database_sqlite.so"),
    "postgresql": ("NativeWeave.Database.PostgreSql", "POSTGRES", "libnw_database_postgresql.so"),
}
CHECKS = {"typed-entity-materialization", "typed-native-database-roundtrip", "typed-fluent-dto-query",
          "database-local-owners-retired", "actual-database-native-mapping", "only-selected-database-module"}
FLAGS = ["-p:UseSharedCompilation=false", "-nr:false", "-m:1"]


def require(value, message):
    if not value:
        raise ValueError(message)


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def save(path, value):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    temporary.replace(path)


def relative_file(root, name):
    require(isinstance(name, str) and name and "\\" not in name and ":" not in name,
            "Noncanonical artifact path")
    require(not PurePosixPath(name).is_absolute() and all(x not in ("", ".", "..") for x in name.split("/")),
            "Artifact path escapes its root")
    value = root / name
    require(value.resolve().is_relative_to(root.resolve()), "Artifact symlink escapes its root")
    return value


def load_release(path, expected_source):
    require(re.fullmatch(r"[0-9a-f]{40}", expected_source), "An exact accepted source commit is required")
    record = json.loads(path.read_text())
    require(record.get("schema_version") == 1 and record.get("rid") == RID,
            "Expected the accepted Linux x64 release record")
    require(record.get("source_commit") == expected_source and record.get("source_dirty") is False,
            "Release provenance differs from the accepted source")
    items = record.get("packages", [])
    require(len(items) == 12 and {item.get("id") for item in items} == set(DEPENDENCIES),
            "Expected exactly the twelve accepted database package IDs")
    versions = {name: COMMON_VERSION if name in COMMON else PROVIDER_VERSION for name in DEPENDENCIES}
    index = {item["id"]: item for item in items}
    for name, item in index.items():
        require(item.get("version") == versions[name], "Unaccepted package version: " + name)
        require(item.get("source_commit") == expected_source and item.get("repository") == REPOSITORY,
                "Package provenance differs: " + name)
        require(item.get("dependencies") == {dep: versions[dep] for dep in DEPENDENCIES[name]},
                "Package dependency graph differs: " + name)
        entries = item.get("unsigned_entries", {})
        require(entries and all(re.fullmatch(r"[0-9a-f]{64}", value) for value in entries.values()),
                "Missing accepted unsigned-entry hashes: " + name)
        require(".signature.p7s" not in entries, "The release record must describe unsigned payload entries")
    for provider, (facade, _, filename) in PROVIDERS.items():
        native = index[facade + ".Native"].get("native", {})
        require(native.get("rid") == RID and native.get("path") == "runtimes/linux-x64/native/" + filename
                and native.get("abi_major") == 1
                and index[facade + ".Native"]["unsigned_entries"].get(native["path"]) == native.get("sha256"),
                "Accepted native identity differs: " + provider)
    return index


def load_policies(path, owner):
    record = json.loads(path.read_text())
    require(record.get("schemaVersion") == 1 and record.get("service_index") == SOURCE
            and record.get("owners") == [owner], "NuGet signature-policy identity differs")
    require(str(record.get("repositorySignaturesUrl", "")).startswith("https://api.nuget.org/")
            and re.fullmatch(r"[0-9a-f]{64}", record.get("repositorySignaturesSha256", "")),
            "Policy is missing its official repository-signatures source identity")
    policies = record.get("policies", [])
    require(policies, "No NuGet.org repository signing certificate is available")
    for policy in policies:
        require(policy.get("route") == "repository-primary" and policy.get("service_index") == SOURCE
                and policy.get("owners") == [owner]
                and re.fullmatch(r"[0-9a-fA-F]{64}", policy.get("certificate_sha256", ""))
                and isinstance(policy.get("subject"), str) and policy["subject"],
                "Unpermitted repository signing policy")
    return policies


def signature_identity(text, policies):
    require(re.findall(r"^Signature type: (.+)$", text, re.M) == ["Repository"],
            "Expected one NuGet repository primary signature")
    require(re.findall(r"^Service index: (.+)$", text, re.M) == [SOURCE],
            "Signed repository service index differs")
    rows = re.findall(r"^Owners: (.+)$", text, re.M)
    require(len(rows) == 1, "Missing signed repository owners")
    owners = sorted(value.strip() for value in rows[0].split(","))
    match = re.search(r"Verifying the repository primary signature with certificate:\s*\n"
                      r"  Subject Name: (?P<subject>[^\n]+)\n  SHA1 hash: [^\n]+\n"
                      r"  SHA256 hash: (?P<hash>[0-9A-Fa-f]+)", text)
    require(match is not None, "Missing verified repository signing certificate")
    selected = [policy for policy in policies if owners == sorted(policy["owners"])
                and match["subject"] == policy["subject"]
                and match["hash"].lower() == policy["certificate_sha256"].lower()]
    require(len(selected) == 1, "Signed owner or repository certificate is outside the official policy")
    return {"verified": True, "service_index": SOURCE, "owners": owners,
            "certificate_sha256": match["hash"].lower(), "subject": match["subject"]}


def selected_packages(index, facade):
    selected = {}

    def visit(name):
        if name not in selected:
            selected[name] = index[name]
            for dependency in index[name]["dependencies"]:
                visit(dependency)

    visit(facade)
    return selected


def validate_payload(package, expected):
    """Check actual restored bytes; cryptographic signature verification is separate."""
    entries = {}
    total = 0
    with zipfile.ZipFile(package) as archive:
        infos = archive.infolist()
        require(len(infos) <= 1024, "Unexpectedly large package entry list")
        for info in infos:
            name = info.filename
            relative_file(Path("/virtual-package-root"), name)
            require(name not in entries and (info.external_attr >> 16) & 0o170000 != 0o120000,
                    "Duplicate or symlink archive entry")
            total += info.file_size
            require(info.file_size <= 128 * 1024 * 1024 and total <= 256 * 1024 * 1024,
                    "Package exceeds the bounded payload profile")
            with archive.open(info) as stream:
                entries[name] = hashlib.file_digest(stream, "sha256").hexdigest()
        require(".signature.p7s" in entries, "Public NuGet.org package is not repository signed")
        entries.pop(".signature.p7s")
        require(entries == expected["unsigned_entries"], "Restored package payload differs from accepted bytes")
        names = [name for name in entries if name.endswith(".nuspec")]
        require(len(names) == 1, "Expected one nuspec")
        raw = archive.read(names[0])
        require(len(raw) <= 1024 * 1024 and b"<!DOCTYPE" not in raw.upper() and b"<!ENTITY" not in raw.upper(),
                "Unexpected nuspec structure")
        metadata = ET.fromstring(raw).find("{*}metadata")
        require(metadata is not None and metadata.findtext("{*}id") == expected["id"]
                and metadata.findtext("{*}version") == expected["version"], "Restored nuspec identity differs")
        repository = metadata.find("{*}repository")
        require(repository is not None and repository.get("url") == REPOSITORY
                and repository.get("commit") == expected["source_commit"], "Restored nuspec source differs")
        dependencies = metadata.findall(".//{*}dependency")
        require(len({node.attrib["id"] for node in dependencies}) == len(dependencies),
                "Duplicate nuspec dependency")
        require({node.attrib["id"]: node.attrib["version"] for node in dependencies}
                == {name: "[" + version + "]" for name, version in expected["dependencies"].items()},
                "Restored nuspec dependency closure differs")
    return {"id": expected["id"], "version": expected["version"], "archive_sha256": sha(package),
            "accepted_unsigned_entries_equal": True, "unsigned_entry_count": len(entries),
            "source_commit": expected["source_commit"]}


def create_project(directory, provider):
    directory.mkdir()
    for name in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"):
        (directory / name).write_text("<Project />\n")
    shutil.copyfile(ROOT / SAMPLE, directory / "Program.cs")
    shutil.copyfile(ROOT / "global.json", directory / "global.json")
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="nuget.org", value=SOURCE, protocolVersion="3")
    for name in ("disabledPackageSources", "fallbackPackageFolders"):
        ET.SubElement(ET.SubElement(config, name), "clear")
    mapping = ET.SubElement(config, "packageSourceMapping")
    ET.SubElement(mapping, "clear")
    ET.SubElement(ET.SubElement(mapping, "packageSource", key="nuget.org"), "package", pattern="*")
    ET.ElementTree(config).write(directory / "NuGet.Config", encoding="utf-8", xml_declaration=True)
    project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    props = ET.SubElement(project, "PropertyGroup")
    properties = {
        "TargetFramework": "net11.0", "LangVersion": "15.0", "OutputType": "Exe",
        "AssemblyName": "NuGetPublicConsumer", "Nullable": "enable", "ImplicitUsings": "enable",
        "TreatWarningsAsErrors": "true", "IsAotCompatible": "true", "NuGetAudit": "true",
        "NuGetAuditMode": "all", "JsonSerializerIsReflectionEnabledByDefault": "false",
        "RestorePackagesWithLockFile": "true", "DefineConstants": PROVIDERS[provider][1],
        "DisableImplicitNuGetFallbackFolder": "true", "RestoreFallbackFolders": "",
        "RestoreAdditionalProjectFallbackFolders": "", "RestoreAdditionalProjectSources": "",
    }
    for name, value in properties.items():
        ET.SubElement(props, name).text = value
    ET.SubElement(ET.SubElement(project, "ItemGroup"), "PackageReference",
                  Include=PROVIDERS[provider][0], Version="[" + PROVIDER_VERSION + "]")
    ET.ElementTree(project).write(directory / "Consumer.csproj", encoding="utf-8", xml_declaration=True)


def restored_graph(directory, selected):
    assets_path = directory / "obj/project.assets.json"
    assets = json.loads(assets_path.read_text())
    restore = assets["project"]["restore"]
    require(set(restore.get("sources", {})) == {SOURCE}, "Restore used an undeclared package source")
    require(not restore.get("fallbackFolders"), "Restore used a fallback package folder")
    require({Path(name).resolve() for name in assets.get("packageFolders", {})}
            == {(directory / "cache").resolve()}, "Restore escaped its fresh global-packages folder")
    actual = {}
    for identity, value in assets["libraries"].items():
        require(value["type"] == "package", "Unexpected project/reference in the external consumer")
        name, version = identity.rsplit("/", 1)
        if name.lower().startswith("nativeweave."):
            actual[name.lower()] = version
    require(actual == {name.lower(): item["version"] for name, item in selected.items()},
            "Actual restored NativeWeave closure differs from the selected provider")
    metadata = []
    for path in sorted((directory / "cache").glob("*/*/.nupkg.metadata")):
        value = json.loads(path.read_text())
        require(value.get("version", 0) >= 2 and value.get("source") == SOURCE,
                "A restored package did not originate at NuGet.org")
        metadata.append({"id": path.parent.parent.name, "version": path.parent.name,
                         "source": value["source"], "content_hash": value.get("contentHash"),
                         "metadata_sha256": sha(path)})
    require(metadata, "No actual restored package metadata exists")
    native = {(item["id"], item["version"]) for item in metadata if item["id"].startswith("nativeweave.")}
    require(native == {(name.lower(), value["version"]) for name, value in selected.items()},
            "Restored cache contains a different NativeWeave package set")
    return {"project_assets_sha256": sha(assets_path), "sources": [SOURCE],
            "selected_packages": {name: item["version"] for name, item in selected.items()},
            "restored_package_metadata": metadata}


def native_output(directory, provider, index):
    facade, _, filename = PROVIDERS[provider]
    expected = index[facade + ".Native"]["native"]
    record_path = directory / ("NativeWeave.database." + provider + ".assets.json")
    record = json.loads(record_path.read_text())
    for name, value in {"schemaVersion": 1, "engineId": "database." + provider, "packageId": facade,
                        "packageVersion": PROVIDER_VERSION, "rid": RID, "sourcePath": expected["path"],
                        "sha256": expected["sha256"]}.items():
        require(record.get(name) == value, "Native output record differs: " + name)
    library = relative_file(directory, record["outputPath"])
    require(library.name == filename and sha(library) == expected["sha256"], "Actual native output differs")
    require({path.resolve() for path in directory.rglob("*.so")} == {library.resolve()},
            "Unexpected native library in the consumer output")
    require({path.name for path in directory.glob("NativeWeave.*.assets.json")} == {record_path.name},
            "Unexpected provider asset record")
    selected = selected_packages(index, facade)
    require(all(path.stem in selected for path in directory.rglob("NativeWeave.*.dll")),
            "Runtime output contains an undeclared NativeWeave assembly")
    require(not any(directory.rglob("NativeWeave.Query.Generator.dll"))
            and not any(directory.rglob("NativeWeave.Packaging.Tasks.dll")), "Build tooling leaked into runtime output")
    return {"file": filename, "sha256": sha(library), "record_sha256": sha(record_path),
            "relative_path": record["outputPath"]}


def business_receipt(text, provider, mode, index):
    rows = [line.removeprefix("B24_RESULT ") for line in text.splitlines() if line.startswith("B24_RESULT ")]
    require(len(rows) == 1, "Consumer did not produce one terminal business receipt")
    receipt = json.loads(rows[0])
    require(receipt.get("schemaVersion") == 1 and receipt.get("task") == "DB-CPP-01"
            and receipt.get("provider") == provider, "Business receipt identity differs")
    require(receipt.get("dynamicCodeSupported") is (mode == "jit"), "Actual JIT/AOT mode differs")
    require(CHECKS.issubset(receipt.get("checks", [])), "Consumer did not complete all business/retirement checks")
    if mode == "aot":
        require("actual-native-aot" in receipt["checks"], "Missing actual Native AOT execution witness")
    facade, _, filename = PROVIDERS[provider]
    require(receipt.get("nativeLibraries") == [{"file": filename, "sha256": index[facade + ".Native"]["native"]["sha256"]}],
            "Actually mapped native library differs from the accepted package")
    return receipt


class PublicConsumer:
    def __init__(self, args):
        self.args = args
        self.index = load_release(args.release_record, args.expected_source)
        self.policies = load_policies(args.signature_policy, args.owner)
        require(platform.system() == "Linux" and platform.machine() == "x86_64", "Public acceptance requires Linux x64")
        require(os.environ.get("NW_TEST_POSTGRES"), "An explicit disposable PostgreSQL 18 NW_TEST_POSTGRES is required")
        require(shutil.which("dotnet") is not None and shutil.which("clang") is not None,
                "The consumer requires .NET SDK 11.0.100 and the normal Native AOT Clang/linker prerequisites")
        args.output.mkdir(parents=True, exist_ok=False)
        self.logs = args.output / "logs"
        self.logs.mkdir()
        self.work = Path(tempfile.mkdtemp(prefix="nativeweave-nuget-public-", dir=args.work_parent)).resolve()
        require(not self.work.is_relative_to(ROOT), "The public consumer must be outside the source checkout")
        self.result = {"schema_version": 1, "task": "database-nuget-publication", "passed": False,
                       "kind": "actual-public-package-consumption", "rid": RID, "source_commit": args.expected_source,
                       "release_record_sha256": sha(args.release_record), "signature_policy_sha256": sha(args.signature_policy),
                       "service_index": SOURCE, "owner": args.owner, "outside_source_tree": True,
                       "pristine_machine": False, "native_product_build_invoked": False,
                       "same_accepted_business_source": False, "commands": [], "cells": {}}
        self.flush()

    def flush(self):
        save(self.args.output / "receipt.json", self.result)

    def environment(self, directory):
        env = os.environ.copy()
        for name in tuple(env):
            if name.upper().startswith(("NUGET_", "NUGETPACKAGESOURCECREDENTIALS_", "MSBUILD", "CARGO_", "RUSTUP_")):
                env.pop(name)
        for name in ("NW_TEST_POSTGRES", "GITHUB_TOKEN", "GH_TOKEN", "ACTIONS_ID_TOKEN_REQUEST_TOKEN",
                     "ACTIONS_ID_TOKEN_REQUEST_URL", "ACTIONS_RUNTIME_TOKEN", "DOTNET_STARTUP_HOOKS",
                     "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE"):
            env.pop(name, None)
        env.update(NUGET_PACKAGES=str(directory / "cache"), NUGET_HTTP_CACHE_PATH=str(directory / "http-cache"),
                   NUGET_SCRATCH=str(directory / "nuget-scratch"), NUGET_PLUGINS_CACHE_PATH=str(directory / "plugins-cache"),
                   DOTNET_CLI_HOME=str(directory / "dotnet-home"), DOTNET_CLI_TELEMETRY_OPTOUT="1",
                   DOTNET_CLI_USE_MSBUILD_SERVER="0", DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1",
                   DOTNET_MULTILEVEL_LOOKUP="0", MSBUILDDISABLENODEREUSE="1", DOTNET_NOLOGO="true")
        return env

    def run(self, label, argv, directory, env, timeout=600):
        started = time.monotonic()
        process = subprocess.Popen([str(value) for value in argv], cwd=directory, env=env,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True)
        timed_out = False
        try:
            stdout, stderr = process.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            os.killpg(process.pid, signal.SIGKILL)
            stdout, stderr = process.communicate()
        row = {"label": label, "exit_code": process.returncode, "timed_out": timed_out,
               "duration_seconds": round(time.monotonic() - started, 3)}
        texts = []
        for name, raw in (("stdout", stdout), ("stderr", stderr)):
            text = raw.decode("utf-8", errors="replace").replace(os.environ["NW_TEST_POSTGRES"], "<database-uri>")
            text = re.sub(r"postgres(?:ql)?://[^\s\"']+", "<database-uri>", text)
            path = self.logs / (label + "." + name)
            path.write_text(text, encoding="utf-8")
            row[name + "_sha256"] = sha(path)
            texts.append(text)
        self.result["commands"].append(row)
        self.flush()
        print(label + ": " + ("timeout" if timed_out else str(process.returncode)), flush=True)
        require(not timed_out and process.returncode == 0, "Public consumer command failed: " + label)
        return "\n".join(texts)

    def consume(self, provider, mode):
        label = provider + "-" + mode
        directory = self.work / label
        create_project(directory, provider)
        env = self.environment(directory)
        for name in ("cache", "http-cache", "nuget-scratch", "plugins-cache", "dotnet-home"):
            (directory / name).mkdir()
            require(not any((directory / name).iterdir()), "Consumer cache was not empty before restore")
        row = {"passed": False, "fresh_empty_caches": True, "project_sha256": sha(directory / "Consumer.csproj"),
               "source_sha256": sha(directory / "Program.cs"), "nuget_config_sha256": sha(directory / "NuGet.Config")}
        self.result["cells"][label] = row
        self.flush()
        require(self.run(label + "-sdk", ["dotnet", "--version"], directory, env).strip() == SDK,
                "Use the pinned .NET SDK 11.0.100")
        restore = ["dotnet", "restore", "Consumer.csproj", "--configfile", directory / "NuGet.Config",
                   "--packages", directory / "cache", "--no-http-cache", "--force", *FLAGS]
        if mode == "aot":
            restore.extend(["-r", RID, "-p:PublishAot=true", "-p:SelfContained=true"])
        self.run(label + "-restore", restore, directory, env)
        selected = selected_packages(self.index, PROVIDERS[provider][0])
        row["restore"] = restored_graph(directory, selected)
        row["packages"] = []
        for name, expected in sorted(selected.items()):
            folder = directory / "cache" / name.lower() / expected["version"].lower()
            package = folder / (name.lower() + "." + expected["version"].lower() + ".nupkg")
            package_receipt = validate_payload(package, expected)
            output = self.run(label + "-signature-" + name, ["dotnet", "nuget", "verify", package, "--all",
                              "--verbosity", "detailed", "--force-english-output", "--configfile", directory / "NuGet.Config"],
                              directory, env, timeout=120)
            package_receipt["signature"] = signature_identity(output, self.policies)
            row["packages"].append(package_receipt)
        output = directory / "output"
        if mode == "aot":
            self.run(label + "-publish", ["dotnet", "publish", "Consumer.csproj", "-c", "Release", "-r", RID,
                     "-p:PublishAot=true", "-p:IlcTreatWarningsAsErrors=true", "--self-contained", "true",
                     "--no-restore", "-o", output, *FLAGS], directory, env, timeout=900)
            require(not (output / "NuGetPublicConsumer.dll").exists(), "AOT output contains the managed application")
            executable = output / "NuGetPublicConsumer"
            with executable.open("rb") as stream:
                header = stream.read(64)
            require(len(header) == 64 and header[:7] == b"\x7fELF\x02\x01\x01"
                    and int.from_bytes(header[18:20], "little") == 62, "AOT output is not a Linux x64 ELF")
            command = [executable, "--require-aot"]
        else:
            self.run(label + "-build", ["dotnet", "build", "Consumer.csproj", "-c", "Release", "--no-restore",
                     "-o", output, *FLAGS], directory, env)
            executable = output / "NuGetPublicConsumer.dll"
            command = ["dotnet", executable]
        row["native_output"] = native_output(output, provider, self.index)
        row["executable_sha256"] = sha(executable)
        runtime_env = env.copy()
        runtime_env.pop("LD_LIBRARY_PATH", None)
        runtime_env["B24_PROVIDER"] = provider
        runtime_env["B24_DATABASE"] = os.environ["NW_TEST_POSTGRES"] if provider == "postgresql" else str(directory / "database.sqlite")
        observed = self.run(label + "-execute", command, directory, runtime_env, timeout=120)
        row["business_receipt"] = business_receipt(observed, provider, mode, self.index)
        require(sha(directory / "Program.cs") == self.result["business_source_sha256"], "Business source changed during consumption")
        require(restored_graph(directory, selected) == row["restore"], "Build changed the restored package graph")
        for expected in selected.values():
            package = directory / "cache" / expected["id"].lower() / expected["version"] / (expected["id"].lower() + "." + expected["version"] + ".nupkg")
            validate_payload(package, expected)
        row["lock_sha256"] = sha(directory / "packages.lock.json")
        row["passed"] = True
        self.flush()

    def finish(self):
        result = subprocess.run(["git", "show", self.args.expected_source + ":" + SAMPLE], cwd=ROOT,
                                capture_output=True, timeout=30, check=True)
        source = (ROOT / SAMPLE).read_bytes()
        require(result.stdout == source, "Business sample is not the exact accepted source")
        self.result.update(same_accepted_business_source=True, business_source_sha256=hashlib.sha256(source).hexdigest(),
                           verifier_sha256=sha(Path(__file__)))
        self.flush()
        for provider in PROVIDERS:
            for mode in ("jit", "aot"):
                self.consume(provider, mode)
        require(len(self.result["cells"]) == 4 and all(row["passed"] for row in self.result["cells"].values()),
                "The public provider/JIT/AOT matrix is incomplete")
        identities = {item["id"] for row in self.result["cells"].values() for item in row["packages"]}
        require(identities == set(DEPENDENCIES), "Public consumers did not cover all twelve package IDs")
        self.result.update(passed=True, public_package_consumption_verified=True,
                           distinct_nativeweave_packages_verified=12)
        self.flush()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--release-record", type=Path, required=True)
    parser.add_argument("--signature-policy", type=Path, required=True)
    parser.add_argument("--expected-source", required=True)
    parser.add_argument("--owner", default="dekueon")
    parser.add_argument("--output", type=Path, required=True, help="New receipt/log directory; must not exist")
    parser.add_argument("--work-parent", type=Path, help="Existing directory outside the checkout for fresh consumer projects")
    args = parser.parse_args()
    for name in ("release_record", "signature_policy", "output"):
        setattr(args, name, getattr(args, name).resolve())
    consumer = None
    try:
        consumer = PublicConsumer(args)
        consumer.finish()
    except Exception as error:
        reason = str(error) if isinstance(error, ValueError) else type(error).__name__
        if consumer is not None:
            consumer.result.update(passed=False, failure_type=type(error).__name__, failure_reason=reason)
            consumer.flush()
        print("Public consumption failed: " + reason + ". Inspect the retained receipt and logs.", file=sys.stderr)
        return 1
    print("PASS NuGet.org PostgreSQL/SQLite × JIT/Native AOT; all twelve accepted package identities verified.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
