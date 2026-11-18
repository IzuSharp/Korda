"""Focused actual domain closure negatives, using the shipped A16 task.

These are invoked only after the new combined package graph actually executes.
Existing release_cases.py retains publisher/signature/rollback ownership.
"""
from __future__ import annotations

import copy
import json
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET

from model import relative_file, save, sha

FLAGS = ["-p:UseSharedCompilation=false", "-nr:false", "-m:1"]


def run_negatives(consumer, directory, published):
    c = consumer
    root = c.work / "negative-cases"
    root.mkdir()
    env = c.environment_for(directory)
    def record(name, **details):
        c.result["negativeCases"].append({"name": name, "passed": True, **details})
        c.flush()

    # Fresh processes: an already mapped resident module cannot exercise a new
    # load/ABI check, and process termination is never counted as owner proof.
    executable = published / "B24Consumer"
    runtime_env = env.copy()
    runtime_env.update(B24_PROVIDER="sqlite", B24_DATABASE=str(root / "never-opened.sqlite.db"))
    output = c.run("negative-actual-domain-bootstrap", [executable, "--require-aot", "--reject-abi"], env=runtime_env)
    if "B24_ABI_REJECTED_BEFORE_DOMAIN_OWNER" not in output or (root / "never-opened.sqlite.db").exists():
        raise ValueError("Actual incompatible bootstrap did not precede domain creation")
    record("actual-domain-bootstrap-incompatible-abi", requiredMajor=99, domainOwnerCreated=False,
           mechanism="Real SQLite bootstrap through existing public Core descriptor; exact actual domain bytes, no native fixture or FFI implementation.")
    asset_file = published / "NativeWeave.database.sqlite.assets.json"
    record_bytes = asset_file.read_bytes()
    native = relative_file(published, json.loads(record_bytes)["outputPath"])
    native_bytes = native.read_bytes()
    try:
        asset_file.unlink()
        c.run("negative-missing-output-record", [executable, "--require-aot"], env=runtime_env, reject="NWPKG002")
        record("missing-domain-output-record", rejectionCode="NWPKG002")
        asset_file.write_bytes(record_bytes)
        native.write_bytes(native_bytes[:-1] + bytes([native_bytes[-1] ^ 1]))
        c.run("negative-runtime-native-hash", [executable, "--require-aot"], env=runtime_env, reject="do not match the package SHA-256")
        record("runtime-native-content-tamper", hashCheckedBeforeDomainOwner=True)
    finally:
        asset_file.write_bytes(record_bytes)
        native.write_bytes(native_bytes)
    c.native_identity("combined", published)

    # A direct owned override must fail the actual build even after suppressing
    # NuGet's warning. Each restore has a fresh cache, with exact source mapping.
    previous_version = next(item["version"] for item in c.manifest["previous"]["packages"] if item["id"] == "NativeWeave.Database")
    for suppressed in (False, True):
        name = "override-suppressed" if suppressed else "override-visible"
        negative = root / name
        c.template("combined", negative, override=("NativeWeave.Database", previous_version))
        neg_env = c.environment_for(negative)
        flags = [*FLAGS, "-p:TreatWarningsAsErrors=false", *(["-p:NoWarn=NU1608%3BNU1605"] if suppressed else [])]
        text = c.run("negative-" + name + "-restore", ["dotnet", "restore", negative / "Consumer.csproj", *flags], cwd=negative, env=neg_env)
        if not suppressed and "NU1608" not in text:
            raise ValueError("Direct override warning was not actually witnessed")
        c.run("negative-" + name + "-build", ["dotnet", "build", negative / "Consumer.csproj", "--no-restore", *flags],
              cwd=negative, env=neg_env, reject="NWPKG001")
        record(name, rejectionCode="NWPKG001", freshRestoreCache=True, noWarnSuppressed=suppressed)

    # Missing native leaf at actual NuGet resolution: all other destination
    # archives remain exact, and mapping forbids fallback to a public ID.
    from consumer_cases import config
    incomplete_feed = root / "incomplete-feed"
    incomplete_feed.mkdir()
    excluded = c.packages["NativeWeave.Database.Sqlite.Native"].name
    for source in c.feed.glob("*.nupkg"):
        if source.name != excluded:
            shutil.copyfile(source, incomplete_feed / source.name)
    missing = root / "missing-leaf"
    c.template("sqlite", missing)
    config(missing / "NuGet.Config", incomplete_feed)
    c.run("negative-missing-native-leaf-restore", ["dotnet", "restore", missing / "Consumer.csproj", *FLAGS],
          cwd=missing, env=c.environment_for(missing), reject=("NU1101", "NU1102"))
    record("missing-exact-native-leaf-at-restore", exactLeafOmitted=True, freshRestoreCache=True)

    # Exercise the shipped closure task against this actual domain graph and
    # restored cache. Only these negative fixture copies are edited.
    closure_path = relative_file(c.manifest_path.parent, c.manifest[c.closure]["buildClosure"])
    closure = json.loads(closure_path.read_text())
    assets_path = directory / "obj/project.assets.json"
    task = directory / "cache/nativeweave.database" / c.items["NativeWeave.Database"]["version"].lower() / "tools/net11.0/NativeWeave.Packaging.Tasks.dll"
    def msbuild(name, code=None, *, rid="linux-x64", omit=None, tamper=False):
        work = root / name
        work.mkdir()
        copied = work / "closure.json"
        save(copied, closure)
        project = ET.Element("Project")
        ET.SubElement(project, "UsingTask", TaskName="NativeWeave.Packaging.PackageClosure", AssemblyFile=str(task))
        items = ET.SubElement(project, "ItemGroup")
        for item in closure["packages"]:
            for asset in item.get("nativeAssets", []):
                if item["id"] == omit:
                    continue
                selected = directory / "cache" / item["id"].lower() / item["version"].lower() / asset["sourcePath"]
                if tamper and asset["engineId"] == "database.sqlite":
                    altered = work / "tampered" / asset["sourcePath"]
                    altered.parent.mkdir(parents=True)
                    altered.write_bytes(selected.read_bytes() + b"domain-negative-hash")
                    selected = altered
                element = ET.SubElement(items, "SelectedNative", Include=str(selected))
                ET.SubElement(element, "NuGetPackageId").text = item["id"]
                ET.SubElement(element, "PathInPackage").text = asset["sourcePath"]
                ET.SubElement(element, "DestinationSubPath").text = Path(asset["sourcePath"]).name
        target = ET.SubElement(project, "Target", Name="Check")
        ET.SubElement(target, "PackageClosure", ClosureFiles=str(copied), AssetsFile=str(assets_path),
                      RuntimeIdentifier=rid, Mode="build", OutputDirectory=str(work / "output"), ResolvedFiles="@(SelectedNative)")
        path = work / "Check.proj"
        ET.ElementTree(project).write(path, encoding="utf-8", xml_declaration=True)
        c.run("negative-task-" + name, ["dotnet", "msbuild", path, "-t:Check", "-nologo", "-nr:false", "-v:minimal"], env=env, reject=code)
        record(name, rejectionCode=code, actualShippedTaskSha256=sha(task), newDomainClosureSha256=sha(closure_path),
               actualRestoredAssetsSha256=sha(assets_path))
    msbuild("valid-domain-task-control")
    msbuild("missing-selected-domain-native", "NWPKG002", omit="NativeWeave.Database.Sqlite.Native")
    msbuild("unsupported-domain-rid", "NWPKG003", rid="linux-arm64")
    msbuild("domain-native-hash-before-compile", "NWPKG004", tamper=True)
