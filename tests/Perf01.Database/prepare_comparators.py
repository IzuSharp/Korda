#!/usr/bin/env python3
"""Build fresh exact B23 comparator artifacts; this producer performs no timing.

The unchanged comparator applications are copied into an isolated source tree.
Only their actual golden vectors are executed here. HTTP/SQL readiness and all
performance cells remain the integration controller's responsibility.
"""
from __future__ import annotations

import argparse
import ctypes
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import time
import tomllib

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
COMPARATORS = Path("src/Benchmarks.Domain/comparators")


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def save(path, value):
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n")


def utc():
    return datetime.now(timezone.utc).isoformat()


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def inputs(root):
    paths = {Path(name) for name in ["global.json", "rust-toolchain.toml", "Cargo.lock", "LICENSE",
        "Directory.Build.props", "Directory.Build.targets", "NuGet.Config", "src/Build.Integration/NativeWeave.Build.targets",
        "src/Reference.TaskService/Protocol.cs", "src/Reference.TaskService/BlobStore.cs",
        "tests/Reference.TaskService/golden.py", "tests/Reference.TaskService/golden-v1.json"]}
    paths.update(path.relative_to(root) for path in (root / "src/Reference.TaskService/Migrations").glob("*.sql"))
    for directory, patterns in [("managed", ["*.cs", "*.csproj", "*.lock.json"]),
                                ("direct-rust", ["Cargo.toml", "Cargo.lock", "src/*.rs"])]:
        for pattern in patterns:
            paths.update(path.relative_to(root) for path in (root / COMPARATORS / directory).glob(pattern))
    paths.add(COMPARATORS / "build_sqlite.py")
    paths.add(COMPARATORS / "README.md")
    paths.add(COMPARATORS / "api-applicability.json")
    return {str(path): sha(root / path) for path in sorted(paths)}


