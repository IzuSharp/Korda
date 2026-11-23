"""Compose public provider packages from verified component bytes; never publish here."""

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

from impact import IDS, PROVIDERS
from provider_metadata import render

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/Packaging.Domain"))
from model import DATABASE_CAPABILITIES, REPOSITORY, RID, json_bytes, module, save, sha
from model import MANAGED
from prepare import TASK_PROJECT, inventory, managed_entries, managed_project

compat = module("compatibility", "src/Verification.Compatibility/compatibility.py")
packer = module("linux_packages", "src/Packaging.Linux/packages.py")
GENERATOR = "Korda.Query.Generator"
BUILD_TASK = "Korda.Packaging.Tasks"
OUT = Path("artifacts/public")


def shared_files(entries):
    private = {f"lib/net11.0/{identity}{ext}" for identity in IDS.values() for ext in (".dll", ".xml")}
    return {n: v for n, v in entries.items() if n.startswith(("lib/", "tools/")) and n not in private}


def compile_project(project, version, extra=()):
    subprocess.run(["dotnet", "restore", project, "--locked-mode", *extra], check=True)
    subprocess.run(
        [
            "dotnet",
            "build",
            project,
            "-c",
            "Release",
            "--no-restore",
            f"-p:Version={version}",
            *extra,
        ],
        check=True,
    )


def selected_files(provider, version, manifest):
    identity = IDS[provider]
    entries = {}
    compile_project(TASK_PROJECT, version)
    for suffix in (".dll", ".xml"):
        filename = BUILD_TASK + suffix
        entries["tools/net11.0/" + filename] = (
            TASK_PROJECT.parent / "bin/Release/net11.0" / filename
        ).read_bytes()
    build, assets = inventory.collect(manifest, provider)
    asset = assets["database." + provider]
    name = "runtimes/linux-x64/native/" + asset["path"].name
    entries.update(asset["entries"])
    entries[name] = asset["path"].read_bytes()
    prop = "KordaSqliteAssetPath" if provider == "sqlite" else "KordaPostgresAssetPath"
    compile_project(managed_project(identity), version, [f"-p:{prop}={asset['path']}"])
    # The facade builds its project references; the source generator is packaged as a tool.
    for component in sorted((set(MANAGED) - set(IDS.values())) | {identity}):
        for entry, file in managed_entries(component).items():
            if entry.startswith(("lib/", "tools/")):
                entries[entry] = file.read_bytes()
    native = dict(path=name, sha256=asset["sha256"], rid=RID, abi_major=1)
    entries["korda/manifest.json"] = json_bytes(
        dict(
            schemaVersion=1,
            packageId=identity,
            packageVersion=version,
            rid=RID,
            nativePath=name,
            nativeSha256=asset["sha256"],
            abiMajor=1,
            sourceCommit=build["sourceCommit"],
            sourceDirty=False,
        )
    )
    return entries, native


def compose(provider, version, entries, native, commit, fingerprint, payload_fingerprint):
    identity = IDS[provider]
    for notice in ("LICENSE", "THIRD_PARTY_NOTICES.md"):
        entries[notice] = (ROOT / notice).read_bytes()
    entries["tools/Korda.Packaging.targets"] = (
        ROOT / "src/Packaging.Release/buildTransitive/Korda.Packaging.targets"
    ).read_bytes()
    entries["tools/" + GENERATOR + ".targets"] = (
        ROOT / f"src/Query.Generator/buildTransitive/{GENERATOR}.targets"
    ).read_bytes()
    entries["korda/public.json"] = json_bytes({"inputs": fingerprint, "payload_inputs": payload_fingerprint})
    descriptor = dict(
        engineId="database." + provider,
        facadePackageId=identity,
        facadePackageVersion=version,
        rid=RID,
        sourcePath=native["path"],
        sha256=native["sha256"],
        abiMajor=1,
        abiMinor=0,
        cpuBaseline="x86-64",
        capabilities={cap: bit for bit, cap in enumerate(DATABASE_CAPABILITIES)},
    )
    entries["korda/closure.json"] = json_bytes(
        {
            "schemaVersion": 1,
            "packages": [
                dict(
                    id=identity,
                    version=version,
                    dependencies={},
                    nativeAssets=[descriptor],
                )
            ],
        }
    )
    original = json.loads(entries["korda/manifest.json"])
    native = {**native, "source_commit": original["sourceCommit"]}
    original.update(packageId=identity, packageVersion=version, packagingProducerCommit=commit)
    entries["korda/manifest.json"] = json_bytes(original)
    shared = {n: hashlib.sha256(v).hexdigest() for n, v in shared_files(entries).items()}
    digest = hashlib.sha256(json_bytes(shared)).hexdigest()
    wrapper = (ROOT / "eng/ci/provider.targets").read_text()
    entries[f"buildTransitive/{identity}.targets"] = (
        wrapper.replace("@PROVIDER@", identity).replace("@SHARED@", digest).encode()
    )
    description, entries["README.md"] = render(provider, identity, version)
    path = packer.pack(
        OUT / "feed",
        identity,
        version,
        entries,
        repository=(REPOSITORY, commit),
        description=description,
        readme="README.md",
    )
    return dict(
        id=identity,
        version=version,
        source_commit=commit,
        repository=REPOSITORY,
        dependencies={},
        native=native,
        upload_archive_sha256=sha(path),
        unsigned_entries=compat.package_entries(path),
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plan", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--provider", choices=PROVIDERS, required=True)
    parser.add_argument("--native", type=Path, required=True)
    args = parser.parse_args()
    plan = json.loads(args.plan.read_text())
    provider = args.provider
    if provider not in plan["public_checks"] or args.version != plan["versions"][provider]:
        raise ValueError("Package or version differs from the verification plan")
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
    tree = subprocess.check_output(["git", "rev-parse", "HEAD^{tree}"], text=True).strip()
    entries, native = selected_files(provider, args.version, args.native)
    records = [
        compose(
            provider,
            args.version,
            entries,
            native,
            commit,
            plan["inputs"][provider],
            plan["payload_inputs"][provider],
        )
    ]
    record = dict(
        schema_version=1,
        source_commit=commit,
        source_tree=tree,
        rid=RID,
        source_dirty=False,
        packages=records,
        publish=plan["packages"],
        inputs=plan["inputs"],
    )
    save(OUT / "release.json", record)
    compat.validate_closure(
        record,
        {p["id"]: OUT / "feed" / f"{p['id']}.{p['version']}.nupkg" for p in records},
    )


if __name__ == "__main__":
    main()
