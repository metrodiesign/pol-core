#!/usr/bin/env bash
# guard.sh — Codex PreToolUse(Bash) adapter (Tier 2 harness hook).
#
# Thin adapter ONLY: it extracts the bash command Codex is about to run and delegates
# the actual decision to the single-source check engine in .ai/bin/. It must contain no
# guard logic of its own — keep all regex/policy in .ai/bin/check-*.sh so Claude, Codex,
# OpenCode and CI all enforce byte-for-byte the same rules.
#
# Codex PreToolUse(Bash) payload is DOC-CONFIRMED (developers.openai.com/codex/hooks):
#   {"tool_name":"Bash","tool_input":{"command":"..."},"tool_use_id":...,"session_id":...}
# so tool_input.command (the first jq path below) is the documented shape; the remaining
# paths + the argv fallback are kept as defense against schema drift. The git+CI floor
# (Tier 1: .githooks/ + .github/workflows/ci.yml) is the hard guarantee regardless — this
# hook is the fast in-loop convenience layer. Parsing is exercised by
# .claude/hooks/tests/codex-adapters.test.sh. FIRING: discovered project hooks run only in
# INTERACTIVE Codex after a one-time `/hooks` trust; headless `codex exec` did NOT fire them
# (Codex 0.135/0.139, even with trust override + --dangerously-bypass-hook-trust) — see
# .ai/agents/codex/AGENT.md "Hook firing requires trust" (issue #26).
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
BIN="$REPO_ROOT/.ai/bin"

# --- CMD extraction (Codex PreToolUse Bash shape — doc-confirmed) -------------------------
# tool_input.command is the documented key (issue #26). The remaining jq paths
# (.input.command / .arguments.command / .command) and the argv fallback below stay as
# defensive fallbacks against schema drift across Codex versions.
INPUT="$(cat 2>/dev/null || true)"
CMD=""
if [ -n "$INPUT" ] && command -v jq >/dev/null 2>&1; then
  CMD=$(printf '%s' "$INPUT" | jq -r '
    .tool_input.command // .input.command // .arguments.command // .command // empty
  ' 2>/dev/null || true)
fi
# Fallback: command passed as first argv.
[ -z "$CMD" ] && CMD="${1:-}"

# Nothing to inspect -> allow.
[ -z "$CMD" ] && exit 0

# --- Delegate to the single-source engine ------------------------------------------------
# .ai/bin/check-*.sh: exit 2 = block, 0 = ok. Propagate a block by exiting non-zero.
for c in check-destructive check-bypass; do
  if ! "$BIN/$c.sh" "$CMD"; then
    # check-*.sh already printed the reason on stderr; propagate the block.
    exit 2
  fi
done

exit 0
