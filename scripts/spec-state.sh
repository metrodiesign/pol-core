#!/usr/bin/env bash
# spec-state.sh — compatibility evidence plus canonical derived state.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
SPECS_DIR=".ai/specs"

if [[ $# -ne 1 ]]; then
  printf 'Usage: %s <feature-name>\n' "$0" >&2
  exit 2
fi

FEATURE="$1"
cd "$REPO"
SPEC_DIR="$SPECS_DIR/$FEATURE"

# derived state ต้องไม่กลืน raw evidence: feature ที่ blocked หรือไม่รู้จักคือกรณีที่ต้องเห็น [a]-[d] มากที่สุด
STATE_RC=0
STATE="$(python3 "$REPO/scripts/spec_contract.py" state --feature "$FEATURE")" || STATE_RC=$?

printf '== [a] artifacts: %s/ ==\n' "$SPEC_DIR"
ls -la "$SPEC_DIR/" || printf 'MISSING %s/\n' "$SPEC_DIR"
printf '\n== [b] checkboxes: %s/tasks.md ==\n' "$SPEC_DIR"
if [[ -f "$SPEC_DIR/tasks.md" ]]; then
  grep -n '^- \[.\]' "$SPEC_DIR/tasks.md" || printf '(ไม่มีบรรทัด checkbox ใน tasks.md)\n'
else
  printf '(ยังไม่มี tasks.md — phase นี้ยังไปไม่ถึง tasks)\n'
fi
printf '\n== [c] git: log -15 + status --short (เห็น untracked ??) ==\n'
git log --oneline -15 || printf '(ยังไม่มี commit)\n'
printf '%s\n' '--'
git status --short || printf '(ไม่ใช่ git repository)\n'
printf '\n== [d] disk artifacts ==\n'
CODE_DIR="${SDD_CODE_DIR:-src}"
ls "$CODE_DIR/" 2>/dev/null || printf 'MISSING %s/\n' "$CODE_DIR"
MANIFEST="${SDD_MANIFEST:-pol-core.slnx}"
test -f "$MANIFEST" && printf '%s: yes\n' "$MANIFEST" || printf '%s: MISSING\n' "$MANIFEST"
printf '\n== [e] derived state ==\n%s\n' "$STATE"

exit "$STATE_RC"
