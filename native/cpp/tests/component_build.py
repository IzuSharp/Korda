"""Shared native build gate for the retained B05/B06/B07 consumers."""
import os
from pathlib import Path
import shlex

ROOT = Path(__file__).resolve().parents[3]


def prepare(run, build, targets, test_regex, sources):
    build = Path(build).resolve()
    preset = os.environ.get("NW_CMAKE_PRESET", "linux-x64-release")
    compiler = run("native-clang", ["clang++", "--version"])
    if "clang version 23.1.5" not in compiler:
        raise RuntimeError("Use the locked LLVM Clang 23.1.5 native toolchain.")
    formatter = run("native-format-version", ["clang-format", "--version"])
    if "23.1.5" not in formatter:
        raise RuntimeError("Use clang-format 23.1.5 with the repository rules.")
    run("native-configure", ["cmake", "--preset", preset, "-B", str(build),
                             *shlex.split(os.environ.get("NW_NATIVE_CMAKE_ARGS", ""))])
    provenance = (build / "compiler-provenance.txt").read_text()
    if "ID=Clang\n" not in provenance or "VERSION=23.1.5\n" not in provenance or "TARGET=x86_64-" not in provenance:
        raise RuntimeError("Configured compiler provenance does not match Clang 23.1.5.")
    run("native-build", ["cmake", "--build", str(build), "--parallel",
                         os.environ.get("CMAKE_BUILD_PARALLEL_LEVEL", "3"), "--target", *targets])
    run("native-format", ["clang-format", "--dry-run", "--Werror", *map(str, sources)])
    run("native-tests", ["ctest", "--test-dir", str(build), "--output-on-failure", "-R", test_regex])
    return provenance
