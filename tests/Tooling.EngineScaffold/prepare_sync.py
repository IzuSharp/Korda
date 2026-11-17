#!/usr/bin/env python3
"""Bind the accepted A17 source/native/package inputs to a real scaffold descriptor.

This stages a different generated repository; it does not rerun or relabel A17's
accepted component checks. All inputs stay within this maintainer checkout.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tomllib

ROOT = Path(__file__).resolve().parents[2]


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--engine-root", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    engine, evidence, out = args.engine_root.resolve(), args.evidence.resolve(), args.output.resolve()
    if not out.is_relative_to(ROOT):
        raise ValueError("Stage reviewed input copies inside this checkout")
    out.mkdir(parents=True, exist_ok=False)
    result = json.loads((evidence / "result.json").read_text())
    if not result["passed"] or not result["source_stable"] or result["source_dirty"]:
        raise ValueError("A17 source/native/package evidence is incomplete")
    def source(name):
        path = engine / name
        if result["source_sha256"].get(name) != sha(path):
            raise ValueError("A17 source changed after its accepted run: " + name)
        return path
    def copy(path, destination, expected=None):
        if expected and sha(path) != expected:
            raise ValueError("Accepted artifact changed: " + str(path))
        target = out / destination
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)
        return {"path": target.relative_to(ROOT).as_posix(), "sha256": sha(target)}
    native = copy(evidence / "package-aot/libnativeweave_engine_syncproof.so", "native/libnativeweave_engine_syncproof.so", result["artifacts"]["native"]["sha256"])
    feed = out / "feed"
    for name, digest in result["package"]["package_sha256"].items():
        copy(evidence / "packages" / name, "feed/" + name, digest)
    bindings = {"sidecar": copy(source("native/engines/sync-proof/binding.json"), "binding.json"),
                "adapter": copy(source("native/engines/sync-proof/src/lib.rs"), "adapter.rs"),
                "inventory": copy(evidence / "parsed.json", "parsed.json"),
                "linked_symbols": copy(evidence / "linked.txt", "linked.txt"), "library": "nativeweave_engine_syncproof"}
    files = {}
    for destination, origin in {
        "src/Engine/ZlibCodec.cs": "src/Engines.SyncProof/ZlibCodec.cs",
        "samples/Consumer/Program.cs": "tests/Engines.SyncProof/Program.cs",
        "docs/public-api.expected.json": "src/Engines.SyncProof/public-api.expected.json",
        "docs/guide.md": "docs/guides/engines-syncproof.md",
        "docs/THIRD-PARTY-NOTICES.txt": "src/Engines.SyncProof/THIRD-PARTY-NOTICES.txt",
    }.items():
        files[destination] = copy(source(origin), "source/" + destination)
    # Use the current accepted A16 task, copied/built by this worktree's owner.
    task = ROOT / "src/Packaging.Release/BuildTasks/NativeWeave.Packaging.Tasks.csproj"
    for label, args in (("restore", ["restore", task, "--locked-mode"]),
                        ("build", ["build", task, "-c", "Release", "--no-restore"])):
        run = subprocess.run(["dotnet", *map(str, args), "-p:UseSharedCompilation=false", "-nr:false", "-m:1"], cwd=ROOT, capture_output=True)
        (out / ("packaging-" + label + ".stdout")).write_bytes(run.stdout)
        (out / ("packaging-" + label + ".stderr")).write_bytes(run.stderr)
        if run.returncode:
            raise RuntimeError("Packaging tool build failed")
    files["buildTransitive/NativeWeave.Engines.SyncProof.targets"] = copy(ROOT / "src/Packaging.Release/buildTransitive/NativeWeave.Packaging.targets", "tool/NativeWeave.Engines.SyncProof.targets")
    for extension in ("dll", "xml"):
        name = "NativeWeave.Packaging.Tasks." + extension
        files["tools/net11.0/" + name] = copy(task.parent / "bin/Release/net11.0" / name, "tool/" + name)
    files["nativeweave/closure.json"] = copy(evidence / "package-closure.json", "closure.json")
    lock = copy(source("Cargo.lock"), "Cargo.lock")
    (out / "coverage.json").write_text(json.dumps({"task": "A17", "source_commit": result["source_commit"],
        "prior_result_sha256": sha(evidence / "result.json"), "source_sha256": result["source_sha256"],
        "cargo_lock": lock, "native_sha256": native["sha256"],
        "obligations": "Reuse actual zlib interoperability, malformed/capacity/owner cases and exact API/XML baseline in generated package JIT/AOT. Independent scaffold receipt required.",
        "packaging_source": {name: sha(ROOT / name) for name in ("src/Packaging.Release/BuildTasks/PackageClosure.cs", "src/Packaging.Release/buildTransitive/NativeWeave.Packaging.targets")}}, indent=2) + "\n")
    crates = {p["name"]: p for p in tomllib.loads((engine / "Cargo.lock").read_text())["package"] if p["name"] in ("miniz_oxide", "adler2")}
    upstream = []
    for name, repo, commit, features in (
        ("miniz_oxide", "https://github.com/Frommi/miniz_oxide", "4e582392df3a739d2b0dfd2c537dc33e8942be38", ["with-alloc"]),
        ("adler2", "https://github.com/oyvindln/adler2", "89a031a0f42eeff31c70dc598b398cbf31f1680f", [])):
        upstream.append({"crate": name, "repository": repo, "commit": commit,
            "version": crates[name]["version"], "checksum": crates[name]["checksum"], "features": features,
            "patches": [], "license": "MIT"})
    descriptor = {"schema_version": 1, "engine_id": "nativeweave.engines.syncproof",
        "package": {"id": "NativeWeave.Engines.SyncProof", "assembly": "NativeWeave.Engines.SyncProof", "namespace": "NativeWeave.Engines.SyncProof", "version": "0.1.0-proof.1"},
        "abi": {"major": 1, "minor": 0, "bootstrap": "nw_syncproof_get_abi_v1", "error_domain": 0x53594E43},
        "threading": "Synchronous", "components": ["abi", "buffer", "error", "ownership"],
        "capabilities": [{"name": "codec.zlib.v1", "bit": 0}],
        "native": {"asset": native, "filename": "libnativeweave_engine_syncproof.so", "rid": "linux-x64", "target": "x86_64-unknown-linux-gnu", "cpu": "x86-64", "link_mode": "dynamic"},
        "binding": bindings, "upstream": upstream, "files": files, "dependencies": {"NativeWeave.Core": "0.1.0-proof.1"},
        "coverage": {"path": (out / "coverage.json").relative_to(ROOT).as_posix(), "sha256": sha(out / "coverage.json")}}
    (out / "descriptor.json").write_text(json.dumps(descriptor, indent=2) + "\n")
    print(json.dumps({"descriptor": str(out / "descriptor.json"), "feed": str(feed)}))


if __name__ == "__main__":
    main()
