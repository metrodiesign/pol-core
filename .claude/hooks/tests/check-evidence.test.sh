#!/usr/bin/env bash
# check-evidence.test.sh — envelope + exit mapping contract of the Evidence thin CLI.
# allow=0 · policy-fail=1 (EVIDENCE_*) · engine-fail=2 (GATE_*).
set -u

HERE="$(cd "$(dirname "$0")" && pwd)"
BIN="$HERE/../../../.ai/bin/check-evidence.sh"
ENGINE_DIR="$HERE/../../../scripts"
pass=0 fail=0
ok() { pass=$((pass+1)); }
bad() { fail=$((fail+1)); echo "FAIL: $1"; }

TMP="$(mktemp -d "${TMPDIR:-/tmp}/check-evidence.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

cat >"$TMP/golden.md" <<'MD'
- [x] 1. demo task.
     Satisfies: REQ-1.1
     Verify:
       - `x`
     Evidence:
       - test: `true` -> ran 3 tests; OK
       - viewports: n/a — tooling-only
       - deviations: none
MD
cat >"$TMP/before.md" <<'MD'
- [ ] 1. demo task.
MD
cat >"$TMP/red.md" <<'MD'
- [x] 1. demo task.
     Evidence:
       - test: planned only, no command
       - viewports: n/a — tooling-only
       - deviations: none
MD
: >"$TMP/empty"

ranges_of() { python3 "$ENGINE_DIR/spec_contract.py" diff-ranges --before-file "$1" --after-file "$2"; }

# 1. canonical flip -> allow / exit 0
ranges_of "$TMP/before.md" "$TMP/golden.md" >"$TMP/r-ok.json"
code=0; out=$(bash "$BIN" .ai/specs/f/tasks.md "$TMP/before.md" "$TMP/golden.md" "$TMP/r-ok.json" pre-commit) || code=$?
[ "$code" = "0" ] && ok || bad "allow expected rc 0 got $code :: $out"
echo "$out" | grep -q '"verdict": "allow"' && ok || bad "missing allow verdict :: $out"

# 2. broken observation -> policy-fail / exit 1 with stable codes
ranges_of "$TMP/before.md" "$TMP/red.md" >"$TMP/r-red.json"
code=0; out=$(bash "$BIN" f.md "$TMP/before.md" "$TMP/red.md" "$TMP/r-red.json" pre-commit) || code=$?
[ "$code" = "1" ] && ok || bad "policy expected rc 1 got $code"
echo "$out" | grep -q '"verdict": "policy-fail"' && ok || bad "missing policy verdict"
echo "$out" | grep -qE 'EVIDENCE_(COMMAND|RESULT)_MISSING' && ok || bad "expected EVIDENCE code :: $out"

# 3. tampered ranges -> engine-fail / exit 2
printf '{"ranges": []}' >"$TMP/r-stale.json"
code=0; out=$(bash "$BIN" f.md "$TMP/before.md" "$TMP/golden.md" "$TMP/r-stale.json" pre-commit) || code=$?
[ "$code" = "2" ] && ok || bad "engine expected rc 2 got $code"
echo "$out" | grep -q 'GATE_RANGE_INVALID' && ok || bad "expected GATE_RANGE_INVALID :: $out"

# 4. brand-new file -> before-missing path works
ranges_of "$TMP/empty" "$TMP/golden.md" >"$TMP/r-new.json"
code=0; out=$(bash "$BIN" f.md "-" "$TMP/golden.md" "$TMP/r-new.json" ci) || code=$?
[ "$code" = "0" ] && ok || bad "new-file rc got $code :: $out"

# 5. conflicting existence flags -> engine-fail
bash "$BIN" f.md "$TMP/before.md" "$TMP/golden.md" "$TMP/r-new.json" ci >/dev/null 2>&1 \
  && bad "conflict flags must fail" || true

echo "---"
echo "pass=$pass fail=$fail"
[ "$fail" -eq 0 ]
