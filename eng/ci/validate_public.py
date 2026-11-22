"""Validate the public allowlist, provenance and exact package payload before NuGet login."""
import argparse
import json
from pathlib import Path
import subprocess
from bundles import compat
from impact import IDS, inputs
import xml.etree.ElementTree as ET
import zipfile


def validate(folder, plan, version):
    tree = subprocess.check_output(["git", "rev-parse", "HEAD^{tree}"], text=True).strip()
    record = compat.read_json(folder / "release.json")
    expected = {IDS[name] for name in plan["packages"]}
    if record["source_tree"] != tree or record["inputs"] != inputs("HEAD") or record["source_dirty"] is not False:
        raise ValueError("Provider artifact is for a different source tree")
    packages = {p["id"]: p for p in record["packages"]}
    if not expected or set(packages) != expected or len(packages) != len(record["packages"]):
        raise ValueError("Provider selection differs from the main release plan")
    paths = {}
    for name, item in packages.items():
        if item["dependencies"] or item["version"] != version or item["source_commit"] != record["source_commit"]:
            raise ValueError("Unexpected version, source or external component dependencies")
        path = folder / "feed" / f"{name}.{version}.nupkg"
        if compat.file_hash(path) != item["upload_archive_sha256"]:
            raise ValueError("Candidate changed after consumption")
        provider = next(p for p, identity in IDS.items() if identity == name)
        with zipfile.ZipFile(path) as archive:
            if json.loads(archive.read("nativeweave/public.json"))["inputs"] != record["inputs"][provider]:
                raise ValueError("Packaged provider fingerprint differs from the tested inputs")
            if ET.fromstring(archive.read(name + ".nuspec")).find(".//{*}dependency") is not None:
                raise ValueError("Public provider requires another package")
        paths[name] = path
    if set(paths.values()) != set((folder / "feed").iterdir()):
        raise ValueError("Unexpected file in publication feed")
    compat.validate_closure(record, paths)
    return paths


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("plan", type=Path)
    parser.add_argument("version")
    args = parser.parse_args()
    for path in validate(args.directory, json.loads(args.plan.read_text()), args.version).values():
        print(path)
