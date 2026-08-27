#!/usr/bin/env bash
# check-destructive.sh — Destructive Ops PreToolUse guard (Task 4 form).
# Verdicts live in scripts/guard_policy.py on top of the single quote-aware
# span normalizer (scripts/guard_contract.py). This file is a THIN adapter:
# argv/stdin -> policy verdict -> exit code. No de-quoting, no regex side path,
# no command execution here (REQ-9.3/9.4).
#   block = exit 2 + reason; allow = silent exit 0; malformed input = exit 2.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
POLICY="$REPO_ROOT/scripts/guard_policy.py"

C="${*:-$(cat)}"
[ -n "$C" ] || exit 0

BRANCH="$(git branch --show-current 2>/dev/null || true)"
exec python3 "$POLICY" destructive --branch "$BRANCH" "$C"
