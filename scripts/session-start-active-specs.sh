#!/usr/bin/env bash
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
exec python3 "$REPO/scripts/spec_contract.py" state --all --format summary
