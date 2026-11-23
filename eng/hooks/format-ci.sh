#!/usr/bin/env bash
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
# pre-commit supplies staged paths relative to the repository and hides unstaged edits.
for project in eng/ci tests/CI; do
  paths=()
  for file in "$@"; do
    case "$file" in "$project"/*.cs) paths+=("${file#"$project"/}") ;; esac
  done
  if (( ${#paths[@]} )); then
    (cd "$project" && dotnet format whitespace --include "${paths[@]}")
  fi
done
