#!/usr/bin/env python3
"""Exercise immutable staged publication, real destination restore and full rollback."""
from __future__ import annotations

import argparse
import copy
import functools
import http.server
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("release", ROOT / "src/Packaging.Release/release.py")
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepared", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--offline-sdk-cache", type=Path)
    parser.add_argument("--signed-package", type=Path)
    args = parser.parse_args()
    prepared, output = args.prepared.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    preparation = json.loads((prepared / "preparation.json").read_text())
    cases = []
    receipts = {}
    def record(name, result):
        cases.append({"name": name, **result})
        release.atomic_json(output / "result.json", {"passed": all(x.get("passed") is True for x in cases), "cases": cases,
            "source_commit": preparation["source_commit"], "source_dirty": preparation["source_dirty"],
            "scope": "Linux local immutable destination adapter and real signature readback; public package publication unchanged"})
        print(name, result.get("passed"), flush=True)
    def reject(name, action, expected):
        try:
            action()
        except (release.compat.Rejected, FileNotFoundError) as error:
            if expected not in str(error):
                raise
            record(name, {"passed": True, "error": str(error)})
        else:
            raise AssertionError("Expected rejected case: " + name)
    def actual_consume(version, destination, name):
        result_dir = output / name
        command = ["python3", ROOT / "tests/Packaging.Release/consume.py", "--prepared", prepared, "--output", result_dir,
                   "--version", version, "--feed", destination]
        if args.offline_sdk_cache:
            command.extend(["--offline-sdk-cache", args.offline_sdk_cache])
        result = subprocess.run(list(map(str, command)), capture_output=True, timeout=600)
        (output / (name + ".stdout")).write_bytes(result.stdout)
        (output / (name + ".stderr")).write_bytes(result.stderr)
        if result.returncode:
            raise RuntimeError(result.stderr.decode(errors="replace")[-3500:] + result.stdout.decode(errors="replace")[-1500:])
        receipt = json.loads((result_dir / "result.json").read_text())
        receipts[version] = receipt
        return receipt
    records = [json.loads((prepared / ("closure-" + version + ".json")).read_text()) for version in ["0.1.0-task04.1", "0.1.0-task04.2"]]
    for manifest in records:
        if manifest["source_dirty"]:
            raise ValueError("Final immutable staging tests require a committed clean source identity")
    destination = output / "destination"
    previous = records[0]
    restore_missing_leaf(previous, prepared / "feed", output, record)
    attempted = []
    def interrupt(phase, item):
        attempted.append(item["id"])
        if len(attempted) == 2:
            raise InterruptedError("controlled upload interruption")
    journal, catalog = output / "journal.json", output / "catalog.json"
    try:
        release.publish(previous, prepared / "feed", destination, journal, catalog,
                        release.verify_local, lambda *_: {}, interrupt)
    except InterruptedError:
        if catalog.exists() or json.loads(journal.read_text())["status"] != "partial-unadvertised":
            raise AssertionError("Interrupted uploads advertised a complete release")
        record("partial-publication-unadvertised", {"passed": True, "uploaded_leaves": attempted})
    else:
        raise AssertionError("Controlled interruption did not execute")
    resumed = release.publish(previous, prepared / "feed", destination, journal, catalog, release.verify_local,
        lambda record, paths: actual_consume("0.1.0-task04.1", destination, "previous-destination-consumer"))
    record("resume-and-actual-complete-consumption", {"passed": True, "package_count": len(resumed["packages"]),
            "journal_sha256": release.compat.file_hash(journal), "catalog_sha256": release.compat.file_hash(catalog)})
    again = release.publish(previous, prepared / "feed", destination, output / "existing-journal.json", output / "existing-catalog.json",
                            release.verify_local, lambda *_: receipts["0.1.0-task04.1"])
    record("matching-existing-version-readback", {"passed": True, "package_count": len(again["packages"]),
            "consumption_reuse": "exact same package bytes and already-executed destination closure"})
    new_result = release.publish(records[1], prepared / "feed", destination, output / "new-journal.json", output / "new-catalog.json",
        release.verify_local, lambda record, paths: actual_consume("0.1.0-task04.2", destination, "candidate-destination-consumer"))
    record("candidate-complete-closure", {"passed": True, "package_count": len(new_result["packages"])})
    package_map = {item["id"]: destination / f"{item['id']}.{item['version']}.nupkg" for item in previous["packages"]}
    rollback = release.rollback(previous, package_map, receipts["0.1.0-task04.1"], 0)
    record("previous-complete-closure-rollback", {"passed": rollback["complete"], "deployment": rollback["deployment"]})
    reject("live-process-rollback", lambda: release.rollback(previous, package_map, receipts["0.1.0-task04.1"], 1), "NWPKG017")
    incomplete = dict(package_map)
    incomplete.pop(next(iter(incomplete)))
    reject("incomplete-rollback", lambda: release.rollback(previous, incomplete, receipts["0.1.0-task04.1"], 0), "NWPKG011")
    unknown = copy.deepcopy(previous)
    unknown["builder_identity"] = "unknown-builder"
    reject("unknown-builder", lambda: release.publish(unknown, prepared / "feed", output / "unknown", output / "unknown.json", output / "unknown-catalog.json", release.verify_local, lambda *_: {}), "NWPKG015")
    wrong_subject = copy.deepcopy(previous)
    wrong_subject["packages"][0]["upload_archive_sha256"] = "0" * 64
    reject("wrong-provenance-subject", lambda: release.publish(wrong_subject, prepared / "feed", output / "subject", output / "subject.json", output / "subject-catalog.json", release.verify_local, lambda *_: {}), "NWPKG004")
    conflicts = output / "conflicting-destination"
    shutil.copytree(destination, conflicts)
    selected = previous["packages"][0]
    conflict = conflicts / f"{selected['id']}.{selected['version']}.nupkg"
    conflict.write_bytes(conflict.read_bytes() + b"different immutable archive")
    reject("same-version-conflict", lambda: release.publish(previous, prepared / "feed", conflicts, output / "conflict.json", output / "conflict-catalog.json", release.verify_local, lambda *_: {}), "NWPKG004")
    if (output / "conflict-catalog.json").exists():
        raise AssertionError("Conflicting release was advertised")
    # Withdraw only catalog exposure. The following local V3 fixture marks its
    # registration unlisted but continues serving immutable known-version bytes.
    catalog.unlink()
    restore_unlisted(previous, destination, output, record)
    if args.signed_package:
        signed = args.signed_package.resolve()
        with zipfile.ZipFile(signed) as archive:
            nuspec = next(name for name in archive.namelist() if name.endswith(".nuspec"))
            metadata = ET.fromstring(archive.read(nuspec)).find("{*}metadata")
            identity = metadata.findtext("{*}id")
            version = metadata.findtext("{*}version")
        expected = {"id": identity, "version": version, "dependencies": {}, "unsigned_entries": release.compat.package_entries(signed)}
        unsigned = output / "candidate-unsigned.nupkg"
        with zipfile.ZipFile(signed) as source, zipfile.ZipFile(unsigned, "w") as target:
            for item in source.infolist():
                if item.filename != ".signature.p7s":
                    target.writestr(item, source.read(item))
        policy = json.loads((ROOT / "tests/Verification.Compatibility/signature-policy.json").read_text())
        actual = release.verify_registry(unsigned, signed, expected, policy, output / "signature-readback")
        record("actual-repository-signature-envelope", {"passed": actual["verified"], "upload_sha256": actual["upload_sha256"],
                "registry_sha256": actual["registry_sha256"], "unsigned_entries_equal": actual["unsigned_entries_equal"],
                "scope": "New coordinator integration over previously accepted signed bytes; no old consumer rerun"})
        altered = output / "changed-license.nupkg"
        with zipfile.ZipFile(signed) as source, zipfile.ZipFile(altered, "w") as target:
            for item in source.infolist():
                data = source.read(item)
                if item.filename == nuspec:
                    document = ET.fromstring(data)
                    metadata_node = document.find("{*}metadata")
                    ET.SubElement(metadata_node, "tags").text = "changed-by-a16-negative"
                    data = ET.tostring(document, encoding="utf-8", xml_declaration=True)
                target.writestr(item, data)
        reject("changed-signed-nuspec", lambda: release.verify_registry(unsigned, altered, expected, policy, output / "changed-signature"), "NWPKG013")
        wrong_policy = copy.deepcopy(policy)
        wrong_policy["certificate_sha256"] = "0" * 64
        reject("unexpected-signer", lambda: release.verify_registry(unsigned, signed, expected, wrong_policy, output / "wrong-signer"), "NWPKG015")


