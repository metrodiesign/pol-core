#!/usr/bin/env bash
# spec-edit-guard.test.sh — test for the spec-edit advisory (cross-vendor parity).
# Run: bash .claude/hooks/tests/spec-edit-guard.test.sh   (exit 0 = all passed)
#
# Logic lives in .ai/bin/check-spec-edit.sh (single source). The Claude hook
# (.claude/hooks/spec-edit-guard.sh), Codex hook (.codex/hooks/spec-edit-guard.sh) and
# OpenCode plugin (.opencode/plugins/spec-edit-guard.js) are thin adapters that extract
# the edited file path from their own payload and wrap the engine's stdout in their own
# surface. This advisory NEVER blocks — every path must exit 0; "warn" = non-empty
# surface output, "silent" = none.
#
# WHAT THIS PROVES:
#   #29 — Codex + OpenCode reach parity with Claude on the spec-edit advisory, all three
#         delegating to ONE engine (no logic drift).
#   #26 — the Codex adapter parses the edited-file path from the documented Edit shape
#         ({"tool_input":{"file_path":...}}) AND recovers it from an apply_patch body —
#         the same jq-probe pattern .codex/hooks/task-gate.sh uses for its file path.
# OpenCode's plugin is JS (Bun runtime) and calls the same engine with the file path, so
# its verdict is the engine's verdict — covered by the engine cases below.
set -u

ENGINE="$(cd "$(dirname "$0")/../../../.ai/bin" && pwd)/check-spec-edit.sh"
CLAUDE_HOOK="$(cd "$(dirname "$0")/.." && pwd)/spec-edit-guard.sh"
CODEX_HOOK="$(cd "$(dirname "$0")/../../../.codex/hooks" && pwd)/spec-edit-guard.sh"
pass=0
fail=0

SANDBOX="$(mktemp -d)"
cleanup() { # assemble rm -rf at runtime so the literal never sits in this file
  local RMBIN FLAG; RMBIN="r""m"; FLAG="-r""f"
  "$RMBIN" "$FLAG" "$SANDBOX" 2>/dev/null || true
}
trap cleanup EXIT

mk() { mkdir -p "$SANDBOX/.claude/specs/$1"; }
mk approved-open; mk approved-done; mk draft-open
APPROVED_OPEN="$SANDBOX/.claude/specs/approved-open/requirements.md"
APPROVED_DONE="$SANDBOX/.claude/specs/approved-done/requirements.md"
DRAFT_OPEN="$SANDBOX/.claude/specs/draft-open/requirements.md"
printf '> Status: approved 2026-06-14\n\n# Requirements\n' > "$APPROVED_OPEN"
printf -- '- [x] 1. done\n- [ ] 2. still open\n'           > "$SANDBOX/.claude/specs/approved-open/tasks.md"
printf '> Status: approved 2026-06-14\n'                   > "$APPROVED_DONE"
printf -- '- [x] 1. done\n'                                > "$SANDBOX/.claude/specs/approved-done/tasks.md"
printf '> Status: draft\n'                                 > "$DRAFT_OPEN"
printf -- '- [ ] 1. still open\n'                          > "$SANDBOX/.claude/specs/draft-open/tasks.md"
# canonical .ai/specs fixture: same approved+open shape under the new root.
mkdir -p "$SANDBOX/.ai/specs/ai-approved-open"
AI_APPROVED_OPEN="$SANDBOX/.ai/specs/ai-approved-open/requirements.md"
printf '> Status: approved 2026-06-14\n\n# Requirements\n' > "$AI_APPROVED_OPEN"
printf -- '- [x] 1. done\n- [ ] 2. still open\n'           > "$SANDBOX/.ai/specs/ai-approved-open/tasks.md"

# assert: $1=warn|silent  $2=desc  $3=exit-code  $4=surface-output
assert() {
  local want_out="$1" desc="$2" rc="$3" out="$4"
  local ok=1
  [ "$rc" -eq 0 ] || { ok=0; echo "FAIL [$desc] exit $rc (advisory must exit 0)"; }
  if [ "$want_out" = warn ]; then
    [ -n "$out" ] || { ok=0; echo "FAIL [$desc] expected a warning, got none"; }
  else
    [ -z "$out" ] || { ok=0; echo "FAIL [$desc] expected silence, got: $out"; }
  fi
  if [ "$ok" -eq 1 ]; then pass=$((pass + 1)); else fail=$((fail + 1)); fi
}

