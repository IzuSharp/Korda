#!/usr/bin/env python3
"""Run the scoped actual Linux x64 A08 executor and close consumers."""
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
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--offline", action="store_true")
    parser.add_argument("--allow-dirty", action="store_true")
    args = parser.parse_args()
    if platform.system() != "Linux" or platform.machine() != "x86_64":
        parser.error("This profile accepts Linux x64 only.")
    output = (args.output or ROOT / "artifacts/task04/A08" / time.strftime("run-%Y%m%dT%H%M%SZ", time.gmtime())).resolve()
    output.mkdir(parents=True, exist_ok=False)
    env = os.environ.copy()
    env.update(DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_CLI_USE_MSBUILD_SERVER="0", MSBUILDDISABLENODEREUSE="1")
    env["LD_LIBRARY_PATH"] = str(CPP_FIXTURES) + (":" + env["LD_LIBRARY_PATH"] if env.get("LD_LIBRARY_PATH") else "")
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
    report = {"task": "A08", "platform": "linux-x64", "passed": False, "commands": [], "artifacts": {},
              "os_release": Path("/etc/os-release").read_text(),
              "scope": "Actual optional engine-local execution/admission/close component; no Windows, full-package, pristine machine or load/performance acceptance.",
              "bounds": {"native_close_permit_races": 64, "managed_close_start_races": 32,
                         "repeated_canceled_close_waits": 8, "exhaustive": False},
              "features": {"normal_engine_dependency": [], "native_conformance_adapter": ["conformance"]},
              "offline_audit_not_refreshed": args.offline}

    def run(label, command, *, environment=env, expected=(0,)):
        command = list(map(str, command))
        result = subprocess.run(command, cwd=ROOT, env=environment, capture_output=True, timeout=300)
        for suffix, data in [("stdout", result.stdout), ("stderr", result.stderr)]:
            (output / (label + "." + suffix)).write_bytes(data)
        report["commands"].append({"label": label, "argv": command, "exit": result.returncode,
                                  "stdout_sha256": hashlib.sha256(result.stdout).hexdigest(),
                                  "stderr_sha256": hashlib.sha256(result.stderr).hexdigest()})
        print(label, result.returncode, flush=True)
        if result.returncode not in expected:
            raise RuntimeError(label + ": " + (result.stdout + result.stderr).decode(errors="replace")[-4000:])
        return result.stdout.decode(errors="replace")

    try:
        report["source_commit"] = run("source-commit", ["git", "rev-parse", "HEAD"]).strip()
        report["source_dirty"] = bool(run("source-status", ["git", "status", "--porcelain"]).strip())
        if report["source_dirty"] and not args.allow_dirty:
            raise ValueError("Formal A08 verification requires a clean committed source; use --allow-dirty only for development.")
        sdk_version = run("dotnet-version", ["dotnet", "--version"]).strip()
        if sdk_version != "11.0.100":
            raise ValueError("Use the fixed .NET SDK 11.0.100.")
        run("clang-version", ["clang", "--version"])
        scopes = ["src/Interop.Execution", "tests/Interop.Execution", "native/interop/execution", "samples/api/interop-execution",
                  "docs/guides/interop-execution.md", "src/NativeWeave.Core", "src/Interop.Abi", "src/Interop.Negotiation",
                  "src/Interop.Lifetime", "src/Interop.Memory", "src/Interop.Errors", "src/Interop.Callbacks", "src/Interop.Async",
                  "native/interop/abi", "native/interop/errors", "native/interop/async",
                  "Directory.Build.props", "Directory.Build.targets", "global.json", *NATIVE_INPUTS]
        paths = run("source-files", ["git", "ls-files", "--cached", "--others", "--exclude-standard", "--", *scopes]).splitlines()
        report["source_sha256"] = {p: sha(ROOT / p) for p in sorted(set(paths)) if (ROOT / p).is_file()}
        report["native_compiler_provenance"] = prepare_native(run, ["nativeweave_interop_execution", "nw_execution_downstream", "nw_execution_tests"], test_regex=r"^native\.(interop|execution)$")
        downstream = CPP_FIXTURES / "libnw_execution_downstream.so"
        exported = run("downstream-exports", ["nm", "-D", "--defined-only", downstream])
        exports = exported[0] if isinstance(exported, tuple) else exported
        names = sorted(line.split()[-1] for line in exports.splitlines() if line.strip())
        if names != ["nw_execution_link_probe_v1"]:
            raise RuntimeError("Reusable C++ component leaked fixture/helper exports: " + repr(names))
        report["default_downstream_exports"] = names
        report["artifacts"]["default_downstream"] = {"path": str(downstream), "sha256": sha(downstream)}
        native = CPP_FIXTURES / "libnativeweave_interop_execution.so"
        report["artifacts"]["native"] = {"path": str(native), "sha256": sha(native)}
        run("native-conformance-exports", ["nm", "-D", "--defined-only", native])
        common = ["-p:UseSharedCompilation=false", "-nr:false", "-m:1"]
        restore = ["--locked-mode"] + (["-p:NuGetAudit=false", "--ignore-failed-sources"] if args.offline else [])
        for label, project, assembly in [
            ("tests", "tests/Interop.Execution/Interop.Execution.Tests.csproj", "NativeWeave.Interop.Execution.Tests"),
            ("sample", "samples/api/interop-execution/Interop.Execution.Sample.csproj", "NativeWeave.Interop.Execution.Sample")]:
            run(label + "-restore", ["dotnet", "restore", project, *restore, *common])
            run(label + "-build", ["dotnet", "build", project, "-c", "Release", "--no-restore", *common])
            dll = ROOT / Path(project).parent / "bin/Release/net11.0" / (assembly + ".dll")
            report[label + "_jit"] = run(label + "-jit", ["dotnet", dll]).strip()
            if label == "tests":
                clean_env = env.copy()
                clean_env.pop("LD_LIBRARY_PATH", None)
                run("core-only-jit", ["dotnet", dll, "--core-only"], environment=clean_env)
            run(label + "-aot-restore", ["dotnet", "restore", project, "-r", "linux-x64", "-p:PublishAot=true", *restore, *common])
            destination = output / (label + "-aot")
            run(label + "-aot-publish", ["dotnet", "publish", project, "-c", "Release", "-r", "linux-x64", "-p:PublishAot=true",
                                       "-p:IlcTreatWarningsAsErrors=true", "--no-restore", "-o", destination, *common])
            executable = destination / assembly
            if (destination / (assembly + ".dll")).exists():
                raise ValueError("Managed apphost is not the required Native AOT executable.")
            report[label + "_aot"] = run(label + "-aot-run", [executable, "--require-aot"]).strip()
            if label == "tests":
                run("core-only-aot", [executable, "--require-aot", "--core-only"], environment=clean_env)
            report["artifacts"][label + "_jit"] = {"path": str(dll), "sha256": sha(dll)}
            report["artifacts"][label + "_aot"] = {"path": str(executable), "sha256": sha(executable)}
        project = "tests/Interop.Execution/ApiCheck/ApiCheck.csproj"
        run("api-restore", ["dotnet", "restore", project, *restore, *common])
        run("api-build", ["dotnet", "build", project, "-c", "Release", "--no-restore", *common])
        sdk = Path(shutil.which("dotnet")).resolve().parent
        run("api-xml-reference", ["dotnet", ROOT / "tests/Interop.Execution/ApiCheck/bin/Release/net11.0/NativeWeave.Interop.Execution.ApiCheck.dll",
                                  ROOT, sdk, ROOT / "src/NativeWeave.Core/bin/Release/net11.0/NativeWeave.Core.dll"])
        owned_cs = [p for p in report["source_sha256"] if p.endswith(".cs") and p.startswith(("src/Interop.Execution/", "tests/Interop.Execution/", "samples/api/interop-execution/"))]
        run("managed-format", ["dotnet", "format", "whitespace", ".", "--folder", "--include", *owned_cs, "--verify-no-changes"])
        run("diff-check", ["git", "diff", "--check"])
        for path, digest in report["source_sha256"].items():
            if sha(ROOT / path) != digest:
                raise ValueError("Source changed during verification: " + path)
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
