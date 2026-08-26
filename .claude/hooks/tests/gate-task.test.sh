#!/usr/bin/env bash
# gate-task.test.sh — Task 3 regression fixtures for .ai/bin/gate-task.sh.
# Proves: exact .NET default commands under unset envs (REQ-3.12/3.13), red-command
# blocking (REQ-3.16/3.17), real exit honored with no zero-test special pass (3.18),
# green-only cache reuse with Evidence always re-validated (3.19–3.21) and
# SDD_GATE_NO_CACHE=1 forcing real execution (3.22/3.23).
# A PATH shim records `dotnet` argv; SDD_GATE_SHELL keeps the shim visible because
# login shells reset PATH on macOS.
set -u

HERE="$(cd "$(dirname "$0")" && pwd)"
GATE="$HERE/../../../.ai/bin/gate-task.sh"
pass=0 fail=0
ok() { pass=$((pass+1)); }
bad() { fail=$((fail+1)); echo "FAIL: $1"; }

TMP="$(mktemp -d "${TMPDIR:-/tmp}/gate-task-test.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

SHIM="$TMP/shim"; LOG="$TMP/dotnet.log"; COUNT="$TMP/exec.count"
mkdir -p "$SHIM"; echo 0 >"$COUNT"; : >"$LOG"
cat >"$SHIM/dotnet" <<EOF
#!/usr/bin/env bash
printf '%s\n' "\$*" >>"$LOG"
if [[ "\$1" == "test" ]]; then
  n=\$(( \$(cat "$COUNT") + 1 )); echo "\$n" >"$COUNT"
  exit "\${SDD_FAKE_TEST_RC:-0}"
fi
exit "\${SDD_FAKE_BUILD_RC:-0}"
EOF
chmod +x "$SHIM/dotnet"

# The gate executes from the REPO root it derives from its own location, so the
# fixture redirects it via SDD_GATE_REPO to a sandbox that IS a git repository
# (cache lives under its .git) and ships a pol-core.slnx for default resolution.
REPO_DIR="$TMP/repo"; mkdir -p "$REPO_DIR"; : >"$REPO_DIR/pol-core.slnx"
git -C "$REPO_DIR" init -q 2>/dev/null || true

run_gate() { # $1 = after-file ; $2 = no-cache(0|1) ; $3 = fake test rc ("" => 0)
  local before="$TMP/golden-before.md" ranges="$TMP/r.json"
  python3 "$HERE/../../../scripts/spec_contract.py" diff-ranges \
    --before-file "$before" --after-file "$1" >"$ranges" || return 9
  (
    export PATH="$SHIM:$PATH"
    export SDD_GATE_SHELL='bash -c'
    export SDD_GATE_REPO="$REPO_DIR"
    unset SDD_TYPECHECK_CMD SDD_TEST_CMD
    if [ "${2:-}" = "1" ]; then export SDD_GATE_NO_CACHE=1; else unset SDD_GATE_NO_CACHE || true; fi
    if [ -n "${3:-}" ]; then export SDD_FAKE_TEST_RC="$3"; else unset SDD_FAKE_TEST_RC || true; fi
    bash "$GATE" .ai/specs/fx/tasks.md "$before" "$1" "$ranges" pre-commit 2>"$TMP/stderr"
  )
}

cat >"$TMP/golden-before.md" <<'MD'
- [ ] 1. demo task.
MD
cat >"$TMP/golden-after.md" <<'MD'
- [x] 1. demo task.
     Evidence:
       - test: `dotnet test` -> ran 1929 tests; OK
       - viewports: n/a — tooling-only gate fixtures
       - deviations: none
MD
cat >"$TMP/red-after.md" <<'MD'
- [x] 1. demo task.
     Evidence:
       - test: planned, not yet run
       - viewports: n/a — tooling-only gate fixtures
       - deviations: none
MD

CACHE_DIR="$REPO_DIR/.git/sdd-gate-cache/v1"

# A. unset envs -> exact default commands executed (in sandbox repo with pol-core.slnx)
rc=$(run_gate "$TMP/golden-after.md" 1; echo $? )
[ "$rc" = "0" ] && ok || bad "A expected rc 0 got $rc :: $(cat "$TMP/stderr")"
grep -qx 'build pol-core.slnx -warnaserror' "$LOG" \
  && ok || bad "A default build argv missing :: $(tr '\n' '|' <"$LOG")"
grep -q 'test pol-core.slnx --no-build --filter Category!=Integration\|test pol-core.slnx --no-build --filter.\"Category!=Integration\"' "$LOG" \
  && ok || bad "A default test argv missing :: $(tr '\n' '|' <"$LOG")"
: >"$LOG"

# B. red command blocks with exit 2 and label diagnostics
rc=$(run_gate "$TMP/golden-after.md" "" 3; echo $?)
[ "$rc" = "2" ] && ok || bad "B red-command expected rc 2 got $rc"
grep -q 'test ไม่ผ่าน' "$TMP/stderr" && ok || bad "B red diagnostic missing"

# E'. zero tests: real exit status honored (shim exits 3 above == non-zero) — already proven by B.
# C. cache never bypasses Evidence validation:
rm -rf "$REPO_DIR/.git/sdd-gate-cache"
rc=$(run_gate "$TMP/golden-after.md" ""; echo $?)
[ "$rc" = "0" ] && ok || bad "C warm-up expected rc 0 got $rc"
ls "$CACHE_DIR" | grep -c . | grep -qx '[1-9]' && ok || bad "C cache key not persisted"
out=$(run_gate "$TMP/red-after.md"); rc=$?   # tasks.md excluded from inventory -> key stable
[ "$rc" = "2" ] && ok || bad "C cached-but-red must block got $rc :: $out"
grep -q 'EVIDENCE_' "$TMP/stderr" && ok || bad "C EVIDENCE_* diagnostic missing"
python3 - "$CACHE_DIR" <<'PY'
import shutil, sys
shutil.rmtree(sys.argv[1], ignore_errors=True)
PY

# D/E. SDD_GATE_NO_CACHE=1 forces real execution every time...
echo 0 >"$COUNT"
run_gate "$TMP/golden-after.md" 1 >/dev/null 2>&1
run_gate "$TMP/golden-after.md" 1 >/dev/null 2>&1
execs=$(cat "$COUNT")
[ "$execs" = "2" ] && ok || bad "D no-cache executions expected 2 got $execs"
# ...while cache mode reuses an observed green pair without re-running tools
echo 0 >"$COUNT"
rm -rf "$CACHE_DIR"
run_gate "$TMP/golden-after.md" >/dev/null 2>&1          # fills cache
first_execs=$(cat "$COUNT")
run_gate "$TMP/golden-after.md" >/dev/null 2>&1          # should hit cache
second_execs=$(cat "$COUNT")
[ "$second_execs" = "$first_execs" ] && ok || bad "E cache-hit re-executed ($first_execs -> $second_execs)"
python3 - "$CACHE_DIR" <<'PY'
import shutil, sys
shutil.rmtree(sys.argv[1], ignore_errors=True)
PY

echo "---"
echo "pass=$pass fail=$fail"
[ "$fail" -eq 0 ]
