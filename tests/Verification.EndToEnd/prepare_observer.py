#!/usr/bin/env python3
"""Build an explicitly instrumented consumer from exact accepted application sources."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

from harness import HERE, require, sha, write_json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference-source", type=Path, required=True)
    parser.add_argument("--feed", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--modes", choices=("jit", "aot"), nargs="+", default=["jit", "aot"])
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    output, source, feed = args.output.resolve(), args.reference_source.resolve(), args.feed.resolve()
    output.mkdir(parents=True, exist_ok=False)
    project = output / "consumer"; project.mkdir()
    manifest = {"schemaVersion": 1, "task": "B22", "profile": "public-observer-eventpipe", "status": "building",
        "referenceSources": {}, "observerSources": {}, "commands": [], "builds": {},
        "instrumentation": "public MeterListener, public NativeLibrary diagnostic export, public FirstChanceException; EventSourceSupport=true; joined application entrypoint"}
    for path in sorted(source.rglob("*")):
        relative = path.relative_to(source)
        if not path.is_file() or {"bin", "obj"}.intersection(relative.parts): continue
        if path.suffix not in (".cs", ".csproj", ".sql", ".json") or path.name == "NuGet.Config": continue
        destination = project / relative; destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(path, destination); manifest["referenceSources"][str(relative)] = sha(path)
    for observer in sorted((HERE.parents[1] / "src/Verification.EndToEnd/Observer").glob("*.cs")):
        shutil.copyfile(observer, project / observer.name); manifest["observerSources"][observer.name] = sha(observer)
    program = project / "Program.cs"
    original = program.read_text()
    old_signature = "private static async Task<int> Main(string[] args)"
    new_signature = "internal static async Task<int> Main(string[] args)"
    require(original.count(old_signature) == 1, "The exact application entrypoint signature changed; review the observer profile")
    instrumented = original.replace(old_signature, new_signature, 1)
    program.write_text(instrumented)
    manifest["applicationSourceDelta"] = {"path": "Program.cs", "originalSha256": manifest["referenceSources"]["Program.cs"],
        "instrumentedSha256": sha(program), "before": old_signature, "after": new_signature,
        "bodyUnchanged": instrumented.replace(new_signature, old_signature, 1) == original}
    csproj = project / "Reference.TaskService.csproj"
    document = ET.parse(csproj); properties = ET.SubElement(document.getroot(), "PropertyGroup")
    for name, value in {"TargetFramework": "net11.0", "LangVersion": "15.0", "Nullable": "enable",
        "ImplicitUsings": "enable", "TreatWarningsAsErrors": "true", "RestorePackagesWithLockFile": "true",
        "IsPackable": "false", "EventSourceSupport": "true", "AllowUnsafeBlocks": "true",
        "StartupObject": "NativeWeave.Verification.EndToEnd.ObserverEntryPoint"}.items():
        ET.SubElement(properties, name).text = value
    document.write(csproj, encoding="unicode")
    for name in ("Directory.Build.props", "Directory.Build.targets"):
        (output / name).write_text("<Project />\n")
    global_json = HERE.parents[1] / "global.json"
    shutil.copyfile(global_json, output / "global.json")
    config = ET.Element("configuration"); sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear"); ET.SubElement(sources, "add", key="candidate", value=str(feed))
    ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
    mapping = ET.SubElement(config, "packageSourceMapping"); ET.SubElement(mapping, "clear")
    ET.SubElement(ET.SubElement(mapping, "packageSource", key="candidate"), "package", pattern="NativeWeave.*")
    ET.SubElement(ET.SubElement(mapping, "packageSource", key="nuget.org"), "package", pattern="*")
    ET.ElementTree(config).write(output / "NuGet.Config", encoding="unicode")
    manifest["projectSha256"] = sha(csproj)
    manifest["packages"] = {p.name: sha(p) for p in sorted(feed.glob("*.nupkg"))}
    manifest["sdkGlobalJsonSha256"] = sha(global_json)
    def command(label, argv, timeout=900):
        log_path = output / (label + ".log")
        record = {"label": label, "argv": list(map(str, argv)), "status": "running"}
        manifest["commands"].append(record); write_json(output / "manifest.json", manifest)
        with log_path.open("wb") as log:
            result = subprocess.run(record["argv"], cwd=output, stdout=log, stderr=subprocess.STDOUT, timeout=timeout)
        record.update(status="passed" if result.returncode == 0 else "failed", exitCode=result.returncode, logSha256=sha(log_path))
        write_json(output / "manifest.json", manifest)
        require(result.returncode == 0, "Observer consumer command failed: " + label)
    try:
        for mode in args.modes:
            aot = ["-r", "linux-x64", "-p:PublishAot=true", "-p:IlcInstructionSet=x86-64-v2"] if mode == "aot" else []
            command(mode + "-restore", [args.dotnet, "restore", csproj, "--configfile", output / "NuGet.Config", *aot])
            destination = output / "artifacts" / mode
            command(mode + "-build", [args.dotnet, "publish" if mode == "aot" else "build", csproj, "-c", "Release", "--no-restore", "-o", destination, *aot])
            manifest["builds"][mode] = {"directory": str(destination), "files": {str(p.relative_to(destination)): sha(p) for p in sorted(destination.rglob("*")) if p.is_file()}}
            write_json(output / "manifest.json", manifest)
        manifest["status"] = "built-not-executed"
    except BaseException:
        manifest["status"] = "failed-with-retained-progress"; raise
    finally:
        manifest["locks"] = {p.name: sha(p) for p in sorted(project.glob("packages*.lock.json"))}
        write_json(output / "manifest.json", manifest)
    print(json.dumps({"status": manifest["status"], "manifest": str(output / "manifest.json")}))


if __name__ == "__main__": main()
