#!/usr/bin/env bash
# task-gate.sh — thin Claude adapter (PostToolUse: Edit|Write), Task 3 raw-selection.
# Reads the pre-tool snapshot captured by .claude/hooks/task-snapshot.sh, reads the
# post-tool bytes from disk, gets canonical ranges from spec_contract.py and lets
# .ai/bin/gate-task.sh own Evidence + commands + cache + build/test verdicts.
# เขียว = เงียบ exit 0, แดง = exit 2 (engine-fail หรือ policy-fail map เป็น block)
set -uo pipefail

INPUT="$(cat)"
[[ -z "$INPUT" ]] && exit 0
command -v jq >/dev/null 2>&1 || exit 0

FILE="$(printf '%s' "$INPUT" | jq -r '.tool_input.file_path // empty')"
case "$FILE" in
  */.ai/specs/*/tasks.md|.ai/specs/*/tasks.md|*/.claude/specs/*/tasks.md|.claude/specs/*/tasks.md) ;;
  *) exit 0 ;;
esac

REPO="$(cd "$(dirname "$0")/../.." && pwd)"
if ROOT="$(git -C "$REPO" rev-parse --show-toplevel 2>/dev/null)"; then REPO="$ROOT"; fi
ENGINE="$REPO/scripts/spec_contract.py"
GATE="$REPO/.ai/bin/gate-task.sh"
STORE="$REPO/.git/sdd-task-snapshots"

KEY="$(python3 -c 'import hashlib,sys; print(hashlib.sha256(os.path.abspath(sys.argv[1]).encode()).hexdigest())' "$FILE" 2>/dev/null)" || KEY=""
SNAP="$STORE/$KEY"

block() { printf '%s\n' "$1" >&2; exit 2; }

[[ -f "$SNAP" ]] || block "GATE_SNAPSHOT_MISSING: ไม่มี pre-tool snapshot สำหรับ $FILE (task-snapshot hook ต้อง capture ก่อน Edit/Write)"

TMP="$(mktemp -d "${TMPDIR:-/tmp}/sdd-claude-gate.XXXXXX")" || block 'engine-fail: mktemp'
trap 'rm -rf "$TMP"' EXIT

python3 - "$SNAP" "$FILE" "$TMP/before.bin" "$TMP/after.bin" "$TMP/state" <<'PY' || block 'GATE_SNAPSHOT_MISSING: snapshot decode failed'
import base64, json, sys
payload = json.load(open(sys.argv[1]))
before = base64.b64decode(payload.get("before_b64") or "")
if payload.get("before_exists") is False:
    before = b""
open(sys.argv[3], "wb").write(before)
open(sys.argv[4], "wb").write(open(sys.argv[2], "rb").read())
open(sys.argv[5], "w").write("EXISTS" if payload.get("before_exists") else "MISSING")
PY
BEFORE_FLAG="-"
if [[ "$(cat "$TMP/state")" == "EXISTS" ]]; then BEFORE_FLAG="$TMP/before.bin"; fi
RANGES="$TMP/ranges.json"
: >"$TMP/empty"
if [[ "$BEFORE_FLAG" == "-" ]]; then
  python3 "$ENGINE" diff-ranges --before-file "$TMP/empty" --after-file "$TMP/after.bin" >"$RANGES" \
    || block 'engine-fail: diff-ranges'
else
  python3 "$ENGINE" diff-ranges --before-file "$BEFORE_FLAG" --after-file "$TMP/after.bin" >"$RANGES" \
    || block 'engine-fail: diff-ranges'
fi

rm -f "$SNAP"
exec bash "$GATE" "$FILE" "$BEFORE_FLAG" "$TMP/after.bin" "$RANGES" claude-edit
