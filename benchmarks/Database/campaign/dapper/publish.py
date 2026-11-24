#!/usr/bin/env python3
"""Publish one frozen standalone Dapper campaign consumer; do not create data or run load."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from datetime import datetime, timezone


HERE = Path(__file__).resolve().parent
REPOSITORY = HERE.parents[3]


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n")


def executable(value):
    found = shutil.which(str(value))
    if found is None:
        raise FileNotFoundError("Executable not found: " + str(value))
    return Path(found).resolve()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--provider", choices=("sqlite", "postgresql"), required=True)
    parser.add_argument("--output", type=Path, required=True, help="A new directory outside the repository")
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--clang", default="clang")
    parser.add_argument("--manifest", type=Path, default=HERE.parent / "contract-v1.json")
    parser.add_argument("--fallback-cache", type=Path, action="append", default=[],
                        help="Existing NuGet package folder used read-only; repeat for SDK and ORM caches")
    args = parser.parse_args()
    output = args.output.resolve()
    if output.is_relative_to(REPOSITORY):
        raise ValueError("Standalone build output must be outside the repository")
    output.mkdir(parents=True, exist_ok=False)
    records = []
    source = output / "source" / "dapper"
    source.mkdir(parents=True)
    dotnet, clang = executable(args.dotnet), executable(args.clang)
    env = os.environ.copy()
    env.update({"PATH": str(clang.parent) + ":" + str(dotnet.parent) + ":" + env["PATH"],
                "DOTNET_ROOT": str(dotnet.parent), "DOTNET_CLI_HOME": str(output / "dotnet-home"),
                "NUGET_PACKAGES": str(output / "cache"), "DOTNET_NOLOGO": "true",
                "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
                "MSBUILDDISABLENODEREUSE": "1"})

    def run(name, command, cwd):
        started = datetime.now(timezone.utc).isoformat()
        path = output / "logs" / (name + ".log")
        path.parent.mkdir(parents=True, exist_ok=True)
        command = list(map(str, command))
        with path.open("wb") as stream:
            result = subprocess.run(command, cwd=cwd, env=env, stdout=stream, stderr=subprocess.STDOUT)
        records.append({"name": name, "command": command, "cwd": str(cwd),
                        "started_utc": started, "finished_utc": datetime.now(timezone.utc).isoformat(),
                        "exit_code": result.returncode, "log": str(path.relative_to(output)), "log_sha256": sha(path)})
        save(output / "evidence" / "commands.json", records)
        print(f"{name}: exit={result.returncode}; log={path}", flush=True)
        if result.returncode:
            raise RuntimeError(f"{name} failed; raw output retained at {path}")
        return path.read_text(errors="replace")

    try:
        allowed = {".cs", ".csproj", ".props", ".targets", ".json", ".Config", ".py", ".md"}
        originals = sorted(path for path in HERE.rglob("*") if path.is_file()
                           and path.suffix in allowed and not {"bin", "obj", "out", "__pycache__"}.intersection(path.parts))
        common = sorted((HERE.parent / "common").glob("*.cs"))
        if not any(path.name == "Runner.cs" for path in common):
            raise FileNotFoundError("The shared campaign Runner.cs must exist before publishing")
        frozen = []
        for original in originals + common:
            relative = original.relative_to(HERE.parent)
            copied = output / "source" / relative
            digest = sha(original)
            copied.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(original, copied)
            if sha(original) != digest or sha(copied) != digest:
                raise RuntimeError("An input changed while freezing the standalone consumer: " + str(original))
            frozen.append({"repository_path": str(original.relative_to(REPOSITORY)),
                           "copied_path": str(copied.relative_to(output)), "sha256": digest})
        for entry, original in zip(frozen, originals + common):
            if sha(original) != entry["sha256"]:
                raise RuntimeError("The source set changed during snapshot: " + str(original))
        contract_source = args.manifest.resolve()
        copied_contract = output / "source/contract-v1.json"
        contract_hash = sha(contract_source)
        shutil.copyfile(contract_source, copied_contract)
        if sha(contract_source) != contract_hash or sha(copied_contract) != contract_hash:
            raise RuntimeError("The contract changed while freezing the standalone consumer")
        frozen.append({"repository_path": str(contract_source), "copied_path": str(copied_contract.relative_to(output)), "sha256": contract_hash})
        save(output / "evidence" / "source-manifest.json", frozen)

        config = ET.parse(source / "NuGet.Config")
        if args.fallback_cache:
            fallback = ET.SubElement(config.getroot(), "fallbackPackageFolders")
            ET.SubElement(fallback, "clear")
            for index, folder in enumerate(args.fallback_cache):
                if not folder.resolve().is_dir():
                    raise FileNotFoundError(folder)
                ET.SubElement(fallback, "add", key="readonly-" + str(index), value=str(folder.resolve()))
        config.write(source / "NuGet.Config")
        save(output / "evidence" / "build-environment.json", {
            "dotnet": {"path": str(dotnet), "sha256": sha(dotnet)},
            "clang": {"path": str(clang), "sha256": sha(clang)},
            "nuget_config_sha256": sha(source / "NuGet.Config"),
            "readonly_fallback_folders": [str(folder.resolve()) for folder in args.fallback_cache],
            "environment": {key: env[key] for key in ("DOTNET_ROOT", "DOTNET_CLI_HOME", "NUGET_PACKAGES", "PATH")},
        })
        if run("sdk-version", [dotnet, "--version"], source).strip() != "11.0.100":
            raise ValueError("The campaign currently pins SDK 11.0.100")
        if "clang version 23.1.5" not in run("clang-version", [clang, "--version"], source):
            raise ValueError("The campaign currently pins Clang 23.1.5")
        generated_check = run("prepared-source-check", [sys.executable, source / "freeze_prepared.py",
                              "--manifest", copied_contract, "--output", source / "PreparedReads.cs", "--check"], source)
        save(output / "evidence" / "prepared-generation.json", json.loads(generated_check))
        project = source / args.provider
        publish = output / "publish"
        run("publish", [dotnet, "publish", "DbPerf.Dapper.csproj", "-m:1", "-nr:false",
                        "-p:UseSharedCompilation=false", "-p:RestoreLockedMode=true", "-c", "Release", "-r", "linux-x64",
                        "-p:PublishAot=true", "-p:CppCompilerAndLinker=" + str(clang),
                        "-p:DbPerfPython=" + sys.executable, "-o", publish], project)
        proof_path = publish / "dapper-interceptors.json"
        proof = json.loads(proof_path.read_text())
        if proof["status"] != "pass" or proof["binary"]["sha256"] != sha(publish / "DbPerf.Dapper"):
            raise RuntimeError("Published interceptor evidence is missing or refers to another executable")
        assets = json.loads((project / "obj/project.assets.json").read_text())
        package_inputs = []
        for key, library in assets["libraries"].items():
            name, version = key.rsplit("/", 1)
            if name not in {"Dapper", "Dapper.AOT", "Npgsql", "Microsoft.Data.Sqlite.Core",
                            "SQLitePCLRaw.core", "SQLitePCLRaw.provider.sqlite3"}:
                continue
            candidates = [Path(folder) / library["path"] / f"{name.lower()}.{version}.nupkg"
                          for folder in assets["packageFolders"]]
            package = next((path for path in candidates if path.is_file()), None)
            if package is None:
                raise FileNotFoundError("Resolved ORM/provider package bytes unavailable: " + key)
            package_inputs.append({"id": name, "version": version, "path": str(package),
                                   "bytes": package.stat().st_size, "sha256": sha(package),
                                   "nuget_sha512": library["sha512"]})
        save(output / "evidence" / "package-inputs.json", package_inputs)
        save(output / "evidence" / "result.json", {
            "status": "compiled-aot-generated-call-gate-passed", "provider": args.provider,
            "executable": str(publish / "DbPerf.Dapper"), "executable_sha256": sha(publish / "DbPerf.Dapper"),
            "interceptors_proof_sha256": sha(proof_path), "source_manifest_sha256": sha(output / "evidence/source-manifest.json"),
            "package_inputs_sha256": sha(output / "evidence/package-inputs.json"),
            "limitations": ["No dataset was created", "No database operation or load was executed",
                            "Real result/digest validation and all performance/attribution claims remain pending"]})
    except BaseException as error:
        save(output / "evidence" / "failure.json", {"status": "failed", "error": repr(error), "command_count": len(records)})
        raise


if __name__ == "__main__":
    main()
