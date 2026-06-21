# Workflow: Bug Fix (root-cause-first)

Vendor-neutral procedure for any agent (Claude / Codex / OpenCode / Pi). หาราก (root
cause) ก่อนเสมอ — ห้าม patch อาการ. ทุกการแก้ปิดด้วย regression test ที่ RED ก่อนแก้ /
GREEN หลังแก้.

## Purpose

แก้บั๊กในเส้นทางวิกฤต / regression ซ้ำ / บั๊กที่ root cause ยังไม่ชัด โดยแยก "การวิเคราะห์ราก"
ออกจาก "การแก้" — วิเคราะห์ในบริบทสะอาด ระบุ root cause จริง (ไม่ใช่ symptom) และ list
พฤติกรรมที่ต้องไม่พัง (regression risk) ก่อนแตะโค้ด.

## When to use

- บั๊กใน critical path, regression ที่กลับมาซ้ำ, หรือ root cause ไม่ชัด.
- ไม่ใช้กับ: ฟีเจอร์ใหม่ (ใช้ `feature-development.md`), typo/แก้ประโยคเดียวที่เห็น cause ชัดทันที.

## Required context files

อ่านก่อนเริ่ม (relative จากไฟล์นี้):

- [../roles/bug-investigator.md](../roles/bug-investigator.md) — persona ที่ต้อง adopt ใน Phase 1:
  root-cause analysis เท่านั้น ห้ามแก้ไฟล์.
- [../shared/TASK_PROTOCOL.md](../shared/TASK_PROTOCOL.md) — สัญญาการ implement task + Evidence.
- [../shared/EARS.md](../shared/EARS.md) — F-ID (expected fix) และ B-ID (unchanged behavior) เขียนแบบ EARS.
- [../shared/TESTING_PROTOCOL.md](../shared/TESTING_PROTOCOL.md) — กฎ test: รันด้วย project test runner
  (กำหนดผ่าน `SDD_TEST_CMD` env หรือ test script ใน package.json สำหรับโปรเจกต์ Node), test co-located
  กับ logic ที่คุมใน project test directory, assert observable failure mode.
- [../shared/ARCHITECTURE.md](../shared/ARCHITECTURE.md) — file organization ที่ต้องเคารพ.

## Step-by-step process

1. **Phase 0 — Intake.** ถ้าโจทย์ยังไม่ตอบ ให้ถามใน ONE batched message (ภาษาไทย):
   (1) repro steps — page/viewport/command/input ที่แสดงบั๊ก; (2) current defective behavior
   วัดได้; (3) expected behavior; (4) constraints — ไฟล์/พฤติกรรมที่ห้ามแตะ (do-not-modify list
   = HARD scope: ไม่มี task ใดแก้ไฟล์เหล่านั้นได้).
   -> verify: ตอบครบ 4 ข้อ; do-not-modify list ชัดเจน.

2. **Phase 1 — Root-cause analysis (adopt `bug-investigator` persona).** reproduce บั๊กจริงด้วย
   Bash เมื่อรันโค้ดได้ (build/test/run); fall back เป็น code-only analysis เฉพาะเมื่อรันไม่ได้ +
   ระบุชัดว่าไม่ได้ reproduce live. trace cause จริง (ไม่ใช่ symptom) อ้าง file:line. ระบุ
   พฤติกรรมที่ต้องไม่เปลี่ยน เขียนเป็น `WHEN <condition> THEN THE SYSTEM SHALL CONTINUE TO
   <behavior>`. STOP — present findings, รอผู้ใช้ยืนยัน root cause. ห้ามแก้ไฟล์ใน phase นี้.
   -> verify: root cause อ้าง file:line จริง + reproduce ได้ (หรือระบุว่าทำไมรันไม่ได้); ผู้ใช้ยืนยัน.

3. **Phase 2 — bugfix.md.** หลังผู้ใช้ confirm สร้าง `.ai/specs/bugfix-<short>/bugfix.md`:
   - `## Current Behavior (Defect)` — `WHEN <repro> THEN <defect>` พร้อม repro ที่รันได้จริง
     (page/viewport/command/measured value ไม่ใช่ prose).
   - `## Expected Behavior` — F1, F2... (EARS, stable F-IDs, หนึ่ง criterion ต่อ fix).
   - `## Unchanged Behavior` — B1, B2... (`WHEN ... THE SYSTEM SHALL CONTINUE TO ...`; ครอบทุก
     regression risk จาก Phase 1 รวมทุกอย่างใน do-not-modify list).
   STOP for review. เมื่อ approve flip header เป็น `> Status: approved <YYYY-MM-DD>`.
   -> verify: ทุก regression risk มี B-ID; ไม่มี placeholder `?`.

