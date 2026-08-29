#!/usr/bin/env bash
# ci-evidence-scope.sh — resolve the CI diff range and feed raw base/HEAD snapshot
# pairs to the shared Evidence engine (design §Entry points). Owns NO Evidence or
# task semantics; enumerates changed tasks.md paths only.
#
# Interface:
#   $1 = BASE commit-ish (default $SDD_BASE_SHA, then merge-base with origin/develop)
#   $2 = HEAD commit-ish (default $SDD_HEAD_SHA, then HEAD)
#   --repo DIR  : operate on another checkout (tests)  default: this repo root
# Exit: 0 allow · 1 policy-fail · 2 engine-fail — one envelope JSON line per file.
set -uo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
BASE_ARG=""
HEAD_ARG=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo) REPO="$(cd "$2" && pwd)"; shift 2 ;;
    *) if [[ -z "$BASE_ARG" ]]; then BASE_ARG="$1"; else HEAD_ARG="$1"; fi; shift ;;
  esac
done

ENGINE="$REPO/scripts/spec_contract.py"
BIN_DIR="$REPO/.ai/bin"

fail_engine() { printf 'ENGINE_INTERNAL: %s\n' "$1" >&2; exit 2; }

git_or_fail() { git -C "$REPO" "$@" || fail_engine "git $* failed"; }

BASE="${BASE_ARG:-${SDD_BASE_SHA:-}}"
HEAD_REV="${HEAD_ARG:-${SDD_HEAD_SHA:-HEAD}}"
if [[ -z "$BASE" ]]; then
  BASE="$(git_or_fail merge-base "$HEAD_REV" origin/develop 2>/dev/null)" || fail_engine 'RANGE_BASE_UNRESOLVED: no origin/develop merge-base'
fi
if [[ "$BASE" = "0000000000000000000000000000000000000000" ]]; then
  BASE="$(git_or_fail hash-object -t tree --stdin < /dev/null)" || fail_engine 'RANGE_BASE_UNRESOLVED: empty tree'
fi
git_or_fail cat-file -e "${BASE}^{tree}" || fail_engine "RANGE_BASE_UNRESOLVED: ${BASE}"
git_or_fail cat-file -e "${HEAD_REV}^{commit}" || fail_engine "RANGE_HEAD_UNRESOLVED: ${HEAD_REV}"

WORST=0
SNAP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/sdd-ci-evidence.XXXXXX")" || fail_engine mktemp
trap 'rm -rf "$SNAP_DIR"' EXIT

changed="$(git_or_fail diff --name-only "${BASE}".."${HEAD_REV}" | grep -E '(^|/)tasks\.md$' || true)"
i=0
for f in $changed; do
  i=$((i + 1))
  AFTER="$SNAP_DIR/after-$i"
  BEFORE_FLAG="-"
  if git -C "$REPO" cat-file -e "${BASE}:${f}" 2>/dev/null; then
    BEFORE_FLAG="$SNAP_DIR/before-$i"
    git_or_fail show "${BASE}:${f}" >"$BEFORE_FLAG"
  fi
  git_or_fail show "${HEAD_REV}:${f}" >"$AFTER"
  RANGES_JSON="$SNAP_DIR/ranges-$i.json"
  : >"$SNAP_DIR/empty-base"
  if [ "$BEFORE_FLAG" = "-" ]; then
    python3 "$ENGINE" diff-ranges --before-file "$SNAP_DIR/empty-base" --after-file "$AFTER" >"$RANGES_JSON" || fail_engine "diff-ranges $f"
  else
    python3 "$ENGINE" diff-ranges --before-file "$BEFORE_FLAG" --after-file "$AFTER" >"$RANGES_JSON" || fail_engine "diff-ranges $f"
  fi
  ENVELOPE="$(bash "$BIN_DIR/check-evidence.sh" "$f" "$BEFORE_FLAG" "$AFTER" "$RANGES_JSON" ci)"
  RC=$?
  printf '%s\n' "$ENVELOPE"
  [[ "$RC" -eq 2 ]] && WORST=2
  [[ "$RC" -eq 1 && "$WORST" -eq 0 ]] && WORST=1
done

exit "$WORST"
