# Workflow: Code Review

Vendor-neutral procedure for any agent (Claude / Codex / OpenCode / Pi). ตรวจ diff ด้วย
fresh-context adversarial mindset แล้วออก report ที่ tag severity ทุก finding.

## Purpose

หา bug ที่ถูกต้องจริง + จุดที่ลดความซับซ้อน/ reuse / efficiency ได้ ก่อน merge — โดยเทียบ diff
กับ requirements/design ของ spec ที่เกี่ยวข้อง และมาตรฐานโปรเจกต์. output เป็น report ที่จัด
priority ด้วย severity ให้คนตัดสินใจได้เร็ว ไม่ใช่ความเห็นกระจัดกระจาย.

## When to use

- ก่อน merge PR, หลัง implement ฟีเจอร์/แก้บั๊กใหญ่, หรือเมื่อผู้ใช้ขอ review diff.
- ไม่ใช้กับ: การเขียน test (ใช้ `test-generation.md`), การ implement (ใช้ `feature-development.md`).

## Required context files

อ่านก่อนเริ่ม (relative จากไฟล์นี้):

- [../shared/REVIEW_PROTOCOL.md](../shared/REVIEW_PROTOCOL.md) — เกณฑ์ review + severity scale +
  รูปแบบ report ที่ต้อง apply.
- [../shared/PROJECT_CONTEXT.md](../shared/PROJECT_CONTEXT.md) — what/why/non-goals (จับ scope creep).
- [../shared/ARCHITECTURE.md](../shared/ARCHITECTURE.md) — folder layout, naming, patterns, anti-patterns.
- [../shared/CODING_STANDARDS.md](../shared/CODING_STANDARDS.md) — stack + hard constraints
  (type-safety, no empty placeholder, no broken layout).
- [../shared/LESSONS.md](../shared/LESSONS.md) — failure mode ที่เคยเกิด (เช่น verdict ของ verifier ที่ตาย).
- [../roles/spec-architect.md](../roles/spec-architect.md) — persona adversarial reviewer (adopt เมื่อ diff
  แตะ CORE domain logic / design decision).

## Step-by-step process

1. **กำหนดขอบเขต diff.** ระบุชุดการเปลี่ยนที่ review: `git diff <base>...<head>` หรือ working tree.
   หา spec ที่เกี่ยวข้องใน `.ai/specs/<feature>/` (requirements.md + design.md) เพื่อรู้
   intended behavior. รัน `scripts/spec-state.sh <feature>` เพื่อเห็นสถานะจริง (รวม untracked `??`
   ที่ `git diff --stat` ไม่เห็น).
   -> verify: รู้ทุกไฟล์ที่เปลี่ยนจริง (รวม untracked) + REQ/F-ID ที่ diff ควรครอบ.

2. **ตรวจ correctness ก่อน.** ไล่หา bug ที่ถูกต้องจริง: off-by-one, race ใน async render, null/edge,
   logic ที่ขัด REQ, error path ที่หาย, regression ของ behavior ที่ควรคงเดิม. แต่ละ finding ต้อง
   reproduce ได้ทางความคิดหรือชี้ input ที่ทำให้พัง อ้าง file:line. เมื่อ diff แตะ CORE domain logic
   (pure logic ใน the project test directory, co-located กับ logic under test) adopt [../roles/spec-architect.md](../roles/spec-architect.md)
   (mode=critique) — หา unstated assumption, missing error path, REQ coverage gap.
   -> verify: ทุก correctness finding อ้าง file:line + อธิบายว่าพังเมื่อ input/condition ใด.

3. **ตรวจ quality (reuse / simplification / efficiency / altitude).** code ซ้ำที่ reuse ได้,
   abstraction เกินจำเป็นสำหรับโค้ดใช้ครั้งเดียว, นามธรรมผิด layer, dead code ที่ diff สร้างขึ้น.
   match existing style; ห้ามเสนอ "improve" โค้ดข้างเคียงที่ไม่เกี่ยว diff.
   -> verify: quality finding แยกออกจาก correctness; ไม่ปนกับ scope creep.

