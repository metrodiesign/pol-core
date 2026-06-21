#!/usr/bin/env bash
# check-spec-edit.sh — harness-agnostic spec-edit advisory (single source).
#
# Input: $1 = the file path about to be / just edited (or stdin if no argv).
# Output: if that file is a requirements.md which is APPROVED while its sibling
#   tasks.md still has open tasks, print a ONE-LINE advisory message to stdout;
#   otherwise print nothing. ALWAYS exit 0 — this informs, it NEVER blocks.
#
# Each harness adapter wraps this stdout in its own surface — Claude
# (.claude/hooks/spec-edit-guard.sh) -> hookSpecificOutput.additionalContext JSON;
# Codex (.codex/hooks/spec-edit-guard.sh) -> stderr; OpenCode
# (.opencode/plugins/spec-edit-guard.js) -> console.error. The approved/open-task
# policy lives HERE once so all three warn identically.
set -u

FILE="${1:-$(cat 2>/dev/null)}"
[ -n "$FILE" ] || exit 0

# only requirements.md under .ai/specs/** (or legacy .claude/specs/**) — editing tasks.md/design.md is silent
case "$FILE" in
  */.ai/specs/*/requirements.md) ;;
  .ai/specs/*/requirements.md) ;;
  */.claude/specs/*/requirements.md) ;;
  .claude/specs/*/requirements.md) ;;
  *) exit 0 ;;
esac

# read the file on disk (ground truth); a not-yet-written file has nothing to warn about
[ -f "$FILE" ] || exit 0

# only when the spec is marked approved (covers "approved <date>", "(quick, no gates)",
# "approved <orig>, amended <date>"); draft or no Status line -> silent
grep -m1 -iE '^> *Status: *approved' "$FILE" >/dev/null 2>&1 || exit 0

# only when the sibling tasks.md still has an open task
TASKS="${FILE%/requirements.md}/tasks.md"
[ -f "$TASKS" ] || exit 0
OPEN=$(grep -cE '^[[:space:]]*- \[ \]' "$TASKS" 2>/dev/null)
[ "${OPEN:-0}" -gt 0 ] 2>/dev/null || exit 0

printf 'เตือน: %s ถูก mark approved แล้ว แต่ยังมี %s task ค้างใน tasks.md (- [ ]). การแก้ requirements ตอนนี้ต้อง propagate ไป design.md/tasks.md (CLAUDE.md: keep specs in sync) และอาจต้อง re-approve. ยืนยันว่าตั้งใจแก้.\n' "$FILE" "$OPEN"
exit 0
