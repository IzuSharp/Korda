#!/usr/bin/env python3
"""Explicit maintainer Cargo build; never imported by an application generator.

Python 3.9+. Profiles bind source/binding bytes, tools, target, features, Cargo
closure, exported symbols and ELF dependencies. SHA checks establish consistency,
not publisher authentication; A22/A16 own the release trust/closure boundary.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import signal
import subprocess
import sys
import tempfile


class BuildError(RuntimeError):
    """A missing, stale or inconsistent build input/output."""


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def digest(path):
    with Path(path).open("rb") as stream:
        result = hashlib.sha256()
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(block)
    return result.hexdigest()


def content_hash(value):
    return hashlib.sha256(canonical(value).encode("utf-8")).hexdigest()


def relative(root, name):
    if not isinstance(name, str) or not name or "\\" in name or ":" in name:
        raise BuildError("Use a nonempty canonical relative path")
    parts = name.split("/")
    if any(part in ("", ".", "..") for part in parts) or PurePosixPath(name).is_absolute():
        raise BuildError("Absolute and traversal paths are forbidden")
    path = root / name
    if not path.resolve().is_relative_to(root.resolve()):
        raise BuildError("An input symlink escapes the source root")
    return path


def unique_strings(values, label):
    if (not isinstance(values, list) or any(not isinstance(v, str) or not v for v in values)
            or values != sorted(set(values))):
        raise BuildError(label + " must contain sorted unique nonempty strings")
    return values


def load_profile(root, path):
    profile = json.loads(path.read_text(encoding="utf-8"))
    keys = {"schema_version", "engine_id", "package", "manifest", "artifact", "kind",
            "rid", "target", "profile", "features", "default_features", "abi", "binding",
            "input_sha256", "exports", "native_dependencies", "cargo_packages", "tools"}
    if set(profile) != keys or profile["schema_version"] != 1:
        raise BuildError("Unknown or incomplete maintainer build profile")
    for key in ("engine_id", "package", "artifact"):
        if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_.-]{0,127}", profile[key]):
            raise BuildError("Invalid profile identifier: " + key)
    # The runner has one verified platform profile. Adding a target needs its own
    # artifact reader and actual target tests, not a guessed suffix.
    if (profile["rid"], profile["target"]) != ("linux-x64", "x86_64-unknown-linux-gnu"):
        raise BuildError("This build runner has only the Linux x64 GNU profile")
    if profile["kind"] not in ("cdylib", "staticlib") or profile["profile"] not in ("dev", "release"):
        raise BuildError("Unsupported Cargo artifact/profile")
    if not isinstance(profile["default_features"], bool):
        raise BuildError("default_features must be explicit")
    for key in ("features", "exports", "native_dependencies", "cargo_packages"):
        unique_strings(profile[key], key)
    if any(not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", symbol) for symbol in profile["exports"]):
        raise BuildError("Exports must be exact C identifiers")
    if profile["kind"] == "staticlib" and profile["native_dependencies"]:
        raise BuildError("Static linkage needs a final consumer dependency check")
    if set(profile["abi"]) != {"major", "minor", "schema_sha256", "semantics_sha256"}:
        raise BuildError("The ABI identity must separate layout/schema and semantics")
    for key in ("major", "minor"):
        if type(profile["abi"][key]) is not int or not 0 <= profile["abi"][key] <= 65535:
            raise BuildError("Invalid ABI version")
    for key in ("schema_sha256", "semantics_sha256"):
        if not re.fullmatch(r"[a-f0-9]{64}", profile["abi"][key]):
            raise BuildError("Invalid ABI digest")
    if not isinstance(profile["input_sha256"], dict) or not profile["input_sha256"]:
        raise BuildError("Exact source hashes are required")
    for name, expected in profile["input_sha256"].items():
        file = relative(root, name)
        if not re.fullmatch(r"[a-f0-9]{64}", expected) or not file.is_file() or digest(file) != expected:
            raise BuildError("Stale or missing input: " + name)
    for key in ("manifest", "binding"):
        relative(root, profile[key])
        if profile[key] not in profile["input_sha256"]:
            raise BuildError(key + " must be included in exact input hashes")
    if profile["tools"] != {"rust": "1.100.0", "dotnet": "11.0.100"}:
        raise BuildError("Use the repository's exact stable compiler/SDK pins")
    return profile


def run(arguments, cwd, log_dir, label, environment=None):
    log_dir.mkdir(parents=True, exist_ok=True)
    process = subprocess.Popen([str(a) for a in arguments], cwd=cwd, env=environment,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               start_new_session=True)
    try:
        stdout, stderr = process.communicate()
    except BaseException:
        os.killpg(process.pid, signal.SIGTERM)
        try:
            stdout, stderr = process.communicate(timeout=5)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            stdout, stderr = process.communicate()
        (log_dir / (label + ".stdout")).write_bytes(stdout)
        (log_dir / (label + ".stderr")).write_bytes(stderr)
        raise
    (log_dir / (label + ".stdout")).write_bytes(stdout)
    (log_dir / (label + ".stderr")).write_bytes(stderr)
    if process.returncode:
        raise BuildError(f"{label} failed with exit {process.returncode}; inspect its retained logs")
    return stdout.decode("utf-8", errors="strict")


def tool_identity(root, logs):
    result = {}
    for name, args in (("rustc", ["-Vv"]), ("cargo", ["--version"]), ("dotnet", ["--version"]),
                       ("cc", ["--version"]), ("ld", ["--version"]), ("nm", ["--version"]), ("readelf", ["--version"])):
        executable = shutil.which(name)
        if executable is None:
            raise BuildError("Missing maintainer tool: " + name)
        result[name] = {"version": run([executable] + args, root, logs, name).strip(),
                        "sha256": digest(Path(executable).resolve())}
    if not result["rustc"]["version"].startswith("rustc 1.100.0 "):
        raise BuildError("Rust 1.100.0 is required")
    if not result["cargo"]["version"].startswith("cargo 1.100.0 ") or result["dotnet"]["version"] != "11.0.100":
        raise BuildError("Cargo 1.100.0 and SDK 11.0.100 are required")
    return result


def cargo_flags(profile):
    flags = ["--manifest-path", profile["manifest"], "--locked"]
    if not profile["default_features"]:
        flags.append("--no-default-features")
    if profile["features"]:
        flags.extend(["--features", ",".join(profile["features"])])
    return flags


def resolved_inputs(root, profile, logs, env):
    metadata = json.loads(run(["cargo", "metadata", "--format-version=1", "--filter-platform",
                               profile["target"]] + cargo_flags(profile), root, logs, "metadata", env))
    packages = {p["id"]: p for p in metadata["packages"]}
    roots = [p for p in packages.values() if p["name"] == profile["package"]
             and Path(p["manifest_path"]).resolve() == relative(root, profile["manifest"]).resolve()]
    if len(roots) != 1:
        raise BuildError("The requested manifest must identify exactly the named Cargo package")
    nodes = {node["id"]: node for node in metadata["resolve"]["nodes"]}
    queue = [roots[0]["id"]]
    visited = set()
    while queue:
        current = queue.pop()
        if current in visited:
            continue
        visited.add(current)
        queue.extend(dep["pkg"] for dep in nodes[current]["deps"]
                     if any(kind["kind"] != "dev" for kind in dep["dep_kinds"]))
    closure = []
    for key in sorted(visited):
        package = packages[key]
        source = package["source"]
        if source is None:
            folder = Path(package["manifest_path"]).resolve().parent
            if not folder.is_relative_to(root.resolve()):
                raise BuildError("An undeclared path dependency escapes the source root")
            source = "path:" + folder.relative_to(root).as_posix()
            files = [folder / "Cargo.toml"] + list(folder.rglob("*.rs"))
            for file in files:
                if any(part in ("target", "artifacts", ".git") for part in file.relative_to(folder).parts):
                    continue
                if file.relative_to(root).as_posix() not in profile["input_sha256"]:
                    raise BuildError("Unbound native source input: " + file.relative_to(root).as_posix())
        closure.append(package["name"] + "@" + package["version"] + "|" + source)
    if sorted(closure) != profile["cargo_packages"]:
        raise BuildError("Undeclared or changed Cargo dependency closure")
    lock = Path(metadata["workspace_root"]) / "Cargo.lock"
    if not lock.resolve().is_relative_to(root) or lock.relative_to(root).as_posix() not in profile["input_sha256"]:
        raise BuildError("The exact Cargo workspace lock must be bound")
    # A root workspace or .cargo config can affect an owned crate without being
    # below its manifest. Bind those files too; never publish their raw contents.
    workspace_manifest = Path(metadata["workspace_root"]) / "Cargo.toml"
    for file in (workspace_manifest, root / ".cargo/config", root / ".cargo/config.toml"):
        if file.is_file() and file.relative_to(root).as_posix() not in profile["input_sha256"]:
            raise BuildError("Unbound Cargo configuration: " + file.relative_to(root).as_posix())
    return roots[0]["id"], sorted(closure), digest(lock)


def inspect_binary(root, binary, kind, logs):
    flags = ["-D", "--defined-only"] if kind == "cdylib" else ["-g", "--defined-only"]
    symbols = run(["nm"] + flags + [str(binary)], root, logs, "symbols")
    exports = sorted({line.split()[-1] for line in symbols.splitlines()
                      if len(line.split()) >= 3 and line.split()[-2] in ("T", "D", "B", "R", "W", "V")})
    dependencies = []
    if kind == "cdylib":
        header = run(["readelf", "-h", binary], root, logs, "elf-header")
        if "ELF64" not in header or "Advanced Micro Devices X86-64" not in header:
            raise BuildError("Produced artifact has the wrong target architecture")
        dynamic = run(["readelf", "-d", binary], root, logs, "elf-dynamic")
        dependencies = sorted(re.findall(r"\(NEEDED\).*?\[(.*?)\]", dynamic))
    return exports, dependencies


def verify(root, profile_path, manifest_path):
    root = root.resolve()
    profile = load_profile(root, profile_path)
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    keys = {"schema_version", "engine_id", "rid", "target", "profile", "kind", "profile_sha256",
            "input_sha256", "source_commit", "source_dirty", "abi", "binding_sha256", "tools",
            "runner_sha256", "features", "default_features", "cargo_lock_sha256", "cargo_packages",
            "exports", "native_dependencies", "files", "trust"}
    if set(manifest) != keys:
        raise BuildError("Unknown or incomplete build manifest")
    if manifest.get("schema_version") != 1 or manifest.get("profile_sha256") != digest(profile_path):
        raise BuildError("Build manifest is stale or belongs to a different profile")
    for key in ("engine_id", "rid", "target", "profile", "kind", "input_sha256", "abi", "features",
                "default_features", "cargo_packages", "exports", "native_dependencies"):
        if manifest[key] != profile[key]:
            raise BuildError("Build manifest/profile identity changed: " + key)
    if (manifest["binding_sha256"] != digest(relative(root, profile["binding"]))
            or manifest["runner_sha256"] != digest(Path(__file__))):
        raise BuildError("Build manifest binding/runner identity changed")
    logs = manifest_path.parent / "verify-logs"
    # This is an explicit local maintainer verification, not a consumer build
    # hook or a replacement for release/package authentication. Bind claimed
    # provenance to the actual selected tools and current source checkout.
    identities = tool_identity(root, logs)
    if manifest["tools"] != identities:
        raise BuildError("Build manifest tool identity changed")
    env = os.environ.copy()
    _, closure, lock_hash = resolved_inputs(root, profile, logs, env)
    if manifest["cargo_lock_sha256"] != lock_hash or manifest["cargo_packages"] != closure:
        raise BuildError("Build manifest lock/dependency identity changed")
    if manifest["source_commit"] != run(["git", "rev-parse", "HEAD"], root, logs, "source-commit").strip():
        raise BuildError("Build manifest source commit is stale")
    if type(manifest["source_dirty"]) is not bool:
        raise BuildError("Build manifest must declare its source dirty state")
    if manifest["trust"] != "local consistency only; publisher authentication is a separate release gate":
        raise BuildError("Build manifest trust statement changed")
    files = manifest.get("files", [])
    if not isinstance(files, list) or len(files) != 1 or set(files[0]) != {"path", "sha256"}:
        raise BuildError("Exactly one selected native artifact is required")
    extension = ".so" if profile["kind"] == "cdylib" else ".a"
    if files[0]["path"] != "runtimes/" + profile["rid"] + "/native/lib" + profile["artifact"] + extension:
        raise BuildError("Native artifact path differs from the selected target/profile")
    binary = relative(manifest_path.parent, files[0]["path"])
    if not binary.is_file() or digest(binary) != files[0]["sha256"]:
        raise BuildError("Native bytes are missing or substituted")
    exports, dependencies = inspect_binary(root, binary, profile["kind"], logs)
    if exports != profile["exports"] or dependencies != profile["native_dependencies"]:
        raise BuildError("Native export/dependency inventory differs from the profile")
    return manifest


def build(root, profile_path, output=None):
    root = root.resolve()
    profile_path = profile_path.resolve()
    output = (output or root / "artifacts/build-integration").resolve()
    if not output.is_relative_to(root / "artifacts"):
        raise BuildError("Maintainer output must be isolated below this worktree's artifacts directory")
    output.mkdir(parents=True, exist_ok=True)
    # Name the slot by the exact profile location, even when its contents are
    # invalid. A failed attempt must revoke its last advertised successful output.
    slot_id = hashlib.sha256(str(profile_path).encode()).hexdigest()[:20]
    slot = output / slot_id
    slot.mkdir(exist_ok=True)
    lock_path = slot / "building.lock"
    try:
        lock = os.open(lock_path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    except FileExistsError as error:
        raise BuildError("A build already owns this worktree/profile slot") from error
    os.close(lock)
    latest = slot / "latest.json"
    latest.unlink(missing_ok=True)
    attempt = Path(tempfile.mkdtemp(prefix="attempt-", dir=slot))
    try:
        profile = load_profile(root, profile_path)
        profile_hash = digest(profile_path)
        env = os.environ.copy()
        overrides = {"RUSTFLAGS", "CARGO_ENCODED_RUSTFLAGS", "RUSTC_WRAPPER", "RUSTC_WORKSPACE_WRAPPER",
                     "CARGO_BUILD_RUSTFLAGS", "RUSTC", "RUSTDOC", "CC", "CXX", "AR", "CFLAGS", "CXXFLAGS", "LDFLAGS"}
        overrides.update(name for name in env if name.startswith("CARGO_PROFILE_") or
                         (name.startswith("CARGO_TARGET_") and name.endswith(("_RUSTFLAGS", "_LINKER"))))
        for name in sorted(overrides):
            if env.get(name):
                raise BuildError("Declare compiler inputs in the profile; remove environment override " + name)
        env["CARGO_ENCODED_RUSTFLAGS"] = "--remap-path-prefix=" + str(root) + "=/nativeweave"
        identities = tool_identity(root, attempt)
        package_id, closure, lock_hash = resolved_inputs(root, profile, attempt, env)
        runner_hash = digest(Path(__file__))
        key = content_hash({"tools": identities, "runner_sha256": runner_hash,
                            "target": profile["target"], "profile": profile["profile"],
                            "features": profile["features"], "default_features": profile["default_features"],
                            "lock_sha256": lock_hash, "profile_sha256": profile_hash})
        target_dir = slot / "cache" / key
        arguments = (["cargo", "build", "--package", profile["package"], "--target", profile["target"],
                      "--profile", profile["profile"], "--target-dir", str(target_dir),
                      "--message-format=json-render-diagnostics"] + cargo_flags(profile))
        lines = run(arguments, root, attempt, "cargo-build", env)
        artifacts = []
        for line in lines.splitlines():
            if not line.startswith("{"):
                continue
            record = json.loads(line)
            if (record.get("reason") == "compiler-artifact" and record["package_id"] == package_id
                    and record["target"]["name"] == profile["artifact"]
                    and profile["kind"] in record["target"]["crate_types"]):
                extension = ".so" if profile["kind"] == "cdylib" else ".a"
                artifacts.extend(Path(name) for name in record["filenames"] if name.endswith(extension))
        if len(artifacts) != 1 or not artifacts[0].is_file() or not artifacts[0].resolve().is_relative_to(target_dir):
            raise BuildError("Cargo did not return exactly one current selected compiler-artifact")
        exports, dependencies = inspect_binary(root, artifacts[0], profile["kind"], attempt)
        if exports != profile["exports"]:
            raise BuildError("Missing or unexpected final linked export")
        if dependencies != profile["native_dependencies"]:
            raise BuildError("Undeclared final native dependency")
        # Close the source-change race before advertising any artifact.
        load_profile(root, profile_path)
        if digest(profile_path) != profile_hash:
            raise BuildError("Build profile changed while compiling")
        head = run(["git", "rev-parse", "HEAD"], root, attempt, "source-commit").strip()
        dirty = bool(run(["git", "status", "--porcelain", "--untracked-files=normal"], root, attempt, "source-status").strip())
        staging = attempt / "staging"
        native = staging / "runtimes" / profile["rid"] / "native" / artifacts[0].name
        native.parent.mkdir(parents=True)
        shutil.copy2(artifacts[0], native)
        manifest = {"schema_version": 1, "engine_id": profile["engine_id"], "rid": profile["rid"],
                    "target": profile["target"], "profile": profile["profile"], "kind": profile["kind"],
                    "profile_sha256": profile_hash, "input_sha256": profile["input_sha256"],
                    "source_commit": head, "source_dirty": dirty, "abi": profile["abi"],
                    "binding_sha256": digest(relative(root, profile["binding"])), "tools": identities,
                    "runner_sha256": runner_hash,
                    "features": profile["features"], "default_features": profile["default_features"],
                    "cargo_lock_sha256": lock_hash, "cargo_packages": closure,
                    "exports": exports, "native_dependencies": dependencies,
                    "files": [{"path": native.relative_to(staging).as_posix(), "sha256": digest(native)}],
                    "trust": "local consistency only; publisher authentication is a separate release gate"}
        (staging / "manifest.json").write_text(canonical(manifest) + "\n", encoding="utf-8")
        artifact_id = content_hash(manifest)
        final = slot / "completed" / artifact_id
        final.parent.mkdir(exist_ok=True)
        if final.exists():
            verify(root, profile_path, final / "manifest.json")
            shutil.rmtree(staging)
        else:
            staging.rename(final)
        pointer = {"manifest": (final / "manifest.json").relative_to(root).as_posix(),
                   "manifest_sha256": digest(final / "manifest.json")}
        temporary = attempt / "latest.json"
        temporary.write_text(canonical(pointer) + "\n", encoding="utf-8")
        temporary.replace(latest)
        return final / "manifest.json"
    finally:
        # Logs survive both failures and cancellation; incomplete staged assets do not.
        shutil.rmtree(attempt / "staging", ignore_errors=True)
        lock_path.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["build", "verify"])
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--profile", type=Path, required=True)
    parser.add_argument("--manifest", type=Path)
    args = parser.parse_args()
    def cancel(_signal, _frame):
        raise KeyboardInterrupt("Maintainer build cancelled")
    signal.signal(signal.SIGTERM, cancel)
    root = args.root.resolve()
    profile = args.profile.resolve()
    try:
        if args.command == "build":
            result = build(root, profile)
            print(canonical({"status": "built", "manifest": str(result), "sha256": digest(result)}))
        else:
            if args.manifest is None:
                raise BuildError("verify requires an explicit manifest; never infer an old success")
            verify(root, profile, args.manifest.resolve())
            print(canonical({"status": "verified", "manifest_sha256": digest(args.manifest)}))
    except (BuildError, OSError, ValueError, KeyboardInterrupt) as error:
        print(str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
