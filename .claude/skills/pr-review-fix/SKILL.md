---
name: pr-review-fix
description: ดำเนินงานต่อจาก review comment บน PR — ดึง findings, verify กับโค้ดจริง, แก้บน branch เดิม, sync spec, gate เขียว, push + ตอบกลับ reviewer
argument-hint: "<PR number หรือ URL ของ review comment>"
---

# PR review follow-up: $ARGUMENTS

จัดการ findings จาก review (คน / claude[bot] / Codex) บน PR ที่ระบุ ให้จบใน branch เดิมของ PR
narrate เป็นไทยทุกขั้น; commit message / code ตาม convention repo

หมายเหตุ credential: ทุก Bash call ที่แตะ `gh`/`git push` ให้ `unset GH_TOKEN` ก่อน
(stale token shadow keyring — memory `gh-token-env-shadows-keyring`)

## 1. ดึง review

- $ARGUMENTS เป็น comment URL (`#issuecomment-<id>`) → `gh api repos/<owner>/<repo>/issues/comments/<id>`
- เป็นเลข PR → กวาดทั้ง 3 แหล่ง: `gh pr view <n> --comments`, review comments
  (`gh api repos/.../pulls/<n>/comments`), reviews (`gh api repos/.../pulls/<n>/reviews`)
- เก็บ `headRefName` ของ PR ไว้: `gh pr view <n> --json headRefName,state` — `state` ต้องเป็น `OPEN`
  (MERGED/CLOSED → หยุดรายงาน ไม่มี branch ให้แก้ต่อ)

## 2. Triage findings

แยกเป็นรายการ: ตัวไหน blocking (Should Fix / P1 / Must) ตัวไหน non-blocking (nice-to-have / P3)
- blocking → ต้องปิดทุกตัว (แก้ หรือ rebut พร้อมหลักฐาน)
- non-blocking → ประเมินทีละตัว คุ้มแก้รอบนี้ไหม — ไม่คุ้มให้บันทึกเหตุผลไว้ตอบกลับ ไม่เงียบหาย

## 3. Verify ก่อนแก้ (ห้ามข้าม)

Reviewer ผิดได้. ทุก finding ต้องเปิดไฟล์/บรรทัดที่อ้างแล้วยืนยันเองว่า:
- ปัญหามีจริงในโค้ดปัจจุบันของ branch (ไม่ใช่โค้ดเก่า/คนละ branch)
- แนวแก้ที่ reviewer เสนอสอดคล้อง convention ของ repo — ถ้า repo มี precedent อื่น ให้ยึด precedent
- ผิดจริงแต่แนวแก้ผิด → แก้ตามแนว repo แล้วอธิบายใน reply
- finding ไม่จริง → REBUT ใน reply พร้อมชี้ไฟล์:บรรทัด ห้ามแก้ตาม reviewer แบบไม่ตรวจ
- finding ที่จริง → วิเคราะห์หา root cause ก่อนลงมือ: trace flow จริง (ใครเรียก, ข้อมูลไหลจากไหน,
  ทำไมถึงเกิด) — จุดที่ reviewer ชี้อาจเป็นปลายเหตุ; แก้ที่ต้นเหตุจุดเดียวที่ทุก path วิ่งผ่าน
  ไม่ patch เฉพาะจุดที่ถูกชี้แล้วปล่อย sibling path พังต่อ
- ห้ามเดาทุกกรณี — ทุกข้อสรุป (ปัญหาจริง/ไม่จริง, root cause, แนวแก้) ต้องมีหลักฐานอ้างได้:
  ไฟล์:บรรทัด, ผล grep callers, ผลรัน test/reproduce จริง — ไม่มีหลักฐาน = ยังสรุปไม่ได้ ต้องหาต่อ

## 4. แก้บน branch เดิมของ PR

- `git checkout <headRefName>` — working tree ต้องสะอาดก่อนสลับ (มีของค้าง → หยุดถาม user)
- ก่อนแก้แต่ละ finding สรุป **ก่อน → หลัง**: จะเปลี่ยนอะไร, กระทบ caller/module/contract/test ไหนบ้าง
  (จาก grep callers จริง ไม่ใช่คาดเดา) — เจอผลกระทบเกิน scope ของ finding → หยุดรายงาน user ก่อนลงมือ
- แก้ตาม findings ที่ยืนยันแล้ว + **เขียน/ขยาย test ที่จับ regression ของ finding นั้นโดยตรง**
  (finding ที่ไม่มี test จับ = ยังไม่ปิด)
- feature มาจาก spec (`.ai/specs/<feature>/`) → sync spec artifacts ตามกฎ sync mode:
  requirements.md/design.md ที่ approved แล้ว → re-stamp `> Status: approved <เดิม>, amended <วันนี้>`
  + patch เฉพาะส่วนที่เปลี่ยน; tasks.md เติม Evidence ของงานแก้; รัน `scripts/spec-trace.sh <feature>` ให้ผ่าน

## 5. Gate + push

- รัน test ชุดที่แตะ + full gate ถ้าการแก้ข้ามโมดูล (`dotnet build` + `dotnet test`;
  Integration ต้อง `source .env.integration` ใน call เดียวกัน) — เขียวก่อน push เท่านั้น
- add/commit แยกคนละ Bash call; commit message อ้าง review: `fix(scope): ... (review PR #<n>)`
- `unset GH_TOKEN; git push` เข้า branch เดิม — trigger `synchronize` ให้ CI รันใหม่เอง

## 6. ตอบกลับ + รายงาน

- ตอบกลับใต้ PR ด้วย `unset GH_TOKEN; gh pr comment <n> --body ...`: ตาราง finding → ผล
  (fixed + commit SHA / rebutted + เหตุผล / deferred + เหตุผล) — evidence จริงเท่านั้น
- รายงาน user: สรุป findings, สิ่งที่แก้, ผล test, ลิงก์ commit + comment
  + ผลกระทบต่อระบบของแต่ละการแก้ (พฤติกรรมก่อน → หลัง, โมดูล/endpoint ที่กระทบ)
  เทียบกับที่ประเมินไว้ก่อนแก้
- **ห้าม merge เอง** — จบที่ push + reply เสมอ

## ข้อห้ามยืนพื้น

- ห้ามแก้ตาม finding โดยไม่ verify กับโค้ดจริงก่อน
- ห้าม push ตรง develop / force push / `--no-verify`
- ห้ามปิด finding แบบเงียบ (ไม่แก้และไม่ตอบ) — ทุกตัวต้องมีคำตอบใน reply
- ห้ามสรุป root cause หรือผลกระทบจากการเดา — ไม่มีหลักฐาน = ไม่ลงมือ
- CI แดงหลัง push → แก้ต่อใน branch เดิม ห้ามทิ้งค้างโดยไม่รายงาน
