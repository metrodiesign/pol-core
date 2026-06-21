# Workflow: Feature Development (spec flow)

Vendor-neutral procedure for any agent (Claude / Codex / OpenCode / Pi). Specs come
before code, ALWAYS. ทุกฟีเจอร์ที่ไม่ trivial ไหลผ่าน requirements -> design -> tasks ->
implement โดยมี APPROVAL GATE หลังแต่ละ artifact.

## Purpose

ผลิตฟีเจอร์ใหม่แบบ spec-driven: เขียน WHAT (requirements ใน EARS) ก่อน HOW (design)
ก่อนลงมือ (tasks + implement) เพื่อให้ความถูกต้องถูกตัดสินที่ระดับ requirement ซึ่งแก้ถูกกว่า
แก้ในโค้ดทีหลัง. spec files คือ source of truth ถาวร; การสนทนาเป็น working memory ชั่วคราว.

## When to use

- ฟีเจอร์ใหม่ที่ไม่ trivial (มีพฤติกรรมสังเกตได้หลายอย่าง, logic, หรือหลาย section/ไฟล์).
- ไม่ใช้กับ: การแก้บั๊ก (ใช้ `bug-fix.md`), การแก้ประโยคเดียวที่ชัดเจน (แก้ตรงได้ ไม่ต้องมี spec),
  ฟีเจอร์เล็กที่เข้าใจดีและไม่อยากหยุดตรวจทุก phase (เดินทุก phase รวดเดียวแบบ no-gate ได้ แต่ยัง
  เขียน artifact ครบ).

## Required context files

อ่านก่อนเริ่ม (relative จากไฟล์นี้):

- [../shared/TASK_PROTOCOL.md](../shared/TASK_PROTOCOL.md) — สัญญาการทำงานต่อ task: implement
  ทั้ง task end-to-end (รวม test), mark `- [x]` + แนบ Evidence, pause ที่ TASK boundary.
- [../shared/EARS.md](../shared/EARS.md) — EARS notation (บังคับสำหรับ requirements).
- [../shared/PROJECT_CONTEXT.md](../shared/PROJECT_CONTEXT.md) — product: what / why / non-goals.
- [../shared/ARCHITECTURE.md](../shared/ARCHITECTURE.md) — folder layout, naming, patterns ที่ต้องตรงเป๊ะ.
- [../shared/CODING_STANDARDS.md](../shared/CODING_STANDARDS.md) — stack ที่ต้อง prefer + hard constraints.
- [../shared/LESSONS.md](../shared/LESSONS.md) — บทเรียน process ที่กันความผิดซ้ำ.
- [../roles/spec-architect.md](../roles/spec-architect.md) — persona สำหรับ critique design / audit requirements
  (adopt เมื่อ design แตะ CORE domain logic).

## Step-by-step process

ลำดับมาตรฐาน requirements-first (Design-First สลับ step 1 กับ 2 — gate เหมือนกัน). หลังแต่ละ
artifact: STOP, สรุปให้ผู้ใช้ review, รอ approval ชัด ("approved"/"continue") ก่อนไป phase ถัดไป.
เมื่อ approve: flip header ในไฟล์เป็น `> Status: approved <YYYY-MM-DD>` — approval อยู่ในไฟล์
ไม่ใช่แค่ในการสนทนา.

1. **Intake / workflow choice.** ถ้าโจทย์ยังไม่ตอบ who/what/why/success criteria/edge cases/
   constraints ให้ถามคำถาม clarifying ทั้งหมดใน ONE batched message (ภาษาไทย) แล้วเลือก workflow
   (requirements-first default; Design-First เมื่อสถาปัตยกรรมต้องตัดสินก่อน). สร้างโฟลเดอร์
   `.ai/specs/<kebab-case-name>/`.
   -> verify: โฟลเดอร์ spec มีอยู่; คำถามถูกตอบครบ.

2. **requirements.md (EARS).** เขียนตาม [../shared/EARS.md](../shared/EARS.md): ทุก functional
   requirement มี stable ID (REQ-N.M), atomic, testable, ครอบ happy path + error/edge (IF...THEN).
   หนึ่ง observable behavior ต่อ criterion — แยก compound ที่ต่อด้วย "and"; ปฏิเสธคำ subjective
   ("fast", "ดูดี") เว้นแต่ระบุ threshold วัดได้.
   -> verify: ทุก REQ มี ID + เป็น EARS pattern ที่ถูกต้อง; STOP for review.

3. **Analyze (เลือก, คุ้มเสมอสำหรับฟีเจอร์ที่มี logic).** audit requirements ใน 5 หมวด: logical
   inconsistencies, ambiguities, conflicting constraints, gaps, unstated assumptions. reason
   ข้าม requirement SET (เป็นคู่/กลุ่ม) ไม่ใช่ทีละข้อ. batch ทุกคำถามใน ONE Thai message; แก้
   requirements หลังผู้ใช้ตัดสิน แล้ว log ทุก finding + decision ใต้ "Edge Cases & Open Questions".
   -> verify: ไม่มี gap/conflict ค้างที่ทำให้เขียน test ไม่ได้.

4. **design.md.** เขียน architecture: components + responsibilities, sequence diagrams (Mermaid),
   data models & interfaces, technology decisions (prefer `CODING_STANDARDS.md`), error handling
   strategy, testing strategy (map -> REQ IDs), และ `## Requirement Traceability` table (design
   element -> REQ-x.y). เมื่อ design แตะ CORE domain logic (core algorithm / validation ใน
   the project test directory, co-located กับ logic under test) ให้ adopt
   [../roles/spec-architect.md](../roles/spec-architect.md) (mode=critique) เป็น fresh-context
   reviewer — apply หรือ rebut ทุก finding ก่อน STOP.
   -> verify: ทุก REQ ปรากฏใน traceability table; STOP for review.