4. **ตรวจ standards + constraints.** เทียบกับ `ARCHITECTURE.md` (naming, import order,
   layer boundaries, logic แยกออกจาก UI) และ `CODING_STANDARDS.md` (type-safety,
   ไม่มีกล่องว่างเป็น placeholder, accessibility ของ UI ถ้ามี, ไม่มี broken layout).
   ถ้าเป็นโค้ด spec-driven เช็ก REQ coverage ด้วย `scripts/spec-trace.sh <feature>`.
   -> verify: รัน `scripts/spec-trace.sh <feature>` (ถ้ามี spec); ระบุ anti-pattern ที่ละเมิด.

5. **ยืนยันว่าเขียวจริงก่อนสรุป.** อย่าเชื่อ "ผ่าน" ที่ไม่ได้เห็นผล: รัน `.ai/bin/gate-task.sh`
   ซึ่งรัน the project typecheck command (via `SDD_TYPECHECK_CMD`, หรือ package.json typecheck script
   สำหรับ Node project) + the project test runner (via `SDD_TEST_CMD`, หรือ package.json test script);
   บันทึกผลจริงในreport. ถ้าผลของ verifier/subagent ใดมี failure ให้ re-verify
   finding นั้นด้วยมือ — verdict ที่หายไป = "ยังไม่ตัดสิน" ไม่ใช่ "ผ่าน" (บทเรียน LESSONS.md).
   -> verify: typecheck + test output จริงแนบใน report; ไม่มี finding ที่อ้าง "rejected" ทั้งที่ไม่เคยถูกตรวจ.

6. **ออก report ตาม `REVIEW_PROTOCOL.md`.** จัดทุก finding ด้วย severity (เช่น Blocker / High /
   Medium / Low / Nit ตาม protocol), location (file:line), why-it-is-a-problem, และ concrete fix
   หรือคำถาม. ปิดด้วย verdict รวม (approve / request changes) + REQ ที่ยังไม่ครอบ ถ้ามี.

## Expected output

- review report ที่จัด finding ตาม severity (สูง -> ต่ำ), แต่ละข้อมี: location, severity, problem,
  fix/question. แยก correctness จาก quality. ปิดด้วย overall verdict + uncovered REQ (ถ้ามี).
- typecheck + test output จริงแนบเป็นหลักฐาน.
- ไม่เขียนเป็นไฟล์ report เว้นแต่ผู้ใช้ขอ — ส่งกลับเป็น message ตรง.

## Definition of done

- [ ] ทุกไฟล์ที่เปลี่ยนจริง (รวม untracked) ถูก review — ไม่พึ่ง `git diff --stat` อย่างเดียว.
- [ ] ทุก finding มี severity + location (file:line) + problem + concrete fix/question.
- [ ] correctness finding แต่ละข้อชี้ input/condition ที่ทำให้พัง (reproducible ทางความคิด).
- [ ] `.ai/bin/gate-task.sh` (the project typecheck + test commands) ถูกรันจริง + ผลแนบใน report.
- [ ] REQ coverage เช็กด้วย `scripts/spec-trace.sh <feature>` (ถ้า diff เป็นงาน spec-driven).
- [ ] report ปิดด้วย overall verdict (approve / request changes).

## Common mistakes to avoid

- เชื่อ `git diff --stat` อย่างเดียว — untracked `??` ไม่โผล่ -> review ตกหล่นโค้ดจริง
  (`scripts/spec-state.sh` / `git status` เห็น `??`).
- สรุป "ไม่มีปัญหา" โดยไม่รัน the project typecheck/test (ผ่าน `.ai/bin/gate-task.sh`) จริง — assertion ก่อน evidence.
- ตี verdict ที่หายไป/verifier ที่ตาย เป็น "rejected/ผ่าน" — ต้องถือเป็น "ยังไม่ตัดสิน" + re-verify มือ.
- ปน quality nit กับ correctness blocker ใน severity เดียวกัน — คนอ่านจัด priority ไม่ได้.
- เสนอ refactor โค้ดข้างเคียงที่ diff ไม่ได้แตะ (scope creep) — ตรงตาม REVIEW_PROTOCOL surgical rule.
- finding ลอยๆ ไม่มี file:line / ไม่มี fix — รับไปทำต่อไม่ได้.
