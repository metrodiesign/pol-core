#!/usr/bin/env bash
# task-gate.sh — Codex PostToolUse adapter (Tier 2 harness hook).
#
# Thin adapter ONLY: it inspects what Codex just wrote and, when a
# .ai/specs/<feature>/tasks.md (or legacy .claude/specs/) checkbox was flipped to [x], delegates the
# actual typecheck/test/Evidence decision to the single-source engine in
# .ai/bin/gate-task.sh. No gate logic lives here — keep typecheck/test/Evidence
# policy in .ai/bin/gate-task.sh so Claude, Codex, OpenCode and CI all share one
# gate policy. (This adapter reduces the patch to its added lines before the check;
# the engine then scopes the Evidence requirement PER flipped [x] task, so the verdict
# is identical whether it receives these added lines or the whole file from OpenCode.)
#
# !!! Codex PostToolUse input/format per https://developers.openai.com/codex/hooks
# !!! — confirm tool_input shape for apply_patch; the exact key path for the patch
# !!! body / edited file path is not fully pinned in the public docs, so we read
# !!! stdin once and try the most likely shapes, then fall back to argv. Adjust the
# !!! jq paths below once the real schema is confirmed.
# !!! The git+CI floor (Tier 1: .githooks/ pre-commit Evidence gate + CI) still
# !!! applies as the hard guarantee even if this in-loop convenience hook needs
# !!! tuning — i.e. even with imperfect extraction, a [x] without green code +
# !!! Evidence is still caught at commit/push/PR.
set -uo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
ENGINE="$REPO_ROOT/.ai/bin/gate-task.sh"

INPUT="$(cat 2>/dev/null || true)"

FILE=""
NEW=""
if [ -n "$INPUT" ] && command -v jq >/dev/null 2>&1; then
  # Edited file path — try the documented + likely shapes (Edit/Write/apply_patch).
  FILE=$(printf '%s' "$INPUT" | jq -r '
    .tool_input.file_path // .tool_input.path // .input.file_path // .input.path // empty
  ' 2>/dev/null || true)
  # The content/patch body the tool introduced — Write content, Edit new_string, or
  # an apply_patch / Bash command string (which embeds the patch + added lines).
  NEW=$(printf '%s' "$INPUT" | jq -r '
    .tool_input.content
    // .tool_input.new_string
    // .tool_input.command
    // .tool_input.input
    // .input.command
    // .command
    // empty
  ' 2>/dev/null || true)
fi

# Fallback to argv if stdin parsing produced nothing.
[ -z "$FILE" ] && FILE="${1:-}"
[ -z "$NEW" ]  && NEW="${2:-${NEW}}"

# If the file path is still unknown (e.g. apply_patch carries it only inside the
# patch body), recover it from the patch/command text by matching a tasks.md path.
if [ -z "$FILE" ] && [ -n "$NEW" ]; then
  FILE=$(printf '%s\n' "$NEW" \
    | grep -oE '[^[:space:]]*\.(ai|claude)/specs/[^[:space:]]*/tasks\.md' \
    | head -n1 || true)
fi

# Only care about a tasks.md edit; anything else -> allow silently.
case "$FILE" in
  */.ai/specs/*/tasks.md) ;;
  .ai/specs/*/tasks.md) ;;
  */.claude/specs/*/tasks.md) ;;
  .claude/specs/*/tasks.md) ;;
  *) exit 0 ;;
esac

# Reduce a patch body to its added lines (leading '+', dropping the '+++' header)
# so the engine sees the new content the same way it would for an Edit new_string.
# For plain Write/Edit content this is a no-op (passes the text through unchanged).
ADDED=$(printf '%s\n' "$NEW" \
  | sed -n 's/^+\([^+].*\)$/\1/p; s/^+$//p' 2>/dev/null || true)
[ -n "$ADDED" ] && NEW="$ADDED"

# Trigger only when the new content flips a checkbox to [x]; otherwise allow.
printf '%s\n' "$NEW" | grep -qi -- '- \[x\]' || exit 0

# Delegate to the single-source engine. It re-checks the [x] flip, runs
# typecheck + test, and requires an Evidence: block in $NEW. Propagate its exit
# code so a red gate (exit 2) blocks per Codex PostToolUse semantics.
exec "$ENGINE" "$FILE" "$NEW"
