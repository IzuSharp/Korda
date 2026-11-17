#!/usr/bin/env python3
"""Stage the accepted real A18 facade and precompiled closure for A24.

No component suite or native build runs here. The staged descriptor still needs
the separate generated-package JIT/AOT conformance entrypoint and review.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import tomllib
import zipfile

ROOT = Path(__file__).resolve().parents[2]
IDENTITY = "NativeWeave.Engines.AsyncProof"
VERSION = "0.1.0-proof.1"
ENGINE = "nativeweave.engines.asyncproof"
LIBRARY = "libnativeweave_engine_asyncproof.so"
NATIVE_PATH = "runtimes/linux-x64/native/" + LIBRARY


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def relative(root, name):
    if not isinstance(name, str) or not name or "\\" in name or ":" in name or any(
            part in ("", ".", "..") for part in name.split("/")):
        raise ValueError("Expected a canonical relative input path")
    path = (root / name).resolve()
    if not path.is_relative_to(root):
        raise ValueError("An accepted input escapes its source root")
    return path


def stage(engine, evidence, out):
    if not out.is_relative_to(ROOT):
        raise ValueError("Stage reviewed input copies inside this checkout")
    out.mkdir(parents=True, exist_ok=False)
    receipt = {"task": "A24", "profile": "A18", "platform": "linux-x64", "staged": False,
               "accepted": False, "component_reexecuted": False, "native_rebuilt": False}
    try:
        result_path = evidence / "result.json"
        result = json.loads(result_path.read_text())
        if result.get("task") != "A18" or result.get("platform") != "linux-x64" or not result.get("passed") or \
                result.get("preparation_only") or not result.get("source_stable") or result.get("source_dirty"):
            raise ValueError("A18 source/native/package evidence is incomplete")
        source_hashes = result["source_sha256"]

        def source(name):
            path = relative(engine, name)
            if source_hashes.get(name) != sha(path):
                raise ValueError("A18 source changed after its accepted run: " + name)
            return path

        def copy(path, destination, expected=None):
            if expected and sha(path) != expected:
                raise ValueError("Accepted artifact changed: " + str(path))
            target = relative(out, destination)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, target)
            if expected and sha(target) != expected:
                raise ValueError("Accepted artifact changed while staging: " + str(path))
            return {"path": target.relative_to(ROOT).as_posix(), "sha256": sha(target)}

        def log(label):
            command = next(c for c in result["commands"] if c["label"] == label)
            if command["exit"] != 0:
                raise ValueError("Required A18 inspection did not succeed: " + label)
            return copy(evidence / (label + ".stdout"), "evidence/" + label + ".stdout",
                        command["stdout_sha256"])

        for name in source_hashes:
            source(name)
        package = result["package"]
        if not package["outside_source_tree"] or not package["packaging_source_stable"] or \
                package["packaging_source_dirty"] or package["rust_trap_fired"] or \
                not package["native_unchanged_after_managed_change"]:
            raise ValueError("A18 independent package verification is incomplete")
        packages = {name: copy(relative(evidence / "packages", name), "feed/" + name, digest)
                    for name, digest in package["package_sha256"].items()}
        package_name = next(name for name in packages
                            if name.casefold() == (IDENTITY + "." + VERSION + ".nupkg").casefold())
        facade_package = out / "feed" / package_name
        native_hash = result["artifacts"]["native"]["sha256"]
        if package["native_sha256"] != native_hash:
            raise ValueError("Component and package native identities differ")
        record = package["publish_record"]
        expected_record = {"schemaVersion": 1, "engineId": ENGINE, "packageId": IDENTITY,
                           "packageVersion": VERSION, "rid": "linux-x64", "sourcePath": NATIVE_PATH,
                           "sha256": native_hash}
        if any(record.get(key) != value for key, value in expected_record.items()):
            raise ValueError("A18 published native identity differs")
        native = copy(relative(evidence / "package-aot", record["outputPath"]), "native/" + LIBRARY, native_hash)
        files = {}
        with zipfile.ZipFile(facade_package) as archive:
            if hashlib.sha256(archive.read(NATIVE_PATH)).hexdigest() != native_hash:
                raise ValueError("Accepted facade archive contains different native bytes")
            closure = json.loads(archive.read("nativeweave/closure.json"))
            if closure != json.loads((evidence / "package-closure.json").read_text()):
                raise ValueError("Accepted closure differs from the actually consumed package")
            node = next(p for p in closure["packages"] if p["id"] == IDENTITY)
            if node["version"] != VERSION or node["dependencies"] != {"NativeWeave.Core": VERSION} or \
                    len(node["nativeAssets"]) != 1 or node["nativeAssets"][0]["sha256"] != native_hash:
                raise ValueError("Unexpected A18 package dependency/native closure")
            provenance = json.loads(archive.read("nativeweave/source.json"))
            if provenance["sourceCommit"] != result["source_commit"] or provenance["nativeSha256"] != native_hash:
                raise ValueError("Accepted package source identity differs")
            # Reuse the exact A16 target/task bytes already consumed by A18.
            # This avoids sharing a build directory or rebuilding packaging tools.
            for name in ["buildTransitive/" + IDENTITY + ".targets", "nativeweave/closure.json",
                         "tools/net11.0/NativeWeave.Packaging.Tasks.dll",
                         "tools/net11.0/NativeWeave.Packaging.Tasks.xml"]:
                target = relative(out, "package/" + name)
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(archive.read(name))
                files[name] = {"path": target.relative_to(ROOT).as_posix(), "sha256": sha(target)}
        bindings = {"sidecar": copy(source("native/engines/async-proof/binding.json"), "binding.json"),
                    "adapter": copy(source("native/engines/async-proof/src/lib.rs"), "adapter.rs"),
                    "inventory": copy(evidence / "parsed.json", "parsed.json"),
                    "linked_symbols": copy(evidence / "linked.txt", "linked.txt"),
                    "library": "nativeweave_engine_asyncproof"}
        parsed = json.loads((out / "parsed.json").read_text())
        if parsed["adapter_sha256"] != bindings["adapter"]["sha256"]:
            raise ValueError("Parsed native inventory has a different adapter source")
        linked_log = log("linked-exports")
        linked = [line.split()[-1] for line in (ROOT / linked_log["path"]).read_text().splitlines() if line.strip()]
        if linked != (out / "linked.txt").read_text().splitlines():
            raise ValueError("Linked inventory differs from the accepted nm output")
        sidecar = json.loads((out / "binding.json").read_text())
        if set(linked) != {e["symbol"] for e in sidecar["exports"]}:
            raise ValueError("Actual native and declared export inventories differ")
        for destination, origin in {
            "src/Engine/HttpProofHost.cs": "src/Engines.AsyncProof/HttpProofHost.cs",
            "src/Engine/OperationCalls.cs": "src/Engines.AsyncProof/OperationCalls.cs",
            "samples/Consumer/Program.cs": "tests/Engines.AsyncProof/Program.cs",
            "docs/public-api.expected.json": "src/Engines.AsyncProof/public-api.expected.json",
            "docs/guide.md": "docs/guides/engines-asyncproof.md",
            "docs/THIRD-PARTY-NOTICES.txt": "src/Engines.AsyncProof/THIRD-PARTY-NOTICES.txt",
        }.items():
            files[destination] = copy(source(origin), "source/" + destination, source_hashes[origin])
        lock = copy(source("Cargo.lock"), "Cargo.lock")
        upstream_input = copy(source("native/engines/async-proof/upstream.json"), "upstream.json")
        upstream_source = json.loads((out / "upstream.json").read_text())
        if upstream_source["platform"] != "linux-x64":
            raise ValueError("Upstream feature inventory has a different platform")
        for name in ["Cargo.toml", "native/engines/async-proof/Cargo.toml"]:
            manifest = tomllib.loads(source(name).read_text())
            if manifest.get("patch") or manifest.get("replace"):
                raise ValueError("This reviewed A18 profile has no upstream patch queue")
        crates = {(p["name"], p["version"]): p for p in tomllib.loads((out / "Cargo.lock").read_text())["package"]}
        upstream = []
        for dependency in upstream_source["dependencies"]:
            features = dependency["features"]
            if features != sorted(set(features)) or any(not feature for feature in features):
                raise ValueError("Upstream features must be exact nonempty names")
            crate = crates[(dependency["crate"], dependency["version"])]
            upstream.append({"crate": dependency["crate"], "version": dependency["version"],
                "repository": dependency["repository"], "commit": dependency["vcs_commit"],
                "checksum": crate["checksum"], "features": features, "patches": [], "license": dependency["license"]})
        coverage = {"task": "A18", "source_commit": result["source_commit"],
            "prior_result": copy(result_path, "evidence/accepted-a18-result.json"),
            "source_sha256": source_hashes, "cargo_lock": lock, "upstream": upstream_input,
            "native_sha256": native_hash, "accepted_packages": packages,
            "dependency_tree": log("native-dependency-tree"),
            "core_callback_authority": {"notification_table": "NwCallbacksV1", "notification_owner": "NwHttpHost",
                "start": "nw_asyncproof_host_start_v1", "stop": "nw_asyncproof_host_stop_v1",
                "drain": "nw_asyncproof_host_retire_v1", "release": "nw_asyncproof_host_release_v1",
                "operation_table": "NwOperationCallbacksV1", "operations": sidecar["operations"],
                "implementation": "Actual shared Core A06/A07 UCO entries; no new native reverse export or lifecycle state machine."},
            "packaging_source_commit": package["packaging_source_commit"],
            "packaging_source_sha256": package["reused_packaging_sources"],
            "obligations": "Run the full real HTTP/Handler/cancel/close/retained-byte/observer-budget consumer and exact API/XML baseline in the generated package under JIT and Native AOT. The accepted A18 native suite/direct-Rust oracles remain their original component evidence.",
            "friend_prerequisite": "Preserve NativeWeave.Engines.AsyncProof and its existing Core friend grant; generated consumer uses the facade's explicit .Conformance friend."}
        (out / "coverage.json").write_text(json.dumps(coverage, indent=2) + "\n")
        descriptor = {"schema_version": 1, "engine_id": ENGINE,
            "package": {"id": IDENTITY, "assembly": IDENTITY, "namespace": IDENTITY, "version": VERSION},
            "abi": {"major": 1, "minor": 0, "bootstrap": "nw_asyncproof_get_abi_v1", "error_domain": 0x48545450},
            "threading": "EngineLocalAsync", "components": ["abi", "async", "buffer", "callback", "error", "ownership"],
            "capabilities": [{"name": "http.loopback.stream.v1", "bit": 0}],
            "native": {"asset": native, "filename": LIBRARY, "rid": "linux-x64", "target": "x86_64-unknown-linux-gnu", "cpu": "x86-64", "link_mode": "dynamic"},
            "binding": bindings, "upstream": upstream, "files": files, "dependencies": {"NativeWeave.Core": VERSION},
            "coverage": {"path": (out / "coverage.json").relative_to(ROOT).as_posix(), "sha256": sha(out / "coverage.json")}}
        for name in source_hashes:
            source(name)
        descriptor_path = out / "descriptor.json"
        descriptor_path.write_text(json.dumps(descriptor, indent=2) + "\n")
        receipt.update(staged=True, source_commit=result["source_commit"], prior_result_sha256=sha(result_path),
                       descriptor_sha256=sha(descriptor_path), native_sha256=native_hash)
        return {"descriptor": str(descriptor_path), "feed": str(out / "feed")}
    except BaseException as error:
        receipt["failure"] = str(error)
        raise
    finally:
        (out / "result.json").write_text(json.dumps(receipt, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--engine-root", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(stage(args.engine_root.resolve(), args.evidence.resolve(), args.output.resolve())))


if __name__ == "__main__":
    main()
