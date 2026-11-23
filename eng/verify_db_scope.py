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
    "src/Query.Binding", "src/Orm.Relations", "src/Korda.Core", "src/Interop.Abi",
]
FORBIDDEN = ("Korda.Server", "Korda.Protocols", "Korda.HelloWorld",
             "Korda.Engines", "Korda.Mcp", "Korda.Grpc")
EXPECTED_PACKAGES = {
    "Korda.Interop.Abi", "Korda.Core", "Korda.Data",
    "Korda.Database.Transactions", "Korda.Data.Streaming", "Korda.Query.Binding",
    "Korda.Query.Generator", "Korda.Database", "Korda.Database.Sqlite",
    "Korda.Database.PostgreSql", "Korda.Database.Sqlite.Native", "Korda.Database.PostgreSql.Native",
}


def check():
    for name in REQUIRED:
        assert (ROOT / name).is_dir(), "Retained database dependency is missing: " + name
    cmake = (ROOT / "CMakeLists.txt").read_text()
    for name in ("nw_database_sqlite", "nw_database_postgresql", "korda_database_host"):
        assert "nw_database_target(" + name + " " in cmake, name
    assert (ROOT / "native/dependencies.lock.json").is_file()
    solution = ET.parse(ROOT / "Korda.slnx").getroot()
    for node in solution.iter("Project"):
        assert (ROOT / node.attrib["Path"]).is_file(), node.attrib
    tracked = subprocess.check_output(["git", "ls-files", "-z", "--", "*.csproj"], cwd=ROOT).decode().split("\0")
    # Check only the database product's projects and their transitive project references.
    pending = [ROOT / path for path in tracked if path.startswith(tuple(name + "/" for name in REQUIRED))]
    visited = set()
    while pending:
        path = pending.pop().resolve()
        if path in visited:
            continue
        visited.add(path)
        for node in ET.parse(path).iter():
            if node.tag in ("PackageReference", "ProjectReference"):
                include = node.get("Include", "")
                assert not any(x in include for x in FORBIDDEN), (path, include)
                if node.tag == "ProjectReference" and "$(" not in include:
                    referenced = (path.parent / include).resolve()
                    assert referenced.is_file(), (path, include)
                    pending.append(referenced)
    sys.path.insert(0, str(ROOT / "src/Packaging.Domain"))
    spec = importlib.util.spec_from_file_location("database_package_model", ROOT / "src/Packaging.Domain/model.py")
    model = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(model)
    assert set(model.MANAGED) | set(model.LEAVES) == EXPECTED_PACKAGES
    assert set(model.ENGINES) == {"database.sqlite", "database.postgresql"}
    print(json.dumps({"task": "DB-CPP-01", "status": "passed", "databaseHostRetained": True,
                      "nativeImplementation": "C++20", "managedProjects": len(visited),
                      "databasePackages": sorted(EXPECTED_PACKAGES)}, indent=2))


if __name__ == "__main__":
    check()
