#!/usr/bin/env bash
# ci-dotnet-integration-fail-closed.test.sh — regression guard for bugfix-sim-guardrails F11/F12.
# Run: bash .claude/hooks/tests/ci-dotnet-integration-fail-closed.test.sh   (exit 0 = all passed)
#
# WHY A STATIC GUARD (not a live-CI test): F11/F12 require the `dotnet integration (live SQL
# 2025)` required check to fail CLOSED — GitHub counts `skipped` as a passed required check, so a
# `needs:`/`if:` path that can resolve to "skip" lets a broken or secret-less run merge anyway.
# Proving the ORIGINAL bug (D3) for real means deleting the repo's MSSQL_SA_PASSWORD secret and
# running CI, which is out of reach for an automated test. This guard instead pins the fix
# statically on .github/workflows/ci.yml: job `dotnet-integration` must carry no job-level
# `needs:` or `if:` key at all (F12's own fix description). If either key reappears, this test
# reds job `verify` (wired in alongside .claude/hooks/tests/*.test.sh, docker/entrypoint.test.sh,
# docker/migrate-entrypoint.test.sh — see the "Guard regression tests" step).
set -u

CI_FILE="$(cd "$(dirname "$0")/../../.." && pwd)/.github/workflows/ci.yml"
pass=0
fail=0

# Extracts the top-level job block for $1 (job key, e.g. "dotnet-integration") from $2 (file):
# every line from the "  <job>:" line up to (not including) the next top-level "  <key>:" line,
# or EOF. Job keys sit at exactly 2-space indent; everything inside a job body sits deeper.
extract_job_block() {
    awk -v want="  $1:" '
        $0 == want { flag=1; print; next }
        flag && /^  [A-Za-z]/ { flag = 0 }
        flag { print }
    ' "$2"
}

check_no_job_level_key() { # $1=desc $2=block $3=key regex, e.g. "needs:" or "if:"
    if printf '%s\n' "$2" | grep -qE "^    $3"; then
        fail=$((fail + 1)); echo "FAIL [$1] job-level '$3' found — must not exist (F12)"
    else
        pass=$((pass + 1))
    fi
}

block="$(extract_job_block "dotnet-integration" "$CI_FILE")"

if [ -z "$block" ]; then
    fail=$((fail + 1)); echo "FAIL [sanity] job 'dotnet-integration' not found in $CI_FILE"
else
    pass=$((pass + 1))
fi

check_no_job_level_key "dotnet-integration: no needs:" "$block" "needs:"
check_no_job_level_key "dotnet-integration: no if:"    "$block" "if:"

echo ""
echo "pass=$pass fail=$fail"
[ "$fail" -eq 0 ]
