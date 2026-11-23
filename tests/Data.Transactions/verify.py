#!/usr/bin/env python3
"""B05 component verification: exact owned API, native coordinator and actual Native AOT."""
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
sys.path.insert(0, str(ROOT / "native/cpp/tests"))
from component_build import prepare

OWNED = ["src/Data.Transactions", "native/cpp", "tests/Data.Transactions", "samples/api/data-transactions", "docs/guides/data-transactions.md"]


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def source_identity():
    paths = subprocess.check_output(["git", "ls-files", "--cached", "--others", "--exclude-standard", "--", *OWNED,
                                     "src/Data.Abstractions", "native/database/binding", "native/interop/abi", "CMakeLists.txt", "CMakePresets.json", ".clang-format", ".clang-tidy", "Directory.Build.props", "global.json"], cwd=ROOT, text=True).splitlines()
    return {p: sha(ROOT / p) for p in sorted(set(paths)) if (ROOT / p).is_file()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/B05/component-run")
    parser.add_argument("--offline-config", type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--design", type=Path)
    parser.add_argument("--native-build", type=Path, default=ROOT / "artifacts/cmake/linux-x64-release")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    env = os.environ.copy()
    env.update(DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1", DOTNET_CLI_USE_MSBUILD_SERVER="0")
    commands = []
    initial = source_identity()
    manifest = {"scope": "B05-linux-x64-component", "status": "running", "platform": platform.platform(),
                "sourceCommit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
                "sourceDirty": bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip()),
                "sourceSha256": initial, "commands": commands, "artifacts": {},
                "realProviderComposition": "real SQLite Host with controlled completion; provider-specific suites remain separate", "assembledHostAcceptance": False,
                "independentReview": False, "allPlatformAcceptance": False}

    def save():
        (output / "result.json").write_text(json.dumps(manifest, indent=2) + "\n")

    def run(label, command, custom_env=None, timeout=600):
        start = time.monotonic()
        result = subprocess.run(list(map(str, command)), cwd=ROOT, env=custom_env or env, capture_output=True, text=True, timeout=timeout)
        stdout = output / (label + ".stdout.log")
        stderr = output / (label + ".stderr.log")
        stdout.write_text(result.stdout)
        stderr.write_text(result.stderr)
        commands.append({"label": label, "command": list(map(str, command)), "exitCode": result.returncode,
                         "seconds": round(time.monotonic() - start, 3), "stdoutSha256": sha(stdout), "stderrSha256": sha(stderr)})
        save()
        print(label + ": " + str(result.returncode), flush=True)
        if result.returncode:
            raise RuntimeError(label + " failed; see retained raw logs")
        return result.stdout

    dotnet = shutil.which(args.dotnet) or str(Path(args.dotnet).resolve())
    sdk = Path(dotnet).resolve().parent
    build_flags = ["-p:UseSharedCompilation=false", "-nr:false", "-m:1"]
    ordinary = ["src/Data.Transactions/Korda.Database.Transactions.csproj",
                "tests/Data.Transactions/Korda.Data.Transactions.Tests.csproj",
                "tests/Data.Transactions/ApiCheck/ApiCheck.csproj",
                "samples/api/data-transactions/Data.Transactions.Sample.csproj"]

    def restore(label, project, aot=False):
        command = [dotnet, "restore", project, "--locked-mode", *build_flags]
        if aot:
            command.extend(["-r", "linux-x64", "-p:PublishAot=true"])
        if args.offline_config:
            command.extend(["--configfile", args.offline_config.resolve(), "-p:NuGetAudit=false"])
        run(label, command)

    try:
        assert platform.system() == "Linux" and platform.machine() == "x86_64", "This run only accepts Linux x64"
        if args.design:
            pins = json.loads((ROOT / "tests/Data.Transactions/design-inputs.json").read_text())
            assert subprocess.check_output(["git", "-C", str(args.design), "rev-parse", "HEAD"], text=True).strip() == pins["commit"]
            for path, expected in pins["sha256"].items():
                assert sha(args.design / path) == expected, path
            manifest["pinnedDesignInputs"] = {"commit": pins["commit"], "verifiedCount": len(pins["sha256"])}
        assert run("sdk", [dotnet, "--version"]).strip() == "11.0.100"
        run("sdk-info", [dotnet, "--info"])
        provenance = prepare(run, args.native_build,
                             ["korda_transactions_fixture", "nw_host_review_tests", "nw_transaction_tests"],
                             r"^native\.(host_review|transactions)$",
                             [ROOT / "native/cpp/tests/fixtures/transactions.cpp", ROOT / "native/cpp/tests/transaction_tests.cpp"])
        (output / "compiler-provenance.txt").write_text(provenance)
        native = args.native_build.resolve() / "fixtures/libkorda_transactions_fixture.so"
        run("native-elf-linker", ["readelf", "-p", ".comment", native])
        execution_env = env.copy()
        execution_env["LD_LIBRARY_PATH"] = str(native.parent) + (":" + env["LD_LIBRARY_PATH"] if env.get("LD_LIBRARY_PATH") else "")
        for index, project in enumerate(ordinary):
            restore("ordinary-restore-" + str(index), project)
            run("ordinary-build-" + str(index), [dotnet, "build", project, "-c", "Release", "--no-restore", *build_flags])
        run("managed-whitespace", [dotnet, "format", "whitespace", ".", "--folder", "--verify-no-changes", "--include",
                                    "src/Data.Transactions", "tests/Data.Transactions/Program.cs", "tests/Data.Transactions/NativeFixtureTransport.cs",
                                    "tests/Data.Transactions/ApiCheck/Program.cs", "samples/api/data-transactions/TransactionExamples.cs", "--verbosity", "quiet"])
        assembly = ROOT / "src/Data.Transactions/bin/Release/net11.0/Korda.Database.Transactions.dll"
        run("compiled-api-xml-reference", [dotnet, "tests/Data.Transactions/ApiCheck/bin/Release/net11.0/Korda.Transactions.ApiCheck.dll", ROOT, sdk, assembly])
        restore("aot-restore", ordinary[1], True)
        publish = output / "publish"
        run("aot-publish", [dotnet, "publish", ordinary[1], "-c", "Release", "-r", "linux-x64", "-p:PublishAot=true", "--no-restore", "-p:IlcTreatWarningsAsErrors=true", *build_flags, "-o", publish])
        executable = publish / "Korda.Data.Transactions.Tests"
        run("aot-execution", [executable, "--aot"], execution_env)
        run("aot-elf-linker", ["readelf", "-p", ".comment", executable])
        for artifact in [native, assembly, executable]:
            manifest["artifacts"][str(artifact)] = sha(artifact)
        for tool in [dotnet, shutil.which("clang++"), shutil.which("clang-format"), shutil.which("cmake")]:
            if tool and Path(tool).is_file():
                manifest.setdefault("toolHashes", {})[str(Path(tool).resolve())] = sha(Path(tool).resolve())
        final = source_identity()
        assert final == initial, "Implementation/lock bytes changed during verification"
        manifest["status"] = "passed"
        manifest["sourceStable"] = True
        manifest["actualNativeAotExecuted"] = True
        manifest["localRestoreAuditRefresh"] = "unavailable-local-feed" if args.offline_config else "normal-repository-policy"
        manifest["fullManagedFormatter"] = "existing-CI-required; local-folder-whitespace-check-only"
    except Exception as error:
        manifest["status"] = "failed"
        manifest["failure"] = str(error)
        save()
        raise
    save()
    print("PASS: B05 component, exact API, C++ Host coordinator, actual Native AOT; provider-specific fault suites remain separate.")


if __name__ == "__main__":
    main()
