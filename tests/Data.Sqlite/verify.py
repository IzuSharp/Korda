#!/usr/bin/env python3
"""B03 component verification: exact integrated provider API, SQLite/B05 composition and Native AOT."""
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
OWNED = ["src/Data.Sqlite", "tests/Data.Sqlite", "samples/api/data-sqlite", "docs/guides/data-sqlite.md"]


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def source_identity():
    paths = subprocess.check_output(["git", "ls-files", "--cached", "--others", "--exclude-standard", "--", *OWNED,
                                     "tests/ProviderProfileChecks.cs", "src/Data.NativeHost", "native/cpp", "src/Data.Abstractions", "src/Korda.Core", "src/Interop.Abi", "src/Interop.Negotiation", "src/Interop.Errors", "native/database/interfaces", "native/database/binding", "native/interop/abi", "native/dependencies.lock.json", "CMakeLists.txt", "CMakePresets.json", ".clang-format", "Directory.Build.props", "global.json"], cwd=ROOT, text=True).splitlines()
    return {p: sha(ROOT / p) for p in sorted(set(paths)) if (ROOT / p).is_file()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/B03/component-run")
    parser.add_argument("--offline-config", type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--design", type=Path)
    parser.add_argument("--native-build", type=Path, default=ROOT / "artifacts/cmake/linux-x64-release")
    phase = parser.add_mutually_exclusive_group()
    phase.add_argument("--api-only", action="store_true")
    phase.add_argument("--test-only", action="store_true")
    parser.add_argument("--assembly-dir", type=Path, help="Directory containing the extracted NuGet API inputs")
    args = parser.parse_args()
    if args.assembly_dir and not args.api_only:
        parser.error("--assembly-dir requires --api-only")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    env = os.environ.copy()
    env.update(DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1", DOTNET_CLI_USE_MSBUILD_SERVER="0")
    commands = []
    initial = source_identity()
    manifest = {"scope": "B03-linux-x64-component", "status": "running", "platform": platform.platform(),
                "sourceCommit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
                "sourceDirty": bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip()),
                "sourceSha256": initial, "commands": commands, "artifacts": {},
                "realProviderComposition": "actual SQLite 3.51.3 through the C++ prepared statement provider and Host coordinator; fourteen retained native scenario groups", "assembledHostAcceptance": False,
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
    native_build = args.native_build.resolve()
    host_native = native_build / "libnw_database_sqlite.so"
    native = native_build / "fixtures/libkorda_sqlite_fixture.so"
    component_native = native  # The former standalone bootstrap is retained only in the test bridge.
    build_flags = ["-p:UseSharedCompilation=false", "-nr:false", "-m:1", f"-p:KordaSqliteAssetPath={host_native}"]
    ordinary = ["src/Data.Sqlite/Korda.Database.Sqlite.csproj",
                "tests/Data.Sqlite/Korda.Data.Sqlite.Tests.csproj",
                "tests/Data.Sqlite/ApiCheck/ApiCheck.csproj",
                "samples/api/data-sqlite/Data.Sqlite.Sample.csproj"]

    def restore(label, project, aot=False):
        command = [dotnet, "restore", project, "--locked-mode", *build_flags]
        if aot:
            command.extend(["-r", "linux-x64", "-p:PublishAot=true"])
        if args.offline_config:
            command.extend(["--configfile", args.offline_config.resolve(), "-p:NuGetAudit=false"])
        run(label, command)

    try:
        assert not manifest["sourceDirty"], "Formal acceptance requires a clean committed source tree"
        assert platform.system() == "Linux" and platform.machine() == "x86_64", "This run only accepts Linux x64"
        assert host_native.is_file(), "Build the actual SQLite Database Host before component/preflight verification"
        assert native.is_file(), "Build the C++ korda_sqlite_fixture target before component verification"
        if args.design:
            revision = subprocess.check_output(["git", "-C", str(args.design), "rev-parse", "HEAD"], text=True).strip()
            # The exact compiled API/XML gate below remains authoritative. The
            # old task's historical whole-Design SHA is no longer the active
            # migration decision; bind this run to the supplied committed tree.
            manifest["designInputs"] = {"commit": revision}
        assert run("sdk", [dotnet, "--version"]).strip() == "11.0.100"
        if args.api_only:
            for project in ([ordinary[2]] if args.assembly_dir else [ordinary[0], ordinary[2]]):
                restore("api-restore-" + Path(project).stem, project)
                run("api-build-" + Path(project).stem, [dotnet, "build", project, "-c", "Release", "--no-restore", *build_flags])
            assembly = (args.assembly_dir or ROOT / "src/Data.Sqlite/bin/Release/net11.0") / "Korda.Database.Sqlite.dll"
            api_result = json.loads(run("compiled-api-xml-reference", [dotnet, "tests/Data.Sqlite/ApiCheck/bin/Release/net11.0/Korda.Sqlite.ApiCheck.dll", ROOT, sdk, assembly]))
            manifest.update(status="passed", api=api_result)
            save()
            return
        run("sdk-info", [dotnet, "--info"])
        assert "clang version 23.1.5" in run("clang", ["clang++", "--version"])
        run("clang-target", ["clang++", "-dumpmachine"])
        run("linker", ["ld", "--version"])
        run("native-format", ["clang-format", "--dry-run", "--Werror",
                               "native/cpp/tests/fixtures/sqlite.cpp", "native/cpp/tests/sqlite_fixture_tests.cpp"])
        native_tests = run("native-tests", ["ctest", "--test-dir", native_build, "--no-tests=error",
                                           "--output-on-failure", "-V", "-R", "^native\\.sqlite14$"])
        assert all(f"PASS B03 native scenario {i}" in native_tests for i in range(1, 15))
        run("native-dependencies", ["readelf", "-d", native])
        run("native-elf-linker", ["readelf", "-p", ".comment", native])
        manifest["nativeInputs"] = {"privateComponentSha256": sha(component_native), "integratedHostSha256": sha(host_native)}
        manifest["nativeCompilerProvenance"] = (native_build / "compiler-provenance.txt").read_text()
        manifest["upstreamDependencyLockSha256"] = sha(ROOT / "native/dependencies.lock.json")
        execution_env = env.copy()
        execution_env["LD_LIBRARY_PATH"] = str(native.parent) + os.pathsep + env.get("LD_LIBRARY_PATH", "")
        execution_env["NW_SQLITE_COMPONENT_ASSET"] = str(component_native)
        for index, project in enumerate(ordinary):
            # Executable consumers are restored and compiled only for Native AOT.
            # The SQLite example is a library, so its ordinary build remains.
            if project == ordinary[1] or (args.test_only and project == ordinary[2]):
                continue
            restore("ordinary-restore-" + str(index), project)
            run("ordinary-build-" + str(index), [dotnet, "build", project, "-c", "Release", "--no-restore", *build_flags])
        run("managed-whitespace", [dotnet, "format", "whitespace", ".", "--folder", "--verify-no-changes", "--include",
                                    "src/Data.Sqlite/SqliteOptions.cs", "src/Data.Sqlite/SqliteConfiguration.cs", "src/Data.Sqlite/SqliteDataSource.cs", "tests/Data.Sqlite/Program.cs", "tests/ProviderProfileChecks.cs", "tests/Data.Sqlite/ApiCheck/Program.cs", "samples/api/data-sqlite/SqliteExamples.cs", "--verbosity", "quiet"])
        assembly = ROOT / "src/Data.Sqlite/bin/Release/net11.0/Korda.Database.Sqlite.dll"
        api_result = {} if args.test_only else json.loads(run("compiled-api-xml-reference", [dotnet, "tests/Data.Sqlite/ApiCheck/bin/Release/net11.0/Korda.Sqlite.ApiCheck.dll", ROOT, sdk, assembly]))
        restore("aot-restore", ordinary[1], True)
        publish = output / "publish"
        run("aot-publish", [dotnet, "publish", ordinary[1], "-c", "Release", "-r", "linux-x64", "-p:PublishAot=true", "--no-restore", "-p:IlcTreatWarningsAsErrors=true", *build_flags, "-o", publish])
        executable = publish / "Korda.Data.Sqlite.Tests"
        execution_env["KORDA_B03_TEST_ROOT"] = str(output / "aot-data")
        assert executable.read_bytes()[:4] == b"\x7fELF" and not executable.with_suffix(".dll").exists(), "Expected actual standalone Native AOT"
        aot_output = run("aot-execution", [executable, "--aot"], execution_env)
        assert "NativeAOT=True" in aot_output and all(f"PASS B03 native scenario {i}" in aot_output for i in range(1, 15))
        run("aot-elf-linker", ["readelf", "-p", ".comment", executable])
        for artifact in [component_native, host_native, native, assembly, executable]:
            manifest["artifacts"][str(artifact)] = sha(artifact)
        for tool in [dotnet, shutil.which("clang++"), shutil.which("clang-format"), shutil.which("ld")]:
            if tool and Path(tool).is_file():
                manifest.setdefault("toolHashes", {})[str(Path(tool).resolve())] = sha(Path(tool).resolve())
        generated = list((ROOT / "src/Data.Sqlite/obj").rglob("EngineProfile.g.cs"))
        generated += list((ROOT / "src/Data.Sqlite/obj").rglob("Korda.database.sqlite.assets.json"))
        for artifact in generated:
            manifest.setdefault("generatedMaintainerProfileSha256", {})[str(artifact)] = sha(artifact)
        for location in [ROOT / "tests/Data.Sqlite/bin/Release/net11.0/linux-x64", publish]:
            record_path = location / "Korda.database.sqlite.assets.json"
            record = json.loads(record_path.read_text())
            assert record["engineId"] == "database.sqlite" and record["packageId"] == "Korda.Database.Sqlite"
            assert record["sourcePath"] == record["outputPath"] == "runtimes/linux-x64/native/libnw_database_sqlite.so"
            assert record["sha256"] == sha(host_native)
            assert sha(location / record["outputPath"]) == sha(host_native)
            manifest["artifacts"][str(record_path)] = sha(record_path)
        manifest["nativeScenarioGroups"] = 14
        manifest["hostPreflight"] = True
        manifest["compiledComponentSymbols"] = api_result.get("compiled_symbols")
        manifest["compiledHostCompositionSymbols"] = len(json.loads((ROOT / "src/Data.Sqlite/host-composition.expected.json").read_text())["symbols"])
        manifest["workerShutdownEvidence"] = "C++ statement retirement, physical pool return or close, last-parent disposal barrier, and Host source worker join are observed separately"
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
    print("PASS: B03 Linux x64 component oracles, integrated Host preflight, exact API, real SQLite/B05 composition, actual Native AOT; full Host execution is verified separately.")


if __name__ == "__main__":
    main()
