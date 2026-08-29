#!/usr/bin/env bash
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"

if [[ $# -ne 2 ]]; then
  printf 'Usage: %s <feature> <task-id>\n' "$0" >&2
  exit 2
fi

exec python3 "$REPO/scripts/spec_contract.py" slice --feature "$1" --task "$2"
