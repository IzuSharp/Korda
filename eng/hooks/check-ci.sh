#!/usr/bin/env bash
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
python3 eng/ci/worker.py check-index
# The fixtures create disposable Git repositories. A commit hook can export the
# parent's GIT_DIR, GIT_INDEX_FILE and related worktree state.
while IFS= read -r variable; do
  unset "$variable"
done < <(git rev-parse --local-env-vars)
python3 -m unittest discover -s tests/Tooling -v