def restore_missing_leaf(manifest, feed, output, record):
    missing = next(item for item in manifest["packages"] if "native" in item)
    incomplete_feed = output / "incomplete-feed"
    incomplete_feed.mkdir()
    for item in manifest["packages"]:
        if item is not missing:
            name = f"{item['id']}.{item['version']}.nupkg"
            shutil.copy2(feed / name, incomplete_feed / name)
    consumer = Path(tempfile.mkdtemp(prefix="nativeweave-a16-missing-leaf-"))
    project = consumer / "Consumer.csproj"
    project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="NativeWeave.Conformance.Packaging" Version="[0.1.0-task04.1]"/></ItemGroup></Project>')
    config = consumer / "NuGet.Config"
    document = ET.Element("configuration")
    sources = ET.SubElement(document, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="missing-leaf-fixture", value=str(incomplete_feed))
    ET.ElementTree(document).write(config, encoding="utf-8", xml_declaration=True)
    env = os.environ.copy()
    env["NUGET_PACKAGES"] = str(consumer / "cache")
    result = subprocess.run(["dotnet", "restore", project, "--configfile", config, "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-nr:false"],
                            cwd=consumer, env=env, capture_output=True, timeout=120)
    (output / "missing-leaf.stdout").write_bytes(result.stdout)
    (output / "missing-leaf.stderr").write_bytes(result.stderr)
    diagnostic = (result.stdout + result.stderr).decode(errors="replace")
    if result.returncode == 0 or "NU1101" not in diagnostic or missing["id"] not in diagnostic:
        raise AssertionError("A real fresh-cache restore did not reject the missing native leaf: " + diagnostic)
    record("missing-native-leaf-restore", {"passed": True, "exit": result.returncode,
           "missing": missing["id"], "fresh_cache": True, "actual_nuget_restore": True})


