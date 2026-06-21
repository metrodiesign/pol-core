#!/usr/bin/env bash
# codex-adapters.test.sh — simulated-payload regression test for the Codex hook adapters.
# Run: bash .claude/hooks/tests/codex-adapters.test.sh   (exit 0 = all passed)
#
# WHAT THIS PROVES (issue #26): the Codex PreToolUse(Bash) guard (.codex/hooks/guard.sh)
# correctly parses the DOCUMENTED Codex payload shape and routes to the single-source
# engine. Per developers.openai.com/codex/hooks the Bash payload is:
#   {"tool_name":"Bash","tool_input":{"command":"..."},"tool_use_id":...,"session_id":...}
# i.e. the command lives at tool_input.command — guard.sh's FIRST jq path. A correct
# parse of a destructive/bypass command -> exit 2 (block); a parse FAILURE would yield an
# empty command -> exit 0, so the block cases below are fully distinguishing. We also
# exercise the documented fallbacks (.input.command alt shape, and bare argv).
#
# task-gate.sh + spec-edit-guard.sh share the SAME jq-probe for their file path/content;
# that probe is proven distinguishingly by spec-edit-guard.test.sh (Codex Edit +
# apply_patch cases) and the gate verdicts by gate-task.test.sh. The hard floor (git
# pre-commit + CI) backstops all of it regardless of in-loop hook fidelity.
set -u

GUARD="$(cd "$(dirname "$0")/../../../.codex/hooks" && pwd)/guard.sh"
pass=0
fail=0

# assemble the recursive-force token at runtime so the literal never sits in this file
RM="r""m"; FLAG="-r""f"
DESTRUCTIVE="$RM $FLAG /tmp/codex-probe"
BYPASS='git commit --no-verify -m x'
BYPASS_N='git commit -n -m x'
BENIGN='git status'

# $1=expect(block|allow) $2=desc $3=command $4=shape(docbash|altinput|argv)
check_guard() {
  local want=2; [ "$1" = allow ] && want=0
  local rc
  case "$4" in
    docbash)
      printf '{"tool_name":"Bash","tool_input":{"command":%s},"session_id":"s1"}' \
        "$(printf '%s' "$3" | jq -Rs .)" | "$GUARD" >/dev/null 2>&1; rc=$? ;;
    altinput)
      printf '{"input":{"command":%s}}' "$(printf '%s' "$3" | jq -Rs .)" \
        | "$GUARD" >/dev/null 2>&1; rc=$? ;;
    argv)
      "$GUARD" "$3" </dev/null >/dev/null 2>&1; rc=$? ;;
  esac
  if [ "$rc" -eq "$want" ]; then pass=$((pass + 1)); else
    fail=$((fail + 1)); echo "FAIL [$4][$1] $2 -> exit $rc (want $want) :: $3"
  fi
}

# --- doc-confirmed shape: tool_input.command (guard.sh's first jq path) ---
check_guard block "destructive via tool_input.command" "$DESTRUCTIVE" docbash
check_guard block "bypass --no-verify via tool_input.command" "$BYPASS" docbash
check_guard block "bypass -n via tool_input.command" "$BYPASS_N" docbash
check_guard allow "benign via tool_input.command" "$BENIGN" docbash

# --- documented fallbacks still resolve the command ---
check_guard block "destructive via .input.command fallback" "$DESTRUCTIVE" altinput
check_guard block "destructive via bare argv fallback" "$DESTRUCTIVE" argv
check_guard allow "benign via bare argv fallback" "$BENIGN" argv

echo "---"
echo "pass=$pass fail=$fail"
[ "$fail" -eq 0 ]