5. **tasks.md.** แตกเป็น COHESIVE, independently verifiable slices (vertical slice: model -> API ->
   validation -> tests) — ~5-10 tasks ต่อฟีเจอร์ ไม่ใช่ 20-30. ห้าม pre-split เป็น 1.1/1.2 (model
   ที่ implement จัดการ micro-sequencing เองด้วย internal TODO). map แต่ละ task -> REQ IDs บนบรรทัด
   `Satisfies:`. ก่อน STOP รัน `scripts/spec-trace.sh <feature>` — ทุก REQ ต้องปรากฏบน Satisfies:
   ของอย่างน้อยหนึ่ง task; REQ ที่ไม่ถูกครอบ = blocker (รายงานดังๆ ห้าม skip เงียบ).
   -> verify: `scripts/spec-trace.sh <feature>` ผ่าน (REQ coverage ครบ); STOP for review.

6. **implement (per `TASK_PROTOCOL.md`).** สำหรับแต่ละ task: รัน `scripts/spec-state.sh <feature>`
   ก่อน loop เพื่อ reconcile checkbox กับ filesystem (filesystem คือ ground truth — checkbox/git log
   โกหกได้; untracked file ไม่โผล่ใน `git diff --stat`). อ่าน task + linked REQ + ส่วนที่เกี่ยวของ
   design + `ARCHITECTURE.md`. implement ทั้ง task ในรอบเดียว (อาจแตะหลายไฟล์), เขียน/ขยาย test ที่
   พิสูจน์ REQ IDs. รัน the project typecheck command (via `SDD_TYPECHECK_CMD` env, หรือ package.json
   typecheck script) + the project test runner (declared via `SDD_TEST_CMD` env, หรือ package.json
   test script สำหรับ Node project). ก่อน mark task สุดท้าย (หรือ assembly task)
   รัน `scripts/spec-trace.sh <feature>` อีกครั้ง. mark `- [x]` + แนบ `Evidence:` block ใน edit
   เดียวกัน (test command + result, ผล UI-verify ถ้าโปรเจ็กต์ ship UI, deviations). pause ที่ TASK
   boundary.
   -> verify: typecheck เขียว, test เขียว (ผ่าน `.ai/bin/gate-task.sh`), Evidence block ครบทุก task ที่ done.

> Coupled feature (tasks แชร์ primitives/data/lib) DEFAULT = implement ทุก task ใน session เดียว
> (`scripts/pane-loop.sh <feature> all-in-one`). แยก session เฉพาะ task ที่อิสระจริง หรือเพื่อ
> isolate accuracy ของ CORE domain — เป็น accuracy trade ไม่ใช่ cost win (~30-40% แพงกว่า).

## Expected output

- โฟลเดอร์ `.ai/specs/<feature>/` มี requirements.md, design.md, tasks.md ที่ header เป็น
  `> Status: approved <YYYY-MM-DD>`.
- โค้ดฟีเจอร์ + co-located unit test ใน the project test directory, co-located กับ logic under test สำหรับ pure logic.
- tasks.md ทุก task `- [x]` พร้อม Evidence block.
- typecheck + test เขียว; `scripts/spec-trace.sh <feature>` รายงาน REQ coverage ครบ.

## Definition of done

- [ ] ทุก REQ ใน requirements.md ถูกครอบโดยอย่างน้อยหนึ่ง task (`scripts/spec-trace.sh` ผ่าน).
- [ ] task code-green ผ่าน `.ai/bin/gate-task.sh` ซึ่งรัน typecheck + test ตาม `SDD_TYPECHECK_CMD` /
      `SDD_TEST_CMD` (auto-detect package.json scripts สำหรับ Node).
- [ ] ทุก task `- [x]` + Evidence (command จริงที่รัน + ผลที่สังเกต, ไม่ใช่บรรทัด Verify: ที่วางแผน).
- [ ] artifact ทั้ง 3 header = approved; decision + rationale บันทึกในไฟล์ (ไม่ใช่แค่ในการสนทนา).
- [ ] ถ้าโปรเจ็กต์ ship UI: verify ใน the project target runtime (ดู the project UI-verify reference)
      และ accessibility ผ่าน.

## Common mistakes to avoid

- กระโดดไป implement โดยข้าม requirements/design สำหรับฟีเจอร์ที่ไม่ trivial.
- เชื่อ checkbox/git log แทน filesystem — commit อ้าง task เสร็จ แต่ artifact จริงไม่มี
  (`scripts/spec-state.sh` มีไว้จับเคสนี้; `git diff --stat` ไม่เห็น untracked `??`).
- mark `- [x]` โดยไม่แนบ Evidence หรือ Evidence เป็นบรรทัดที่ "วางแผน" ไม่ใช่ที่รันจริง.
- pre-split task เป็น 1.1/1.2 ใน tasks.md (ผิด sizing สำหรับ model นี้).
- ลืม cross-check ทุก section ใน REQ-1.1 (ลำดับหน้า) เทียบ component ที่มีจริง ตอน assemble — section
  ที่ตกร่อง decomposition จับได้ที่ assembly เท่านั้น (สร้างเป็น prerequisite ของ assembly + flag,
  ไม่แก้เงียบ).
- horizontal layer ("create model", "create repository") ที่ทำอะไรไม่ได้ตามลำพัง — ใช้ vertical slice.
- compact/clear กลาง task ที่ state อยู่แค่ในการสนทนา — persist active task id, decisions, next step
  ลง tasks.md ก่อน.
