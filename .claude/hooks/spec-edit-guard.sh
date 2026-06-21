#!/usr/bin/env bash
# spec-edit-guard.sh — Claude PreToolUse(Edit) adapter (Tier 2 harness hook).
#
# Thin adapter ONLY: extracts the edited file path from the Claude JSON payload and
# delegates the advisory decision to the single-source engine
# .ai/bin/check-spec-edit.sh. No logic lives here — keep the approved/open-task policy
# in the engine so Claude, Codex and OpenCode warn identically. Always exit 0 — this
# informs, it never blocks. (Behavior is unchanged from the previous inline version:
# same trigger conditions, same additionalContext message.)
INPUT=$(cat 2>/dev/null)
FILE=$(printf '%s' "$INPUT" | jq -r '.tool_input.file_path // empty' 2>/dev/null)
[ -n "$FILE" ] || exit 0

ENGINE="$(dirname "$0")/../../.ai/bin/check-spec-edit.sh"
MSG=$("$ENGINE" "$FILE" 2>/dev/null)
[ -n "$MSG" ] || exit 0

jq -n --arg m "$MSG" \
  '{hookSpecificOutput:{hookEventName:"PreToolUse",additionalContext:$m}}'
exit 0
