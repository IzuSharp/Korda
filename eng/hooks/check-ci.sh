#!/usr/bin/env bash
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
dotnet build eng/ci -c Release -p:RestoreLockedMode=true --nologo
dotnet run --project eng/ci -c Release --no-build -- check-index
# The fixtures create disposable Git repositories. A commit hook can export the
# parent's GIT_DIR, GIT_INDEX_FILE and related worktree state.
while IFS= read -r variable; do
  unset "$variable"
done < <(git rev-parse --local-env-vars)
dotnet test tests/CI -c Release -p:RestoreLockedMode=true --nologo
