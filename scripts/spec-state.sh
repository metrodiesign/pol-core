#!/usr/bin/env bash
#
# spec-state.sh — ground-truth state checker for the spec-driven workflow.
# Print 4 blocks of raw evidence so the agent trusts the FILESYSTEM, not memory:
#   [a] artifacts   — which phase files exist under .ai/specs/<feature>/
#   [b] checkboxes  — task checkbox lines from tasks.md (done vs pending)
#   [c] git         — log --oneline + status --short (status shows untracked `??`
#                     files, which `git diff --stat` NEVER shows — that is the point)
#   [d] disk        — do the project's code dir and manifest actually exist on disk
#
# Usage: scripts/spec-state.sh <feature-name>
#   e.g.:  scripts/spec-state.sh my-feature
#
set -uo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
SPECS_DIR=".ai/specs"
FEATURE="${1:-}"

cd "$REPO"

usage() {
  echo "Usage: $0 <feature-name>" >&2
  echo "spec ที่มีอยู่ใน $SPECS_DIR/:" >&2
  for d in "$SPECS_DIR"/*/; do
    [[ -d "$d" ]] && echo "  - $(basename "$d")" >&2
  done
  exit 1
}

[[ -z "$FEATURE" ]] && usage
SPEC_DIR="$SPECS_DIR/$FEATURE"
[[ -d "$SPEC_DIR" ]] || { echo "ไม่พบ spec '$FEATURE'" >&2; usage; }

echo "== [a] artifacts: $SPEC_DIR/ =="
ls -la "$SPEC_DIR/"

echo ""
echo "== [b] checkboxes: $SPEC_DIR/tasks.md =="
if [[ -f "$SPEC_DIR/tasks.md" ]]; then
  grep -n '^- \[.\]' "$SPEC_DIR/tasks.md" || echo "(ไม่มีบรรทัด checkbox ใน tasks.md)"
else
  echo "(ยังไม่มี tasks.md — phase นี้ยังไปไม่ถึง tasks)"
fi

echo ""
echo "== [c] git: log -15 + status --short (เห็น untracked ??) =="
git log --oneline -15
echo "--"
git status --short

echo ""
echo "== [d] disk artifacts =="
# Code dir + project manifest. Override the code dir via SDD_CODE_DIR; the manifest
# is whatever the project actually has (e.g. package.json for a Node project).
CODE_DIR="${SDD_CODE_DIR:-app}"
ls "$CODE_DIR/" 2>/dev/null || echo "MISSING $CODE_DIR/"
MANIFEST="${SDD_MANIFEST:-package.json}"
test -f "$MANIFEST" && echo "$MANIFEST: yes" || echo "$MANIFEST: MISSING"

exit 0
