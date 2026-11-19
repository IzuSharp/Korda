#!/usr/bin/env python3
"""Publish only the isolated public Inspect profiler from an existing codec feed.

This does not produce a package, invoke Rust, run the profiler, or change a frozen
driver. Run only outside every measurement window, using the locked toolchain.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import xml.etree.ElementTree as ET
import zipfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
PACKAGE_NAMES = ("NativeWeave.Core.0.1.0-proof.1.nupkg", "NativeWeave.Engines.SyncProof.0.1.0-proof.1.nupkg")


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def save(path, value):
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n")
    temporary.replace(path)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--codec-manifest", type=Path, required=True)
    parser.add_argument("--codec-manifest-sha256", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    producer_path, output = args.codec_manifest.resolve(), args.output.resolve()
    require(not output.exists() and not output.is_relative_to(ROOT), "Use a new build directory outside the checkout.")
    require(sha(producer_path) == args.codec_manifest_sha256, "Use the exact actual codec producer manifest SHA.")
    producer = json.loads(producer_path.read_text())
    require(producer["task"] == "Perf-01" and producer["source_dirty"] is False and
            producer["toolchain"]["dotnet"] == "11.0.100", "An actual clean locked codec producer is required.")
    require(sha(ROOT / "global.json") == producer["product_locks"]["global.json"], "SDK lock differs from the actual producer.")
    source_commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    require(not subprocess.check_output(["git", "status", "--porcelain", "--", str(HERE), "global.json"], cwd=ROOT),
            "Commit this profiler and SDK lock before preparation.")
    sources = {str(path): sha(path) for path in (HERE / "Program.cs", HERE / "InspectProbe.csproj", HERE / "prepare.py", HERE / "README.md", ROOT / "global.json")}
    packages = {}
    for name in PACKAGE_NAMES:
        path = producer_path.parent / "packages" / name
        expected = producer["artifacts"][str(path)]
        require(sha(path) == expected == producer["package_receipt"]["package_sha256"][name], "Package identity differs: " + name)
        packages[name] = expected
    with zipfile.ZipFile(producer_path.parent / "packages" / PACKAGE_NAMES[1]) as archive:
        package_source = json.loads(archive.read("nativeweave/source.json"))
        require(package_source["sourceCommit"] == producer["source_commit"] and
                package_source["nativeSha256"] == producer["native_sha256"], "Embedded product/native source identity differs.")
        native = archive.read("runtimes/linux-x64/native/libnativeweave_engine_syncproof.so")
        require(hashlib.sha256(native).hexdigest() == producer["native_sha256"], "Packaged native bytes differ.")
    locks = [(Path(path), value) for path, value in producer["consumer_inputs"].items()
             if Path(path).name == "packages.linux-x64.aot.lock.json"]
    require(len(locks) == 1 and sha(locks[0][0]) == locks[0][1], "Use the actual codec consumer's exact AOT dependency lock.")
    output.mkdir(parents=True)
    consumer, feed = output / "consumer", output / "feed"
    consumer.mkdir(); feed.mkdir()
    for name in ("Program.cs", "InspectProbe.csproj"):
        shutil.copy2(HERE / name, consumer / name)
    shutil.copy2(locks[0][0], consumer / "packages.linux-x64.aot.lock.json")
    shutil.copy2(ROOT / "global.json", output / "global.json")
    shutil.copy2(producer_path, output / "codec-manifest.json")
    for name, expected in packages.items():
        shutil.copy2(producer_path.parent / "packages" / name, feed / name)
        require(sha(feed / name) == expected, "Copied package differs.")
    for name in ("Directory.Build.props", "Directory.Build.targets"):
        (output / name).write_text("<Project />\n")
    config = ET.Element("configuration")
    sources_xml = ET.SubElement(config, "packageSources")
    ET.SubElement(sources_xml, "clear")
    ET.SubElement(sources_xml, "add", key="candidate", value=str(feed))
    ET.SubElement(sources_xml, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
    mapping = ET.SubElement(config, "packageSourceMapping")
    ET.SubElement(ET.SubElement(mapping, "packageSource", key="candidate"), "package", pattern="NativeWeave.*")
    ET.SubElement(ET.SubElement(mapping, "packageSource", key="nuget.org"), "package", pattern="*")
    ET.ElementTree(config).write(output / "NuGet.Config", encoding="unicode")
    trap = output / "no-rust"
    trap.mkdir()
    for name in ("cargo", "rustc", "rustup"):
        path = trap / name
        path.write_text("#!/bin/sh\necho unexpected-consumer-Rust >&2\nexit 197\n")
        path.chmod(0o755)
    environment = os.environ.copy()
    environment.update(NUGET_PACKAGES=str(output / "nuget-cache"), DOTNET_CLI_USE_MSBUILD_SERVER="0", MSBUILDDISABLENODEREUSE="1")
    environment["PATH"] = str(trap) + os.pathsep + environment["PATH"]
    record = {"task": "Perf-01", "kind": "public Inspect(null) profile consumer preparation only", "state": "preparing",
              "started_utc": datetime.now(timezone.utc).isoformat(), "product_commit": producer["source_commit"],
              "profile_source_commit": source_commit, "source_files_sha256": sources, "codec_manifest": str(producer_path),
              "codec_manifest_sha256": args.codec_manifest_sha256, "packages": packages,
              "native_sha256": producer["native_sha256"], "inherited_aot_lock_sha256": locks[0][1],
              "isolated_nuget_cache": environment["NUGET_PACKAGES"], "commands": [], "profiling_executed": False,
              "formal_timing_eligible": False, "duration_credit_seconds": 0}
    receipt = output / "manifest.json"
    save(receipt, record)
    project = consumer / "InspectProbe.csproj"
    flags = ["-r", "linux-x64", "-p:PublishAot=true", "-p:IlcInstructionSet=x86-64-v2",
             "-p:IlcTreatWarningsAsErrors=true", "-p:UseSharedCompilation=false", "-nr:false", "-m:1"]
    try:
        for label, command in (("sdk", ["dotnet", "--version"]),
                               ("restore", ["dotnet", "restore", str(project), "--locked-mode", *flags]),
                               ("publish", ["dotnet", "publish", str(project), "-c", "Release", "--no-restore", "-o", str(output / "aot"), *flags])):
            log = output / (label + ".log")
            with log.open("xb") as stream:
                completed = subprocess.run(command, cwd=output, env=environment, stdout=stream, stderr=subprocess.STDOUT)
            record["commands"].append({"argv": command, "exit_code": completed.returncode, "log_sha256": sha(log)})
            save(receipt, record)
            require(completed.returncode == 0, label + " failed; original log retained.")
            if label == "sdk":
                require(log.read_text().strip() == "11.0.100", "Use exact SDK 11.0.100.")
        require(sha(consumer / "packages.linux-x64.aot.lock.json") == locks[0][1], "Locked restore changed dependency bytes.")
        require(all(sha(Path(path)) == expected for path, expected in sources.items()), "Profiler source changed while building.")
        require(sha(producer_path) == args.codec_manifest_sha256 and all(sha(feed / name) == value for name, value in packages.items()),
                "Producer or copied packages changed while building.")
        record["artifacts"] = {str(path.relative_to(output)): sha(path) for path in sorted((output / "aot").rglob("*")) if path.is_file()}
        actual_native = [path for path in (output / "aot").rglob("libnativeweave_engine_syncproof.so")]
        require(len(actual_native) == 1 and sha(actual_native[0]) == producer["native_sha256"], "Published native asset differs.")
        require("aot/NativeWeave.Perf01.InspectProbe" in record["artifacts"], "Missing AOT executable.")
        record["consumer_inputs_sha256"] = {str(path.relative_to(output)): sha(path) for path in
            (consumer / "Program.cs", project, consumer / "packages.linux-x64.aot.lock.json", output / "global.json", output / "NuGet.Config")}
        record.update(state="built", ended_utc=datetime.now(timezone.utc).isoformat())
    except BaseException as error:
        record.update(state="failed", ended_utc=datetime.now(timezone.utc).isoformat(), error=type(error).__name__ + ": " + str(error))
        raise
    finally:
        save(receipt, record)
    print(json.dumps({"manifest": str(receipt), "sha256": sha(receipt), "profiling_executed": False}))


if __name__ == "__main__":
    main()
