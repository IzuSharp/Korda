#!/usr/bin/env python3
"""Build an isolated Native AOT profiling consumer from one exact private feed."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--feed", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--probe", choices=("retirement", "page"), default="retirement")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    source = output / "consumer"
    probe = {"retirement": "RetirementProbe", "page": "PageProbe"}[args.probe]
    shutil.copytree(HERE / probe, source, ignore=shutil.ignore_patterns("bin", "obj"))
    shutil.copy2(ROOT / "global.json", output / "global.json")
    for name in ("Directory.Build.props", "Directory.Build.targets"):
        (output / name).write_text("<Project />\n")
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="candidate", value=str(args.feed.resolve()))
    ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
    mapping = ET.SubElement(config, "packageSourceMapping")
    ET.SubElement(ET.SubElement(mapping, "packageSource", key="candidate"), "package", pattern="NativeWeave.*")
    ET.SubElement(ET.SubElement(mapping, "packageSource", key="nuget.org"), "package", pattern="*")
    ET.ElementTree(config).write(output / "NuGet.Config", encoding="unicode")
    environment = os.environ.copy()
    environment.update(NUGET_PACKAGES=str(output / "packages"), DOTNET_CLI_USE_MSBUILD_SERVER="0", MSBUILDDISABLENODEREUSE="1")
    trap = output / "no-rust"
    trap.mkdir()
    for tool in ("cargo", "rustc", "rustup"):
        path = trap / tool
        path.write_text("#!/bin/sh\necho unexpected-consumer-Rust >&2\nexit 197\n")
        path.chmod(0o755)
    environment["PATH"] = str(trap) + os.pathsep + environment["PATH"]
    project = source / (probe + ".csproj")
    flags = ["-r", "linux-x64", "-p:PublishAot=true", "-p:IlcInstructionSet=x86-64-v2",
             "-p:NativeWeavePackageVersion=" + args.version, "-p:UseSharedCompilation=false", "-nr:false", "-m:1"]
    record = {"task": "Perf-01", "kind": "profiling-only", "probe": args.probe, "version": args.version,
              "driverSources": {str(path.relative_to(HERE)): sha(path) for path in HERE.rglob("*") if path.is_file() and not {"bin", "obj", "__pycache__"}.intersection(path.parts)},
              "packageFiles": {path.name: sha(path) for path in sorted(args.feed.glob("*.nupkg"))}, "commands": []}
    for label, command in (("restore", ["dotnet", "restore", project, *flags]),
                           ("publish", ["dotnet", "publish", project, "-c", "Release", "--no-restore", "-o", output / "aot", *flags])):
        log_path = output / (label + ".log")
        with log_path.open("wb") as log:
            process = subprocess.run(list(map(str, command)), cwd=output, env=environment, stdout=log, stderr=subprocess.STDOUT)
        record["commands"].append({"argv": list(map(str, command)), "exitCode": process.returncode, "logSha256": sha(log_path)})
        (output / "manifest.json").write_text(json.dumps(record, indent=2) + "\n")
        if process.returncode:
            raise RuntimeError(label + " failed: " + str(log_path))
    record["artifacts"] = {str(path.relative_to(output)): sha(path) for path in (output / "aot").rglob("*") if path.is_file()}
    record["consumerInputs"] = {str(path.relative_to(output)): sha(path) for path in source.rglob("*")
                               if path.is_file() and (path.suffix in {".cs", ".csproj"} or path.name.startswith("packages.") and path.suffix == ".json")
                               and not {"bin", "obj"}.intersection(path.relative_to(source).parts)}
    record["executed"] = False
    (output / "manifest.json").write_text(json.dumps(record, indent=2) + "\n")
    print(str(output / "manifest.json"))


if __name__ == "__main__":
    main()