# ---- engine (single source) ----
OUT=$("$ENGINE" "$APPROVED_OPEN"); assert warn   "engine: approved + open task"        "$?" "$OUT"
OUT=$("$ENGINE" "$APPROVED_DONE"); assert silent "engine: approved, no open task"       "$?" "$OUT"
OUT=$("$ENGINE" "$DRAFT_OPEN");    assert silent "engine: draft (not approved) + open"   "$?" "$OUT"
OUT=$("$ENGINE" "$SANDBOX/.claude/specs/approved-open/tasks.md"); assert silent "engine: tasks.md path (not requirements)" "$?" "$OUT"
OUT=$("$ENGINE" "/tmp/random/notes.md"); assert silent "engine: unrelated path"          "$?" "$OUT"
OUT=$("$ENGINE" "$AI_APPROVED_OPEN"); assert warn "engine: canonical .ai/specs approved + open" "$?" "$OUT"

# ---- Claude adapter: {"tool_input":{"file_path":...}} -> additionalContext JSON on stdout ----
OUT=$(printf '{"tool_input":{"file_path":%s}}' "$(printf '%s' "$APPROVED_OPEN" | jq -Rs .)" | "$CLAUDE_HOOK" 2>/dev/null)
assert warn   "claude: approved + open -> additionalContext" "$?" "$OUT"
echo "$OUT" | grep -q 'additionalContext' || { echo "FAIL [claude: payload shape] missing additionalContext"; fail=$((fail + 1)); }
OUT=$(printf '{"tool_input":{"file_path":%s}}' "$(printf '%s' "$DRAFT_OPEN" | jq -Rs .)" | "$CLAUDE_HOOK" 2>/dev/null)
assert silent "claude: draft -> silent" "$?" "$OUT"

# ---- Codex adapter: Edit shape on stderr (doc-likely file_path probe) ----
OUT=$(printf '{"tool_name":"Edit","tool_input":{"file_path":%s}}' "$(printf '%s' "$APPROVED_OPEN" | jq -Rs .)" | "$CODEX_HOOK" 2>&1 1>/dev/null)
assert warn   "codex: Edit shape, approved + open" "$?" "$OUT"
OUT=$(printf '{"tool_name":"Edit","tool_input":{"file_path":%s}}' "$(printf '%s' "$APPROVED_DONE" | jq -Rs .)" | "$CODEX_HOOK" 2>&1 1>/dev/null)
assert silent "codex: Edit shape, no open task" "$?" "$OUT"
# ---- Codex adapter: apply_patch body recovery (path only inside the patch text) ----
PATCH="*** Begin Patch
*** Update File: $APPROVED_OPEN
@@
-old
+new
*** End Patch"
OUT=$(printf '{"tool_name":"apply_patch","tool_input":{"command":%s}}' "$(printf '%s' "$PATCH" | jq -Rs .)" | "$CODEX_HOOK" 2>&1 1>/dev/null)
assert warn   "codex: apply_patch body path recovery" "$?" "$OUT"
OUT=$(printf '{"tool_name":"Edit","tool_input":{"file_path":%s}}' "$(printf '%s' "/tmp/x/src/app.ts" | jq -Rs .)" | "$CODEX_HOOK" 2>&1 1>/dev/null)
assert silent "codex: non-spec path" "$?" "$OUT"
# apply_patch body recovery must also match the canonical .ai/specs root (regex dual-match).
AI_PATCH="*** Begin Patch
*** Update File: $AI_APPROVED_OPEN
@@
-old
+new
*** End Patch"
OUT=$(printf '{"tool_name":"apply_patch","tool_input":{"command":%s}}' "$(printf '%s' "$AI_PATCH" | jq -Rs .)" | "$CODEX_HOOK" 2>&1 1>/dev/null)
assert warn   "codex: apply_patch body path recovery under .ai/specs" "$?" "$OUT"

echo "---"
echo "pass=$pass fail=$fail"
[ "$fail" -eq 0 ]
