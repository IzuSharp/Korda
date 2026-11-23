#!/usr/bin/env bash
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
# Archive integrity checks require hashlib.file_digest, available since Python 3.11.
for ci_python in python3 python3.14 python3.13 python3.12 python3.11; do
  if command -v "$ci_python" >/dev/null && "$ci_python" -c 'import hashlib; assert hasattr(hashlib,"file_digest")' 2>/dev/null; then
    break
  fi
done
if ! command -v "$ci_python" >/dev/null; then
  echo "Install Python 3.11 or newer to run CI tooling tests." >&2
  exit 1
fi
"$ci_python" eng/ci/worker.py check-index
# The fixtures create disposable Git repositories. A commit hook can export the
# parent's GIT_DIR, GIT_INDEX_FILE and related worktree state.
while IFS= read -r variable; do
  unset "$variable"
done < <(git rev-parse --local-env-vars)
"$ci_python" -m unittest discover -s tests/Tooling -v