4. **Phase 3 — tasks.md + implement.** tasks.md format checkbox เดียวกับ feature spec; stamp
   header approved ตั้งแต่สร้าง (gate ของ Phase 2 ครอบแล้ว). ทุก F-ID และ B-ID ต้องถูกอ้างบน
   `Satisfies:` ของบาง task. task ที่แก้ไฟล์ do-not-modify = spec conflict -> STOP ถาม ห้ามขยาย
   scope เงียบ. implement ตาม [../shared/TASK_PROTOCOL.md](../shared/TASK_PROTOCOL.md). validation
   สามมิติ ALL of:
   - (a) repro test ที่ **RED ก่อนแก้ / GREEN หลังแก้** (ครอบ F-IDs: defect -> expected);
   - (b) ทุก B-ID มี assertion 1:1;
   - (c) ทุก assertion เช็ก **observable failure mode** (rendered output / computed value /
     layout measurement) ไม่ใช่ internal implementation detail (anti-pattern: assert CSS selector
     แทน observable failure mode จริง แล้วพลาด failure mode จริง ที่ผู้ใช้เจอ).
   test ที่รัน headless ได้อยู่ใน project test directory, co-located กับ logic ที่คุม; observable mode ที่
   เห็นได้เฉพาะใน UI — ถ้าโปรเจกต์ ship UI ให้ verify ใน project target runtime (ดู project UI-verify
   reference). พิสูจน์ task green ผ่าน `.ai/bin/gate-task.sh` ที่อ่าน `SDD_TYPECHECK_CMD` / `SDD_TEST_CMD`
   (auto-detect package.json scripts สำหรับ Node).
   -> verify: repro test RED->GREEN พิสูจน์ได้ (รันบน commit ก่อนแก้ = fail, หลังแก้ = pass);
   typecheck + test เขียว; `scripts/spec-trace.sh bugfix-<short>` ครอบทุก F/B-ID.

## Expected output

- `.ai/specs/bugfix-<short>/bugfix.md` (Defect / Expected F-IDs / Unchanged B-IDs) + tasks.md.
- โค้ดที่แก้ (เฉพาะ root cause ไม่แตะ do-not-modify list).
- regression test suite: repro test (RED->GREEN) + assertion ต่อทุก B-ID, ใน project test directory
  co-located กับ logic ที่คุม.
- tasks.md ทุก task `- [x]` + Evidence; typecheck + test เขียว.

## Definition of done

- [ ] root cause จริงถูกระบุ + อ้าง file:line + ผู้ใช้ยืนยัน (ไม่ใช่ patch อาการ).
- [ ] repro test RED ก่อนแก้, GREEN หลังแก้ (พิสูจน์ transition defect -> expected).
- [ ] ทุก B-ID มี assertion 1:1 ที่เช็ก observable mode; ไม่แตะไฟล์ do-not-modify.
- [ ] typecheck + test เขียว (ผ่าน `.ai/bin/gate-task.sh` อ่าน `SDD_TYPECHECK_CMD` / `SDD_TEST_CMD`);
      `scripts/spec-trace.sh bugfix-<short>` ครอบ F/B-ID ครบ.
- [ ] ไม่มี placeholder `?` ใน artifact ใด.

## Common mistakes to avoid

- กระโดดแก้ก่อนยืนยัน root cause — แก้ symptom ทำบั๊กกลับมาในรูปอื่น.
- regression test ที่ assert implementation detail (CSS selector, ชื่อ internal fn) แทน observable
  failure mode — ผ่านแต่ไม่ได้กันบั๊กจริง.
- ไม่ยืนยันว่า test RED ก่อนแก้ — test ที่ GREEN อยู่แล้วก่อนแก้ ไม่ได้พิสูจน์อะไร.
- แตะไฟล์ใน do-not-modify list เพื่อความสะดวก — เป็น spec conflict ต้อง STOP ถาม.
- เขียน test นอก project test directory — runner ไม่ include = test ไม่รัน ผ่าน vacuously.
- "reproduce" ด้วยการอ่านโค้ดอย่างเดียวทั้งที่รันได้ — ต้อง reproduce live หรือระบุชัดว่าทำไมรันไม่ได้.
