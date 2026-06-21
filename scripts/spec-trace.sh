#!/usr/bin/env bash
# ตัวตรวจ requirements traceability — wrapper บางๆ เรียก spec_trace.py
# ใช้: scripts/spec-trace.sh <feature>   (feature = โฟลเดอร์ใต้ .ai/specs/)
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec python3 "$SCRIPT_DIR/spec_trace.py" "$@"
