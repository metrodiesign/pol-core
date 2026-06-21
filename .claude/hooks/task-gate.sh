#!/usr/bin/env bash
# task-gate.sh — thin Claude adapter (PostToolUse: Edit|Write)
# Keeps the Claude stdin-JSON parse + tasks.md path filter + FLIPPED detection here,
# then delegates typecheck/test/Evidence to .ai/bin/gate-task.sh (GATE_FILE/GATE_NEW).
# เขียว = เงียบ exit 0, แดง = exit 2 + stderr ให้แก้ก่อน mark เสร็จ

INPUT=$(cat)
FILE=$(echo "$INPUT" | jq -r '.tool_input.file_path // empty')
case "$FILE" in
  */.ai/specs/*/tasks.md) ;;
  .ai/specs/*/tasks.md) ;;
  */.claude/specs/*/tasks.md) ;;
  .claude/specs/*/tasks.md) ;;
  *) exit 0 ;;
esac

ENGINE="$(dirname "$0")/../../.ai/bin/gate-task.sh"

TOOL=$(echo "$INPUT" | jq -r '.tool_name // empty')
if [ "$TOOL" = "Edit" ]; then
  OLD=$(echo "$INPUT" | jq -r '.tool_input.old_string // empty')
  NEW=$(echo "$INPUT" | jq -r '.tool_input.new_string // empty')
  OLD_X=$(printf '%s\n' "$OLD" | grep -ci -- '- \[x\]')
  NEW_X=$(printf '%s\n' "$NEW" | grep -ci -- '- \[x\]')
  [ "$NEW_X" -gt "$OLD_X" ] || exit 0
  # Edit flip: delegate with the real new_string. The engine scopes the Evidence check
  # PER FLIPPED TASK over this hunk, so its verdict matches the other adapters.
  exec "$ENGINE" "$FILE" "$NEW"
else
  # Write ทับทั้งไฟล์ เทียบ count ก่อน/หลังไม่ได้ — ยอม trigger เมื่อ content มี [x] ใดๆ
  CONTENT=$(echo "$INPUT" | jq -r '.tool_input.content // empty')
  printf '%s\n' "$CONTENT" | grep -qi -- '- \[x\]' || exit 0
  # Write path passes the REAL whole-file content (no synthetic Evidence injection).
  # The engine scopes Evidence per [x] task over the same whole-file input, so a flip
  # without its own real Evidence blocks here exactly as it does via OpenCode/Edit —
  # one gate policy, identical verdict regardless of which tool wrote the file.
  exec "$ENGINE" "$FILE" "$CONTENT"
fi
