#!/usr/bin/env bash
# ci-evidence-scope.test.sh — CI range resolver feeds base/HEAD snapshots to the
# shared engine and maps verdicts to 0/1/2 without touching task semantics here.
set -u

HERE="$(cd "$(dirname "$0")" && pwd)"
pass=0 fail=0
ok() { pass=$((pass+1)); }
bad() { fail=$((fail+1)); echo "FAIL: $1"; }

TMP="$(mktemp -d "${TMPDIR:-/tmp}/ci-evidence.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

SANDBOX="$TMP/repo"; mkdir -p "$SANDBOX/.ai/specs/fx"
mkdir -p "$SANDBOX/scripts" "$SANDBOX/.ai/bin"
cp "$HERE/../../../scripts/spec_contract.py" "$SANDBOX/scripts/"
cp "$HERE/../../../.ai/bin/check-evidence.sh" "$SANDBOX/.ai/bin/"
git -C "$SANDBOX" init -q >/dev/null 2>&1
git -C "$SANDBOX" config user.email t@t; git -C "$SANDBOX" config user.name t

cat >"$SANDBOX/.ai/specs/fx/tasks.md" <<'MD'
- [ ] 1. demo task.
MD
git -C "$SANDBOX" add . && git -C "$SANDBOX" commit -qm base
BASE=$(git -C "$SANDBOX" rev-parse HEAD)

cat >"$SANDBOX/.ai/specs/fx/tasks.md" <<'MD'
- [x] 1. demo task.
     Evidence:
       - test: `true` -> ran 3 tests; OK
       - viewports: n/a — tooling-only
       - deviations: none
MD
git -C "$SANDBOX" add . && git -C "$SANDBOX" commit -qm flip-good
HEAD=$(git -C "$SANDBOX" rev-parse HEAD)

# 1. good flip in range -> exit 0 with an allow envelope
out=$(bash "$HERE/../../../scripts/ci-evidence-scope.sh" --repo "$SANDBOX" "$BASE" "$HEAD")
rc=$?
[ "$rc" = "0" ] && ok || bad "good-range rc=$rc :: $out"
echo "$out" | grep -q '"verdict": "allow"' && ok || bad "no allow envelope :: $out"

# 2. broken evidence committed in HEAD -> policy-fail rc 1 + code surfaced
cat >"$SANDBOX/.ai/specs/fx/tasks.md" <<'MD'
- [x] 1. demo task.
     Evidence:
       - test: planned only
       - viewports: n/a — tooling-only
       - deviations: none
MD
git -C "$SANDBOX" add . && git -C "$SANDBOX" commit -qm flip-bad
HEAD_BAD=$(git -C "$SANDBOX" rev-parse HEAD)
out=$(bash "$HERE/../../../scripts/ci-evidence-scope.sh" --repo "$SANDBOX" "$BASE" "$HEAD_BAD")
rc=$?
[ "$rc" = "1" ] && ok || bad "bad-range rc=$rc :: $out"
echo "$out" | grep -qE 'EVIDENCE_(COMMAND|RESULT)_MISSING|EVIDENCE_PLANNED_ONLY' \
  && ok || bad "expected EVIDENCE code :: $out"

# 3. untouched tasks.md range -> silent allow, rc 0
TIP=$(git -C "$SANDBOX" rev-parse HEAD)
out=$(bash "$HERE/../../../scripts/ci-evidence-scope.sh" --repo "$SANDBOX" "$TIP" "$TIP")
rc=$?
[ "$rc" = "0" ] && ok || bad "same-sha rc=$rc :: $out"
[ -z "$out" ] && ok || bad "identical range should emit nothing :: $out"

echo "---"
echo "pass=$pass fail=$fail"
[ "$fail" -eq 0 ]
