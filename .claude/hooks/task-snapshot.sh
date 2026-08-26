#!/usr/bin/env bash
# task-snapshot.sh — PreToolUse(Edit|Write) raw capture for the Task 3 gate.
# Stores the FULL pre-tool bytes of a tasks.md so .claude/hooks/task-gate.sh can
# hand spec_contract.py a real GateSelection pair (design §Adapter Seams rule 1).
# Snapshot store: $(git rev-parse --show-toplevel)/.git/sdd-task-snapshots/<sha256(abs-path)>
# This hook NEVER blocks: it always exits 0.
set -uo pipefail

INPUT="$(cat)"
[[ -z "$INPUT" ]] && exit 0

command -v jq >/dev/null 2>&1 || exit 0
FILE="$(printf '%s' "$INPUT" | jq -r '.tool_input.file_path // empty')" || exit 0
case "$FILE" in
  */.ai/specs/*/tasks.md|.ai/specs/*/tasks.md|*/.claude/specs/*/tasks.md|.claude/specs/*/tasks.md) ;;
  *) exit 0 ;;
esac

REPO="$(cd "$(dirname "$0")/../.." && pwd)"
if ROOT="$(git -C "$REPO" rev-parse --show-toplevel 2>/dev/null)"; then REPO="$ROOT"; fi
STORE="$REPO/.git/sdd-task-snapshots"
mkdir -p "$STORE" 2>/dev/null || exit 0

ABS_PATH="$(python3 -c 'import os,sys; print(os.path.abspath(sys.argv[1]))' "$FILE")"
KEY="$(python3 -c 'import hashlib,sys; print(hashlib.sha256(sys.argv[1].encode()).hexdigest())' "$ABS_PATH")"
if [[ -e "$ABS_PATH" ]]; then
  PAYLOAD="$(python3 - "$ABS_PATH" <<'PY'
import base64, json, sys
data = open(sys.argv[1], "rb").read()
print(json.dumps({"before_exists": True, "before_b64": base64.b64encode(data).decode()}, sort_keys=True))
PY
)"
else
  PAYLOAD='{"before_exists": false, "before_b64": ""}'
fi
TMP="$STORE/.tmp-$KEY.$$"
printf '%s' "$PAYLOAD" >"$TMP" && mv -f "$TMP" "$STORE/$KEY"
exit 0
