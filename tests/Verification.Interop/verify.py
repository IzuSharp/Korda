#!/usr/bin/env python3
"""Run the bounded Linux x64 actual-Core A20 property, race and fuzz profile."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import resource
import shutil
import signal
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests"))
from native_build import prepare as prepare_native, FIXTURES as CPP_FIXTURES, BINARIES as CPP_BINARIES, PRESET as CPP_PRESET, SOURCE_INPUTS as NATIVE_INPUTS
OWNED = ["src/Verification.Interop", "tests/Verification.Interop",
         "samples/api/verification-interop", "docs/guides/verification-interop.md"]
INPUTS = OWNED + ["src/NativeWeave.Core", "src/Interop.Abi", "src/Interop.Negotiation",
    "src/Interop.Lifetime", "src/Interop.Memory", "src/Interop.Errors", "src/Interop.Callbacks",
    "src/Interop.Async", "native/interop/abi", "native/interop/lifetime", "native/interop/memory",
    "native/interop/errors", "native/interop/callbacks", "native/interop/async",
    "src/Build.Integration/ApiReference", "global.json",
    "Directory.Build.props", "Directory.Build.targets", *NATIVE_INPUTS]


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def utc():
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--prepare", action="store_true", help="Prepare locks/API documentation; no acceptance claim.")
    parser.add_argument("--prepare-native", action="store_true", help="Stage maintainer native assets only.")
    parser.add_argument("--disable-leak-sanitizer", action="store_true",
                        help="Explicitly omit unsupported LSan (for example ptrace hosts); keep ASan/UBSan.")
    args = parser.parse_args()
    if platform.system() != "Linux" or platform.machine() != "x86_64":
        parser.error("This profile requires Linux x64.")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
    env = os.environ.copy()
    env.update(DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_CLI_USE_MSBUILD_SERVER="0", MSBUILDDISABLENODEREUSE="1")
    staged = ROOT / "artifacts/a20-native/linux-x64"
    staged.mkdir(parents=True, exist_ok=True)
    env["LD_LIBRARY_PATH"] = str(staged) + (":" + env["LD_LIBRARY_PATH"] if env.get("LD_LIBRARY_PATH") else "")
    sanitized = env.copy()
    sanitized["ASAN_OPTIONS"] = "detect_leaks=" + ("0" if args.disable_leak_sanitizer else "1") + ":halt_on_error=1"
    sanitized["UBSAN_OPTIONS"] = "halt_on_error=1:print_stacktrace=1"
    symbolizer = shutil.which("llvm-symbolizer")
    if symbolizer:
        sanitized["ASAN_SYMBOLIZER_PATH"] = symbolizer
    report = dict(task="A20", platform="linux-x64", passed=False, started_utc=utc(),
        preparation_only=args.prepare or args.prepare_native, commands=[], artifacts={},
        os_release=Path("/etc/os-release").read_text(), design_commit="c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9",
        scope="Actual Core C/C++/C# differential properties and bounded schedules; maintainer harness, no distributed package.",
        coverage=json.loads((ROOT / "tests/Verification.Interop/coverage.json").read_text()),
        sanitizer={"instrumented": ["C serialized-input decoder", "C independent oracles", "C fatal child driver"],
                   "uninstrumented": ["Release C++ fixture libraries (native ASan/UBSan CTest is a separate gate)", "CLR", "Native AOT runtime"],
                   "address": True, "undefined_behavior": True, "leaks": not args.disable_leak_sanitizer,
                   "environment": {key: value for key, value in sanitized.items() if key in ("ASAN_OPTIONS", "UBSAN_OPTIONS", "ASAN_SYMBOLIZER_PATH")}},
        claims_excluded=["Windows", "all weak-memory schedules", "unbounded liveness", "full FFI sanitizer coverage",
                         "whole-product acceptance", "pristine package consumption", "performance or memory improvement"])

    def save():
        (output / "result.json").write_text(json.dumps(report, indent=2) + "\n")

    def run(label, argv, *, environment=env, expected=(0,), timeout=600, contains=()):
        argv = list(map(str, argv)); started = utc(); clock = time.monotonic()
        process = subprocess.Popen(argv, cwd=ROOT, env=environment, stdout=subprocess.PIPE,
                                   stderr=subprocess.PIPE, start_new_session=True)
        timed_out = False
        try:
            stdout, stderr = process.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            os.killpg(process.pid, signal.SIGKILL)
            stdout, stderr = process.communicate()
        for suffix, data in [("stdout", stdout), ("stderr", stderr)]:
            (output / (label + "." + suffix)).write_bytes(data)
        row = dict(label=label, argv=argv, cwd=str(ROOT), started_utc=started, ended_utc=utc(),
                   seconds=round(time.monotonic() - clock, 3), exit=process.returncode,
                   timeout_seconds=timeout, timed_out=timed_out, expected_exit=list(expected),
                   stdout_sha256=sha(output / (label + ".stdout")), stderr_sha256=sha(output / (label + ".stderr")))
        report["commands"].append(row)
        save(); print(label, process.returncode, flush=True)
        combined = (stdout + stderr).decode(errors="replace")
        if timed_out or process.returncode not in expected or any(text not in combined for text in contains):
            raise RuntimeError(label + " failed or inconclusive\n" + combined[-5000:])
        return combined

    def artifact(label, path):
        path = Path(path)
        report["artifacts"][label] = dict(path=str(path), sha256=sha(path), bytes=path.stat().st_size)

    try:
        report["source_commit"] = run("source-commit", ["git", "rev-parse", "HEAD"]).strip()
        report["source_dirty"] = bool(run("source-status", ["git", "status", "--porcelain"]).strip())
        if report["source_dirty"] and not report["preparation_only"]:
            raise RuntimeError("Commit the exact A20 source before formal verification.")
        paths = run("source-files", ["git", "ls-files", "--cached", "--others", "--exclude-standard", "--", *INPUTS]).splitlines()
        report["source_sha256"] = {p: sha(ROOT / p) for p in sorted(set(paths)) if (ROOT / p).is_file()}
        if run("sdk", ["dotnet", "--version"]).strip() != "11.0.100":
            raise RuntimeError("Pinned SDK 11.0.100 required.")
        run("clang-version", ["clang", "--version"])
        for name in ["dotnet", "clang", "clang++", "cmake", "ninja", "llvm-symbolizer"]:
            executable = shutil.which(name)
            if executable:
                artifact("tool-" + name, Path(executable).resolve())
        report["native_compiler_provenance"] = prepare_native(run, ["nativeweave_verification_interop",
            "nativeweave_verification_interop_c", "nativeweave_interop_memory",
            "nativeweave_interop_callbacks", "nativeweave_interop_async"])
        for name in ["verification_interop", "interop_memory", "interop_callbacks", "interop_async"]:
            filename = "libnativeweave_" + name + ".so"
            shutil.copy2(CPP_FIXTURES / filename, staged / filename)
        shutil.copy2(CPP_FIXTURES / "libnativeweave_verification_interop_c.so", staged / "libnativeweave_verification_interop_c.so")
        for path in sorted(staged.glob("*.so")):
            artifact(path.name, path)
        run("native-dependencies", ["readelf", "-d", staged / "libnativeweave_verification_interop.so"])
        if args.prepare_native:
            report["native_staged"] = True
            return

        common = ["-p:UseSharedCompilation=false", "-nr:false", "-m:1"]
        locked = [] if args.prepare else ["--locked-mode"]
        managed = []
        for label, project, assembly in [
            ("tests", "tests/Verification.Interop/NativeWeave.Verification.Interop.Tests.csproj", "NativeWeave.Verification.Interop.Tests"),
            ("sample", "samples/api/verification-interop/Interop.Sample.csproj", "NativeWeave.Verification.Interop.Sample")]:
            run(label + "-restore", ["dotnet", "restore", project, *locked, *common])
            run(label + "-build", ["dotnet", "build", project, "-c", "Release", "--no-restore", *common])
            dll = ROOT / Path(project).parent / "bin/Release/net11.0" / (assembly + ".dll")
            run(label + "-aot-restore", ["dotnet", "restore", project, "-r", "linux-x64", "-p:PublishAot=true", *locked, *common])
            if not args.prepare:
                aot = output / (label + "-aot")
                run(label + "-aot-publish", ["dotnet", "publish", project, "-c", "Release", "-r", "linux-x64",
                    "-p:PublishAot=true", "-p:IlcTreatWarningsAsErrors=true", "--no-restore", "-o", aot, *common])
                executable = aot / assembly
                run(label + "-aot-execute", [executable, "--require-aot"], contains=("PASS A20",))
                if label == "sample":
                    run("sample-aot-advanced", [executable, "--require-aot", "--advanced"], contains=("PASS A20",))
                if (aot / (assembly + ".dll")).exists():
                    raise RuntimeError("Expected actual Native AOT output.")
                artifact(label + "-aot", executable)
                if label == "tests":
                    managed = [("aot", [executable])]
        project = "src/Build.Integration/ApiReference/ApiReference.csproj"
        run("api-restore", ["dotnet", "restore", project, "--locked-mode", *common])
        run("api-build", ["dotnet", "build", project, "-c", "Release", "--no-restore", *common])
        run("api-xml-reference", ["dotnet", ROOT / "src/Build.Integration/ApiReference/bin/Release/net11.0/NativeWeave.Build.ApiReference.dll",
            ROOT, Path(shutil.which("dotnet")).resolve().parent, "src/Verification.Interop/api.json", *(["--write"] if args.prepare else [])],
            contains=('"compiled_symbols":0',))
        if args.prepare:
            report["preparation_complete"] = True
            return

        for mode, command in managed:
            run(mode + "-quarantine-child", [*command, "--child-indeterminate"], expected=(43,), timeout=45,
                contains=("EXPECTED_QUARANTINE", "successful-retirement=0"))
            run(mode + "-stale-cookie-child", [*command, "--child-stale-cookie"], expected=(42,), timeout=45,
                contains=("EXPECTED_PROTOCOL_VIOLATION stale-cookie-reuse", "no safe-retirement claim"))
        fuzz = output / "fuzz"; negative = output / "fuzz-oracle-negative"; fatal = output / "fatal"
        run("fuzz-build", ["cmake", "--build", "--preset", CPP_PRESET, "--parallel", "4", "--target",
            "nw_interop_fuzz", "nw_interop_fuzz_negative", "nw_interop_fatal"])
        for target, path in [("nw_interop_fuzz", fuzz), ("nw_interop_fuzz_negative", negative), ("nw_interop_fatal", fatal)]:
            shutil.copy2(CPP_BINARIES / target, path)
        for name, path in [("fuzz", fuzz), ("fuzz-oracle-negative", negative), ("fatal", fatal)]:
            artifact(name, path)
        corpus = output / "corpus"; corpus.mkdir()
        seeds = json.loads((ROOT / "tests/Verification.Interop/corpus/seeds.json").read_text())
        for case in seeds["cases"]:
            (corpus / case["name"]).write_bytes(bytes.fromhex(case["hex"]))
        report["seed_schema"] = seeds
        report["initial_corpus_sha256"] = {p.name: sha(p) for p in sorted(corpus.iterdir())}
        failures = output / "unexpected-failures"; failures.mkdir()
        budget = ["-seed=20201117", "-max_len=256", "-timeout=5", "-rss_limit_mb=2048"]
        log = run("fuzz-execute", [fuzz, *budget, "-runs=20000", "-print_final_stats=1", "-artifact_prefix=" + str(failures) + "/", corpus],
                  environment=sanitized, timeout=180, contains=("DONE", "stat::number_of_executed_units: 20000"))
        if list(failures.iterdir()):
            raise RuntimeError("Unexpected fuzz failure artifacts require triage.")
        report["fuzz_coverage"] = re.findall(r"#20000\s+DONE\s+cov: (\d+) ft: (\d+)", log)
        report["final_corpus_sha256"] = {p.name: sha(p) for p in sorted(corpus.iterdir())}
        original = output / "oracle-original.bin"; shutil.copy2(corpus / "end-wrap", original)
        minimized = output / "oracle-minimized.bin"
        run("oracle-negative-original", [negative, original], environment=sanitized, expected=(77,), timeout=30,
            contains=("Assertion", "status == (expected ? NW_OK : NW_INVALID_ARGUMENT)"))
        # libFuzzer minimizes only the deliberate numeric-oracle defect. Product Core is unmodified.
        run("oracle-minimize", [negative, *budget, "-minimize_crash=1", "-runs=1000", "-exact_artifact_path=" + str(minimized), original],
            environment=sanitized, timeout=90)
        if not minimized.is_file():
            raise RuntimeError("No retained minimized oracle counterexample.")
        run("oracle-negative-minimized", [negative, minimized], environment=sanitized, expected=(77,), timeout=30,
            contains=("Assertion", "status == (expected ? NW_OK : NW_INVALID_ARGUMENT)"))
        run("oracle-corrected-replay", [fuzz, original, minimized], environment=sanitized, timeout=30)
        artifact("oracle-original", original); artifact("oracle-minimized", minimized)
        run("fatal-invalid-pointer", [fatal, "invalid-pointer"], environment=sanitized, expected=(1,), timeout=30,
            contains=("EXPECTED_FATAL actual-Core-copy-from-invalid-address", "ERROR: AddressSanitizer: SEGV"))
        run("fatal-wrong-release", [fatal, "wrong-release"], environment=sanitized, expected=(1,), timeout=30,
            contains=("EXPECTED_FATAL wrong-creator-free-after-owner-return", "ERROR: AddressSanitizer: attempting double-free"))
        owned_cs = [p for p in report["source_sha256"] if p.endswith(".cs") and p.startswith(tuple(OWNED[:3]))]
        run("managed-format", ["dotnet", "format", "whitespace", ".", "--folder", "--include", *owned_cs, "--verify-no-changes"])
        run("diff-check", ["git", "diff", "--check"])
        for path, digest in report["source_sha256"].items():
            if sha(ROOT / path) != digest:
                raise RuntimeError("Source changed during formal verification: " + path)
        for name in ["verification_interop", "interop_memory", "interop_callbacks", "interop_async"]:
            filename = "libnativeweave_" + name + ".so"
            if sha(staged / filename) != report["artifacts"][filename]["sha256"]:
                raise RuntimeError("Managed verification changed a native asset.")
        report["source_unchanged"] = True
        report["passed"] = True
    except Exception as error:
        report["failure"] = str(error)
        raise
    finally:
        report["ended_utc"] = utc()
        save(); print("Evidence:", output, flush=True)


if __name__ == "__main__":
    main()
