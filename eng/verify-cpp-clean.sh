#!/usr/bin/env bash
# Build, package and consume on a fresh Ubuntu 24.04 filesystem with no Rust.
set -euo pipefail
cd "$(dirname "$0")/.."
cpp_clean_image='mcr.microsoft.com/dotnet/runtime-deps:10.0.15-noble-amd64@sha256:ebb73a172b49758c6de2462305e872ea0cb37ad3ca27cc0d86bc48b6271eaace'
mkdir -p artifacts/cpp-clean
git bundle create artifacts/cpp-clean/source.bundle HEAD
cpp_clean_dotnet="$(dirname "$(readlink -f "$(command -v dotnet)")")"
docker run --rm --network host \
  -v "$PWD:/input:ro" -v "$PWD/artifacts/cpp-clean:/output" -v "$cpp_clean_dotnet:/dotnet:ro" \
  -e NW_TEST_POSTGRES -e "CPP_SOURCE=$(git rev-parse HEAD)" \
  -e "CPP_VERSION=${PACKAGE_VERSION:-0.1.0-db-cpp.1}.clean" \
  -e "CPP_CLEAN_IMAGE=$cpp_clean_image" "$cpp_clean_image" bash -c '
    set -euo pipefail
    for tool in cargo rustc rustup; do
      if command -v "$tool" >/dev/null 2>&1; then echo "Unexpected Rust tool: $tool" >&2; exit 1; fi
    done
    for path in /root/.cargo /usr/local/cargo /opt/rust /usr/local/rustup; do test ! -e "$path"; done
    apt-get update
    apt-get install -y --no-install-recommends git python3 curl ca-certificates libssl-dev zlib1g-dev build-essential
    git config --global --add safe.directory /input
    git clone /input/artifacts/cpp-clean/source.bundle /tmp/nativeweave
    cd /tmp/nativeweave
    git checkout --detach "$CPP_SOURCE"
    export DOTNET_ROOT=/dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
    export PATH=/input/artifacts/toolchain/llvm-23.1.5/bin:/input/artifacts/toolchain/cmake-4.3.3/bin:/input/artifacts/toolchain/ninja-1.13.2:/dotnet:/usr/bin:/bin
    export CC=/input/artifacts/toolchain/llvm-23.1.5/bin/clang CXX=/input/artifacts/toolchain/llvm-23.1.5/bin/clang++
    export NW_DOWNLOAD_CACHE=/input/artifacts/dependencies/downloads
    export NW_POSTGRES_BIN=/input/artifacts/postgres-tools/install/bin
    export CMAKE_PREFIX_PATH=/input/artifacts/postgres-tools/install
    python3 eng/build.py pack --version "$CPP_VERSION"
    python3 eng/build.py consume --consumer-environment pristine
    python3 - <<"PY"
import hashlib,json,os,shutil,subprocess
from pathlib import Path
absence={name:shutil.which(name) is None for name in ("cargo","rustc","rustup")}
assert all(absence.values())
native=Path("artifacts/db-scope/native-build/manifest.json")
packages=Path("artifacts/db-scope/prepared/manifest.json")
record={"task":"DB-CPP-01","status":"passed","image":os.environ["CPP_CLEAN_IMAGE"],"rustToolsAbsent":absence,
        "sourceCommit":subprocess.check_output(["git","rev-parse","HEAD"],text=True).strip(),
        "nativeManifestSha256":hashlib.sha256(native.read_bytes()).hexdigest(),
        "packagesManifestSha256":hashlib.sha256(packages.read_bytes()).hexdigest(),
        "commands":["eng/build.py pack (includes exact native build/install)","eng/build.py consume --consumer-environment pristine"]}
Path("/output/receipt.json").write_text(json.dumps(record,indent=2)+"\n")
for name in ("native-build","prepared/evidence","prepared/candidate/feed","local-consumer/evidence","pristine-consumer/evidence"):
    path=Path("artifacts/db-scope")/name
    if path.exists():shutil.copytree(path,Path("/output")/name)
for name in ("prepared/manifest.json","prepared/candidate/release.json"):
    path=Path("artifacts/db-scope")/name
    target=Path("/output")/name;target.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(path,target)
PY
  '
docker image inspect "$cpp_clean_image" > artifacts/cpp-clean/image.json
