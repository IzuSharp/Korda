#!/usr/bin/env python3
"""Build one outside-source, package-only Native AOT consumer for three Find SQL layers."""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import shutil
import subprocess
import xml.etree.ElementTree as ET

from contracts import MODEL_REF, extract_models, read_contract, sha, write_json

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workload", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--support-cache", type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    output = args.output.resolve()
    if output.exists() or output == ROOT or ROOT in output.parents:
        raise ValueError("Use a fresh external consumer directory")
    contract = read_contract(args.workload)
    output.mkdir(parents=True)
    for name in ("Program.cs", "FindLayers.csproj", "Directory.Build.props"):
        shutil.copy2(HERE / name, output / name)
    shutil.copy2(ROOT / "global.json", output / "global.json")
    shutil.copy2(args.workload, output / "workload.json")
    source_bindings = {}
    for name in ("Models.cs", "Dataset.cs", "Digest.cs"):
        relative = "benchmarks/db-perf/managed/Dapper/" + name
        source = subprocess.check_output(["git", "show", MODEL_REF + ":" + relative], cwd=ROOT)
        if (ROOT / relative).read_bytes() != source:
            raise ValueError("Original model/handler/seed/digest source changed: " + relative)
        source_copy = output / "original-source" / name
        source_copy.parent.mkdir(exist_ok=True)
        source_copy.write_bytes(source)
        binding = {"source_commit": MODEL_REF, "repository_path": relative,
                   "original_copy": str(source_copy), "sha256": sha(source_copy)}
        if name == "Models.cs":
            generated, extraction = extract_models(source)
            (output / "Models.generated.cs").write_bytes(generated)
            binding["extraction"] = extraction
        else:
            (output / name).write_bytes(source)
        source_bindings[name] = binding
    lines = ["// Generated from the exact retained workload; unchanged SQL literals remain visible.",
             "internal static class SqlContracts", "{",
             "    internal const string WorkloadSha256 = " + json.dumps(contract["workload_sha256"]) + ";"]
    for item in contract["lanes"]:
        name = item["name"].title()
        lines += ["    internal const string " + name + " = " + json.dumps(item["client_sql"]) + ";",
                  "    internal const string " + name + "Server = " + json.dumps(item["expected_server_sql"]) + ";"]
    (output / "SqlContracts.generated.cs").write_text("\n".join([*lines, "}", ""]))
    project = output / "FindLayers.csproj"
    if ET.parse(project).getroot().findall(".//ProjectReference"):
        raise ValueError("ProjectReference is forbidden")
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="nuget", value="https://api.nuget.org/v3/index.json")
    if args.support_cache:
        cache = args.support_cache.resolve()
        if not cache.is_dir(): raise ValueError("Support cache is absent")
        allowed = output / "support-cache"
        allowed.mkdir()
        for folder in sorted(cache.iterdir()):
            if folder.is_dir() and not folder.name.lower().startswith("korda."):
                (allowed / folder.name).symlink_to(folder.resolve(), target_is_directory=True)
        fallback = ET.SubElement(config, "fallbackPackageFolders")
        ET.SubElement(fallback, "clear")
        ET.SubElement(fallback, "add", key="support", value=str(allowed))
    ET.ElementTree(config).write(output / "NuGet.Config", encoding="utf-8", xml_declaration=True)
    source_names = ["Program.cs", "FindLayers.csproj", "Directory.Build.props", "global.json", "Models.generated.cs",
                    "Dataset.cs", "Digest.cs", "SqlContracts.generated.cs"]
    command = [args.dotnet, "publish", str(project), "-c", "Release", "-r", "linux-x64",
               "-p:PublishAot=true", "-p:IlcTreatWarningsAsErrors=true", "-p:UseSharedCompilation=false",
               "-m:1", "-nr:false", "-o", str(output / "out")]
    env = os.environ | {"NUGET_PACKAGES": str(output / "packages"), "DOTNET_PROCESSOR_COUNT": "2",
                        "DOTNET_CLI_USE_MSBUILD_SERVER": "0", "MSBUILDDISABLENODEREUSE": "1"}
    receipt = {"schema": "DB-Perf-01.find-sql-layer-build.v1", "started_utc": datetime.now(timezone.utc).isoformat(),
               "command": command, "builder_sha256": sha(__file__), "contracts_helper_sha256": sha(HERE / "contracts.py"),
               "model_source_bindings": source_bindings, "contract": contract, "project_references": 0,
               "sources": {name: sha(output / name) for name in source_names}, "config_sha256": sha(output / "NuGet.Config"),
               "compile_only_DOTNET_PROCESSOR_COUNT": "2"}
    try:
        with (output / "publish.log").open("x") as log:
            result = subprocess.run(command, cwd=output, env=env, stdout=log, stderr=subprocess.STDOUT)
        receipt["exit_code"] = result.returncode
        if result.returncode: raise RuntimeError("Native AOT publish failed; original publish log retained")
        assets = output / "obj/project.assets.json"
        graph = json.loads(assets.read_text())
        if any(key.lower().startswith("korda.") for key in graph["libraries"]):
            raise ValueError("Independent SQL layer app unexpectedly references Korda")
        receipt["packages"] = {}
        for identity, item in graph["libraries"].items():
            if item["type"] != "package": raise ValueError("Only PackageReference inputs are supported")
            for folder in graph["packageFolders"]:
                archive = Path(folder) / item["path"] / (identity.replace("/", ".").lower() + ".nupkg")
                if archive.is_file():
                    receipt["packages"][identity] = {"path": str(archive.resolve()), "sha256": sha(archive)}
                    break
            else: raise ValueError("Actual restored archive is missing: " + identity)
        for identity in ("Dapper/2.1.100", "Dapper.AOT/1.1.0", "Npgsql/11.0.0"):
            if identity not in receipt["packages"]: raise ValueError("Pinned package not restored: " + identity)
        executable = output / "out/FindLayers"
        with executable.open("rb") as stream:
            if stream.read(4) != b"\x7fELF" or executable.with_suffix(".dll").exists():
                raise ValueError("Standalone Native AOT ELF is required")
        receipt["executable_sha256"] = sha(executable)
        receipt["assets_sha256"] = sha(assets)
        shutil.copy2(assets, output / "project.assets.json")
        generated = output / "obj/Release/net11.0/linux-x64/generated"
        if not generated.is_dir(): raise ValueError("Generated Dapper mapping and JSON source must be retained")
        shutil.copytree(generated, output / "generated")
        receipt["generated"] = {str(p.relative_to(output / "generated")): sha(p) for p in sorted((output / "generated").rglob("*.cs"))}
    except BaseException as error:
        receipt["validation_error"] = {"type": type(error).__name__, "message": str(error)}
        raise
    finally:
        receipt["finished_utc"] = datetime.now(timezone.utc).isoformat()
        if (output / "publish.log").exists(): receipt["publish_log_sha256"] = sha(output / "publish.log")
        write_json(output / "build-receipt.json", receipt)
    print(json.dumps({"status": "Native AOT built", "receipt": str(output / "build-receipt.json"),
                      "executable_sha256": receipt["executable_sha256"]}))


if __name__ == "__main__":
    main()
