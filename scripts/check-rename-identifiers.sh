#!/usr/bin/env bash
# Rename identifier gate (hierarchical-naming task 12, REQ-15.3/15.4/15.5).
# Thin wrapper — logic + the shipped exception list live in check_rename_identifiers.py.
set -euo pipefail
cd "$(dirname "$0")/.."
exec python3 scripts/check_rename_identifiers.py "$@"
