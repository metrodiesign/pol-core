#!/usr/bin/env bash
# สร้าง GitHub labels สำหรับ spec-sync-github bridge (idempotent — รันซ้ำปลอดภัย).
# ใช้: scripts/bootstrap-labels.sh [feature ...]
#   ไม่ใส่ feature = สแกนทุกโฟลเดอร์ใต้ .ai/specs/ แล้วสร้าง label spec:<feature> ให้ครบ
# ต้อง gh auth login มาก่อน. สีอ้างพาเลตแบรนด์ (rules/tech.md).
set -euo pipefail

ensure_label() {
  local name="$1" color="$2" desc="$3"
  if gh label create "$name" --color "$color" --description "$desc" >/dev/null 2>&1; then
    echo "created: $name"
  elif gh label edit "$name" --color "$color" --description "$desc" >/dev/null 2>&1; then
    echo "exists (synced): $name"
  else
    echo "skip: $name (gh error — auth? repo?)" >&2
  fi
}

# kind labels (คงที่)
ensure_label "spec-epic" "0E1C50" "Spec epic issue (one per feature; carries the REQ spine)"
ensure_label "spec-task" "1A2238" "Spec task issue (sub-issue of a spec epic)"
ensure_label "req-spine" "FDB913" "Epic carries the requirement (REQ) coverage table"

# per-feature labels
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SPECS_DIR="$SCRIPT_DIR/../.ai/specs"

features=()
if [ "$#" -gt 0 ]; then
  features=("$@")
else
  for d in "$SPECS_DIR"/*/; do
    [ -d "$d" ] && features+=("$(basename "$d")")
  done
fi

for f in "${features[@]:-}"; do
  [ -n "$f" ] && ensure_label "spec:$f" "13266B" "All issues for the '$f' spec"
done
