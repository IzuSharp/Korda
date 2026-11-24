#!/usr/bin/env python3
"""Publish the same buffered-result diagnostic source as an external package-only AOT consumer."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--route", choices=("korda", "dapper"), required=True)
    parser.add_argument("--provider", choices=("sqlite", "postgresql"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--feed", type=Path)
    parser.add_argument("--version")
    parser.add_argument("--support-cache", type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    output = args.output.resolve()
    if output.exists() or output == ROOT or ROOT in output.parents:
        raise ValueError("Use a fresh consumer directory outside the repository.")
    provider = "Sqlite" if args.provider == "sqlite" else "PostgreSql"
    identity = "Korda.Database." + provider if args.route == "korda" else "Dapper"
    candidate = None
    if args.route == "korda":
        if args.feed is None or not args.version:
            raise ValueError("Korda requires the exact candidate feed and version.")
        candidate = args.feed.resolve() / f"{identity}.{args.version}.nupkg"
        if not candidate.is_file():
            raise FileNotFoundError(candidate)
    output.mkdir(parents=True)
    for name in ("Program.cs", "BusinessResults.csproj", "Directory.Build.props"):
        shutil.copy2(HERE / name, output / name)
    shutil.copy2(ROOT / "global.json", output / "global.json")
    project = output / "BusinessResults.csproj"
    if ET.parse(project).getroot().findall(".//ProjectReference"):
        raise ValueError("ProjectReference is forbidden in the external consumer.")
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    if candidate is not None:
        ET.SubElement(sources, "add", key="candidate", value=str(candidate.parent))
    ET.SubElement(sources, "add", key="nuget", value="https://api.nuget.org/v3/index.json")
    mapping = ET.SubElement(config, "packageSourceMapping")
    if candidate is not None:
        selected = ET.SubElement(mapping, "packageSource", key="candidate")
        ET.SubElement(selected, "package", pattern=identity)
    public = ET.SubElement(mapping, "packageSource", key="nuget")
    ET.SubElement(public, "package", pattern="*")
    if args.support_cache is not None:
        cache = args.support_cache.resolve()
        if not cache.is_dir():
            raise FileNotFoundError(cache)
        allowed = output / "support-cache"
        allowed.mkdir()
        for folder in sorted(cache.iterdir()):
            # A fallback must never substitute another Korda candidate archive.
            if folder.is_dir() and not folder.name.lower().startswith("korda."):
                (allowed / folder.name).symlink_to(folder.resolve(), target_is_directory=True)
        fallback = ET.SubElement(config, "fallbackPackageFolders")
        ET.SubElement(fallback, "clear")
        ET.SubElement(fallback, "add", key="support", value=str(allowed))
    ET.ElementTree(config).write(output / "NuGet.Config", encoding="utf-8", xml_declaration=True)
    command = [args.dotnet, "publish", str(project), "-c", "Release", "-r", "linux-x64",
               "-p:Route=" + ("Korda" if args.route == "korda" else "Dapper"), "-p:Provider=" + provider,
               "-p:PublishAot=true", "-p:IlcTreatWarningsAsErrors=true", "-p:UseSharedCompilation=false",
               "-m:1", "-nr:false", "-o", str(output / "out")]
    if candidate is not None:
        command.append("-p:KordaVersion=" + args.version)
    env = os.environ | {"NUGET_PACKAGES": str(output / "packages")}
    receipt = {"schema": "DB-Perf-01.business-result-build.v1", "route": args.route, "provider": args.provider,
               "command": command, "builder_sha256": sha(__file__), "project_references": 0,
               "sources": {name: sha(output / name) for name in
                           ("Program.cs", "BusinessResults.csproj", "Directory.Build.props", "global.json")},
               "config_sha256": sha(output / "NuGet.Config"), "primary_package": identity,
               "expected_candidate_sha256": sha(candidate) if candidate else None}
    with (output / "publish.log").open("w") as log:
        result = subprocess.run(command, cwd=output, env=env, stdout=log, stderr=subprocess.STDOUT, check=False)
    receipt["exit_code"] = result.returncode
    receipt["publish_log_sha256"] = sha(output / "publish.log")
    try:
        if result.returncode == 0:
            assets = output / "obj/project.assets.json"
            graph = json.loads(assets.read_text())
            korda = {key for key in graph["libraries"] if key.lower().startswith("korda.")}
            expected = {identity + "/" + args.version} if candidate else set()
            if korda != expected:
                raise ValueError("Unexpected Korda restore graph: " + repr(korda))
            receipt["packages"] = {}
            for package_identity, item in graph["libraries"].items():
                if item["type"] != "package":
                    raise ValueError("The restore graph contains a non-package input.")
                for folder in graph["packageFolders"]:
                    package = Path(folder) / item["path"] / (package_identity.replace("/", ".").lower() + ".nupkg")
                    if package.is_file():
                        receipt["packages"][package_identity] = {"path": str(package.resolve()), "sha256": sha(package)}
                        break
                else:
                    raise ValueError("Missing actual restored archive: " + package_identity)
            if candidate is not None and receipt["packages"][identity + "/" + args.version]["sha256"] != sha(candidate):
                raise ValueError("The restored Korda archive differs from the selected candidate.")
            executable = output / "out/BusinessResults"
            if executable.read_bytes()[:4] != b"\x7fELF" or executable.with_suffix(".dll").exists():
                raise ValueError("Expected a standalone Native AOT executable.")
            receipt["executable_sha256"] = sha(executable)
            receipt["assets_sha256"] = sha(assets)
            shutil.copy2(assets, output / "project.assets.json")
            generated = output / "obj/Release/net11.0/linux-x64/generated"
            if generated.is_dir():
                shutil.copytree(generated, output / "generated")
            receipt["native_images"] = {str(path.relative_to(output / "out")): sha(path)
                                       for path in sorted((output / "out").rglob("libnw_database_*.so"))}
    except Exception as error:
        receipt["validation_error"] = {"type": type(error).__name__, "message": str(error)}
        raise
    finally:
        (output / "build-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print(json.dumps(receipt, indent=2))
    raise SystemExit(result.returncode)


if __name__ == "__main__":
    main()
