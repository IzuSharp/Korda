"""Compose public provider packages from verified component bytes; never publish here."""

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import zipfile

from impact import IDS, PROVIDERS
from registry import package

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/Packaging.Domain"))
from model import DATABASE_CAPABILITIES, REPOSITORY, RID, json_bytes, module, save, sha
from model import MANAGED
from prepare import TASK_PROJECT, inventory, managed_entries, managed_project

compat = module("compatibility", "src/Verification.Compatibility/compatibility.py")
packer = module("linux_packages", "src/Packaging.Linux/packages.py")
GENERATOR = "NativeWeave.Query.Generator"
BUILD_TASK = "NativeWeave.Packaging.Tasks"
OUT = Path("artifacts/public")
POLICY = ROOT / "tests/Verification.Compatibility/signature-policy.json"


def payload(path):
    with zipfile.ZipFile(path) as archive:
        return {
            name: archive.read(name)
            for name in archive.namelist()
            if not (
                name.endswith(".nuspec") or name in ("[Content_Types].xml", "_rels/.rels", ".signature.p7s")
            )
        }


def published(provider, version):
    cache = Path("artifacts/published") / provider
    cache.mkdir(parents=True, exist_ok=True)
    path = cache / f"{IDS[provider]}.{version}.nupkg"
    if not path.exists():
        path.write_bytes(package(IDS[provider], version))
    compat.verify_signature(path, compat.read_json(POLICY), cache)
    return payload(path)


def shared_files(entries):
    private = {f"lib/net11.0/{identity}{ext}" for identity in IDS.values() for ext in (".dll", ".xml")}
    return {n: v for n, v in entries.items() if n.startswith(("lib/", "tools/")) and n not in private}


def component_files(candidate, provider):
    record = compat.read_json(candidate / "release.json")
    index = {p["id"]: p for p in record["packages"]}
    paths = {n: candidate / "feed" / f"{n}.{p['version']}.nupkg" for n, p in index.items()}
    compat.validate_closure(record, paths)
    identity = IDS[provider]
    leaf = index[identity + ".Native"]
    contents = payload(paths[identity + ".Native"])
    for name in index:
        if name in (p for p in IDS.values() if p != identity) or name.endswith(".Native"):
            continue
        for entry, data in payload(paths[name]).items():
            if entry.startswith(("lib/", "tools/")):
                contents[entry] = data
    return contents, leaf["native"]


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


def selected_files(provider, plan, version, manifest):
    identity = IDS[provider]
    peer = next(p for p in PROVIDERS if p != provider)
    head = plan["published"][peer]
    if head.get("inputs"):
        entries = shared_files(published(peer, head["version"]))
    else:
        # First bundled release: bootstrap shared assets locally, even when the other provider has never shipped.
        entries = {}
        compile_project(TASK_PROJECT, version)
        for suffix in (".dll", ".xml"):
            filename = BUILD_TASK + suffix
            entries["tools/net11.0/" + filename] = (
                TASK_PROJECT.parent / "bin/Release/net11.0" / filename
            ).read_bytes()
        for identity_shared in sorted(set(MANAGED) - set(IDS.values())):
            compile_project(managed_project(identity_shared), version)
            entries.update(
                {
                    name: file.read_bytes()
                    for name, file in managed_entries(identity_shared).items()
                    if name.startswith(("lib/", "tools/"))
                }
            )

    build, assets = inventory.collect(manifest, provider)
    asset = assets["database." + provider]
    name = "runtimes/linux-x64/native/" + asset["path"].name
    entries.update(asset["entries"])
    entries[name] = asset["path"].read_bytes()
    prop = "NativeWeaveSqliteAssetPath" if provider == "sqlite" else "NativeWeavePostgresAssetPath"
    compile_project(managed_project(identity), version, [f"-p:{prop}={asset['path']}"])
    for entry, file in managed_entries(identity).items():
        entries[entry] = file.read_bytes()
    native = dict(path=name, sha256=asset["sha256"], rid=RID, abi_major=1)
    entries["nativeweave/manifest.json"] = json_bytes(
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


def compose(provider, version, entries, native, commit, fingerprint):
    identity = IDS[provider]
    for notice in ("LICENSE", "THIRD_PARTY_NOTICES.md"):
        entries[notice] = (ROOT / notice).read_bytes()
    entries["tools/NativeWeave.Packaging.targets"] = (
        ROOT / "src/Packaging.Release/buildTransitive/NativeWeave.Packaging.targets"
    ).read_bytes()
    entries["tools/" + GENERATOR + ".targets"] = (
        ROOT / f"src/Query.Generator/buildTransitive/{GENERATOR}.targets"
    ).read_bytes()
    entries["nativeweave/public.json"] = json_bytes({"inputs": fingerprint})
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
    entries["nativeweave/closure.json"] = json_bytes(
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
    original = json.loads(entries["nativeweave/manifest.json"])
    native = {**native, "source_commit": original["sourceCommit"]}
    original.update(packageId=identity, packageVersion=version, packagingProducerCommit=commit)
    entries["nativeweave/manifest.json"] = json_bytes(original)
    shared = {n: hashlib.sha256(v).hexdigest() for n, v in shared_files(entries).items()}
    digest = hashlib.sha256(json_bytes(shared)).hexdigest()
    wrapper = (ROOT / "eng/ci/provider.targets").read_text()
    entries[f"buildTransitive/{identity}.targets"] = (
        wrapper.replace("@PROVIDER@", identity).replace("@SHARED@", digest).encode()
    )
    path = packer.pack(OUT / "feed", identity, version, entries, repository=(REPOSITORY, commit))
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
    parser.add_argument("--components", type=Path)
    parser.add_argument("--native", type=Path)
    args = parser.parse_args()
    plan = json.loads(args.plan.read_text())
    names = plan["packages"] or ([plan["scope"]] if plan["scope"] in PROVIDERS else PROVIDERS)
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
    tree = subprocess.check_output(["git", "rev-parse", "HEAD^{tree}"], text=True).strip()
    records = []
    if plan["rebuild_task"] and not args.components and not args.native:
        compile_project(TASK_PROJECT, args.version)
    for provider in names:
        if args.components:
            entries, native = component_files(args.components, provider)
            # An independent release must keep its peer's shared assembly identities.
            if len(names) == 1:
                peer = next(p for p in PROVIDERS if p != provider)
                if plan["published"][peer].get("inputs"):
                    entries.update(shared_files(published(peer, plan["published"][peer]["version"])))
        elif args.native:
            entries, native = selected_files(provider, plan, args.version, args.native)
        else:
            entries = published(provider, plan["published"][provider]["version"])
            info = json.loads(entries["nativeweave/manifest.json"])
            native = dict(
                path=info["nativePath"],
                sha256=info["nativeSha256"],
                rid=RID,
                abi_major=1,
            )
            # Packaging-only changes can update the host task without rebuilding the native library.
            if plan["rebuild_task"]:
                for suffix in (".dll", ".xml"):
                    name = BUILD_TASK + suffix
                    entries["tools/net11.0/" + name] = (
                        TASK_PROJECT.parent / "bin/Release/net11.0" / name
                    ).read_bytes()
        records.append(
            compose(
                provider,
                args.version,
                entries,
                native,
                commit,
                plan["inputs"][provider],
            )
        )
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
