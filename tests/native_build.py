"""Shared Clang/CMake adapter for the retained native component consumers."""

import os
from pathlib import Path
import shlex

ROOT = Path(__file__).resolve().parents[1]
PRESET = os.environ.get("NW_CMAKE_PRESET", "linux-x64-release")
BUILD = ROOT / "artifacts/cmake" / PRESET
FIXTURES = BUILD / "fixtures"
BINARIES = BUILD / "tests"
SOURCE_INPUTS = ["native/cpp", "CMakeLists.txt", "CMakePresets.json", ".clang-format",
                 ".clang-tidy", "tests/native_build.py"]


def stdout(value):
    return value[0] if isinstance(value, tuple) else value


def prepare(run, targets=(), test_regex=r"^native\.interop$"):
    """Log the actual preset build, format, compiler identity and retained CTest."""
    version = stdout(run("native-clang-version", ["clang++", "--version"]))
    if "clang version 23.1.5" not in version:
        raise RuntimeError("Native conformance requires the pinned LLVM 23.1.5.")
    formatter = stdout(run("native-format-version", ["clang-format", "--version"]))
    if "23.1.5" not in formatter:
        raise RuntimeError("Use clang-format 23.1.5 with the shared root rules.")
    run("native-cmake-version", ["cmake", "--version"])
    isolated = PRESET == "interop"
    configure = (["-S", str(ROOT / "eng/ci/interop"), "-B", str(BUILD), "-G", "Ninja",
                  "-DCMAKE_C_COMPILER=clang", "-DCMAKE_CXX_COMPILER=clang++", "-DCMAKE_BUILD_TYPE=Release"]
                 if isolated else ["--preset", PRESET])
    if isolated and os.environ.get("CMAKE_CXX_COMPILER_LAUNCHER"):
        for lang in ("C", "CXX"):
            configure.append(f"-DCMAKE_{lang}_COMPILER_LAUNCHER=" + os.environ["CMAKE_CXX_COMPILER_LAUNCHER"])
    run("native-configure", ["cmake", *configure,
                             *shlex.split(os.environ.get("NW_NATIVE_CMAKE_ARGS", ""))])
    provenance = (BUILD / "compiler-provenance.txt").read_text()
    if "ID=Clang\n" not in provenance or "VERSION=23.1.5\n" not in provenance:
        raise RuntimeError("The configured native build did not select the required Clang.")
    selected = list(dict.fromkeys(["nw_interop_tests", *targets]))
    run("native-build", ["cmake", "--build", str(BUILD), "--parallel",
                         os.environ.get("CMAKE_BUILD_PARALLEL_LEVEL", "4"), "--target", *selected])
    sources = [ROOT / "native/cpp/include/nw/interop.hpp", ROOT / "native/cpp/src/interop.cpp",
               ROOT / "native/cpp/tests/interop_tests.cpp", ROOT / "native/cpp/tests/execution_tests.cpp"]
    sources += sorted((ROOT / "native/cpp/tests/fixtures").glob("*.cpp"))
    sources += sorted((ROOT / "native/cpp/tests/fixtures").glob("*.hpp"))
    run("native-format", ["clang-format", "--dry-run", "--Werror", *sources])
    run("native-tests", ["ctest", "--test-dir", str(BUILD), "--output-on-failure", "--no-tests=error", "-R", test_regex])
    return provenance
