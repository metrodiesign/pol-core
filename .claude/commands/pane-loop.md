---
description: รัน pane-loop orchestrator (/spec-implement → /spec-retro → /clear) ใน iTerm. Default = all-in-one (ทุก task ใน session เดียว) สำหรับ feature ที่ task พึ่งกัน; แยก pane ต่อ task เฉพาะงานอิสระหรือต้อง isolate accuracy
argument-hint: <feature-name> [all-in-one | task-ids...]
allowed-tools: Bash, Read
---

รัน orchestrator `scripts/pane-loop.sh` ด้วย args ที่ผู้ใช้ให้: `$ARGUMENTS`

ขั้นตอน:

1. ก่อนรัน — ตรวจสองอย่าง:
   - ไม่มี pane-loop ค้างอยู่ (background task ที่ยังรัน). ถ้ามี ให้แจ้งและหยุด อย่ารันซ้ำ (pane จะชนกัน, tasks share tree เดียว).
   - `> Status:` ใน tasks.md ของ feature ต้องเป็น approved — ถ้ายัง draft ให้ถาม user ใน session นี้ (จุดเดียวที่มีคนตอบ) ว่า approve ไหม; approve = flip header เป็น `> Status: approved <YYYY-MM-DD>` ก่อนเปิด pane. ห้ามเปิด pane ทั้งที่ยัง draft — pane ที่เปิดแล้วไม่มีคนตอบคำถามยืนยัน จะค้างจน timeout.
2. รัน `bash scripts/pane-loop.sh $ARGUMENTS` ผ่าน Bash tool แบบ `run_in_background: true`.
   - ห้ามใช้ `!`-prefix execution — script เป็น long-running (รอ task ขึ้น `[x]`, timeout ต่อ task default 2400s) ต้องเป็น background.
3. `sleep ~18s` แล้ว Read ไฟล์ output ของ background task — ยืนยันว่า:
   - parse task ถูก (บรรทัด `Feature: <name> | task: ...`)
   - เปิด pane ได้ (บรรทัด `::: task N — เปิด pane interactive` + `พิมพ์ /spec-implement N`)
     ถ้าเห็น error (`เปิด pane ไม่สำเร็จ`, `ไม่พบ tasks.md`, `ไม่มี task ที่จะรัน`) → รายงานและหยุด.
4. รายงานลำดับ task ที่จะรัน + วิธีดูความคืบหน้า (ดู iTerm pane สด, หรือ tail ไฟล์ output). แจ้งว่าจะ notify เมื่อจบทุก task.

หมายเหตุข้อจำกัด:

- **เลือกโหมดตาม coupling**: feature ที่ task พึ่งกัน (shared primitives/data/lib) → `all-in-one` (ทุก task ใน session เดียว) เป็น default — ถูกกว่า ~30-40% เพราะไม่ต้อง re-acquire context ต่อ session. แยก pane ต่อ task เฉพาะงานอิสระจริง หรือต้อง isolate CORE domain เพื่อ accuracy.
- ต้องมี **iTerm2** เปิดอยู่ (script ใช้ AppleScript `current session of current window`).
- task ids = **space-separated** (`12 13 14`) ไม่รองรับ range (`12-15`). ไม่ใส่ ids = ทำทุก task ค้าง.
- hands-free มาจาก `settings.json` (`defaultMode: bypassPermissions`); `CLAUDE_FLAGS` default ว่าง — เพิ่ม flag เองได้ผ่าน env `CLAUDE_FLAGS=...`.
- task ที่ `[x]` แล้วถูกข้ามอัตโนมัติ; ถ้า task ไม่ขึ้น `[x]` ใน timeout → script หยุดและ **เปิด pane ค้างไว้ให้ตรวจ**.
