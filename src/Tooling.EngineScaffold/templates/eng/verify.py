#!/usr/bin/env python3
"""Check an adopted Engine, pack it and execute its independent native consumers."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
from xml.sax.saxutils import escape


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def native_output(directory, descriptor):
    """Resolve the SDK-selected output from the mandatory A16 asset record."""
    record = json.loads((directory / ("NativeWeave." + descriptor["engine_id"] + ".assets.json")).read_text())
    expected = {"schemaVersion": 1, "engineId": descriptor["engine_id"],
                "packageId": descriptor["package"]["id"], "packageVersion": descriptor["package"]["version"],
                "rid": descriptor["native"]["rid"], "sha256": descriptor["native"]["asset"]["sha256"],
                "sourcePath": "runtimes/" + descriptor["native"]["rid"] + "/native/" + descriptor["native"]["filename"]}
    if any(record.get(k) != v for k, v in expected.items()):
        raise ValueError("The selected native output record differs from the descriptor")
    relative = record.get("outputPath")
    if not isinstance(relative, str) or not relative or "\\" in relative or ":" in relative or any(p in ("", ".", "..") for p in relative.split("/")):
        raise ValueError("The selected native output path is not canonical")
    path = (directory / relative).resolve()
    if not path.is_relative_to(directory.resolve()) or not path.is_file() or sha(path) != expected["sha256"]:
        raise ValueError("The selected native output is absent, changed or outside its output directory")
    return path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--feed", type=Path, required=True)
    parser.add_argument("--sdk-root", type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    sdk = args.sdk_root.resolve()
    manifest = json.loads((root / "scaffold-manifest.json").read_text())
    for name, expected in manifest["files"].items():
        if sha(root / name) != expected:
            raise ValueError("Unreviewed generated-source drift: " + name)
    for name, expected in manifest["sdk_tool_sha256"].items():
        if sha(sdk / name) != expected:
            raise ValueError("The pinned SDK check changed: " + name)
    adoption = json.loads((root / "public-api-standard.json").read_text())
    if sha(root / adoption["localPath"]) != adoption["sha256"]:
        raise ValueError("Adopted standard differs")
    descriptor = json.loads((root / "engine.json").read_text())
    output = root / "artifacts/conformance"
    if output.exists():
        raise ValueError("Retain this attempt; use a fresh generated scaffold for a new attempt")
    output.mkdir(parents=True)
    receipt = {"status": "running", "commands": [], "platform": "linux-x64", "pristine_machine": False,
               "scope": "Generated Engine package JIT/AOT. Pristine final closure acceptance remains A16.",
               "source_manifest_sha256": sha(root / "scaffold-manifest.json")}
    env = os.environ.copy()
    scratch = Path(tempfile.mkdtemp(prefix="nativeweave-scaffold-consumer-"))
    env["NUGET_PACKAGES"] = str(scratch / "packages")
    env["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0"
    feed = output / "feed"
    feed.mkdir()
    for file in args.feed.glob("*.nupkg"):
        # This is a new isolated candidate for the reviewed facade. Preserve the
        # source feed instead of silently overwriting a prior package identity.
        if file.name.casefold() == (descriptor["package"]["id"] + "." + descriptor["package"]["version"] + ".nupkg").casefold():
            continue
        shutil.copy2(file, feed / file.name)
    config = scratch / "NuGet.Config"
    config.write_text('<configuration><packageSources><clear/><add key="reviewed" value="' + escape(str(feed)) +
        '"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>')
    def run(label, arguments, cwd=root, expected=0):
        completed = subprocess.run([str(x) for x in arguments], cwd=cwd, env=env, capture_output=True, timeout=600)
        for suffix, value in (("stdout", completed.stdout), ("stderr", completed.stderr)):
            (output / (label + "." + suffix)).write_bytes(value)
        receipt["commands"].append({"label": label, "exit_code": completed.returncode,
            "stdout_sha256": sha(output / (label + ".stdout")), "stderr_sha256": sha(output / (label + ".stderr"))})
        if completed.returncode != expected:
            raise RuntimeError(label + " failed; retained exact output")
        return completed.stdout
    try:
        if run("sdk", ["dotnet", "--version"]).decode().strip() != "11.0.100":
            raise ValueError("Pinned SDK 11.0.100 required")
        project = root / "src/Engine/Engine.csproj"
        common = ["-p:UseSharedCompilation=false", "-nr:false", "-m:1"]
        run("restore", ["dotnet", "restore", project, "--configfile", config, *common])
        run("build", ["dotnet", "build", project, "-c", "Release", "--no-restore", *common])
        # The exact compiled/XML checker is pinned independently of this Engine.
        sdk_copy = output / "sdk-source"
        for name in manifest["sdk_tool_sha256"]:
            destination = sdk_copy / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(sdk / name, destination)
        tool = sdk_copy / "src/Build.Integration/ApiReference/ApiReference.csproj"
        run("api-tool", ["dotnet", "build", tool, "-c", "Release", *common], sdk_copy)
        run("api-xml", ["dotnet", "run", "--project", tool, "-c", "Release", "--no-build", "--", root,
            os.environ.get("DOTNET_ROOT", str(Path(shutil.which("dotnet")).resolve().parent)), root / "api-reference.json", "--write"], sdk_copy)
        run("pack", ["dotnet", "pack", project, "-c", "Release", "--no-build", "--no-restore", "-o", feed, *common])
        consumer = scratch / "Consumer"
        shutil.copytree(root / "samples/Consumer", consumer)
        shutil.copy2(root / "Directory.Build.props", scratch / "Directory.Build.props")
        shutil.copy2(root / "global.json", scratch / "global.json")
        project = consumer / "Consumer.csproj"
        traps = scratch / "rust-traps"
        traps.mkdir()
        marker = scratch / "rust-invoked"
        for name in ("cargo", "rustc", "rustup"):
            trap = traps / name
            trap.write_text('#!/bin/sh\nprintf invoked > "' + str(marker) + '"\nexit 99\n')
            trap.chmod(0o755)
        env["PATH"] = str(traps) + os.pathsep + env["PATH"]
        run("consumer-restore", ["dotnet", "restore", project, "--configfile", config, *common], scratch)
        run("consumer-build", ["dotnet", "build", project, "-c", "Release", "--no-restore", *common], scratch)
        name = descriptor["package"]["assembly"] + ".Conformance"
        run("consumer-jit", ["dotnet", consumer / ("bin/Release/net11.0/" + name + ".dll")], scratch)
        native = native_output(consumer / "bin/Release/net11.0", descriptor)
        before = sha(native)
        if before != descriptor["native"]["asset"]["sha256"]:
            raise ValueError("Restored native bytes differ")
        (consumer / "AddedDto.cs").write_text("internal sealed record AddedDto(int Id, string Name);\n")
        run("consumer-managed-edit", ["dotnet", "build", project, "-c", "Release", "--no-restore", *common], scratch)
        if sha(native) != before or marker.exists():
            raise ValueError("Ordinary managed edit rebuilt native code")
        run("consumer-aot-publish", ["dotnet", "publish", project, "-c", "Release", "-r", "linux-x64", "-p:PublishAot=true",
            "--configfile", config, "-o", output / "aot", *common], scratch)
        env["NATIVEWEAVE_REQUIRE_AOT"] = "1"
        run("consumer-aot-execute", [output / "aot" / name, "--require-aot"], scratch)
        if marker.exists() or sha(native_output(output / "aot", descriptor)) != before:
            raise ValueError("Native package changed or Rust was invoked")
        receipt.update(status="passed", native_sha256=before,
            executable_sha256=sha(output / "aot" / name), managed_edit_native_unchanged=True,
            packages={file.name: sha(file) for file in feed.glob("*.nupkg")}, rust_invoked=False)
        receipt["consumer_source"] = {file.name: sha(file) for file in consumer.iterdir() if file.is_file()}
        snapshot = output / "independent-consumer"
        snapshot.mkdir()
        for file in consumer.iterdir():
            if file.is_file(): shutil.copy2(file, snapshot / file.name)
        shutil.copy2(config, snapshot / config.name)
    except BaseException as error:
        receipt.update(status="failed", error=str(error))
        raise
    finally:
        (output / "result.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print(json.dumps({"status": receipt["status"], "receipt": str(output / "result.json")}))


if __name__ == "__main__":
    main()
