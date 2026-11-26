#!/usr/bin/env python3
"""Verify independent micro-ORM Native AOT support on two real databases.

This is a prerequisite probe, not a benchmark or an allocation measurement.
All consumers, packages, native builds and databases live in a new external output
directory. Every command and raw log is retained, including failed attempts.
"""

import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
REPOSITORY = HERE.parents[2]
PROVIDERS = ("sqlite", "postgresql")
GENERATED = (
    "obj/Release/net11.0/linux-x64/generated/Dapper.AOT.Analyzers/"
    "Dapper.CodeAnalysis.DapperInterceptorGenerator/OrmProbe.generated.cs"
)
SQLITE_SOURCE_SHA256 = "9512509b1bccb7461f79bea8aad6280ae4699e925fa4804381b71f59e7efb0c5"


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + "\n")


def executable(value):
    found = shutil.which(str(value))
    if found is None:
        raise FileNotFoundError("Executable is not available: " + str(value))
    return Path(found).resolve()


def arguments():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True, help="New directory outside the repository")
    parser.add_argument("--dotnet", default="dotnet", help="Path to the real .NET 11.0.100 dotnet host")
    parser.add_argument("--clang", default="clang", help="Path to the pinned Clang 23.1.5 compiler")
    parser.add_argument("--sqlite-source", type=Path, required=True, help="Pinned SQLite 3.51.3 sqlite3.c")
    parser.add_argument("--postgres-bin", type=Path, default=os.environ.get("NW_POSTGRES_BIN"),
                        help="Directory with PostgreSQL 18.0 initdb, postgres and pg_ctl")
    parser.add_argument("--sdk-cache", type=Path, help="Optional NuGet cache containing SDK tooling only")
    parser.add_argument("--feed", type=Path, help="Optional additional package feed; record its origin separately")
    args = parser.parse_args()
    if args.postgres_bin is None:
        parser.error("--postgres-bin or NW_POSTGRES_BIN is required")
    return args


