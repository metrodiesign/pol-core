#!/usr/bin/env bash
# spec-edit-guard.sh — Codex PreToolUse adapter (Tier 2 harness hook).
#
# Thin adapter ONLY: extracts the path Codex is about to edit (apply_patch / Edit) and
# delegates the advisory decision to the single-source engine
# .ai/bin/check-spec-edit.sh. No logic lives here — same engine Claude/OpenCode use.
# Advisory: surfaces a warning on stderr and ALWAYS exits 0 — it informs, never blocks
# (mirrors Claude's non-blocking spec-edit-guard).
#
# !!! Codex apply_patch/Edit input shape is not fully pinned in the public docs, so we
# !!! read stdin once and try the likely shapes (same probing as task-gate.sh),
# !!! recovering the path from the patch body if needed, then fall back to argv. Confirm
# !!! + adjust at https://developers.openai.com/codex/hooks . The Bash PreToolUse shape
# !!! ({"tool_input":{"command":...}}) is doc-confirmed; the edit-tool path keys are not.
set -uo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
ENGINE="$REPO_ROOT/.ai/bin/check-spec-edit.sh"

INPUT="$(cat 2>/dev/null || true)"
FILE=""
NEW=""
if [ -n "$INPUT" ] && command -v jq >/dev/null 2>&1; then
  FILE=$(printf '%s' "$INPUT" | jq -r '
    .tool_input.file_path // .tool_input.path // .input.file_path // .input.path // empty
  ' 2>/dev/null || true)
  NEW=$(printf '%s' "$INPUT" | jq -r '
    .tool_input.content // .tool_input.new_string // .tool_input.command // .tool_input.input // .input.command // .command // empty
  ' 2>/dev/null || true)
fi
# Fallback to argv if stdin parsing produced nothing.
[ -z "$FILE" ] && FILE="${1:-}"

# apply_patch may carry the path only inside the patch body — recover a requirements.md path.
if [ -z "$FILE" ] && [ -n "$NEW" ]; then
  FILE=$(printf '%s\n' "$NEW" \
    | grep -oE '[^[:space:]]*\.(ai|claude)/specs/[^[:space:]]*/requirements\.md' \
    | head -n1 || true)
fi

[ -n "$FILE" ] || exit 0

MSG=$("$ENGINE" "$FILE" 2>/dev/null || true)
[ -n "$MSG" ] && printf '%s\n' "$MSG" >&2
exit 0
