#!/usr/bin/env bash
# precompact-persist.sh — PreCompact hook (Tier 5 item 2)
# Inject a reminder so the model persists active-task state into the spec BEFORE
# history is compacted. Best-effort: the model does the writing; this only reminds.
# Always exit 0 with valid JSON on stdout — never block compaction.

command -v jq >/dev/null 2>&1 || exit 0
INPUT=$(cat 2>/dev/null)
TRIGGER=$(printf '%s' "$INPUT" | jq -r '.trigger // "auto"' 2>/dev/null)
[ -n "$TRIGGER" ] || TRIGGER="auto"

MSG="กำลังจะ compact (trigger: ${TRIGGER}). ก่อน history ถูกย่อ ให้เขียน active-task state ปัจจุบันลง .ai/specs/<feature>/tasks.md (และ design.md ถ้าเกี่ยวกับสถาปัตยกรรม) ตาม CLAUDE.md Context discipline ให้ครบ: (1) active spec + task ID ที่กำลังทำ (2) ไฟล์ที่แก้ไปแล้ว (3) คำสั่ง test/build/run ที่ใช้จริง (4) ทุก architectural decision พร้อมเหตุผล (5) อะไรเสร็จแล้ว + next step. ห้าม compact กลาง task ที่ยังไม่เสร็จถ้า state อยู่แค่ในบทสนทนานี้ — เซฟลงไฟล์ก่อน."

jq -n --arg m "$MSG" '{hookSpecificOutput:{hookEventName:"PreCompact",additionalContext:$m}}'
exit 0
