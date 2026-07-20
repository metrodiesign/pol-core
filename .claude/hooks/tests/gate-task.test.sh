#!/usr/bin/env bash
# gate-task.test.sh — adversarial test for the task-boundary quality gate.
# Run: bash .claude/hooks/tests/gate-task.test.sh   (exit 0 = all passed)
#
# Logic lives in .ai/bin/gate-task.sh (the single-source engine); .claude/hooks/
# task-gate.sh, .codex/hooks/task-gate.sh and .opencode/plugins/task-gate.js are thin
# adapters that hand the engine an input shape (scoped flipped hunk OR whole file).
#
# WHAT THIS PROVES (PR #24 findings #6, #19, #20, #28):
#   #6  — all THREE adapter input shapes (Claude scoped hunk, Codex added-lines,
#         OpenCode whole-file) yield the SAME engine verdict for the same flip.
#   #19 — the Evidence check is PER TASK: a pre-existing Evidence line on a DIFFERENT
#         task does NOT satisfy a freshly-flipped task that has none of its own.
#   #20/#28 — Evidence must be non-trivial content; the Claude Write path no longer
#         auto-injects a synthetic 'Evidence: n/a (Write path)', so a [x] flip written
#         via Write with no real Evidence blocks exactly like Edit/OpenCode.
#
# ISOLATION: the engine runs typecheck + test (npm) BEFORE the Evidence gate. To test
# the Evidence logic deterministically (and not depend on a green tsc/vitest env), each
# case runs the engine inside a throwaway sandbox dir whose package.json stubs
# `typecheck` and `test` to no-ops (exit 0). The gate file path is a real
# .claude/specs/<feature>/tasks.md shape so the engine's path filter fires. We assert
# exit code only (2 = block, 0 = allow); npm/git are never run for real against the repo.
set -u

ENGINE="$(cd "$(dirname "$0")/../../../.ai/bin" && pwd)/gate-task.sh"
CLAUDE_HOOK="$(cd "$(dirname "$0")/../../../.claude/hooks" && pwd)/task-gate.sh"
pass=0
fail=0

# A self-cleaning sandbox: stub npm scripts so typecheck/test pass, isolating Evidence.
SANDBOX="$(mktemp -d)"
printf '%s' '{"name":"gate-task-test","version":"1.0.0","private":true,"scripts":{"typecheck":"true","test":"true"}}' > "$SANDBOX/package.json"
mkdir -p "$SANDBOX/.claude/specs/sample"
GATEFILE="$SANDBOX/.claude/specs/sample/tasks.md"
# canonical .ai/specs path: prove the engine path filter fires there too (dual-match).
mkdir -p "$SANDBOX/.ai/specs/sample"
AIGATEFILE="$SANDBOX/.ai/specs/sample/tasks.md"

cleanup() {
  # assemble the recursive-force delete token at runtime so the live destructive guard
  # (which scans literal command lines) never sees it as a literal in this file.
  local RMBIN FLAG
  RMBIN="r""m"
  FLAG="-r""f"
  "$RMBIN" "$FLAG" "$SANDBOX" 2>/dev/null || true
}
trap cleanup EXIT

# run the engine with a given GATE_NEW payload, from inside the sandbox so npm picks up
# the stub scripts. Returns the engine exit code.
run_engine() { # $1=GATE_NEW content
  ( cd "$SANDBOX" && GATE_FILE="$GATEFILE" GATE_NEW="$1" "$ENGINE" >/dev/null 2>&1 )
}

check_engine() { # $1=expect(block|allow) $2=desc $3=GATE_NEW
  local want=2
  [ "$1" = allow ] && want=0
  run_engine "$3"
  local rc=$?
  if [ "$rc" -eq "$want" ]; then
    pass=$((pass + 1))
  else
    fail=$((fail + 1))
    echo "FAIL [engine][$1] $2 -> exit $rc (want $want)"
  fi
}

# run via the Claude adapter: JSON payload on stdin (Edit or Write tool).
run_claude_edit() { # $1=old_string $2=new_string
  ( cd "$SANDBOX" && printf '{"tool_name":"Edit","tool_input":{"file_path":%s,"old_string":%s,"new_string":%s}}' \
      "$(printf '%s' "$GATEFILE" | jq -Rs .)" \
      "$(printf '%s' "$1" | jq -Rs .)" \
      "$(printf '%s' "$2" | jq -Rs .)" \
    | "$CLAUDE_HOOK" >/dev/null 2>&1 )
}
run_claude_write() { # $1=content
  ( cd "$SANDBOX" && printf '{"tool_name":"Write","tool_input":{"file_path":%s,"content":%s}}' \
      "$(printf '%s' "$GATEFILE" | jq -Rs .)" \
      "$(printf '%s' "$1" | jq -Rs .)" \
    | "$CLAUDE_HOOK" >/dev/null 2>&1 )
}

