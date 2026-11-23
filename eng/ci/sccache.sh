#!/usr/bin/env bash
set -euo pipefail
case "${1:?install or stats}" in
  install)
    dir="$PWD/artifacts/toolchain/sccache"
    mkdir -p "$dir"
    if [ ! -f "$dir/tool.tar.gz" ]; then
      curl -fsSL --retry 3 https://github.com/mozilla/sccache/releases/download/v0.17.0/sccache-v0.17.0-x86_64-unknown-linux-musl.tar.gz -o "$dir/tool.tar.gz"
    fi
    echo "67c4a96dd237c1f518f6b36083f270f9976d516f1e57fce891755ea782e50006  $dir/tool.tar.gz" | sha256sum --check
    tar -xzf "$dir/tool.tar.gz" --strip-components=1 -C "$dir" --wildcards '*/sccache'
    echo "$dir" >> "$GITHUB_PATH"
    echo "CMAKE_C_COMPILER_LAUNCHER=$dir/sccache" >> "$GITHUB_ENV"
    echo "CMAKE_CXX_COMPILER_LAUNCHER=$dir/sccache" >> "$GITHUB_ENV"
    ;;
  stats)
    sccache --show-stats
    ;;
  *) exit 2 ;;
esac