def main():
    args = arguments()
    output = args.output.resolve()
    if output.is_relative_to(REPOSITORY):
        raise ValueError("Use an output directory outside the repository for the ordinary consumer")
    output.mkdir(parents=True, exist_ok=False)
    logs = output / "logs"
    logs.mkdir()
    records = []
    env = os.environ.copy()
    running_pg = False
    pg = None

    def run(name, command, cwd=output, check=True):
        command = list(map(str, command))
        started = datetime.datetime.now(datetime.UTC).isoformat()
        path = logs / (name + ".log")
        with path.open("wb") as stream:
            completed = subprocess.run(command, cwd=cwd, env=env, stdout=stream, stderr=subprocess.STDOUT)
        records.append({"name": name, "command": command, "cwd": str(cwd), "started_utc": started,
                        "finished_utc": datetime.datetime.now(datetime.UTC).isoformat(),
                        "exit_code": completed.returncode, "log": str(path.relative_to(output)),
                        "log_sha256": sha(path)})
        save(output / "evidence/commands.json", records)
        print(f"{name}: exit={completed.returncode}; log={path}", flush=True)
        if completed.returncode and check:
            raise RuntimeError(f"{name} failed; retained raw output: {path}")
        return path.read_text(errors="replace")

    try:
        dotnet, clang = executable(args.dotnet), executable(args.clang)
        pg = args.postgres_bin.resolve()
        for name in ("initdb", "postgres", "pg_ctl"):
            executable(pg / name)
        sqlite_source = args.sqlite_source.resolve()
        if sha(sqlite_source) != SQLITE_SOURCE_SHA256:
            raise ValueError("SQLite source bytes differ from the pinned 3.51.3 input")
        env.update({"PATH": str(clang.parent) + ":" + str(dotnet.parent) + ":" + env["PATH"],
                    "DOTNET_ROOT": str(dotnet.parent), "DOTNET_CLI_HOME": str(output / "dotnet-home"),
                    "NUGET_PACKAGES": str(output / "cache"), "DOTNET_NOLOGO": "true",
                    "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
                    "MSBUILDDISABLENODEREUSE": "1", "LD_LIBRARY_PATH": str(output / "native")})
        source = output / "source"
        source.mkdir()
        for name in ("global.json", "Directory.Build.props", "NuGet.Config", "Program.cs"):
            shutil.copyfile(HERE / name, source / name)
        for provider in PROVIDERS:
            (source / provider).mkdir()
            for name in ("OrmProbe.csproj", "packages.lock.json"):
                shutil.copyfile(HERE / provider / name, source / provider / name)
        config = ET.parse(source / "NuGet.Config")
        if args.feed:
            ET.SubElement(config.find("packageSources"), "add", key="additional", value=str(args.feed.resolve()))
        if args.sdk_cache:
            cache = args.sdk_cache.resolve()
            fallback = output / "sdk-cache"
            fallback.mkdir()
            prefixes = ("microsoft.netcore.app.", "microsoft.aspnetcore.app.", "microsoft.dotnet.",
                        "microsoft.net.illink.", "runtime.linux-x64.microsoft.dotnet.")
            for folder in cache.iterdir():
                if folder.is_dir() and folder.name.lower().startswith(prefixes):
                    (fallback / folder.name).symlink_to(folder, target_is_directory=True)
            folders = ET.SubElement(config.getroot(), "fallbackPackageFolders")
            ET.SubElement(folders, "clear")
            ET.SubElement(folders, "add", key="sdk", value=str(fallback))
        config.write(source / "NuGet.Config")
        if run("sdk-version", [dotnet, "--version"], source).strip() != "11.0.100":
            raise ValueError("The support probe pins SDK 11.0.100")
        if "clang version 23.1.5" not in run("clang-version", [clang, "--version"]):
            raise ValueError("The support probe pins Clang 23.1.5")
        if "18.0" != run("postgres-version", [pg / "postgres", "--version"]).strip().split()[-1]:
            raise ValueError("The support probe pins PostgreSQL server 18.0")
        native = output / "native"
        native.mkdir()
        run("sqlite-native-build", [clang, "-O3", "-DNDEBUG", "-DSQLITE_THREADSAFE=1",
            "-DSQLITE_ENABLE_COLUMN_METADATA", "-fPIC", "-shared", sqlite_source, "-o",
            native / "libsqlite3.so", "-lm", "-ldl", "-lpthread"])
        generated = []
        for provider in PROVIDERS:
            project = source / provider
            run(provider + "-publish", [dotnet, "publish", "OrmProbe.csproj", "-m:1", "-nr:false",
                "-p:UseSharedCompilation=false", "-p:RestoreLockedMode=true", "-c", "Release", "-r",
                "linux-x64", "-p:PublishAot=true", "-o", "out"], project)
            emitted = project / GENERATED
            text = emitted.read_text()
            count = text.count("[global::System.Runtime.CompilerServices.InterceptsLocationAttribute(")
            if count != 7 or any(name not in text for name in ("QuerySingleAsync", "QueryUnbufferedAsync", "QuerySingle", "ExecuteAsync")):
                raise ValueError("Expected seven intercepted Dapper call sites in " + str(emitted))
            target = output / "evidence" / (provider + ".generated.cs")
            shutil.copyfile(emitted, target)
            generated.append({"provider": provider, "generated_source_path": str(emitted),
                              "retained_path": str(target.relative_to(output)), "sha256": sha(target),
                              "intercepted_calls": count, "lock_sha256": sha(project / "packages.lock.json")})
        save(output / "evidence/generated-code.json", generated)
        run("sqlite-execute", [source / "sqlite/out/OrmProbe", output / "probe.db"])
        run("postgres-initdb", [pg / "initdb", "-D", output / "pgdata", "-U", "korda", "-A", "trust", "--no-locale"])
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            port = listener.getsockname()[1]
        running_pg = True
        run("postgres-start", [pg / "pg_ctl", "-D", output / "pgdata", "-l", logs / "postgres-server.log",
            "-o", f"-k '' -h 127.0.0.1 -p {port}", "-w", "start"])
        run("postgresql-execute", [source / "postgresql/out/OrmProbe",
            f"Host=127.0.0.1;Port={port};Database=postgres;Username=korda;Pooling=false;SSL Mode=Disable"])
        run("postgres-stop", [pg / "pg_ctl", "-D", output / "pgdata", "-m", "fast", "-w", "stop"])
        running_pg = False
        packages = []
        for path in sorted((output / "cache").rglob("*.nupkg")):
            packages.append({"path": str(path.relative_to(output)), "bytes": path.stat().st_size, "sha256": sha(path)})
        save(output / "evidence/result.json", {"status": "passed", "purpose": "minimum micro-ORM Native AOT support only",
            "command_count": len(records), "sdk": "11.0.100", "orm": {"Dapper": "2.1.79", "Dapper.AOT": "1.1.0"},
            "providers": {"postgresql": "Npgsql 11.0.0 / PostgreSQL 18.0", "sqlite": "Microsoft.Data.Sqlite.Core 11.0.0 / SQLitePCLRaw.provider.sqlite3 2.1.11 / SQLite 3.51.3"},
            "sqlite_source": {"path": str(sqlite_source), "sha256": sha(sqlite_source)},
            "sqlite_library_sha256": sha(native / "libsqlite3.so"), "engine_options_fully_aligned_with_korda": False,
            "executable_sha256": {p: sha(source / p / "out/OrmProbe") for p in PROVIDERS},
            "results": {p: (logs / (p + "-execute.log")).read_text() for p in PROVIDERS},
            "input_sha256": {str(p.relative_to(HERE)): sha(p) for p in HERE.rglob("*") if p.is_file()},
            "consumer_sha256": {str(p.relative_to(source)): sha(p) for p in source.rglob("*") if p.is_file() and "obj" not in p.parts and "out" not in p.parts and "bin" not in p.parts},
            "downloaded_packages": packages,
            "limitations": ["No performance or allocation measurements", "SQLite async API is synchronous in the provider", "No actual-suspension, JSON, cancellation or high-concurrency coverage", "Native compile options must be aligned before comparisons"]})
        print("Two-provider Native AOT capability probe passed; no performance claim.", flush=True)
    except BaseException as error:
        save(output / "evidence/failure.json", {"status": "failed", "error": repr(error), "command_count": len(records)})
        raise
    finally:
        if running_pg and pg is not None:
            run("postgres-cleanup", [pg / "pg_ctl", "-D", output / "pgdata", "-m", "fast", "-w", "stop"], check=False)


if __name__ == "__main__":
    main()
