#!/usr/bin/env bash
# Repository hygiene gate. Fails when:
#   1. the working tree contains AI-tooling files (.claude/ directory, CLAUDE.md, AGENTS.md) anywhere, or
#   2. any commit message in the given range carries an AI co-author trailer or a "Generated with" footer.
# Usage: scripts/check-hygiene.sh [<git-rev-range>]
#   With no range the whole history reachable from HEAD is checked. CI passes the PR or push range.
# Why a script: the same check runs in CI and on a developer machine before pushing.
set -u

range="${1:-}"
status=0

# --- 1. Forbidden files ------------------------------------------------------------------------
# Plain `find` (not `git ls-files`) so ignored or untracked files are caught locally too.
forbidden=$(find . \( -name .git -o -name bin -o -name obj -o -name node_modules \) -prune -o \
  \( -iname '.claude' -o -iname 'CLAUDE.md' -o -iname 'AGENTS.md' \) -print)
if [ -n "$forbidden" ]; then
  echo "::error::Forbidden files found in the tree:"
  echo "$forbidden"
  status=1
fi

# --- 2. Commit messages ------------------------------------------------------------------------
pattern='Co-Authored-By:.*(Claude|Anthropic)|Generated with'

# A force-push or a brand-new branch can hand us a range whose base is unknown to this clone;
# in that case fall back to checking everything reachable from HEAD rather than silently skipping.
if [ -n "$range" ] && ! git rev-list "$range" >/dev/null 2>&1; then
  echo "Range '$range' is not usable here; checking the full history instead."
  range=""
fi

if [ -n "$range" ]; then revs=$(git rev-list "$range"); else revs=$(git rev-list HEAD); fi

for sha in $revs; do
  if git log -1 --format=%B "$sha" | grep -qiE "$pattern"; then
    echo "::error::Commit $sha has a forbidden trailer or footer in its message:"
    git log -1 --format='  %h %s' "$sha"
    status=1
  fi
done

if [ "$status" -eq 0 ]; then echo "Hygiene check passed."; fi
exit $status
