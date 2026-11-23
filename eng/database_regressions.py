#!/usr/bin/env python3
"""Run retained database C# contracts and their C++ native ports on real providers."""
import argparse
import json
import os
from pathlib import Path
import shutil
import sys

import build

ROOT = build.ROOT
FLAGS = ["-m:1", "-nr:false", "-p:UseSharedCompilation=false"]


SUITES = [
    ("Data.NativeHost", "NativeWeave.Data.NativeHost.Tests", []),
    ("Data.Migrations", "NativeWeave.Data.Migrations.Tests", []),
    ("Data.Materialization", "NativeWeave.Data.Materialization.Tests", ["results"]),
    ("Query.Fluent", "NativeWeave.Query.Fluent.Tests", ["results"]),
    ("Orm.Relations", "NativeWeave.Orm.Relations.Tests", []),
]


def managed_suites(only, output, pg, run, run_isolated):
    for name, assembly, parameters in SUITES:
        if only and name != only:
            continue
        project = ROOT / "tests" / name / (assembly + ".csproj")
        publish = output / name / "aot"
        run(name + "-aot-restore", ["dotnet", "restore", project, "--locked-mode", "-r", "linux-x64", "-p:PublishAot=true", *FLAGS])
        run(name + "-aot-publish", ["dotnet", "publish", project, "-c", "Release", "-r", "linux-x64",
                                   "-p:PublishAot=true", "--no-restore", "-p:IlcTreatWarningsAsErrors=true", *FLAGS, "-o", publish])
        executable = publish / assembly
        if executable.read_bytes()[:4] != b"\x7fELF" or executable.with_suffix(".dll").exists():
            raise RuntimeError("Expected an actual standalone Native AOT executable")
        aot_args = [output / name / "aot-results", "--require-aot"] if parameters else (
            ["--require-aot"] if name == "Orm.Relations" else [])
        run_isolated(name + "-aot", [executable, *aot_args])
        if name == "Data.Migrations":
            run_isolated(name + "-process-aot", [sys.executable, "tests/Data.Migrations/cross_process.py",
                "--postgres-bin", pg, "--output", output / name / "process-aot", "--", executable])

    if not only or only == "Orm.Relations":
        run("relations-type-errors", [sys.executable, "tests/Orm.Relations/compile_negative.py"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--design", type=Path, required=True)
    parser.add_argument("--plan", type=Path, required=True)
    parser.add_argument("--postgres-bin", type=Path)
    parser.add_argument("--native-build", type=Path, default=ROOT / "artifacts/cmake/linux-x64-release")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--suite", choices=[row[0] for row in SUITES])
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    build.LOGS = output / "logs"
    pg = args.postgres_bin or Path(os.environ.get("NW_POSTGRES_BIN", ""))
    if not (pg / "postgres").is_file():
        found = shutil.which("postgres")
        if not found:
            raise RuntimeError("PostgreSQL 18 server tools are required for the retained TLS/failure oracles")
        pg = Path(found).resolve().parent
    pg = pg.resolve()
    design = args.design.resolve()
    native_build = args.native_build.resolve()
    if not (native_build / "CMakeCache.txt").is_file():
        raise RuntimeError("Configure and build the shared linux-x64-release CMake preset before regression execution")
    if not os.environ.get("NW_TEST_POSTGRES"):
        raise RuntimeError("NW_TEST_POSTGRES is required")
    env = os.environ.copy()
    env["NATIVEWEAVE_TEST_POSTGRES_URL"] = env["NW_TEST_POSTGRES"]
    env.update(DOTNET_CLI_USE_MSBUILD_SERVER="0", MSBUILDDISABLENODEREUSE="1")
    records = []
    initial = build.source()

    def run(label, command):
        result = build.run(command, label, env=env)
        log = build.LOGS / (label + ".log")
        records.append({"name": label, "exitCode": 0, "log": str(log.relative_to(output)),
                        "sha256": build.digest(log)})
        (output / "commands.json").write_text(json.dumps(records, indent=2) + "\n")
        return result

    def component(name, *extra):
        run(name.lower(), [sys.executable, f"tests/{name}/verify.py", "--output", output / name, *extra])

    def run_isolated(label, command):
        # These full suites own fixed tables and migration ledgers. Preserve their
        # fresh-database precondition independently for each runtime/process cell.
        run(label, [sys.executable, "tests/Data.NativeHost/run.py", "--postgres-bin", pg,
                    "--output", output / "postgres" / label, "--", *command])

    if args.suite:
        managed_suites(args.suite, output, pg, run, run_isolated)
        if build.source() != initial:
            raise RuntimeError("Source identity changed during component execution")
        (output / "result.json").write_text(json.dumps({"status": "passed", "suite": args.suite,
            "source": initial, "modes": ["aot"], "commands": records}, indent=2) + "\n")
        return

    # These tests are the ports of the old native component assertions. Requiring
    # the complete inventory prevents a missing target or empty regex from being
    # reported as a successful replacement for a Cargo test gate.
    inventory = json.loads(run("native-test-inventory", ["ctest", "--test-dir", native_build,
                                                        "--show-only=json-v1"]))
    expected = {"native.interop", "native.contracts", "native.query", "native.scalar",
                "native.execution", "native.administration", "native.provider"}
    observed = {test["name"] for test in inventory["tests"]}
    if not expected.issubset(observed):
        raise RuntimeError("Missing retained native CTest ports: " + ", ".join(sorted(expected - observed)))
    run("native-component-regressions", ["ctest", "--test-dir", native_build,
                                         "--output-on-failure", "--no-tests=error"])
    # The administration port has additional PostgreSQL-only migration/catalog
    # assertions. Keep its destructive tables isolated from every other suite.
    run_isolated("native-administration-postgresql", [sys.executable, "-c",
        'import os,sys; os.execv(sys.argv[1], [sys.argv[1], os.environ["NW_TEST_POSTGRES"]])',
        native_build / "tests/nw_administration_tests"])
    # Exercise physical PostgreSQL input admission in its own real database.
    run_isolated("native-input-postgresql", [sys.executable, "-c",
        'import os,sys; os.execv(sys.argv[1], [sys.argv[1], "input-postgresql", os.environ["NW_TEST_POSTGRES"]])',
        native_build / "tests/nw_host_review_tests"])
    # Require explicit same-connection protocol synchronization and retirement.
    run_isolated("native-sync-postgresql", [sys.executable, "-c",
        'import os,sys; os.execv(sys.argv[1], [sys.argv[1], os.environ["NW_TEST_POSTGRES"]])',
        native_build / "tests/nw_postgres_sync_tests"])
    version = run("postgres-version", [pg / "psql", env["NW_TEST_POSTGRES"], "-X", "-At", "-v", "ON_ERROR_STOP=1",
                                       "-c", "SHOW server_version"])
    if not version.strip().startswith("18."):
        raise RuntimeError("The actual regression server must be PostgreSQL 18")
    run("canonical-query-abi", [sys.executable, "tests/Query.Binding/generate.py", "--check", "--design", design])
    component("Interop.Negotiation", "--plan-root", args.plan.resolve(), "--design-root", design)
    component("Interop.Execution")
    component("Data.Abstractions", "--native-build", native_build)
    component("Data.Sqlite", "--design", design, "--native-build", native_build)
    # Existing provider tests include real TLS policy and actual connection failures.
    component("Data.Postgres", "--postgres-bin", pg, "--design-root", design, "--native-build", native_build)
    component("Data.Transactions", "--design", design, "--native-build", native_build)
    component("Data.Streaming", "--design-root", design, "--native-build", native_build)
    component("Data.Codecs", "--native-build", native_build)
    component("Query.Binding", "--design", design, "--native-build", native_build)

    # The internal cross-provider query oracle uses a maintainer-only combined Host.
    # Its binary is never added to either provider package.
    component("Query.Native", "--design", design, "--postgres-bin", pg, "--host-source", ROOT,
              "--native-build", native_build, "--native-module", native_build / "libnativeweave_database_host.so")

    # Execute the actual published test applications against isolated databases.
    managed_suites(None, output, pg, run, run_isolated)
    if build.source() != initial:
        raise RuntimeError("Source identity changed during regression execution")
    (output / "result.json").write_text(json.dumps({"task": "DB-CPP-01", "status": "passed",
        "source": initial, "platform": "linux-x64", "postgresVersion": version.strip(),
        "providers": ["sqlite", "postgresql"], "modes": ["aot"], "commands": records,
        "nativeBuild": str(native_build), "nativeTests": sorted(observed),
        "queryOracle": {"cases": 52, "planCommit": "1fe52a8bd792ccdbb4bbea17ba59b0ad0c24349d",
                         "realNativeAot": True},
        "performanceWork": False}, indent=2) + "\n")


if __name__ == "__main__":
    main()
