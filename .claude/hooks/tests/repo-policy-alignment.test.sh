#!/usr/bin/env bash
# repo-policy-alignment.test.sh — Task 8 fixture (REQ-1/6/7 floors):
#   1. real-tree alignment --check is green
#   2. a mutated temp tree fails with the exact documented ALIGN_* code
#   3. Pi stays doc-only: capability claims present, zero .pi/extensions/**
#   4. opencode.json keeps its confirm-before-run MCP pin comment
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"
ENGINE="$ROOT/scripts/repo_policy_alignment.py"
pass=0 fail=0
ok() { pass=$((pass+1)); }
bad() { fail=$((fail+1)); echo "FAIL: $1"; }

# 1. real tree aligned
if python3 "$ENGINE" --check >/dev/null 2>&1; then ok; else bad "real tree misaligned"; fi

# 2a. negative fixture: docs drift -> ALIGN_MODULES_MISMATCH
TMP="$(mktemp -d "${TMPDIR:-/tmp}/align-neg.XXXXXX")"
mkdir -p "$TMP/src/Modules/Orders" "$TMP/.ai/shared"
printf '<Project />\n' >"$TMP/src/Modules/Orders/Orders.csproj"
cat >"$TMP/.ai/shared/ARCHITECTURE.md" <<'EOF'
## As-built registry

### Modules

| Module | Role |
|---|---|
| `Admins` | x |

### Runtime DbContexts

| Context | Cluster |
|---|---|
| `ControlPlaneDbContext` | x |

### CI topology

| Provider | Job |
|---|---|
| `github:verify` | x |
EOF
OUT="$(SDD_ALIGNMENT_REPO="$TMP" python3 "$ENGINE" --check --json 2>/dev/null)"
case "$OUT" in
  *ALIGN_MODULES_MISMATCH*) ok ;;
  *) bad "docs drift did not yield ALIGN_MODULES_MISMATCH: $OUT" ;;
esac

# 2b. negative fixture: workflow job rename -> ALIGN_CI_JOBS_MISMATCH
mkdir -p "$TMP/.github/workflows"
printf 'on:\njobs:\n  verify:\n    runs-on: x\n' >"$TMP/.github/workflows/ci.yml"
printf 'stages:\n  - build\nverify:\n  script: true\n' >"$TMP/.gitlab-ci.yml"
OUT="$(SDD_ALIGNMENT_REPO="$TMP" python3 "$ENGINE" --check --json 2>/dev/null)"
case "$OUT" in
  *ALIGN_CI_JOBS_MISMATCH*) ok ;;
  *) bad "job rename did not yield ALIGN_CI_JOBS_MISMATCH: $OUT" ;;
esac
rm -r "$TMP"

# 3. Pi adapter doc-only floor
if [ ! -d "$ROOT/.pi/extensions" ]; then ok; else bad ".pi/extensions must not exist (REQ-6.9)"; fi
PI_DOC="$ROOT/.ai/agents/pi/AGENT.md"
for needle in "no pre-tool hook" "floor-only" "No built-in subagents"; do
  if python3 -c 'import sys; sys.exit(0 if sys.argv[1].lower() in open(sys.argv[2],encoding="utf-8").read().lower() else 1)' \
      "$needle" "$PI_DOC"; then ok; else bad "Pi doc missing claim '$needle'"; fi
done

# 4. MCP pin comment still demands confirmation before first run
if grep -q 'Confirm package name/version before first run' "$ROOT/opencode.json"; then
  ok
else
  bad "opencode.json lost its MCP pin confirmation comment"
fi

echo "passed=$pass failed=$fail"
[ "$fail" -eq 0 ]