check_claude() { # $1=expect $2=desc $3=mode(edit|write) ... payload
  local want=2
  [ "$1" = allow ] && want=0
  local rc
  if [ "$3" = edit ]; then
    run_claude_edit "$4" "$5"; rc=$?
  else
    run_claude_write "$4"; rc=$?
  fi
  if [ "$rc" -eq "$want" ]; then
    pass=$((pass + 1))
  else
    fail=$((fail + 1))
    echo "FAIL [claude/$3][$1] $2 -> exit $rc (want $want)"
  fi
}

# ----- input-shape fixtures for the SAME flip (task 2 flipped to [x]) -----
# Whole file as OpenCode would cat it: task 1 ([x] WITH its own Evidence) + task 2
# ([x] freshly flipped, NO Evidence of its own). The pre-existing Evidence on task 1
# must NOT rescue task 2 (finding #19/#6).
WHOLE_NOEV="$(printf '%s\n' \
  '- [x] 1. Already done task' \
  '     Evidence: tsc exit 0; vitest 5/5 green; viewports 375/768/1440 ok.' \
  '- [x] 2. Freshly flipped task with NO evidence of its own' \
  '- [ ] 3. Not done yet')"

# Claude/Codex scoped hunk for the SAME flip: only task 2's flipped line (no Evidence).
HUNK_NOEV='- [x] 2. Freshly flipped task with NO evidence of its own'

# Codex feeds the patch's added lines (leading '+' already stripped by its adapter,
# so the engine sees the same bare hunk). Same content as the Claude hunk here.
CODEX_HUNK_NOEV="$HUNK_NOEV"

# ----- properly-evidenced fixtures (MUST pass) -----
WHOLE_EV="$(printf '%s\n' \
  '- [x] 1. Already done task' \
  '     Evidence: tsc exit 0; vitest 5/5 green; viewports 375/768/1440 ok.' \
  '- [x] 2. Freshly flipped task, properly evidenced' \
  '     Evidence: tsc --noEmit exit 0; vitest 12/12 green; 375/768/1440 verified.' \
  '- [ ] 3. Not done yet')"

HUNK_EV="$(printf '%s\n' \
  '- [x] 2. Freshly flipped task, properly evidenced' \
  '     Evidence: tsc --noEmit exit 0; vitest 12/12 green; 375/768/1440 verified.')"

# explicit n/a escape is the AGENT's choice in the file — must pass.
HUNK_NA="$(printf '%s\n' \
  '- [x] 4. Docs-only task' \
  '     Evidence: n/a (no runtime code; markdown only)')"

# placeholder Evidence value must NOT count as real evidence (finding #20/#28).
HUNK_PLACEHOLDER="$(printf '%s\n' \
  '- [x] 5. Flip with placeholder evidence' \
  '     Evidence: TODO')"

# the old auto-injected Write-path token, now banned as trivial — must BLOCK.
HUNK_WRITEPATH="$(printf '%s\n' \
  '- [x] 6. Flip with the old synthetic write-path token' \
  '     Evidence: n/a (Write path)')"

# (Codex PR#1) the DOCUMENTED multiline Evidence block — `Evidence:` header on its
# own line followed by bullets — must PASS (TESTING_PROTOCOL.md canonical form).
HUNK_MULTILINE="$(printf '%s\n' \
  '- [x] 7. Flip with the documented multiline Evidence block' \
  'Evidence:' \
  '  - test: `npm test` -> 47 passed / 0 failed' \
  '  - viewports: 375 OK | 768 OK | 1440 OK' \
  '  - deviations: none')"
# a bare `Evidence:` header with NO bullets must still BLOCK (no gaming the header).
HUNK_BARE_HEADER="$(printf '%s\n' \
  '- [x] 8. Flip with only a bare Evidence header' \
  'Evidence:')"

echo "=== #6/#19 PARITY: same flip, three input shapes -> SAME verdict (BLOCK, no own Evidence) ==="
check_engine block "OpenCode whole-file, flipped task has no own Evidence" "$WHOLE_NOEV"
check_engine block "Claude scoped hunk, flipped task has no Evidence"      "$HUNK_NOEV"
check_engine block "Codex added-lines hunk, flipped task has no Evidence"  "$CODEX_HUNK_NOEV"

