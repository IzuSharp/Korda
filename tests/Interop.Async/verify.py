#!/usr/bin/env python3
"""Run actual Linux x64 A07 setup/cancel/result/retirement and Native AOT conformance."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import resource
import shutil
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests"))
from native_build import prepare as prepare_native, FIXTURES as CPP_FIXTURES, BINARIES as CPP_BINARIES, SOURCE_INPUTS as NATIVE_INPUTS


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--offline", action="store_true")
    args = parser.parse_args()
    if platform.system() != "Linux" or platform.machine() != "x86_64":
        parser.error("This profile executes Linux x64 only.")
    output = (args.output or ROOT / "artifacts/task04/A07" / time.strftime("run-%Y%m%dT%H%M%SZ", time.gmtime())).resolve()
    output.mkdir(parents=True, exist_ok=False)
    env = os.environ.copy()
    env.update(DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_CLI_USE_MSBUILD_SERVER="0", MSBUILDDISABLENODEREUSE="1")
    env["LD_LIBRARY_PATH"] = str(CPP_FIXTURES) + (":" + env["LD_LIBRARY_PATH"] if env.get("LD_LIBRARY_PATH") else "")
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
    report = {"task": "A07", "platform": "linux-x64", "passed": False, "commands": [], "artifacts": {},
              "scope": "Actual Task bridge/native state component; no mature-engine, executor OS-join, pristine-consumer or Windows acceptance.",
              "bounds": {"managed_native_worker_gc_iterations": 32, "native_terminal_race_iterations": 32,
                         "native_terminal_racers": 4, "exhaustive": False},
              "features": {"default_engine_dependency": [], "component_fixture": ["conformance"]},
              "offline_audit_not_refreshed": args.offline}

    def run(label, command, expected=(0,)):
        result = subprocess.run(list(map(str, command)), cwd=ROOT, env=env, capture_output=True, timeout=300)
        for suffix, data in [("stdout", result.stdout), ("stderr", result.stderr)]:
            (output / (label + "." + suffix)).write_bytes(data)
        report["commands"].append({"label": label, "argv": list(map(str, command)), "exit": result.returncode,
                                  "stdout_sha256": hashlib.sha256(result.stdout).hexdigest(),
                                  "stderr_sha256": hashlib.sha256(result.stderr).hexdigest()})
        print(label, result.returncode, flush=True)
        if result.returncode not in expected:
            raise RuntimeError(label + ": " + result.stdout.decode(errors="replace")[-3000:] + result.stderr.decode(errors="replace")[-3000:])
        return result.stdout.decode(errors="replace"), result.stderr.decode(errors="replace")

    try:
        report["source_commit"] = run("commit", ["git", "rev-parse", "HEAD"])[0].strip()
        report["source_dirty"] = bool(run("status", ["git", "status", "--porcelain"])[0].strip())
        for label, command in [("dotnet", ["dotnet", "--version"]), ("cmake", ["cmake", "--version"]),
                               ("ninja", ["ninja", "--version"]), ("clang", ["clang", "--version"])]:
            run(label, command)
        scopes = ["src/Interop.Async", "tests/Interop.Async", "native/interop/async", "samples/api/interop-async",
                  "docs/guides/interop-async.md", "src/Korda.Core", "src/Interop.Abi", "src/Interop.Negotiation",
                  "src/Interop.Lifetime", "src/Interop.Memory", "src/Interop.Errors", "src/Interop.Callbacks",
                  "native/interop/abi", "native/interop/errors", "native/interop/memory", "tests/Interop.Errors/ApiCheck",
                  "Directory.Build.props", "Directory.Build.targets", "global.json", *NATIVE_INPUTS]
        paths = run("source-files", ["git", "ls-files", "--cached", "--others", "--exclude-standard", "--", *scopes])[0].splitlines()
        report["source_sha256"] = {p: sha(ROOT / p) for p in sorted(set(paths)) if (ROOT / p).is_file()}
        report["native_compiler_provenance"] = prepare_native(run, ["korda_interop_async", "nw_async_downstream"])
        downstream = CPP_FIXTURES / "libnw_async_downstream.so"
        exported = run("downstream-exports", ["nm", "-D", "--defined-only", downstream])
        exports = exported[0] if isinstance(exported, tuple) else exported
        names = sorted(line.split()[-1] for line in exports.splitlines() if line.strip())
        if names != ["nw_async_link_probe_v1"]:
            raise RuntimeError("Reusable C++ component leaked fixture/helper exports: " + repr(names))
        report["default_downstream_exports"] = names
        report["artifacts"]["default_downstream"] = {"path": str(downstream), "sha256": sha(downstream)}
        native = CPP_FIXTURES / "libkorda_interop_async.so"
        report["artifacts"]["native"] = {"path": str(native), "sha256": sha(native)}
        common = ["-p:UseSharedCompilation=false", "-nr:false", "-m:1"]
        restore = ["--locked-mode"] + (["-p:NuGetAudit=false", "--ignore-failed-sources"] if args.offline else [])
        projects = [("tests", "tests/Interop.Async/Interop.Async.Tests.csproj", "Korda.Interop.Async.Tests"),
                    ("sample", "samples/api/interop-async/Interop.Async.Sample.csproj", "Korda.Interop.Async.Sample")]
        isolated = ["--indeterminate-start", "--callback-then-reject", "--retire-fault", "--retired-value-task-quarantine",
                    "--direct-retired-value-task-quarantine"]
        for label, project, assembly in projects:
            run(label + "-restore", ["dotnet", "restore", project, *restore, *common])
            run(label + "-build", ["dotnet", "build", project, "-c", "Release", "--no-restore", *common])
            dll = ROOT / Path(project).parent / "bin/Release/net11.0" / (assembly + ".dll")
            run(label + "-aot-restore", ["dotnet", "restore", project, "-r", "linux-x64", "-p:PublishAot=true", *restore, *common])
            destination = output / (label + "-aot")
            run(label + "-aot-publish", ["dotnet", "publish", project, "-c", "Release", "-r", "linux-x64", "-p:PublishAot=true",
                                       "-p:IlcTreatWarningsAsErrors=true", "--no-restore", "-o", destination, *common])
            executable = destination / assembly
            if (destination / (assembly + ".dll")).exists():
                raise RuntimeError("A managed apphost is not Native AOT.")
            report[label + "_aot"] = run(label + "-aot-run", [executable, *(["--require-aot"] if label == "tests" else [])])[0].strip()
            if label == "tests":
                for mode in isolated:
                    run(label + "-aot-" + mode[2:], [executable, "--require-aot", mode])
            report["artifacts"][label + "_aot"] = {"path": str(executable), "sha256": sha(executable)}
        sdk = Path(shutil.which("dotnet")).resolve().parent
        for label, project, assembly in [
            ("api", "tests/Interop.Async/ApiCheck/ApiCheck.csproj", "Korda.Interop.Async.ApiCheck"),
            ("existing-error-api-compatibility", "tests/Interop.Errors/ApiCheck/ApiCheck.csproj", "Korda.Interop.Errors.ApiCheck")]:
            run(label + "-restore", ["dotnet", "restore", project, *restore, *common])
            run(label + "-build", ["dotnet", "build", project, "-c", "Release", "--no-restore", *common])
            run(label + "-xml-reference", ["dotnet", ROOT / Path(project).parent / "bin/Release/net11.0" / (assembly + ".dll"),
                                          ROOT, sdk, ROOT / "src/Korda.Core/bin/Release/net11.0/Korda.Core.dll"])
        run("diff-check", ["git", "diff", "--check"])
        for path, digest in report["source_sha256"].items():
            if sha(ROOT / path) != digest:
                raise RuntimeError("Source changed during verification: " + path)
        report["source_unchanged"] = True
        report["passed"] = True
    except Exception as error:
        report["failure"] = str(error)
        raise
    finally:
        (output / "result.json").write_text(json.dumps(report, indent=2) + "\n")
        print("Evidence:", output, flush=True)


if __name__ == "__main__":
    main()