def shared_versions(root):
    names = ["sqlx", "sqlx-core", "sqlx-postgres", "sqlx-sqlite", "libsqlite3-sys", "sea-query",
             "axum", "hyper", "hyper-util", "tokio", "tokio-rustls", "rustls", "bytes"]
    def versions(path):
        rows = tomllib.loads(path.read_text())["package"]
        return {name: sorted({r["version"] for r in rows if r["name"] == name}) for name in names}
    product = versions(root / "Cargo.lock")
    direct = versions(root / COMPARATORS / "direct-rust/Cargo.lock")
    if product != direct:
        raise ValueError("Comparator shared engine versions do not match the selected product lock")
    return product


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--product-root", type=Path, default=ROOT)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--amalgamation", type=Path)
    parser.add_argument("--compiler", default="clang")
    parser.add_argument("--jobs", type=int, default=3)
    parser.add_argument("--nuget-packages", type=Path)
    args = parser.parse_args()
    if sys.platform != "linux" or platform.machine() != "x86_64":
        raise ValueError("Perf-01 comparator preparation is Linux x64 only")
    if args.jobs < 1:
        raise ValueError("jobs must be positive")
    root, output = args.product_root.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    logs, source = output / "logs", output / "source"
    logs.mkdir(); source.mkdir()
    receipt = {"task": "Perf-01", "state": "preparing", "began_at_utc": utc(), "commands": [],
               "executed_performance": False, "scope": "Fresh comparator build and golden vectors only"}
    save(output / "progress.json", receipt)
    env = os.environ.copy()
    env.update(DOTNET_CLI_USE_MSBUILD_SERVER="0", MSBUILDDISABLENODEREUSE="1",
               NUGET_PACKAGES=str(args.nuget_packages.resolve() if args.nuget_packages else output / "nuget-packages"),
               CARGO_TARGET_DIR=str(output / "cargo-target"), CARGO_BUILD_JOBS=str(args.jobs),
               RUSTFLAGS="-C target-cpu=x86-64", CFLAGS="-march=x86-64", CXXFLAGS="-march=x86-64")
    env.pop("CARGO_ENCODED_RUSTFLAGS", None)
    env.pop("CARGO_TARGET_X86_64_UNKNOWN_LINUX_GNU_RUSTFLAGS", None)

    def run(name, command, cwd=source, timeout=1800):
        command = list(map(str, command))
        started = time.monotonic()
        print(name + " starting", flush=True)
        with (logs / (name + ".stdout")).open("wb") as stdout, (logs / (name + ".stderr")).open("wb") as stderr:
            result = subprocess.run(command, cwd=cwd, env=env, stdout=stdout, stderr=stderr, timeout=timeout)
        row = {"name": name, "command": command, "cwd": str(cwd), "exit_code": result.returncode,
               "elapsed_build_or_check_seconds": time.monotonic() - started,
               "stdout_sha256": sha(logs / (name + ".stdout")), "stderr_sha256": sha(logs / (name + ".stderr"))}
        receipt["commands"].append(row); save(output / "progress.json", receipt)
        if result.returncode:
            raise RuntimeError(name + " failed; inspect " + str(logs / (name + ".stderr")))
        print(name + " passed", flush=True)
        return (logs / (name + ".stdout")).read_text()

    try:
        source_commit = run("source-commit", ["git", "rev-parse", "HEAD"], root).strip()
        if run("source-status", ["git", "status", "--porcelain"], root).strip():
            raise ValueError("Commit the producer and selected source snapshot before preparation")
        pins = inputs(root)
        engines = shared_versions(root)
        for relative, digest in pins.items():
            target = source / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(root / relative, target)
            if sha(target) != digest:
                raise ValueError("Source copy changed: " + relative)
        save(output / "source-inputs.json", {"repository": str(root), "commit": source_commit,
             "files": pins, "shared_product_versions": engines,
             "producer": {"path": str(Path(__file__).resolve()), "sha256": sha(Path(__file__))}})
        dotnet = run("dotnet-version", ["dotnet", "--version"]).strip()
        rustc = run("rustc-version", ["rustc", "-vV"]).strip()
        cargo = run("cargo-version", ["cargo", "--version"]).strip()
        compiler = run("compiler-version", [args.compiler, "--version"]).strip()
        if dotnet != "11.0.100" or not rustc.startswith("rustc 1.100.0 "):
            raise ValueError("Exact repository SDK 11.0.100 and Rust 1.100.0 are required")
        amalgamation = args.amalgamation
        if amalgamation is None:
            cargo_home = Path(env.get("CARGO_HOME", str(Path.home() / ".cargo")))
            matches = sorted((cargo_home / "registry/src").glob("*/libsqlite3-sys-0.37.0/sqlite3"))
            if len(matches) != 1:
                raise ValueError("Provide --amalgamation with the locked libsqlite3-sys 0.37.0 sqlite3 directory")
            amalgamation = matches[0]
        sqlite = output / "sqlite"
        run("sqlite-build", [sys.executable, source / COMPARATORS / "build_sqlite.py", "--amalgamation",
                              amalgamation.resolve(), "--output", sqlite, "--compiler", args.compiler])
        library = ctypes.CDLL(str(sqlite / "libsqlite3.so"))
        library.sqlite3_libversion.restype = ctypes.c_char_p
        if library.sqlite3_libversion().decode() != "3.51.3":
            raise ValueError("Built SQLite engine does not report locked 3.51.3")
        managed = source / COMPARATORS / "managed/ManagedComparator.csproj"
        common = ["-p:UseSharedCompilation=false", "-nr:false", "-m:1", "-p:IlcInstructionSet=x86-64-v2"]
        run("managed-restore", ["dotnet", "restore", managed, "-r", "linux-x64", "-p:PublishAot=true", "--locked-mode", *common])
        run("managed-publish", ["dotnet", "publish", managed, "-c", "Release", "-r", "linux-x64", "-p:PublishAot=true",
                                "--no-restore", *common, "-p:ComparatorSqliteLibrary=" + str(sqlite / "libsqlite3.so"),
                                "-o", output / "managed-aot"])
        run("direct-build", ["cargo", "build", "--locked", "--release", "--jobs", args.jobs,
                             "--manifest-path", source / COMPARATORS / "direct-rust/Cargo.toml"])
        direct_directory = output / "direct-rust"
        direct_directory.mkdir()
        direct = direct_directory / "nativeweave-b23-direct-rust"
        shutil.copy2(output / "cargo-target/release/nativeweave-b23-direct-rust", direct)
        managed_executable = output / "managed-aot/NativeWeave.Benchmarks.ManagedComparator"
        golden = load_module("perf01_comparator_golden", source / "tests/Reference.TaskService/golden.py")
        expected = golden.verify_fixture(source / "tests/Reference.TaskService/golden-v1.json")
        readiness = {}
        for name, executable in [("direct-rust", direct), ("managed-aot", managed_executable)]:
            actual = json.loads(run(name + "-golden", [executable, "--golden-vectors"], timeout=30))
            if actual != expected:
                raise ValueError(name + " golden vectors differ from the independent B21 oracle")
            if name == "managed-aot" and "B23_RUNTIME dynamic=False" not in (logs / (name + "-golden.stderr")).read_text():
                raise ValueError("Managed comparator is not actual Native AOT")
            readiness[name] = {"status": "passed", "scope": "golden vectors only; HTTP/SQL not run by this producer",
                               "stdout_sha256": sha(logs / (name + "-golden.stdout")),
                               "stderr_sha256": sha(logs / (name + "-golden.stderr"))}
        if any(sha(root / p) != digest or sha(source / p) != digest for p, digest in pins.items()):
            raise ValueError("Selected source or lock changed during preparation")
        template = json.loads((root / COMPARATORS / "comparators.json").read_text())
        routes = {}
        for name, executable in [("direct-rust", direct), ("managed-aot", managed_executable)]:
            old = template["routes"][name]
            artifact_paths = [executable, *sorted(executable.parent.rglob("*.so"))]
            source_files = {str(source / p): pins[p] for p in old["source_identity"]["files"]}
            locks = {str(source / p): sha(source / p) for p in old["lock_hashes"]}
            route = {key: old[key] for key in ["supported", "limitations", "transport", "query_route"]}
            route.update(command=[str(executable)], artifacts={str(p): sha(p) for p in artifact_paths}, lock_hashes=locks,
                         source_identity={"base_commit": source_commit, "repository": str(root), "files": source_files},
                         artifact_sizes_bytes={str(p): p.stat().st_size for p in artifact_paths},
                         readiness=[readiness[name]], instrumented=False,
                         toolchain={"target": "linux-x64" if name == "managed-aot" else "x86_64-unknown-linux-gnu",
                                    "dotnet": dotnet, "rustc": rustc, "cargo": cargo, "sqlite_compiler": compiler,
                                    "native_isa": "-C target-cpu=x86-64; SQLite -march=x86-64",
                                    "managed_isa": "x86-64-v2" if name == "managed-aot" else None,
                                    "release": "Rust thin LTO; codegen-units=1; strip=debuginfo; panic=unwind; C# Release Native AOT"})
            if name == "direct-rust":
                route.update(shared_product_dependency_versions_match=True, shared_product_dependency_versions=engines)
            routes[name] = route
        receipt.update(state="built", completed_at_utc=utc(), readiness=readiness,
                       environment={key: env.get(key) for key in ["RUSTFLAGS", "CFLAGS", "CXXFLAGS", "CARGO_BUILD_JOBS", "NUGET_PACKAGES"]},
                       source_inputs_sha256=sha(output / "source-inputs.json"), sqlite_build_sha256=sha(sqlite / "sqlite-build.json"))
        save(output / "progress.json", receipt)
        for route in routes.values():
            route["build_receipts"] = {"path": str(output / "progress.json"), "sha256": sha(output / "progress.json")}
        save(output / "comparators.json", {"schema_version": 1, "task": "Perf-01", "platform": "linux-x64",
             "routes": routes, "source_inputs": {"path": str(output / "source-inputs.json"), "sha256": sha(output / "source-inputs.json")}})
        print(output / "comparators.json", flush=True)
    except BaseException as error:
        receipt.update(state="failed", completed_at_utc=utc(), failure=type(error).__name__ + ": " + str(error))
        save(output / "progress.json", receipt)
        raise


if __name__ == "__main__":
    main()