def restore_unlisted(manifest, destination, output, record):
    webroot = output / "unlisted-v3"
    webroot.mkdir()
    versions = {}
    for item in manifest["packages"]:
        name, version = item["id"].lower(), item["version"]
        folder = webroot / "flat" / name / version
        folder.mkdir(parents=True)
        shutil.copy2(destination / f"{item['id']}.{version}.nupkg", folder / f"{name}.{version}.nupkg")
        versions.setdefault(name, []).append(version)
    for name, values in versions.items():
        release.atomic_json(webroot / "flat" / name / "index.json", {"versions": values})
    requests = []
    class Handler(http.server.SimpleHTTPRequestHandler):
        def log_message(self, format, *args):
            requests.append(self.path)
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(Handler, directory=str(webroot)))
    base = f"http://127.0.0.1:{server.server_port}/"
    release.atomic_json(webroot / "index.json", {"version": "3.0.0", "resources": [
        {"@id": base + "flat/", "@type": "PackageBaseAddress/3.0.0"},
        {"@id": base + "registration/", "@type": "RegistrationsBaseUrl/3.6.0"}]})
    for item in manifest["packages"]:
        folder = webroot / "registration" / item["id"].lower()
        release.atomic_json(folder / "index.json", {"count": 1, "items": [{"count": 1, "items": [{
            "catalogEntry": {"id": item["id"], "version": item["version"], "listed": False},
            "packageContent": base + f"flat/{item['id'].lower()}/{item['version']}/{item['id'].lower()}.{item['version']}.nupkg"}]}]})
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        consumer = Path(tempfile.mkdtemp(prefix="nativeweave-a16-unlisted-"))
        project = consumer / "Consumer.csproj"
        project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="NativeWeave.Conformance.Packaging" Version="[0.1.0-task04.1]"/></ItemGroup></Project>')
        config = consumer / "NuGet.Config"
        config.write_text(f'<configuration><packageSources><clear/><add key="loopback-test" value="{base}index.json" allowInsecureConnections="true"/></packageSources></configuration>')
        env = os.environ.copy()
        env["NUGET_PACKAGES"] = str(consumer / "cache")
        result = subprocess.run(["dotnet", "restore", project, "--configfile", config, "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-nr:false"], cwd=consumer, env=env, capture_output=True, timeout=180)
        (output / "unlisted-restore.stdout").write_bytes(result.stdout)
        (output / "unlisted-restore.stderr").write_bytes(result.stderr)
        if result.returncode:
            raise RuntimeError(result.stdout.decode() + result.stderr.decode())
        packages = {item["id"]: consumer / "cache" / item["id"].lower() / item["version"] / f"{item['id'].lower()}.{item['version']}.nupkg" for item in manifest["packages"]}
        release.compat.validate_closure(manifest, packages, json.loads((consumer / "obj/project.assets.json").read_text()))
        record("unlisted-known-version-restore", {"passed": True, "actual_nuget_restore": True, "fresh_cache": True,
              "packages": {name: release.compat.file_hash(path) for name, path in packages.items()},
              "scope": "Loopback NuGet V3 protocol fixture with listed=false and complete immutable package bytes; no public package was unlisted"})
        release.atomic_json(output / "unlisted-http-requests.json", requests)
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)


if __name__ == "__main__":
    main()
