#!/usr/bin/env python3
"""Run A05's actual native/managed/AOT boundary and isolated fatal cases on Linux x64."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests"))
from native_build import prepare as prepare_native, FIXTURES as CPP_FIXTURES, BINARIES as CPP_BINARIES, SOURCE_INPUTS as NATIVE_INPUTS

def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--nuget-config", type=Path)
    parser.add_argument("--offline", action="store_true")
    args = parser.parse_args()
    if platform.system() != "Linux" or platform.machine() != "x86_64":
        parser.error("This execution profile is Linux x64 only.")
    run_dir = ROOT / "artifacts/interop-errors" / time.strftime("run-%Y%m%dT%H%M%SZ", time.gmtime())
    run_dir.mkdir(parents=True, exist_ok=False)
    env = os.environ.copy()
    env.update(DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_CLI_USE_MSBUILD_SERVER="0", MSBUILDDISABLENODEREUSE="1")
    report = {"task": "A05", "platform": "linux-x64", "passed": False, "commands": [], "artifacts": {}, "source_sha256": {}}
    def run(label, command, *, expected=(0,), extra=None):
        effective = env | (extra or {})
        result = subprocess.run(list(map(str, command)), cwd=ROOT, env=effective, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=240)
        (run_dir / (label + ".stdout")).write_bytes(result.stdout)
        (run_dir / (label + ".stderr")).write_bytes(result.stderr)
        report["commands"].append({"label": label, "command": list(map(str, command)), "returncode": result.returncode,
                                   "stdout_sha256": hashlib.sha256(result.stdout).hexdigest(), "stderr_sha256": hashlib.sha256(result.stderr).hexdigest()})
        print(label, "exit", result.returncode, flush=True)
        if result.returncode not in expected:
            raise RuntimeError(label + " failed: " + result.stdout.decode(errors="replace")[-2000:] + result.stderr.decode(errors="replace")[-4000:])
        return result.stdout.decode(errors="replace"), result.stderr.decode(errors="replace")
    try:
        report["source_commit"] = run("source-commit", ["git", "rev-parse", "HEAD"])[0].strip()
        report["source_dirty"] = bool(run("source-status", ["git", "status", "--porcelain"])[0].strip())
        for label, cmd in [("cmake", ["cmake", "--version"]), ("ninja", ["ninja", "--version"]), ("dotnet", ["dotnet", "--version"]), ("clang", ["clang", "--version"])]:
            report[label] = run(label, cmd)[0].strip()
        source_dirs = ["native/interop/errors", "tests/Interop.Errors", "src/Interop.Errors", "src/NativeWeave.Core", "src/Interop.Abi", "native/interop/abi", "samples/api/interop-errors", "docs/guides/interop-errors.md"]
        tracked = subprocess.check_output(["git", "ls-files", "--cached", "--others", "--exclude-standard", "--", *source_dirs, *NATIVE_INPUTS, "Directory.Build.props", "global.json"], cwd=ROOT, text=True)
        report["source_sha256"] = {p: digest(ROOT / p) for p in sorted(set(tracked.splitlines())) if (ROOT / p).is_file()}
        report["native_compiler_provenance"] = prepare_native(run, ["nativeweave_errors_fixture", "nw_error_probe", "nw_error_abort_probe"])
        native = CPP_FIXTURES / "libnativeweave_errors_fixture.so"
        env["LD_LIBRARY_PATH"] = str(native.parent) + (":" + env["LD_LIBRARY_PATH"] if env.get("LD_LIBRARY_PATH") else "")
        report["artifacts"]["native_fixture"] = {"path": str(native), "sha256": digest(native)}
        probe = CPP_BINARIES / "nw_error_probe"
        report["artifacts"]["unwind_probe"] = {"path": str(probe), "sha256": digest(probe)}
        for mode in ["ordinary", "quarantine"]:
            output, _ = run("panic-" + mode, [probe, mode])
            if "status=9 poisoned=true quarantined=1 active=0" not in output:
                raise RuntimeError("Missing actual containment outcome")
        for mode in ["drop", "double", "hook", "ffi"]:
            output, _ = run("panic-" + mode, [probe, mode], expected=(-6, 101, 134))
            if "status=9" in output: raise RuntimeError("Fatal path was misreported as recovered")
        abort_probe = CPP_BINARIES / "nw_error_abort_probe"
        run("panic-abort", [abort_probe, "abort"], expected=(-6, 134))
        report["artifacts"]["abort_probe"] = {"path": str(abort_probe), "sha256": digest(abort_probe)}
        common = ["-p:UseSharedCompilation=false", "-nr:false", "-m:1"]
        restore = ["--locked-mode"] + (["--configfile", args.nuget_config] if args.nuget_config else []) + (["-p:NuGetAudit=false"] if args.offline else [])
        projects = [("managed", "tests/Interop.Errors/Interop.Errors.Tests.csproj", "NativeWeave.Interop.Errors.Tests"),
                    ("sample", "samples/api/interop-errors/Interop.Errors.Sample.csproj", "NativeWeave.Interop.Errors.Sample"),
                    ("invalid-uco", "tests/Interop.Errors/InvalidUco/InvalidUco.csproj", "InvalidUco")]
        for label, project, assembly in projects:
            run(label + "-restore", ["dotnet", "restore", project, *restore, *common])
            run(label + "-build", ["dotnet", "build", project, "-c", "Release", "--no-restore", *common])
            dll = ROOT / Path(project).parent / "bin/Release/net11.0" / (assembly + ".dll")
            expected = (-6, 134, 1) if label == "invalid-uco" else (0,)
            publish = run_dir / (label + "-aot")
            run(label + "-aot-restore", ["dotnet", "restore", project, "-r", "linux-x64", "-p:PublishAot=true", *restore, *common])
            run(label + "-aot-publish", ["dotnet", "publish", project, "-c", "Release", "-r", "linux-x64", "-p:PublishAot=true", "-p:IlcTreatWarningsAsErrors=true", "--no-restore", "-o", publish, *common])
            executable = publish / assembly
            if (publish / (assembly + ".dll")).exists():
                raise RuntimeError("Expected a native AOT executable, not a managed apphost")
            output, error = run(label + "-aot-run", [executable, *(["--require-aot"] if label == "managed" else [])], expected=expected)
            if label == "invalid-uco" and ("CALLING_INVALID_UCO" not in output or "INVALID_CALLBACK_BODY_ENTERED" in output or "InvalidProgramException" not in error):
                raise RuntimeError("AOT invalid UCO did not fail before its body")
            report["artifacts"][label + "_aot"] = {"path": str(executable), "sha256": digest(executable)}
        api_project = "tests/Interop.Errors/ApiCheck/ApiCheck.csproj"
        run("api-restore", ["dotnet", "restore", api_project, *restore, *common])
        run("api-build", ["dotnet", "build", api_project, "-c", "Release", "--no-restore", *common])
        sdk = Path(shutil.which("dotnet")).resolve().parent
        run("api-xml-reference", ["dotnet", "tests/Interop.Errors/ApiCheck/bin/Release/net11.0/NativeWeave.Interop.Errors.ApiCheck.dll", ROOT, sdk, ROOT / "src/NativeWeave.Core/bin/Release/net11.0/NativeWeave.Core.dll"])
        run("diff-check", ["git", "diff", "--check"])
        for path, expected_hash in report["source_sha256"].items():
            if not (ROOT / path).is_file() or digest(ROOT / path) != expected_hash:
                raise RuntimeError("Source input changed during verification: " + path)
        report["source_unchanged_after_run"] = True
        report["passed"] = True
        report["limitations"] = ["Linux x64 only", "Component fixture, not an assembled domain engine", "Unknown panic payload bytes remain unknown; objects intentionally quarantined", "No independent NuGet release or pristine-machine claim", "Offline restore does not refresh audit data" if args.offline else "Repository audit policy retained"]
    except Exception as error:
        report["failure"] = str(error)
        raise
    finally:
        (run_dir / "result.json").write_text(json.dumps(report, indent=2) + "\n")
        print("Evidence:", run_dir, flush=True)

if __name__ == "__main__":
    main()
