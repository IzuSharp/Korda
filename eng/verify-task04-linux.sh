#!/usr/bin/env bash
# Scoped Task-04 package composition in the existing Linux x64 verification row.
set -euo pipefail
cd "$(dirname "$0")/.."

python3 tests/Packaging.Linux/verify.py --require-baseline --output artifacts/task04/A14/ci
python3 tests/Packaging.Linux/minimum_cpu.py artifacts/task04/A14/ci/runtime-transfer \
  --output artifacts/task04/A14/minimum-cpu --require-baseline
python3 src/Packaging.Release/prepare.py --output artifacts/task04/A16/prepared

# The pinned SDK image is Ubuntu Resolute. It establishes a fresh SDK/no-Rust
# consumer, while the separate Noble image below establishes runtime transfer
# on the declared Ubuntu 24.04 baseline. Neither container mounts a toolchain.
sdk_image='mcr.microsoft.com/dotnet/sdk:11.0.100-resolute-amd64@sha256:7913685c883a29dd8ba79da96172f5c6b29ed9be706ebe95935e3ef45f19c9c1'
runtime_image='mcr.microsoft.com/dotnet/runtime-deps:10.0.15-noble-amd64@sha256:ebb73a172b49758c6de2462305e872ea0cb37ad3ca27cc0d86bc48b6271eaace'
docker run --rm \
  -v "$PWD:/work:ro" -v "$PWD/artifacts:/work/artifacts" -w /work \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 "$sdk_image" bash -c '
    set -euo pipefail
    git config --global --add safe.directory /work
    bash tests/Packaging.Release/consume-linux.sh /work/artifacts/task04/A16/prepared /work/artifacts/task04/A16/consumer
    python3 tests/Packaging.Release/build_cases.py \
      --prepared /work/artifacts/task04/A16/prepared \
      --consumer-receipt /work/artifacts/task04/A16/consumer/result.json \
      --output /work/artifacts/task04/A16/build-cases
  '

# runtime-deps contains OS dependencies, without dotnet or a C/Rust compiler.
# Only transferred application bytes and these standalone verifiers are mounted.
mkdir -p artifacts/task04/runtime
docker run --rm \
  -v "$PWD/artifacts/task04/A14/ci/runtime-transfer:/input/a14:ro" \
  -v "$PWD/artifacts/task04/A16/consumer/runtime-transfer:/input/a16:ro" \
  -v "$PWD/tests/Packaging.Linux/runtime.py:/verify-a14.py:ro" \
  -v "$PWD/tests/Packaging.Release/runtime.py:/verify-a16.py:ro" \
  -v "$PWD/artifacts/task04/runtime:/result" "$runtime_image" bash -c '
    set -euo pipefail
    apt-get update
    apt-get install -y --no-install-recommends python3
    python3 /verify-a14.py /input/a14 --receipt /result/a14.json --require-tool-absence
    python3 /verify-a16.py /input/a16 --receipt /result/a16.json --require-tool-absence
  '
docker image inspect "$sdk_image" "$runtime_image" > artifacts/task04/runtime/images.json
