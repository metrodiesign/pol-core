# Workflow: Test Generation

Vendor-neutral procedure for any agent (Claude / Codex / OpenCode / Pi). สร้างทั้ง
unit test (pure logic) และ property-based test (PBT) จาก requirements — เพื่อ
พิสูจน์ความถูกต้องด้วยตัวอย่าง และทั่วทั้ง input space.

## Purpose

ปิด acceptance ที่เป็นความถูกต้อง (correctness) ก่อน wire UI: แยก logic ที่ทดสอบได้
(สูตร/validation) เป็น pure function ในโปรเจกต์ test directory (co-located กับ logic ที่ทดสอบ)
แล้วเขียน unit test ให้เขียว, จากนั้น
สกัด properties (invariant ที่ต้องจริงสำหรับ input ทุกตัว) แล้วเขียน PBT คุมทั่ว input space
ไม่ใช่แค่ตัวอย่างที่เลือกเอง.

## When to use

- มี pure logic (เช่น สูตรคำนวณ, validation) ที่ต้องพิสูจน์ correctness.
- ต้องการครอบ input space ทั้งหมด ไม่ใช่แค่ happy-path examples (-> เพิ่ม PBT).
- ไม่ใช้กับ: browser interaction verify (อยู่ใน `frontend-task.md`), การ implement ฟีเจอร์
  (ใช้ `feature-development.md` ที่รวม test อยู่แล้ว).

## Required context files

อ่านก่อนเริ่ม (relative จากไฟล์นี้):

- [../shared/TESTING_PROTOCOL.md](../shared/TESTING_PROTOCOL.md) — runner = the project test runner
  (declared via SDD_TEST_CMD env, or a package.json test script for a Node project); test อยู่ในโปรเจกต์
  test directory co-located กับ logic ที่ทดสอบ; ทุก test อ้าง REQ ID.
- [../shared/EARS.md](../shared/EARS.md) — requirements เป็น EARS; properties สกัดจาก EARS criteria.
- [../shared/CODING_STANDARDS.md](../shared/CODING_STANDARDS.md) — dependency rule (ห้ามเพิ่ม lib เงียบ).
- [../roles/pbt-runner.md](../roles/pbt-runner.md) — persona ที่ adopt สำหรับ PBT บน CORE domain logic.

## Step-by-step process

### A. Unit tests (pure logic)

1. **ระบุ pure function เป้าหมาย.** logic ที่คำนวณ/validate แยกอยู่ในโปรเจกต์ test directory
   (co-located กับ logic ที่ทดสอบ; ไม่ฝังในชั้น UI). ถ้า logic ยังฝังใน UI component ให้ flag —
   pure-logic-first: แยกเป็น pure fn ก่อน test.
   -> verify: function เป็น pure (deterministic, ไม่มี side effect) + แยกออกจากชั้น UI.

2. **เขียน unit test co-located.** สร้าง/ขยาย test file co-located กับ logic ที่ทดสอบ. ครอบ happy path +
   error/edge (empty, max, ค่าขอบ, special chars) ตาม IF...THEN ใน requirements. แต่ละ test
   อ้าง REQ ID ที่ validate. ทดสอบ rounding/หน่วยตามที่ requirements ระบุเป๊ะ.
   -> verify: project test runner เขียว; ทุก test มี REQ ID; ครอบทั้ง happy + edge.

### B. Property-based tests (adopt `pbt-runner` persona)

3. **สกัด properties จาก requirements.md.** universal statement ที่ต้องจริงสำหรับ input ทุกตัว
   เขียนเป็น: `For any <inputs> where <precondition>, THE SYSTEM SHALL <invariant>`. link แต่ละ
   property -> REQ ID + ระบุ input space / generator ที่ต้องใช้. present list ให้ผู้ใช้เลือกว่าจะ
   test อันไหน.
   -> verify: ทุก property link REQ ID + ระบุ input space; ผู้ใช้เลือก subset.

4. **เขียน PBT.** เช็ก dependency manifest ของโปรเจกต์ก่อน: ถ้าไม่มี PBT framework (fast-check ฯลฯ) ติดตั้งอยู่
   **ห้ามติดตั้งเงียบ** — เขียน property เป็น randomized-input loop บน project test runner (runner ที่โปรเจกต์มี;
   test อยู่ในโปรเจกต์ test directory co-located กับ logic ที่ทดสอบ) หรือเสนอ framework เป็น dev dependency (พร้อม license +
   maintenance status) แล้วรอ approval ตาม dependency rule ใน CODING_STANDARDS.md. generate input
   range กว้างรวม edge cases. แต่ละ test อ้าง REQ ID. เมื่อ property แตะ CORE domain logic
   (สูตรคำนวณ / validation) adopt [../roles/pbt-runner.md](../roles/pbt-runner.md); inline ได้ถ้าไม่ใช่ CORE.
   -> verify: project test runner รัน PBT loop เขียว; ไม่มี dependency ใหม่ที่ไม่ได้ approve.

5. **Triage counter-example.** เมื่อ property fail รายงาน minimal failing ("shrunk") input แล้ว
   ถามผู้ใช้ว่าจะแก้ implementation, แก้ test, หรือแก้ requirement — ห้ามแก้ requirement โดยไม่
   surface เพื่อ approval.
   -> verify: counter-example เป็น minimal; ทางเลือกแก้ถูก present ให้ผู้ใช้.

## Expected output

- unit test ในโปรเจกต์ test directory (co-located กับ logic ที่ทดสอบ) ครอบ happy + edge ทุก test อ้าง REQ ID.
- PBT (randomized loop บน project test runner หรือ framework ที่ approve แล้ว) สำหรับ property ที่เลือก ทุก test
  อ้าง REQ ID.
- project test runner เขียวทั้งหมด; counter-example (ถ้ามี) ถูก triage + ตัดสิน.

## Definition of done

- [ ] logic ที่ test เป็น pure function แยกออกจากชั้น UI.
- [ ] test ทั้งหมดอยู่ในโปรเจกต์ test directory (co-located กับ logic ที่ทดสอบ).
- [ ] ทุก test/property อ้าง REQ ID ที่ validate.
- [ ] unit test ครอบ happy path + error/edge (empty, max, ค่าขอบ, special chars).
- [ ] PBT generate input space กว้างรวม edge; counter-example ที่เจอถูก report (shrunk) + triage.
- [ ] project test runner เขียว; ไม่มี PBT framework ใหม่ที่ติดตั้งโดยไม่ approve; ไม่มี `.only`/`.skip` ค้าง.

## Common mistakes to avoid

- เขียน test นอกโปรเจกต์ test directory — runner ไม่ include = test ไม่รัน ผ่าน vacuously
  (false green).
- test logic ที่ยังฝังใน UI component — แยกเป็น pure fn ก่อน (pure-logic-first).
- ติดตั้ง fast-check / PBT framework เงียบ — ละเมิด dependency rule; ต้อง approve ก่อน.
- PBT ที่ generate แคบ (เฉพาะ happy range) — พลาด edge ที่ property ควรจับ.
- ไม่ report shrunk counter-example — full random input อ่านยาก debug ยาก.
- แก้ requirement เองเมื่อ property fail โดยไม่ surface — requirement คือ source of truth ต้อง approve.
- assert rounding/หน่วยที่ไม่ตรง requirements — ความถูกต้องเชิงตัวเลขต้องตรง spec เป๊ะ.
