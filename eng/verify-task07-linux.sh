#!/usr/bin/env bash
# Final domain closure in the existing Linux row; public publication stays HelloWorld-only.
set -euo pipefail
cd "$(dirname "$0")/.."

task07_output=artifacts/task07/B24
task07_run="${GITHUB_RUN_ID:-local-$(date -u +%Y%m%d%H%M%S)}"
task07_attempt="${GITHUB_RUN_ATTEMPT:-1}"
task07_version="0.1.0-task07.${task07_run}.${task07_attempt}"

python3 tests/Packaging.Domain/test_release_results.py

# Every CI candidate has fresh immutable identities. Within this pair the eight
# common packages are preserved byte-for-byte; six facades and three native
# leaves advance together. Original legacy preview archives are never replaced.
CARGO_BUILD_JOBS=2 python3 eng/task07_native.py --output "$task07_output/native-build"
python3 src/Packaging.Domain/prepare.py --output "$task07_output/prepared" \
  --previous-version "$task07_version.1" --candidate-version "$task07_version.2" \
  --native-manifest "$task07_output/native-build/manifest.json"

task07_sdk='mcr.microsoft.com/dotnet/sdk:11.0.100-resolute-amd64@sha256:7913685c883a29dd8ba79da96172f5c6b29ed9be706ebe95935e3ef45f19c9c1'
docker run --rm --network host \
  -v "$PWD:/work:ro" -v "$PWD/artifacts:/work/artifacts" -w /work \
  -e NW_TEST_POSTGRES -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e B24_PRISTINE_IMAGE="$task07_sdk" "$task07_sdk" bash -c '
    set -euo pipefail
    for tool in cargo rustc rustup; do
      if command -v "$tool" >/dev/null 2>&1; then
        echo "The pristine domain consumer image unexpectedly contains $tool" >&2
        exit 1
      fi
    done
    apt-get update
    apt-get install -y --no-install-recommends clang zlib1g-dev python3
    git config --global --add safe.directory /work
    python3 tests/Packaging.Domain/release_cases.py \
      --manifest /work/artifacts/task07/B24/prepared/manifest.json \
      --output /work/artifacts/task07/B24/release
  '

# Transfer only the already verified candidate AOT bytes and a standalone
# verifier to the existing Noble runtime baseline. No source/toolchain mount.
task07_runtime='mcr.microsoft.com/dotnet/runtime-deps:10.0.15-noble-amd64@sha256:ebb73a172b49758c6de2462305e872ea0cb37ad3ca27cc0d86bc48b6271eaace'
mkdir -p "$task07_output/runtime"
docker run --rm --network host \
  -v "$PWD/$task07_output/release/candidate-destination/runtime-transfer:/input:ro" \
  -v "$PWD/tests/Packaging.Domain/runtime_verify.py:/verify.py:ro" \
  -v "$PWD/$task07_output/runtime:/result" \
  -e NW_TEST_POSTGRES -e B24_RUNTIME_IMAGE="$task07_runtime" "$task07_runtime" bash -c '
    set -euo pipefail
    apt-get update
    apt-get install -y --no-install-recommends python3
    python3 /verify.py --bundle /input --output /result/evidence
  '
mkdir -p "$task07_output/evidence"
docker image inspect "$task07_sdk" "$task07_runtime" > "$task07_output/evidence/images.json"

