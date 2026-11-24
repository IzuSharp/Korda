#!/usr/bin/env python3
"""Build outside the product tree, then run independent allocation processes.

This is a short baseline/preflight. It neither performs nor claims the dedicated
eight-segment performance campaign. No source/project reference is accepted.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import time
import xml.etree.ElementTree as ET
import zipfile


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def run(command: list[str], cwd: Path, log: Path, env: dict[str, str], timeout: int = 600) -> None:
    log.parent.mkdir(parents=True, exist_ok=True)
    if log.exists():
        attempt = 1
        previous = log.with_name(log.stem + f".previous-{attempt:03}" + log.suffix)
        while previous.exists():
            attempt += 1
            previous = log.with_name(log.stem + f".previous-{attempt:03}" + log.suffix)
        shutil.copy2(log, previous)
    with log.open("w") as output:
        output.write(json.dumps({"command": command, "cwd": str(cwd)}) + "\n")
        output.flush()
        result = subprocess.run(command, cwd=cwd, env=env, stdout=output, stderr=subprocess.STDOUT, timeout=timeout)
    if result.returncode:
        raise RuntimeError(f"Exit {result.returncode}; see {log}")


def package_metadata(path: Path) -> tuple[str, str, str]:
    with zipfile.ZipFile(path) as archive:
        spec = next(name for name in archive.namelist() if name.endswith(".nuspec"))
        root = ET.fromstring(archive.read(spec))
        values = {element.tag.rsplit("}", 1)[-1]: element.text for element in root.iter()}
        repository = next(element for element in root.iter() if element.tag.rsplit("}", 1)[-1] == "repository")
        return str(values["id"]), str(values["version"]), repository.attrib["commit"]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--artifacts", type=Path, required=True, help="Directory with postgresql/ and sqlite/ feed/ and release.json")
    parser.add_argument("--work", type=Path, required=True, help="Fresh directory outside Korda for copies, restore, publish and the dedicated PostgreSQL cluster")
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--dotnet", type=Path, required=True, help="Real dotnet binary, not a wrapper")
    parser.add_argument("--clang-bin", type=Path, required=True)
    parser.add_argument("--postgres-bin", type=Path, required=True)
    parser.add_argument("--support-cache", type=Path, required=True, help="Existing cache containing only SDK support packages to stage")
    parser.add_argument("--warmup", type=int, default=64)
    parser.add_argument("--iterations", type=int, default=128)
    parser.add_argument("--processes", type=int, default=3)
    parser.add_argument("--skip-build", action="store_true", help="Resume already verified publish outputs in the same work directory")
    args = parser.parse_args()
    if args.warmup < 50 or args.iterations < 1 or args.processes < 3:
        parser.error("Require warmup >= 50, iterations >= 1, and at least three independent processes per provider")
    source = Path(__file__).resolve().parent
    product = source.parents[2]
    args.work = args.work.resolve(); args.evidence = args.evidence.resolve()
    if args.work == product or product in args.work.parents:
        parser.error("--work must be outside the product repository")
    args.work.mkdir(parents=True, exist_ok=True); args.evidence.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ)
    env["DOTNET_ROOT"] = str(args.dotnet.resolve().parent)
    env["PATH"] = str(args.clang_bin.resolve()) + os.pathsep + str(args.dotnet.resolve().parent) + os.pathsep + env["PATH"]
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    env["DOTNET_CLI_HOME"] = str(args.work / "cli-home")
    support = args.work / "support-feed"; support.mkdir(exist_ok=True)
    support_extracted = args.work / "support-extracted"; support_extracted.mkdir(exist_ok=True)
    support_names = ("microsoft.dotnet.ilcompiler", "runtime.linux-x64.microsoft.dotnet.ilcompiler", "microsoft.net.illink.tasks", "microsoft.netcore.app.runtime.linux-x64", "microsoft.aspnetcore.app.runtime.linux-x64", "microsoft.netcore.app.runtime.nativeaot.linux-x64")
    support_packages = []
    for name in support_names:
        package = args.support_cache.resolve() / name / "11.0.0" / f"{name}.11.0.0.nupkg"
        if not package.is_file():
            raise FileNotFoundError(f"Missing exact SDK support package: {package}")
        target = support / package.name
        if not target.exists(): target.symlink_to(package)
        fallback = support_extracted / name
        fallback.mkdir(exist_ok=True)
        if not (fallback / "11.0.0").exists(): (fallback / "11.0.0").symlink_to(package.parent, target_is_directory=True)
        support_packages.append({"path": str(package), "sha256": digest(package)})
    manifest: dict = {"schema": "DB-Perf-01.allocation-build.v1", "scope": "short preflight; elapsed time is diagnostic only", "source_files": {}, "support_packages": support_packages, "providers": {}, "runs": []}
    files = ("AllocationProbe.csproj", "Program.cs", "Directory.Build.props", "global.json")
    manifest["source_files"] = {name: digest(source / name) for name in files}
    run([str(args.dotnet), "--info"], args.work, args.evidence / "dotnet-info.log", env)
    run([str(args.clang_bin / "clang"), "--version"], args.work, args.evidence / "clang-version.log", env)
    run([str(args.postgres_bin / "postgres"), "--version"], args.work, args.evidence / "postgres-version.log", env)
    for provider, msbuild in (("postgresql", "PostgreSql"), ("sqlite", "Sqlite")):
        candidate = args.artifacts.resolve() / provider
        release = json.loads((candidate / "release.json").read_text())
        packages = list((candidate / "feed").glob("*.nupkg"))
        if len(packages) != 1: raise ValueError(f"Expected exactly one package for {provider}")
        package = packages[0]; package_id, version, commit = package_metadata(package)
        registered = next(item for item in release["packages"] if item["id"] == package_id)
        if registered["source_commit"] != commit: raise ValueError("Package/release commit mismatch")
        if package_id != f"Korda.Database.{msbuild}": raise ValueError("Provider package identity mismatch")
        external = args.work / provider / "app"; external.mkdir(parents=True, exist_ok=True)
        if not args.skip_build:
            for name in files: shutil.copy2(source / name, external / name)
            config = ET.Element("configuration"); sources = ET.SubElement(config, "packageSources")
            ET.SubElement(sources, "clear")
            ET.SubElement(sources, "add", key="candidate", value=str(candidate / "feed"))
            ET.SubElement(sources, "add", key="sdk-support", value=str(support))
            fallbacks = ET.SubElement(config, "fallbackPackageFolders")
            ET.SubElement(fallbacks, "clear")
            ET.SubElement(fallbacks, "add", key="sdk-support", value=str(support_extracted))
            ET.ElementTree(config).write(external / "NuGet.Config", encoding="utf-8", xml_declaration=True)
            build_env = dict(env); build_env["NUGET_PACKAGES"] = str(args.work / provider / "packages")
            run([str(args.dotnet), "publish", "AllocationProbe.csproj", "-c", "Release", "-r", "linux-x64", "--self-contained", "true",
                 "-o", str(external / "publish"), "-m:1", "-p:BuildInParallel=false", "-p:UseSharedCompilation=false",
                 "-p:IlcSingleThreaded=true", "-p:NuGetAudit=false", "--disable-build-servers",
                 f"-p:Provider={msbuild}", f"-p:KordaVersion={version}", f"-p:CppCompilerAndLinker={args.clang_bin / 'clang'}"],
                external, args.evidence / f"{provider}-publish.log", build_env, timeout=1200)
        for name in files:
            if digest(external / name) != digest(source / name): raise ValueError(f"External source differs: {name}")
        assets = json.loads((external / "obj/project.assets.json").read_text())
        kordas = sorted(key for key in assets["libraries"] if key.startswith("Korda."))
        if kordas != [f"{package_id}/{version}"]: raise ValueError(f"Unexpected Korda package closure: {kordas}")
        executable = external / "publish/AllocationProbe"
        native_path = external / "publish" / registered["native"]["path"]
        if not native_path.is_file():
            matches = list((external / "publish").rglob(Path(registered["native"]["path"]).name))
            if len(matches) != 1: raise ValueError("Published native asset is missing or ambiguous")
            native_path = matches[0]
        if digest(native_path) != registered["native"]["sha256"]: raise ValueError("Published native asset hash differs from release receipt")
        manifest["providers"][provider] = {"package_id": package_id, "version": version, "source_commit": commit,
            "package_path": str(package), "package_sha256": digest(package), "executable_path": str(executable), "executable_sha256": digest(executable),
            "native_path": str(native_path), "native_sha256": digest(native_path), "resolved_korda_packages": kordas,
            "release_receipt_sha256": digest(candidate / "release.json")}
        shutil.copy2(external / "obj/project.assets.json", args.evidence / f"{provider}-project.assets.json")
        shutil.copy2(candidate / "release.json", args.evidence / f"{provider}-release.json")
    (args.evidence / "build-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    pgdata = args.work / "postgres-data"
    if pgdata.exists(): raise ValueError("Refuse to reuse an existing PostgreSQL cluster; choose a fresh --work or remove only a known stopped preflight cluster")
    run([str(args.postgres_bin / "initdb"), "-D", str(pgdata), "--auth=trust", "--no-locale", "--encoding=UTF8"],
        args.work, args.evidence / "postgres-initdb.log", env)
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0)); port = listener.getsockname()[1]
    postgres_log = (args.evidence / "postgres-server.log").open("w")
    server_command = [str(args.postgres_bin / "postgres"), "-D", str(pgdata), "-h", "127.0.0.1", "-p", str(port), "-k", "",
                      "-c", "fsync=on", "-c", "synchronous_commit=on", "-c", "full_page_writes=on"]
    server = subprocess.Popen(server_command, stdout=postgres_log, stderr=subprocess.STDOUT, env=env)
    manifest["postgres_cluster"] = {"data_directory": str(pgdata), "process_id": server.pid, "port": port, "command": server_command,
                                     "dedicated_to_this_preflight": True, "unix_socket_directory": "", "physical_host_exclusivity": "not established"}
    try:
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            if server.poll() is not None: raise RuntimeError("The dedicated PostgreSQL process exited; see postgres-server.log")
            ready = subprocess.run([str(args.postgres_bin / "pg_isready"), "-h", "127.0.0.1", "-p", str(port)], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
            if ready.returncode == 0: break
            time.sleep(0.1)
        else: raise TimeoutError("PostgreSQL readiness timed out")
        for repetition in range(1, args.processes + 1):
            order = ("postgresql", "sqlite") if repetition % 2 else ("sqlite", "postgresql")
            for provider in order:
                info = manifest["providers"][provider]
                connection = str(args.work / f"sqlite-run-{repetition}.db")
                if provider == "postgresql":
                    import pwd
                    connection = f"postgresql://{pwd.getpwuid(os.getuid()).pw_name}@127.0.0.1:{port}/postgres"
                result = args.evidence / f"{provider}-run-{repetition}.json"
                log = args.evidence / f"{provider}-run-{repetition}.log"
                command = [info["executable_path"], connection, str(result), str(args.warmup), str(args.iterations), info["package_path"], info["source_commit"]]
                run(command, args.work, log, env, timeout=300)
                receipt = json.loads(result.read_text())
                for field in ("package_sha256", "executable_sha256"):
                    if receipt[field] != info[field]: raise ValueError(f"Run hash mismatch: {field}")
                if receipt["loaded_native_sha256"] != info["native_sha256"]: raise ValueError("Actually loaded native library differs")
                manifest["runs"].append({"provider": provider, "repetition": repetition, "result": result.name, "result_sha256": digest(result), "log": log.name})
                (args.evidence / "build-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
                print(f"PASS {provider} repetition={repetition} result={result}", flush=True)
    finally:
        if server.poll() is None:
            run([str(args.postgres_bin / "pg_ctl"), "-D", str(pgdata), "stop", "-m", "fast", "-w", "-t", "20"], args.work, args.evidence / "postgres-stop.log", env, timeout=30)
        server.wait(timeout=30); postgres_log.close()
        manifest["postgres_cluster"]["exit_code"] = server.returncode
        (args.evidence / "build-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")


if __name__ == "__main__":
    main()
