#!/usr/bin/env bash
# check-bypass.sh — hook-bypass PreToolUse guard (Task 4 form).
# Verdicts live in scripts/guard_policy.py on top of the single quote-aware
# span normalizer (scripts/guard_contract.py). THIN adapter only: argv/stdin
# -> policy verdict -> exit code. No de-quoting, no regex side path (REQ-9.3).
#   block = exit 2 + reason; allow = silent exit 0; malformed input = exit 2.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
POLICY="$REPO_ROOT/scripts/guard_policy.py"

C="${1:-$(cat)}"
[ -n "$C" ] || exit 0

exec python3 "$POLICY" bypass "$C"
