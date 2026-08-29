#!/usr/bin/env bash
set -u

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
TMP="$(mktemp -d "${TMPDIR:-/tmp}/mktemp-fixture.XXXXXX")" || exit 1
trap 'rm -rf "$TMP"' EXIT
mkdir "$TMP/bin"
cat >"$TMP/bin/mktemp" <<'SH'
#!/bin/sh
exit 1
SH
chmod +x "$TMP/bin/mktemp"

pass=0 fail=0
for fixture in \
  spec-edit-guard.test.sh gate-task.test.sh secrets-guard.test.sh \
  repo-policy-alignment.test.sh destructive-guard.test.sh \
  ci-evidence-scope.test.sh check-evidence.test.sh cross-harness-conformance.test.sh \
  spec-slice.test.sh; do
  PATH="$TMP/bin:$PATH" bash "$ROOT/.claude/hooks/tests/$fixture" >/dev/null 2>&1
  rc=$?
  if [ "$rc" -ne 0 ]; then pass=$((pass + 1)); else fail=$((fail + 1)); echo "FAIL: $fixture continued after mktemp"; fi
done

echo "---"
echo "pass=$pass fail=$fail"
[ "$fail" -eq 0 ]
