#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
TEMP_ROOT="$(mktemp -d)"

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  exit 1
}

mkdir -p "$TEMP_ROOT/scripts" "$TEMP_ROOT/.ai/specs/feature" "$TEMP_ROOT/.ai/specs/bugfix"
cp "$ROOT/scripts/spec_contract.py" "$TEMP_ROOT/scripts/spec_contract.py"
cp "$ROOT/scripts/spec-slice.sh" "$TEMP_ROOT/scripts/spec-slice.sh"
cp "$ROOT/scripts/spec-state.sh" "$TEMP_ROOT/scripts/spec-state.sh"
chmod +x "$TEMP_ROOT/scripts/spec-slice.sh" "$TEMP_ROOT/scripts/spec-state.sh"

approved='> Status: approved 2026-08-25'
cat > "$TEMP_ROOT/.ai/specs/feature/requirements.md" <<EOF
$approved
## REQ-1: Capability
- 1.1 THE SYSTEM SHALL work
EOF
cat > "$TEMP_ROOT/.ai/specs/feature/design.md" <<EOF
$approved
## Build
body

## Requirement Traceability
| REQ | Section |
| --- | --- |
| REQ-1.1 | Build |
EOF
cat > "$TEMP_ROOT/.ai/specs/feature/tasks.md" <<EOF
$approved
- [ ] A1. feature task
  Satisfies: REQ-1.1
EOF
cat > "$TEMP_ROOT/.ai/specs/bugfix/bugfix.md" <<EOF
$approved
- F-1 THE SYSTEM SHALL fix
- B-1 THE SYSTEM SHALL preserve
EOF
cat > "$TEMP_ROOT/.ai/specs/bugfix/tasks.md" <<EOF
$approved
- [ ] B1. bugfix task
  Satisfies: F-1, B-1
EOF

feature_output="$(cd "$TEMP_ROOT" && scripts/spec-slice.sh feature A1)"
printf '%s' "$feature_output" | grep -F -- 'requirements.md: approved' >/dev/null || fail 'feature status missing'
printf '%s' "$feature_output" | grep -F -- '- [ ] A1. feature task' >/dev/null || fail 'feature task is not verbatim'
printf '%s' "$feature_output" | grep -F -- '## Build' >/dev/null || fail 'feature design section missing'

bugfix_output="$(cd "$TEMP_ROOT" && scripts/spec-slice.sh bugfix B1)"
printf '%s' "$bugfix_output" | grep -F -- '- F-1 THE SYSTEM SHALL fix' >/dev/null || fail 'bugfix criterion missing'

if (cd "$TEMP_ROOT" && scripts/spec-slice.sh feature unknown >"$TEMP_ROOT/unknown.out" 2>"$TEMP_ROOT/unknown.err"); then
  fail 'unknown task returned success'
fi
grep -F -- 'available IDs: A1' "$TEMP_ROOT/unknown.err" >/dev/null || fail 'unknown task omitted file-order IDs'

cat > "$TEMP_ROOT/.ai/specs/feature/design.md" <<EOF
$approved
## Requirement Traceability
| REQ | Section |
| --- | --- |
| REQ-1.1 | Missing |
EOF
missing_output="$(cd "$TEMP_ROOT" && scripts/spec-slice.sh feature A1)"
printf '%s' "$missing_output" | grep -F -- 'MISSING: TRACE_SECTION_UNKNOWN:' >/dev/null || fail 'known missing mapping omitted MISSING marker'

state_output="$(cd "$ROOT" && scripts/spec-state.sh sdd-operating-layer-parity)"
printf '%s' "$state_output" | grep -F -- '== [a] artifacts:' >/dev/null || fail 'spec-state removed artifact inventory'
printf '%s' "$state_output" | grep -F -- '== [b] checkboxes:' >/dev/null || fail 'spec-state removed task inventory'
printf '%s' "$state_output" | grep -F -- '== [c] git:' >/dev/null || fail 'spec-state removed git status inventory'
printf '%s' "$state_output" | grep -F -- '== [d] disk artifacts ==' >/dev/null || fail 'spec-state removed disk inventory'
printf '%s' "$state_output" | grep -F -- 'pol-core.slnx: yes' >/dev/null || fail 'spec-state did not use pol-core manifest default'
printf '%s' "$state_output" | grep -F -- '== [e] derived state ==' >/dev/null || fail 'spec-state omitted derived state block'
if (cd "$ROOT" && scripts/spec-state.sh no-such-feature >"$TEMP_ROOT/state-unknown.out" 2>"$TEMP_ROOT/state-unknown.err"); then
  fail 'unknown state feature returned success'
fi
grep -F -- 'SLICE_FEATURE_UNKNOWN:' "$TEMP_ROOT/state-unknown.err" >/dev/null || fail 'unknown state feature omitted engine diagnostic'

mkdir -p "$TEMP_ROOT/.ai/specs/blocked"
printf '%s\n' "$approved" '## REQ-1: Capability' '- 1.1 THE SYSTEM SHALL work' > "$TEMP_ROOT/.ai/specs/blocked/requirements.md"
printf '%s\n' "$approved" '- F-1 THE SYSTEM SHALL fix' > "$TEMP_ROOT/.ai/specs/blocked/bugfix.md"
if (cd "$TEMP_ROOT" && scripts/spec-state.sh blocked >"$TEMP_ROOT/state-blocked.out" 2>"$TEMP_ROOT/state-blocked.err"); then
  fail 'blocked state feature returned success'
fi
for heading in '== [a] artifacts:' '== [b] checkboxes:' '== [c] git:' '== [d] disk artifacts ==' '== [e] derived state =='; do
  grep -F -- "$heading" "$TEMP_ROOT/state-blocked.out" >/dev/null || fail "spec-state swallowed raw evidence block $heading on blocked feature"
done

echo 'PASS: slice and state wrappers preserve compatibility evidence and canonical verdicts'
