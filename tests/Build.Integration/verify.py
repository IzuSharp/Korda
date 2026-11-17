#!/usr/bin/env python3
"""A13 real cold/incremental/parallel, failed-build and consumer checks."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor

ROOT = Path(__file__).resolve().parents[2]
BUILDER = Path("src/Build.Integration/build_native.py")
PROFILE = Path("samples/api/build-integration/linux-x64.json")
NATIVE = Path("tests/Build.Integration/native")
records = []


def sha(file):
    return hashlib.sha256(Path(file).read_bytes()).hexdigest()


def command(args, cwd, logs, label, expect=0, env=None):
    result = subprocess.run([str(a) for a in args], cwd=cwd, env=env, capture_output=True)
    logs.mkdir(parents=True, exist_ok=True)
    (logs / (label + ".stdout")).write_bytes(result.stdout)
    (logs / (label + ".stderr")).write_bytes(result.stderr)
    records.append({"label": label, "command": [str(a) for a in args], "cwd": str(cwd),
                    "exit_code": result.returncode, "expected_exit": expect,
                    "stdout_sha256": sha(logs / (label + ".stdout")),
                    "stderr_sha256": sha(logs / (label + ".stderr"))})
    if (expect == 0 and result.returncode != 0) or (expect != 0 and result.returncode == 0):
        raise RuntimeError(label + " produced unexpected exit " + str(result.returncode))
    return result.stdout.decode(), result.stderr.decode()


def write_profile(root):
    original = json.loads((ROOT / PROFILE).read_text())
    original["input_sha256"] = {name: sha(root / name) for name in original["input_sha256"]}
    destination = root / PROFILE
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(original, indent=2) + "\n")


def fixture(root, logs):
    (root / BUILDER).parent.mkdir(parents=True)
    shutil.copy2(ROOT / BUILDER, root / BUILDER)
    shutil.copytree(ROOT / NATIVE, root / NATIVE,
                    ignore=shutil.ignore_patterns("target", "cancellation-*", "wait-for-cancellation"))
    (root / ".gitignore").write_text("artifacts/\n**/target/\n")
    write_profile(root)
    command(["git", "init", "-q"], root, logs, "fixture-init")
    command(["git", "add", "."], root, logs, "fixture-add")
    command(["git", "-c", "user.name=NativeWeave Verification", "-c", "user.email=verification@example.invalid",
             "commit", "-q", "-m", "Exact A13 conformance inputs"], root, logs, "fixture-commit")


def build(root, logs, label, expect=0):
    output, error = command([sys.executable, root / BUILDER, "build", "--root", root,
                             "--profile", root / PROFILE], root, logs, label, expect)
    if expect:
        if list((root / "artifacts/build-integration").rglob("latest.json")):
            raise AssertionError("Failed build advertised a previous success")
        if list((root / "artifacts/build-integration").rglob("staging")):
            raise AssertionError("Failed build retained incomplete staging")
        return error
    receipt = json.loads(output.strip().splitlines()[-1])
    manifest = Path(receipt["manifest"])
    command([sys.executable, root / BUILDER, "verify", "--root", root,
             "--profile", root / PROFILE, "--manifest", manifest], root, logs, label + "-verify")
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--native-only", action="store_true")
    args = parser.parse_args()
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=False)
    logs = out / "logs"
    summary = {"task": "A13", "rid": "linux-x64", "status": "running", "checks": [],
               "other_platforms_executed": False, "pristine_machine": False, "package_published": False}
    try:
        source_head, _ = command(["git", "rev-parse", "HEAD"], ROOT, logs, "source-head")
        summary["source_commit"] = source_head.strip()
        dirty, _ = command(["git", "status", "--porcelain"], ROOT, logs, "source-status")
        summary["source_dirty"] = bool(dirty.strip())
        files = subprocess.check_output(["git", "ls-files", "src/Build.Integration", "tests/Build.Integration", "samples/api/build-integration", "docs/guides/build-integration.md", "eng/build.py", "Directory.Build.targets", "Directory.Build.props", "Cargo.toml", "Cargo.lock", "global.json", "rust-toolchain.toml", "NativeWeave.slnx", "public-api-standard.json"], cwd=ROOT, text=True).splitlines()
        summary["source_sha256"] = {name: sha(ROOT / name) for name in files}
        first = out / "work-a"
        fixture(first, logs)
        initial = build(first, logs, "cold")
        repeated = build(first, logs, "incremental")
        assert initial == repeated and initial.read_bytes() == repeated.read_bytes()
        compile_records = list((first / "artifacts/build-integration").rglob("cargo-build.stdout"))
        assert any('"fresh":true' in file.read_text() for file in compile_records)
        summary["checks"].append("actual cold and cached Cargo builds have identical native and manifest bytes")
        first_manifest = json.loads(initial.read_text())
        summary["artifact"] = first_manifest

        second, third = out / "work-b", out / "work-c"
        for name, destination in (("b", second), ("c", third)):
            command(["git", "clone", "-q", "--no-hardlinks", first, destination], out, logs, "clone-" + name)
        with ThreadPoolExecutor(max_workers=2) as pool:
            future_b = pool.submit(build, second, logs, "parallel-b")
            future_c = pool.submit(build, third, logs, "parallel-c")
            manifest_b, manifest_c = future_b.result(), future_c.result()
        assert manifest_b.read_bytes() == initial.read_bytes() == manifest_c.read_bytes()
        assert manifest_b.parent != manifest_c.parent
        summary["checks"].append("two parallel worktrees produced identical bytes in independent output/cache slots")

        binding = second / NATIVE / "binding.json"
        original = binding.read_bytes()
        binding.write_bytes(original + b" ")
        assert "Stale or missing input" in build(second, logs, "stale-binding", expect=1)
        binding.write_bytes(original)
        summary["checks"].append("stale binding rejected and previous advertised success revoked")

        source = second / NATIVE / "src/lib.rs"
        original = source.read_bytes()
        source.write_bytes(original + b"\nthis is deliberately invalid Rust\n")
        write_profile(second)
        assert "cargo-build failed" in build(second, logs, "compiler-failure", expect=1)
        source.write_bytes(original)
        write_profile(second)
        summary["checks"].append("actual compiler failure cannot restage an old native artifact")

        source.write_bytes(original + b'\n#[unsafe(no_mangle)]\npub extern "C" fn unexpected_export() -> u32 { 9 }\n')
        write_profile(second)
        assert "unexpected final linked export" in build(second, logs, "extra-export", expect=1)
        source.write_bytes(original)
        write_profile(second)
        summary["checks"].append("unexpected actual ELF export rejected")

        profile = json.loads((second / PROFILE).read_text())
        profile["native_dependencies"].remove("libc.so.6")
        (second / PROFILE).write_text(json.dumps(profile, indent=2) + "\n")
        assert "Undeclared final native dependency" in build(second, logs, "native-dependency", expect=1)
        write_profile(second)
        profile = json.loads((second / PROFILE).read_text())
        profile["cargo_packages"].append("unapproved@1.0.0|registry:unapproved")
        profile["cargo_packages"].sort()
        (second / PROFILE).write_text(json.dumps(profile, indent=2) + "\n")
        assert "Cargo dependency closure" in build(second, logs, "cargo-dependency", expect=1)
        write_profile(second)
        summary["checks"].append("actual native and Cargo dependency allowlists enforced independently")

        native = manifest_b.parent / json.loads(manifest_b.read_text())["files"][0]["path"]
        original = native.read_bytes()
        native.write_bytes(original + b"substituted")
        _, error = command([sys.executable, second / BUILDER, "verify", "--root", second,
                             "--profile", second / PROFILE, "--manifest", manifest_b], second, logs, "tamper", expect=1)
        assert "substituted" in error
        native.write_bytes(original)
        summary["checks"].append("artifact byte substitution rejected before consumption")

        # Interrupt Cargo while an actual tracked build script is running. The
        # process-group cleanup must end the child too and revoke latest output.
        marker = third / NATIVE / "wait-for-cancellation"
        marker.write_text("cancel this build\n")
        process = subprocess.Popen([sys.executable, third / BUILDER, "build", "--root", third,
                                    "--profile", third / PROFILE], cwd=third,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        started = third / NATIVE / "cancellation-build-started"
        deadline = time.monotonic() + 15
        while not started.exists() and process.poll() is None and time.monotonic() < deadline:
            time.sleep(.05)
        assert started.exists(), "Cancellation did not reach the real Cargo child"
        _, collision = command([sys.executable, third / BUILDER, "build", "--root", third,
                                 "--profile", third / PROFILE], third, logs, "same-worktree-concurrency", expect=1)
        assert "already owns" in collision
        process.terminate()
        stdout, stderr = process.communicate(timeout=10)
        (logs / "cancel.stdout").write_bytes(stdout)
        (logs / "cancel.stderr").write_bytes(stderr)
        assert process.returncode != 0 and b"cancelled" in stderr
        assert not list((third / "artifacts/build-integration").rglob("latest.json"))
        assert not list((third / "artifacts/build-integration").rglob("staging"))
        assert not (third / NATIVE / "cancellation-build-completed").exists()
        summary["checks"].append("actual cancellation and competing same-worktree build leave no publishable result")

        if not args.native_only:
            sample = ROOT / "samples/api/build-integration/Build.Integration.Sample.csproj"
            native = initial.parent / first_manifest["files"][0]["path"]
            env = os.environ.copy()
            env["LD_LIBRARY_PATH"] = str(native.parent) + os.pathsep + env.get("LD_LIBRARY_PATH", "")
            trap = out / "no-rust"
            trap.mkdir()
            for name in ("cargo", "rustc", "rustup"):
                script = trap / name
                script.write_text("#!/bin/sh\necho Unexpected-Rust >&2\nexit 97\n")
                script.chmod(0o755)
            env["PATH"] = str(trap) + os.pathsep + env["PATH"]
            command(["dotnet", "restore", sample, "--locked-mode"], ROOT, logs, "consumer-restore", env=env)
            command(["dotnet", "run", "--project", sample, "-c", "Release", "--no-restore", "-p:UseSharedCompilation=false", "-m:1", "-nodeReuse:false"], ROOT, logs, "consumer-jit", env=env)
            publish = out / "aot"
            command(["dotnet", "publish", sample, "-c", "Release", "-r", "linux-x64", "-p:PublishAot=true",
                     "-p:IlcTreatWarningsAsErrors=true", "-p:RestoreLockedMode=true", "-p:UseSharedCompilation=false", "-m:1", "-nodeReuse:false", "-o", publish], ROOT, logs, "consumer-aot-publish", env=env)
            command([publish / "NativeWeave.Build.Integration.Sample"], ROOT, logs, "consumer-aot-run", env=env)
            summary["aot_sha256"] = sha(publish / "NativeWeave.Build.Integration.Sample")
            summary["checks"].append("ordinary managed build/JIT and actual AOT consume precompiled bytes with Rust invocation traps")
        summary["source_unchanged"] = all(sha(ROOT / name) == value for name, value in summary["source_sha256"].items())
        if not summary["source_unchanged"]: raise AssertionError("Source changed during verification")
        summary["status"] = "passed"
    except BaseException as error:
        summary["status"] = "failed"
        summary["failure"] = str(error)
        raise
    finally:
        summary["commands"] = records
        (out / "result.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps({"status": "passed", "checks": len(summary["checks"]), "result": str(out / "result.json")}))


if __name__ == "__main__":
    main()
