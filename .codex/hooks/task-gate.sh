#!/usr/bin/env bash
# task-gate.sh — Codex PostToolUse adapter (Tier 2 harness hook), Task 3 raw-selection.
#
# Design contract (.ai/specs/sdd-operating-layer-parity/design.md §Adapter Seams):
# an adapter must send a raw GateSelection built from full pre/post snapshot bytes.
# The documented Codex hook surface is PostToolUse-only with no pre-write event and
# no full-file before payload, so this adapter CANNOT produce a correlated
# before/after pair by itself. Per the adapter contract, that is fail-closed:
#   - it never guesses Evidence/task state from patch text (no diff re-implementation)
#   - it never suppresses engine-fail into allow
# The durable floor still applies on every path: .githooks/pre-commit + CI run the
# same engine over real HEAD/index snapshots, so an invalid flip cannot land.
# Task 8 revisits this file if a pre-write hook lands in the Codex runtime.
set -uo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"

INPUT="$(cat 2>/dev/null || true)"
[[ -z "$INPUT" ]] && exit 0
command -v jq >/dev/null 2>&1 || exit 0

FILE="$(printf '%s' "$INPUT" | jq -r '
  .tool_input.file_path // .tool_input.path // .input.file_path // .input.path // empty
' 2>/dev/null || true)"
case "$FILE" in
  */.ai/specs/*/tasks.md|.ai/specs/*/tasks.md|*/.claude/specs/*/tasks.md|.claude/specs/*/tasks.md) ;;
  *) exit 0 ;;
esac

echo "GATE_SNAPSHOT_MISSING: Codex hook runtime has no pre-write event; raw GateSelection (before+after bytes) cannot be captured for $FILE" >&2
echo "Durable floor stays active: the .githooks/pre-commit and CI Evidence gates will validate this tasks.md at commit time." >&2
exit 2
