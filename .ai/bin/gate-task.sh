#!/usr/bin/env bash
# gate-task.sh — harness-agnostic task-boundary quality gate (.ai/bin engine)
# Ported from .claude/hooks/task-gate.sh. The code-green check is STACK-AGNOSTIC: a project
# declares its typecheck/test commands via $SDD_TYPECHECK_CMD / $SDD_TEST_CMD, and a Node
# project that ships package.json scripts is auto-detected for backward-compat; when neither
# is present the check is skipped and only the Evidence gate applies. The Evidence check is
# PER TASK (scoped to each flipped [x] region) and requires non-trivial content, so every
# adapter input shape yields the same verdict (PR #24 findings #6/#19/#20/#28).
#
# Fires only when a .ai/specs/*/tasks.md (or legacy .claude/specs/*/) checkbox is being flipped to [x].
# เขียว = เงียบ exit 0, แดง = exit 2 + stderr ให้แก้ก่อน mark เสร็จ
#
# Interface (harness-agnostic):
#   $1 / $GATE_FILE      = tasks.md file path being edited
#   $2 / $GATE_NEW       = the new_string / content the edit introduces (the flip text).
#                          May be a scoped flipped hunk (Claude/Codex) OR the whole
#                          post-edit file (OpenCode) — the engine scopes Evidence PER
#                          TASK so the verdict is IDENTICAL across those input shapes.
# The caller's adapter is responsible for extracting these from its own hook payload.
# When $GATE_NEW carries a "- [x]" line it is treated as a flip; a NON-TRIVIAL Evidence:
# block is then required INSIDE THE REGION of EACH flipped [x] task (not just somewhere
# in the input). A pre-existing Evidence line belonging to a different task therefore
# cannot satisfy the gate for a task that has none of its own.

FILE="${1:-${GATE_FILE:-}}"
NEW="${2:-${GATE_NEW:-}}"

case "$FILE" in
  */.ai/specs/*/tasks.md) ;;
  .ai/specs/*/tasks.md) ;;
  */.claude/specs/*/tasks.md) ;;
  .claude/specs/*/tasks.md) ;;
  *) exit 0 ;;
esac

# trigger only on a flip to [x] in the new content
printf '%s\n' "$NEW" | grep -qi -- '- \[x\]' || exit 0

# --- code-green check (STACK-AGNOSTIC, optional) ---
# The framework does not assume Node/npm. A project declares how its code is proven green:
#   $SDD_TYPECHECK_CMD / $SDD_TEST_CMD  — explicit commands for ANY stack (e.g. "pytest -q").
# Backward-compat: a Node project that ships package.json with "typecheck"/"test" scripts is
# auto-detected so it keeps the original npm behavior with no config. When neither a command
# nor a matching package.json script exists, the check is skipped and only the Evidence gate
# (below) applies. Commands are operator-provided config, run via eval to honor their quoting.
TYPECHECK_CMD="${SDD_TYPECHECK_CMD:-}"
TEST_CMD="${SDD_TEST_CMD:-}"
if [ -z "$TYPECHECK_CMD" ] && [ -f package.json ] && grep -q '"typecheck"' package.json; then
  TYPECHECK_CMD='npm run typecheck --silent'
fi
if [ -z "$TEST_CMD" ] && [ -f package.json ] && grep -q '"test"' package.json; then
  TEST_CMD='npm test --silent'
fi

if [ -n "$TYPECHECK_CMD" ]; then
  OUT=$(eval "$TYPECHECK_CMD" 2>&1) || {
    echo 'Task gate: typecheck ไม่ผ่าน — ห้าม mark [x] จนกว่าเขียว' >&2
    echo "$OUT" | tail -20 >&2
    exit 2
  }
