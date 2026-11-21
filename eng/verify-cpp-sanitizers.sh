#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export ASAN_OPTIONS=detect_leaks=1:strict_string_checks=1
export UBSAN_OPTIONS=halt_on_error=1:print_stacktrace=1
cmake --preset linux-x64-asan-ubsan --fresh
cmake --build --preset linux-x64-asan-ubsan --parallel 4
ctest --preset linux-x64-asan-ubsan
python3 tests/Data.NativeHost/run.py --postgres-bin "$NW_POSTGRES_BIN" \
  --output artifacts/cpp-sanitizers/administration -- \
  python3 -c 'import os,sys; os.execv(sys.argv[1], [sys.argv[1], os.environ["NW_TEST_POSTGRES"]])' \
  "$PWD/artifacts/cmake/linux-x64-asan-ubsan/tests/nw_administration_tests"
python3 tests/Data.NativeHost/run.py --postgres-bin "$NW_POSTGRES_BIN" \
  --output artifacts/cpp-sanitizers/input -- \
  python3 -c 'import os,sys; os.execv(sys.argv[1], [sys.argv[1], "input-postgresql", os.environ["NW_TEST_POSTGRES"]])' \
  "$PWD/artifacts/cmake/linux-x64-asan-ubsan/tests/nw_host_review_tests"
python3 native/cpp/tests/provider_verify.py \
  --binary artifacts/cmake/linux-x64-asan-ubsan/tests/nw_provider_tests \
  --postgres-bin "$NW_POSTGRES_BIN" --output artifacts/cpp-sanitizers/provider
