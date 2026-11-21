#!/usr/bin/env python3
"""Execute every transferred A14 AOT consumer under an observed emulated v2 CPU."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/Packaging.Linux"))
from linux_profile import host_profile, sha


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path, help="A14 runtime-transfer directory")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--emulator", default="qemu-x86_64")
    parser.add_argument("--cc", default=os.environ.get("CC", "clang"))
    parser.add_argument("--require-baseline", action="store_true")
    args = parser.parse_args()
    directory, output = args.directory.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = {"task": "A14", "passed": False, "platform": "linux-x64", "host": host_profile(),
        "execution_mode": "qemu-user CPU emulation", "cpu_model": "Nehalem", "cpu_profile": "x86-64-v2",
        "physical_cpu_certification": False, "commands": [], "consumers": []}
    run_env = os.environ.copy()
    run_env.pop("LD_LIBRARY_PATH", None)

    def save():
        (output / "result.json").write_text(json.dumps(report, indent=2) + "\n")

    def run(label, command, *, env=run_env, cwd=directory, timeout=600):
        result = subprocess.run(list(map(str, command)), cwd=cwd, env=env, capture_output=True, timeout=timeout)
        for suffix, data in [("stdout", result.stdout), ("stderr", result.stderr)]:
            (output / (label + "." + suffix)).write_bytes(data)
        report["commands"].append({"label": label, "argv": list(map(str, command)), "exit": result.returncode,
            "stdout_sha256": hashlib.sha256(result.stdout).hexdigest(), "stderr_sha256": hashlib.sha256(result.stderr).hexdigest()})
        save()
        print(label, result.returncode, flush=True)
        if result.returncode:
            raise RuntimeError((result.stdout + result.stderr).decode(errors="replace")[-3000:])
        return result.stdout.decode(errors="replace")

    try:
        if args.require_baseline and not report["host"]["declared_baseline"]:
            raise ValueError("The combined minimum-distro/CPU row requires Ubuntu 24.04 x64")
        manifest_path = directory / "manifest.json"
        manifest = json.loads(manifest_path.read_text())
        expected = {"A01", "A03", "A04", "A05", "A06", "A07"}
        if len(manifest["consumers"]) != len(expected) or {r["task"] for r in manifest["consumers"]} != expected:
            raise ValueError("Minimum-CPU acceptance requires the complete actual Core package closure")
        report.update(source_commit=manifest["source_commit"], transfer_manifest_sha256=sha(manifest_path),
                      verification_source_commit=subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
                      verification_source_dirty=bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip()))
        emulator = shutil.which(args.emulator)
        compiler = shutil.which(args.cc)
        if emulator is None or compiler is None:
            raise ValueError("Install qemu-user and the pinned Clang compiler on the Linux verifier")
        emulator, compiler = Path(emulator).resolve(), Path(compiler).resolve()
        report["emulator"] = {"path": str(emulator), "sha256": sha(emulator),
                              "version": run("emulator-version", [emulator, "--version"], timeout=60)}
        report["compiler"] = {"path": str(compiler), "sha256": sha(compiler),
                              "version": run("compiler-version", [compiler, "--version"], env=os.environ.copy(), timeout=60),
                              "target": run("compiler-target", [compiler, "-dumpmachine"], env=os.environ.copy(), timeout=60).strip()}
        llvm = json.loads((ROOT / "native/dependencies.lock.json").read_text())["toolchain"]["llvm"]
        if (report["compiler"]["sha256"] != llvm["executables"]["clang"]
                or "clang version " + llvm["version"] not in report["compiler"]["version"]
                or not report["compiler"]["target"].startswith("x86_64-")):
            raise ValueError("The CPU probe requires the exact locked Linux x64 Clang compiler")
        probe_source = ROOT / "tests/Packaging.Linux/cpu_profile.c"
        probe = output / "cpu-profile"
        run("compile-cpuid-probe", [compiler, "-std=c17", "-Wall", "-Wextra", "-Werror", "-O2", "-march=x86-64",
            probe_source, "-o", probe], env=os.environ.copy())
        profile = json.loads(run("emulated-cpuid", [emulator, "-cpu", "Nehalem", probe]))
        if profile.get("x86_64_v2") is not True or any(profile.get(name) is not False for name in ["avx", "avx2", "avx512f", "fma"]):
            raise ValueError("Observed emulated CPU differs from the required v2/no-AVX profile")
        report["cpuid"] = {"features": profile, "source_sha256": sha(probe_source), "executable_sha256": sha(probe)}
        for row in manifest["consumers"]:
            folder = directory / row["task"]
            executable = folder / row["assembly"]
            if sha(executable) != row["executable"]["sha256"]:
                raise ValueError("Transferred AOT executable changed: " + row["task"])
            for name, digest in row["native"].items():
                if Path(name).name != name or sha(folder / name) != digest:
                    raise ValueError("Transferred native asset changed: " + row["task"])
            stdout = run(row["task"] + "-minimum-cpu", [emulator, "-cpu", "Nehalem", executable, "--require-aot"], cwd=folder)
            report["consumers"].append({"task": row["task"], "executable_sha256": sha(executable),
                "native": row["native"], "stdout": stdout.strip(), "execution_mode": "emulated x86-64-v2"})
        report["passed"] = True
    finally:
        save()
        print("Minimum-CPU evidence:", output, flush=True)


if __name__ == "__main__":
    main()