fi
if [ -n "$TEST_CMD" ]; then
  OUT=$(eval "$TEST_CMD" 2>&1) || {
    # a runner that exits non-zero ONLY because it found no tests is not a red test —
    # don't block a task that legitimately has none (vitest / pytest phrasings).
    if ! echo "$OUT" | grep -qiE 'no test files found|no tests ran|collected 0 items'; then
      echo 'Task gate: test ไม่ผ่าน — ห้าม mark [x] จนกว่าเขียว' >&2
      echo "$OUT" | tail -20 >&2
      exit 2
    fi
  }
fi

# evidence gate (PER TASK, non-trivial content): code-green is checked first (above);
# only then require that EACH flipped `- [x]` task carries its own `Evidence:` line —
# scoped to that task's region — whose value is non-trivial (not empty / placeholder).
#
# Region of a task = from its `- [x]` checkbox line up to (but excluding) the next
# checkbox line (`- [ ]` / `- [x]`) or EOF. Scoping per task makes the verdict identical
# whether $NEW is a single flipped hunk (Claude/Codex) or the whole file (OpenCode):
# Evidence belonging to a *different* task can no longer satisfy a task that has none.
#
# Non-trivial = after stripping the `Evidence:` label the remaining value must contain a
# real character and not be a bare placeholder (TODO/TBD/???/-/.). The explicit `n/a`
# escape stays valid — but it is the AGENT's choice in the file, never auto-fabricated.
EV_FAIL=$(printf '%s\n' "$NEW" | awk '
  # real() = a value that is neither empty nor a bare placeholder. Shared by the
  # inline Evidence form and the multiline-block bullets so both judge content
  # the same way.
  function real(s,  l) {
    gsub(/^[[:space:]`"'"'"']+|[[:space:]`"'"'"']+$/, "", s)
    l=tolower(s)
    return (s != "" && l != "todo" && l != "tbd" && l != "???" && \
            l != "-" && l != "." && l != "none" && l != "pending" && \
            l != "n/a (write path)")
  }
  # A checkbox line starts a new task region. Track only [x] regions for Evidence.
  /^[[:space:]]*-[[:space:]]\[[xX]\]/ {
    # entering a new [x] task: the previous [x] region just closed — verdict it.
    if (in_x && !have_ev) { print prev_task; failed=1 }
    in_x=1; have_ev=0; ev_open=0
    prev_task=$0
    next
  }
  /^[[:space:]]*-[[:space:]]\[[[:space:]]\]/ {
    # a [ ] (unchecked) task closes any open [x] region.
    if (in_x && !have_ev) { print prev_task; failed=1 }
    in_x=0; have_ev=0; ev_open=0
    next
  }
  {
    # within the current region, look for non-trivial Evidence.
    if (in_x && !have_ev) {
      line=$0
      # match an Evidence: label (case-insensitive), capture the value after the colon.
      if (line ~ /^[[:space:]]*[Ee][Vv][Ii][Dd][Ee][Nn][Cc][Ee]:/) {
        val=line
        sub(/^[[:space:]]*[Ee][Vv][Ii][Dd][Ee][Nn][Cc][Ee]:[[:space:]]*/, "", val)
        if (real(val)) have_ev=1          # inline form: `Evidence: <value>`
        else if (val == "") ev_open=1     # documented header form: bullets follow
      } else if (ev_open && line ~ /^[[:space:]]*[-*][[:space:]]/) {
        # a bullet inside the Evidence: block (`- test: ...`, `- viewports: ...`):
        # one with real content satisfies the gate.
        v=line
        sub(/^[[:space:]]*[-*][[:space:]]*/, "", v)
        if (real(v)) have_ev=1
      }
    }
  }
  END { if (in_x && !have_ev) { print prev_task; failed=1 } exit (failed?1:0) }
')
if [ -n "$EV_FAIL" ]; then
  echo 'Task gate: ขาด Evidence (per-task) — แต่ละ task ที่ mark [x] ต้องมี Evidence: ของตัวเอง (test result + viewports 375/768/1440 หรือ n/a + deviations) ในบล็อกของ task นั้น ก่อน mark [x]' >&2
  echo "$EV_FAIL" | head -5 >&2
  exit 2
fi
exit 0
