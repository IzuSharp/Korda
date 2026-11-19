#!/usr/bin/env python3
"""Build the unchanged B23 catch/log/rethrow observer for exact Perf-01 packages."""
import argparse
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/Benchmarks.Domain"))
from diagnose_sqlite import build_observer


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package-probe", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    build_observer(args.package_probe.resolve(), args.output.resolve(), args.dotnet)


if __name__ == "__main__":
    main()