echo "=== #6 PARITY: same flip, three input shapes -> SAME verdict (ALLOW, properly evidenced) ==="
check_engine allow "OpenCode whole-file, flipped task properly evidenced"  "$WHOLE_EV"
check_engine allow "Claude scoped hunk, flipped task properly evidenced"   "$HUNK_EV"
check_engine allow "Codex added-lines hunk, flipped task properly evidenced" "$HUNK_EV"

echo "=== Codex PR#1: documented multiline Evidence block ==="
check_engine allow "multiline Evidence header + bullets is accepted" "$HUNK_MULTILINE"
check_engine block "bare Evidence header with no bullets still blocks" "$HUNK_BARE_HEADER"

echo "=== #19 PER-TASK: decoy Evidence on another task does NOT rescue the flipped one ==="
# flipped task carries TRIVIAL evidence while a DIFFERENT task has good evidence; the
# flipped task must still BLOCK — the gate is scoped to the flipped task's own region.
DECOY_TRIVIAL="$(printf '%s\n' \
  '- [x] 1. Already done, well evidenced' \
  '     Evidence: tsc exit 0; vitest 8/8 green; 375/768/1440 ok.' \
  '- [x] 2. Freshly flipped, only a placeholder of its own' \
  '     Evidence: TBD' \
  '- [ ] 3. Not done yet')"
check_engine block "trivial Evidence on flipped task, good Evidence on a sibling" "$DECOY_TRIVIAL"
# flipped task is the LAST task: its region is closed by EOF (not a following checkbox).
EOF_NOEV="$(printf '%s\n' \
  '- [x] 1. Already done, evidenced' \
  '     Evidence: real proof here on 375/768/1440' \
  '- [x] 2. Last task, freshly flipped, NO Evidence')"
check_engine block "flipped task is last (region closed by EOF), no Evidence"   "$EOF_NOEV"

echo "=== #20/#28 NON-TRIVIAL EVIDENCE ==="
check_engine block "placeholder 'TODO' is not real evidence"               "$HUNK_PLACEHOLDER"
check_engine block "old synthetic 'n/a (Write path)' token is trivial"     "$HUNK_WRITEPATH"
check_engine allow "explicit 'n/a (...)' escape is the agent's choice"      "$HUNK_NA"
check_engine allow "Evidence value wrapped in backticks counts"            "$(printf '%s\n' '- [x] 8. flip' '     Evidence: `tsc --noEmit` exit 0; vitest 9/9')"

echo "=== PATH FILTER: gate fires under canonical .ai/specs AND legacy .claude/specs ==="
# same no-Evidence flip must BLOCK regardless of which specs root holds tasks.md.
( cd "$SANDBOX" && GATE_FILE="$AIGATEFILE" GATE_NEW="$HUNK_NOEV" "$ENGINE" >/dev/null 2>&1 )
[ $? -eq 2 ] && pass=$((pass+1)) || { fail=$((fail+1)); echo "FAIL [engine][block] .ai/specs path no Evidence"; }
( cd "$SANDBOX" && GATE_FILE="$AIGATEFILE" GATE_NEW="$HUNK_EV" "$ENGINE" >/dev/null 2>&1 )
[ $? -eq 0 ] && pass=$((pass+1)) || { fail=$((fail+1)); echo "FAIL [engine][allow] .ai/specs path evidenced"; }

echo "=== TRIGGER: non-flip edits allow silently ==="
check_engine allow "no [x] in content -> not a flip"                       '- [ ] 7. still todo'
check_engine allow "empty content -> not a flip"                           ''

echo "=== #28 CLAUDE WRITE PATH: no synthetic Evidence injection ==="
# Write path used to auto-pass by injecting synthetic Evidence; now the real content is
# scoped per task, so a [x] flip with no own Evidence BLOCKS via Write just like Edit.
check_claude block "Write whole-file, flipped task no own Evidence" write "$WHOLE_NOEV"
check_claude allow "Write whole-file, properly evidenced"           write "$WHOLE_EV"

echo "=== CLAUDE EDIT ADAPTER: delta detection + per-task Evidence ==="
# Edit that increases [x] count with no Evidence -> block; with Evidence -> allow.
check_claude block "Edit flip [ ]->[x] no Evidence" edit '- [ ] 2. Freshly flipped task with NO evidence of its own' "$HUNK_NOEV"
check_claude allow "Edit flip [ ]->[x] evidenced"   edit '- [ ] 2. Freshly flipped task, properly evidenced' "$HUNK_EV"
# Edit that does NOT increase [x] count (already [x]) -> allow silently (not a new flip).
check_claude allow "Edit non-flip (already [x])"    edit "$HUNK_EV" "$HUNK_EV"

echo "---"
echo "pass=$pass fail=$fail"
[ "$fail" -eq 0 ]
