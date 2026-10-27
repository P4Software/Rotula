#!/usr/bin/env bash
# Repository hygiene gate. Fails when:
#   1. the working tree contains AI-tooling files (.claude/ directory, CLAUDE.md, AGENTS.md) anywhere, or
#   2. any commit message in the given range carries an AI co-author trailer, an AI session link or a
#      "Generated with" footer, or
#   3. any commit in the given range names an AI assistant as its author or committer. Every commit
#      must be authored by a person; automated sessions must commit under the maintainer's identity.
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
pattern='Co-Authored-By:.*(Claude|Anthropic)|Claude-Session:|Generated with'
# Author and committer, as "name <email>": an AI assistant's name or address fails the check.
identity_pattern='(^|[^a-z])claude([^a-z]|$)|anthropic'

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
  # --- 3. Author and committer ------------------------------------------------------------------
  if git log -1 --format='%an <%ae>%n%cn <%ce>' "$sha" | grep -qiE "$identity_pattern"; then
    echo "::error::Commit $sha is authored or committed under an AI assistant's name:"
    git log -1 --format='  %h %s (author: %an <%ae>, committer: %cn <%ce>)' "$sha"
    status=1
  fi
done

if [ "$status" -eq 0 ]; then echo "Hygiene check passed."; fi
exit $status
