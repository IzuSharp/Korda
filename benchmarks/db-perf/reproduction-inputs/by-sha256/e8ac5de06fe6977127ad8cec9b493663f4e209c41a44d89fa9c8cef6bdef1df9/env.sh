#!/usr/bin/env bash
# Local correctness/diagnostic toolchain; formal campaign runs on its dedicated VM.
# Source this file. It does not start a database or execute any business workload.
export KORDA_DBPERF_TOOLING_ROOT=/workspace/scratch/37c4f8cc3a91/tooling
. "$KORDA_DBPERF_TOOLING_ROOT/native/environment.sh"
export DOTNET_ROOT="$KORDA_DBPERF_TOOLING_ROOT/dotnet"
export DOTNET_CLI_HOME="$KORDA_DBPERF_TOOLING_ROOT/dotnet-cli-home"
export PATH="$DOTNET_ROOT:$KORDA_DBPERF_TOOLING_ROOT/local/usr/bin:$KORDA_DBPERF_TOOLING_ROOT/postgres/install/bin:$PATH"
export BISON_PKGDATADIR="$KORDA_DBPERF_TOOLING_ROOT/local/usr/share/bison"
export M4="$KORDA_DBPERF_TOOLING_ROOT/local/usr/bin/m4"
export NW_POSTGRES_TOOLS_ROOT="$KORDA_DBPERF_TOOLING_ROOT/postgres"
export NW_POSTGRES_BIN="$NW_POSTGRES_TOOLS_ROOT/install/bin"
export CMAKE_PREFIX_PATH="$NW_POSTGRES_TOOLS_ROOT/install${CMAKE_PREFIX_PATH:+:$CMAKE_PREFIX_PATH}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$KORDA_DBPERF_TOOLING_ROOT/nuget-packages}"
export TMPDIR="$KORDA_DBPERF_TOOLING_ROOT/tmp"
export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_USE_MSBUILD_SERVER=0
export MSBUILDDISABLENODEREUSE=1
export UseSharedCompilation=false
# Intentionally do not set DOTNET_PROCESSOR_COUNT or runtime spin/wait overrides.
