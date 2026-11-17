#!/usr/bin/env bash
set -euo pipefail
if [[ $# -ne 2 ]]; then
  echo "Usage: consume-linux.sh PREPARED_DIRECTORY OUTPUT_DIRECTORY" >&2
  exit 2
fi
for tool in cargo rustc rustup; do
  if command -v "$tool" >/dev/null 2>&1; then
    echo "The consumer image unexpectedly contains $tool" >&2
    exit 1
  fi
done
apt-get update
apt-get install -y --no-install-recommends clang zlib1g-dev python3
python3 tests/Packaging.Release/consume.py \
  --prepared "$1" --output "$2" --negative-cases \
  --pristine-image 'mcr.microsoft.com/dotnet/sdk:11.0.100-resolute-amd64@sha256:7913685c883a29dd8ba79da96172f5c6b29ed9be706ebe95935e3ef45f19c9c1'
