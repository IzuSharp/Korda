#!/usr/bin/env python3
"""Maintainer Linux database build, regression and ordinary NuGet/AOT consumption."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = ROOT / "artifacts"
LOGS = ARTIFACTS / "logs"
OUTPUT = ARTIFACTS / "db-scope"


def run(args, label, cwd=ROOT, env=None, json_stdout=False):
    LOGS.mkdir(parents=True, exist_ok=True)
    print("+ " + " ".join(map(str, args)), flush=True)
    result = subprocess.run(list(map(str, args)), cwd=cwd, env=env, text=True,
                            stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE if json_stdout else subprocess.STDOUT)
    logged = result.stdout + ("\n[stderr]\n" + result.stderr if json_stdout and result.stderr else "")
    (LOGS / (label + ".log")).write_text(logged, encoding="utf-8")
    if result.returncode:
        print(logged[-16000:], flush=True)
        raise RuntimeError(f"{label} failed ({result.returncode}); see artifacts/logs/{label}.log")
    if not json_stdout:
        print(result.stdout[-3000:], end="", flush=True)
    return result.stdout


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def export_fixture_results(external, destination):
    """Export bounded result JSON without private configuration or raw argv/logs."""
    allowed = {"progress.json", "receipt.json", "result.json", "summary.json", "status.json", "comparison.json", "independent-sql.json",
               "resolved-archive-hashes.json"}
    private_keys = {"argv", "command", "config", "configuration", "database", "connectionstring",
                    "connection_string", "password", "secret", "cursorkey", "cursor_key"}

    def safe(value):
        if isinstance(value, dict):
            return {key: safe(item) for key, item in value.items() if key.lower() not in private_keys}
        if isinstance(value, list):
            return [safe(item) for item in value]
        if isinstance(value, str):
            return re.sub(r"postgres(?:ql)?://[^\s\"'<>]+", "<database-uri>", value)
        return value

    for path in external.rglob("*.json"):
        relative = path.relative_to(external)
        if path.name not in allowed or any(part in (
                "consumer-cache", "dotnet-home", "consumers", "metadata", "artifacts", "postgres-data")
                for part in relative.parts):
            continue
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps({"sanitized": True, "source_artifact_sha256": digest(path),
                                      "data": safe(json.loads(path.read_text()))}, indent=2) + "\n")


def source():
    return {"commit": run(["git", "rev-parse", "HEAD"], "source-commit").strip(),
            "tree": run(["git", "rev-parse", "HEAD^{tree}"], "source-tree").strip()}


def native():
    output = OUTPUT / "native-build"
    run([sys.executable, "eng/task07_native.py", "--output", output], "database-native")
    manifest = json.loads((output / "manifest.json").read_text())
    for asset in manifest["assets"]:
        path = Path(asset["path"])
        if digest(path) != asset["sha256"]:
            raise RuntimeError("Native candidate changed before source staging")
    return output / "manifest.json"


def pack(version, manifest):
    run([sys.executable, "src/Packaging.Domain/prepare.py", "--output", OUTPUT / "prepared",
         "--previous-version", version + ".1", "--candidate-version", version + ".2",
         "--native-manifest", manifest], "database-packages")


def consume(environment):
    prepared = OUTPUT / "prepared"
    run([sys.executable, "tests/Packaging.Domain/consumer_cases.py", "--manifest", prepared / "manifest.json",
         "--closure", "candidate", "--feed", prepared / "candidate/feed",
         "--output", OUTPUT / (environment + "-consumer"), "--mode", "full",
         "--consumer-environment", environment], "database-" + environment + "-consumer")


def migration_comparison(version):
    external = ROOT.parent / "_consumers" / "migration-cpp"
    reference = external / "rust-reference"
    prepare = [sys.executable, "tests/Migration.Cpp/reference_prepare.py", "--cache",
               ARTIFACTS / "reference-cache", "--output", reference]
    archive = os.environ.get("NW_RUST_REFERENCE_ARCHIVE")
    if archive:
        prepare.extend(["--archive", Path(archive).resolve()])
    run(prepare, "migration-rust-reference")
    common = ["--postgres-bin", os.environ["NW_POSTGRES_BIN"], "--sdk-cache",
              os.environ.get("NUGET_PACKAGES", str(ARTIFACTS / "nuget-packages")),
              "--output", external / "comparison"]
    try:
        run([sys.executable, "tests/Migration.Cpp/run.py", "--phase", "baseline", "--feed",
             reference / "candidate/feed", "--version", "0.1.0-db-scope.1.2", *common],
            "migration-rust-business")
        run([sys.executable, "tests/Migration.Cpp/run.py", "--phase", "candidate", "--feed",
             OUTPUT / "prepared/candidate/feed", "--version", version + ".2", *common],
            "migration-cpp-business")
    finally:
        export_fixture_results(external, OUTPUT / "migration-comparison")


def workspace(destination):
    """Keep future, unrelated projects out of the database solution build."""
    import xml.etree.ElementTree as ET
    sys.path.insert(0, str(ROOT / "eng/ci"))
    from catalog import load_catalog
    catalog = load_catalog()
    database_modules = json.loads((ROOT / "eng/ci/modules/database.json").read_text())
    solution = ET.parse(ROOT / "Korda.slnx")
    for parent in solution.iter():
        for project in list(parent.findall("Project")):
            path = project.attrib["Path"]
            owner, _ = catalog.owner(path)
            is_consumer = path.startswith("samples/") or (
                path.startswith("tests/") and "/ApiCheck/" not in path and "/ApiFixture/" not in path
            )
            # Consumers have their own publish/run checks. The shared managed
            # build produces libraries and compiler/API tooling only.
            if owner not in database_modules or is_consumer:
                parent.remove(project)
            else:
                project.set("Path", os.path.relpath(ROOT / path, destination.parent))
    destination.parent.mkdir(parents=True, exist_ok=True)
    solution.write(destination, encoding="utf-8")
    return str(destination)


def build_workspace():
    solution = workspace(OUTPUT / "database.slnx")
    msbuild = ["-m:1", "-nr:false", "-p:UseSharedCompilation=false"]
    run(["dotnet", "restore", solution, "--locked-mode", "--disable-parallel", *msbuild], "restore")
    run(["dotnet", "build", solution, "-c", "Release", "--no-restore", *msbuild], "build")
    run(["dotnet", "format", solution, "--verify-no-changes", "--no-restore"], "dotnet-format")


def verify_inputs():
    if run(["git", "status", "--porcelain"], "initial-status").strip():
        raise RuntimeError("Commit a clean candidate before collecting source-bound evidence")
    run([sys.executable, "eng/verify_db_scope.py"], "database-scope")
    run([sys.executable, "eng/check_native.py", "format"], "clang-format")


def regressions(design, plan, separate_providers=False):
    try:
        # The driver writes per-check logs and receipts. Forward its progress as each check starts.
        subprocess.run([sys.executable, "eng/database_regressions.py", "--design", design, "--plan", plan,
                        "--output", OUTPUT / "regressions", *(["--separate-provider-tests"] if separate_providers else [])], cwd=ROOT, check=True)
    finally:
        export_fixture_results(OUTPUT / "regressions", OUTPUT / "regression-evidence")


def api_docs(design):
    standard = json.loads((ROOT / "public-api-standard.json").read_text())
    if digest(ROOT / standard["localPath"]) != standard["sha256"]:
        raise RuntimeError("Pinned public API standard bytes changed")
    run([sys.executable, "src/Build.Integration/compose_docs.py", "--config",
         "src/Build.Integration/api/core.json", "--output", "artifacts/docs/core-site"], "core-api-docs")

    run([sys.executable, "tests/Build.Integration/verify_docs.py",
         "--output", OUTPUT / "api-checks/tool"], "api-tool-negative-contracts")
    run(["dotnet", "run", "--project", "tests/Packaging.Release/ApiCheck", "-c", "Release",
         "--no-build", "--", OUTPUT / "api-checks/packaging-task-reference.md"], "packaging-task-api")
    tool = ROOT / "src/Build.Integration/ApiReference/bin/Release/net11.0/Korda.ApiReference.dll"
    if not tool.is_file():
        directory = tool.parent
        tool = next(p for p in directory.glob("*.dll") if p.with_suffix(".runtimeconfig.json").is_file())
    for name in ("Data.Migrations", "Orm.Relations", "Data.NativeHost"):
        run([sys.executable, f"tests/{name}/api.py", "--design", design, "--tool", tool,
             "--configuration", "Release"], name + "-api")
    run(["dotnet", "tests/Query.Fluent/ApiCheck/bin/Release/net11.0/Korda.Query.Fluent.ApiCheck.dll",
         ROOT, design, os.environ["DOTNET_ROOT"]], "fluent-api")
    run(["dotnet", "tests/Query.Generator/ApiCheck/bin/Release/net11.0/Korda.Query.Generator.ApiCheck.dll",
         ROOT, design, os.environ["DOTNET_ROOT"]], "query-generator-api")


def consumer_probes(version, only=None):
    for name, driver in (("database-lifecycle", "tests/Verification.EndToEnd/api_probe.py"),
                         ("database-business", "tests/Reference.TaskService/package_probe.py")):
        if only and only != name:
            continue
        with tempfile.TemporaryDirectory(prefix="korda-" + name + "-") as temp:
            external = Path(temp) / "consumer"
            try:
                run([sys.executable, driver, "--feed", OUTPUT / "prepared/candidate/feed",
                     "--version", version + ".2", "--output", external], name)
            finally:
                export_fixture_results(external, OUTPUT / name)
    if only:
        return
    consume("local")
    run(["git", "diff", "--check"], "diff-check")
    if run(["git", "status", "--porcelain"], "final-status").strip():
        raise RuntimeError("Source or dependency locks changed during verification")
    receipt = {"status": "passed", "rid": "linux-x64", "source": source(), "versionBase": version,
               "nativeManifestSha256": digest(OUTPUT / "native-build/manifest.json"),
               "candidateManifestSha256": digest(OUTPUT / "prepared/manifest.json"),
               "checks": ["external-database-business-aot", "external-database-lifecycle-aot", "local-package-closure-aot"]}
    (ARTIFACTS / "verification.json").write_text(json.dumps(receipt, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["native", "pack", "pack-existing", "compare-existing", "inputs", "regressions", "workspace", "api", "consumer-probes", "verify", "consume"])
    parser.add_argument("--separate-provider-tests", action="store_true")
    parser.add_argument("--version", default="0.1.0-db-cpp.1")
    parser.add_argument("--design", type=Path, default=ARTIFACTS / "frozen-design")
    parser.add_argument("--plan", type=Path, default=ARTIFACTS / "frozen-plan")
    parser.add_argument("--consumer-environment", choices=["local", "pristine"], default="pristine")
    parser.add_argument("--probe", choices=["database-lifecycle","database-business"])
    parser.add_argument("--compare-rust-reference", action="store_true",
                        help="Run the one-time DB-CPP-01 old/new comparison with the hash-locked reference artifact")
    args = parser.parse_args()
    if (platform.system(), platform.machine().lower()) != ("Linux", "x86_64"):
        parser.error("This database acceptance row is Linux x64 only")
    if not re.fullmatch(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*", args.version):
        parser.error("Use a canonical prerelease version base")
    ARTIFACTS.mkdir(exist_ok=True)
    if args.command == "native":
        native()
    elif args.command == "inputs":
        verify_inputs()
    elif args.command == "regressions":
        regressions(args.design, args.plan, args.separate_provider_tests)
    elif args.command == "workspace":
        build_workspace()
    elif args.command == "api":
        api_docs(args.design.resolve())
    elif args.command == "pack-existing":
        pack(args.version, OUTPUT / "native-build/manifest.json")
    elif args.command == "compare-existing":
        migration_comparison(args.version)
    elif args.command == "consumer-probes":
        consumer_probes(args.version,args.probe)
    elif args.command == "verify":
        # The local aggregate uses the same build actions as the hosted worker.
        verify_inputs()
        native()
        build_workspace()
        api_docs(args.design.resolve())
        regressions(args.design, args.plan)
        pack(args.version, OUTPUT / "native-build/manifest.json")
        consumer_probes(args.version)
        if args.compare_rust_reference:
            migration_comparison(args.version)
    elif args.command == "consume":
        consume(args.consumer_environment)
    else:
        pack(args.version, native())


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, OSError, ValueError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        sys.exit(1)
