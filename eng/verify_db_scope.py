#!/usr/bin/env python3
"""Verify the current product build graph keeps Database Host and excludes retired products."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
REQUIRED = [
    "native/cpp/src", "native/cpp/include/nw", "native/cpp/tests", "native/database/binding",
    "src/Data.NativeHost", "src/Data.Postgres", "src/Data.Sqlite", "src/Data.Transactions",
    "src/Data.Streaming", "src/Data.Materialization", "src/Query.Fluent", "src/Query.Generator",
    "src/Query.Binding", "src/Orm.Relations", "src/NativeWeave.Core", "src/Interop.Abi",
]
REMOVED = [
    "native/server", "native/protocols", "native/engines", "native/hello-world",
    "src/Server.Application", "src/Server.Diagnostics", "src/Server.Native", "src/Server.Streaming",
    "src/Protocols.Grpc", "src/Protocols.Mcp", "src/Engines.SyncProof", "src/Engines.AsyncProof",
    "src/Verification.ExtensionProof", "src/NativeWeave.HelloWorld", "tools/ApiDocs",
    "src/Generators.Model", "src/Generators.CSharp", "src/Generators.Rust", "src/Analyzers",
    "src/Benchmarks.Harness", "src/Benchmarks.Domain", "src/Tooling.EngineScaffold",
]
FORBIDDEN = ("NativeWeave.Server", "NativeWeave.Protocols", "NativeWeave.HelloWorld",
             "NativeWeave.Engines", "NativeWeave.Mcp", "NativeWeave.Grpc")
EXPECTED_PACKAGES = {
    "NativeWeave.Interop.Abi", "NativeWeave.Core", "NativeWeave.Data",
    "NativeWeave.Database.Transactions", "NativeWeave.Data.Streaming", "NativeWeave.Query.Binding",
    "NativeWeave.Query.Generator", "NativeWeave.Database", "NativeWeave.Database.Sqlite",
    "NativeWeave.Database.PostgreSql", "NativeWeave.Database.Sqlite.Native", "NativeWeave.Database.PostgreSql.Native",
}


def check():
    for name in REQUIRED:
        assert (ROOT / name).is_dir(), "Retained database dependency is missing: " + name
    for name in REMOVED:
        assert not (ROOT / name).exists(), "Retired product still exists: " + name
    for name in ("Cargo.toml", "Cargo.lock", "rust-toolchain.toml", ".cargo"):
        assert not (ROOT / name).exists(), "Replaced Rust product entry still exists: " + name
    names = subprocess.check_output(["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"], cwd=ROOT).decode().split("\0")
    for name in names:
        if name and (ROOT / name).is_file():
            assert Path(name).suffix != ".rs" and Path(name).name not in ("Cargo.toml", "Cargo.lock"), name
    cmake = (ROOT / "CMakeLists.txt").read_text()
    for name in ("nw_database_sqlite", "nw_database_postgresql", "nativeweave_database_host"):
        assert "nw_database_target(" + name + " " in cmake, name
    assert (ROOT / "native/dependencies.lock.json").is_file()
    solution = ET.parse(ROOT / "NativeWeave.slnx").getroot()
    for node in solution.iter("Project"):
        assert (ROOT / node.attrib["Path"]).is_file(), node.attrib
    tracked = subprocess.check_output(["git", "ls-files", "-z", "--", "*.csproj"], cwd=ROOT).decode().split("\0")
    count = 0
    for name in tracked:
        path = ROOT / name
        if not name or not path.is_file():
            continue
        for node in ET.parse(path).iter():
            if node.tag in ("PackageReference", "ProjectReference"):
                include = node.get("Include", "")
                assert not any(x in include for x in FORBIDDEN), (name, include)
                if node.tag == "ProjectReference" and "$(" not in include:
                    assert (path.parent / include).resolve().is_file(), (name, include)
        count += 1
    sys.path.insert(0, str(ROOT / "src/Packaging.Domain"))
    spec = importlib.util.spec_from_file_location("database_package_model", ROOT / "src/Packaging.Domain/model.py")
    model = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(model)
    assert set(model.MANAGED) | set(model.LEAVES) == EXPECTED_PACKAGES
    assert set(model.ENGINES) == {"database.sqlite", "database.postgresql"}
    print(json.dumps({"task": "DB-CPP-01", "status": "passed", "databaseHostRetained": True,
                      "nativeImplementation": "C++20", "rustProductInputs": 0, "managedProjects": count,
                      "databasePackages": sorted(EXPECTED_PACKAGES)}, indent=2))


if __name__ == "__main__":
    check()
