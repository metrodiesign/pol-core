#!/usr/bin/env bash
# check-evidence.sh — thin CLI wrapper around `spec_contract.py gate evidence`.
# Owns NOTHING: no Evidence parser, no task selection, no ranges math (design §Entry points).
# Interface:
#   $1 = repo-relative tasks.md path
#   $2 = before snapshot file ("-" == --before-missing: whole file did not exist)
#   $3 = after snapshot file
#   $4 = changed-ranges JSON file ({\"ranges\":[[b0,b1,a0,a1],...]}) produced by
#        `spec_contract.py diff-ranges`
#   $5 = GateSelection source enum (claude-edit|codex-edit|opencode|pre-commit|ci)
# Exit: 0 allow · 1 policy-fail · 2 engine-fail — envelope JSON on stdout.
set -euo pipefail

if [[ $# -ne 5 ]]; then
  printf 'Usage: %s <path> <before-file|-> <after-file> <ranges-json> <source>\n' "$0" >&2
  exit 2
fi

REPO="$(cd "$(dirname "$0")/../.." && pwd)"
PATH_ARG="$1"; BEFORE="$2"; AFTER="$3"; RANGES="$4"; SOURCE="$5"

ENGINE_ARGS=(gate evidence --path "$PATH_ARG" --after-file "$AFTER" --ranges-file "$RANGES" --source "$SOURCE")
if [[ "$BEFORE" == "-" ]]; then
  ENGINE_ARGS+=(--before-missing)
else
  ENGINE_ARGS+=(--before-file "$BEFORE")
fi

exec python3 "$REPO/scripts/spec_contract.py" "${ENGINE_ARGS[@]}"
