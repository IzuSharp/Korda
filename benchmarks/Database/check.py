#!/usr/bin/env python3
"""Validate the preflight entry points without starting a load or a database."""

import ast
import json
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET


def main():
    root = Path(__file__).resolve().parent
    inputs = []
    for path in sorted(root.rglob("*")):
        if not path.is_file() or any(p in {"__pycache__", "bin", "obj"} for p in path.parts):
            continue
        if path.suffix == ".py":
            ast.parse(path.read_text(), filename=str(path))
        elif path.suffix == ".json":
            json.loads(path.read_text())
        elif path.suffix in {".csproj", ".props", ".config"}:
            ET.parse(path)
        else:
            continue
        inputs.append(path.relative_to(root).as_posix())
    runners = [
        root / "allocations/run.py",
        root / "independent-orm/run.py",
        root / "native/run_probe.py",
    ]
    for runner in runners:
        subprocess.run(
            [sys.executable, str(runner), "--help"],
            check=True,
            stdout=subprocess.DEVNULL,
            timeout=20,
        )
    print(json.dumps({
        "check": "database-perf-preflight-inputs",
        "status": "passed",
        "validated_inputs": inputs,
        "runner_help_checks": len(runners),
        "product_execution_claimed": False,
        "performance_acceptance_claimed": False,
    }, indent=2))


if __name__ == "__main__":
    main()
