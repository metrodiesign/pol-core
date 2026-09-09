# Handoff: POL Platform รุ่นแรก
> From: spec-authoring session   To: orchestrator หรือ any   Date: 2026-09-09

## Task Summary

active spec `platform-restructure-v1` กำหนดการรื้อ POL Platform รุ่นแรกแบบเป็นช่วง โดย requirements ครอบคลุม `REQ-1` ถึง `REQ-12` และมี design กับ tasks เป็น source of truth สำหรับ implementation การบันทึกนี้คงสถานะ ณ commit `470e0b1c` ซึ่งยังไม่มี implementation ของ Task 1 หรือ task อื่นเริ่มขึ้น

## Current Status

- Requirements: approved 2026-09-09, 12 กลุ่ม รวม 126 เกณฑ์
- Design: approved 2026-09-09, 7 เจ้าของงาน, 4 source projects, 3 test projects และ 2 runtime contexts เป็น target design
- Tasks: approved 2026-09-09, 10 ช่วงเรียงตาม dependency และยังไม่เริ่มทุกช่วง ณ commit `470e0b1c`
- API scope: `api-scope.json` มี 111 v1 operations และ 5 deferred จาก inventory 116 รายการ
- Source/config/database: ไม่มีการแก้ในรอบสร้าง spec
- Commit/push/deploy: ไม่ได้ทำในรอบสร้าง spec

ผู้ใช้อนุมัติเอกสาร spec วันที่ 9 กันยายน 2026 โดยยังไม่สั่งเริ่ม implementation; approval นี้ครอบคลุมเอกสารเท่านั้น

## Files Changed

- `.ai/specs/platform-restructure-v1/requirements.md` — ข้อกำหนด EARS และ acceptance scope — คงใช้
- `.ai/specs/platform-restructure-v1/design.md` — design ของ packaging, ownership, contexts, security และ migration — คงใช้
- `.ai/specs/platform-restructure-v1/tasks.md` — implementation checklist 10 ช่วงพร้อม Verify commands — คงใช้
- `.ai/specs/platform-restructure-v1/api-scope.json` — inventory API v1 และ deferred routes — คงใช้
- `.ai/specs/platform-restructure-v1/handoff.md` — สถานะส่งต่องาน — แก้ schema ให้ตรง `.ai/shared/AGENT_HANDOFF_PROTOCOL.md`

## Important Decisions

- หนึ่ง active `MerchantAccess` ต่อ Account/Merchant และหนึ่ง Client ต่อ SYSTEM Account ที่ผูก Merchant/environment เดียว
- Account/Access ใช้ OAuth state เจ้าของเดียวผ่าน OpenIddict ตาม version และ EF compatibility ใน design
- `Order` เป็นเจ้าของยอดและสถานะธุรกิจ ไม่มี Payment aggregate; `Transaction` เก็บความจริงของทุก attempt รวมผลมาช้าและเงินจริงซ้ำ
- ใช้ maintenance cutover เดียว, deterministic legacy mapping และ recovery ที่รักษา events หลัง cutover
- OTP และ template editor เลื่อนไปก่อน ส่วน Email/SMS ยังอยู่ใน scope พร้อม capability gate
- target packaging คือ 4 source projects และ 3 test projects แต่จะถอด legacy ได้เมื่อ inventory, ownership, isolation และ migration evidence ผ่านเท่านั้น

## Constraints

- สถานะใน handoff นี้หยุดที่ commit `470e0b1c`; ห้ามนำ current dirty Task 1 implementation หรือ handoff จากการทำงานภายหลังมาปะปน
- ห้ามถือ target project/context count เป็นผล refactor ที่ทำแล้ว และห้ามตัด security boundary เพื่อให้จำนวนไฟล์ตรงเป้า
- external prerequisites ได้แก่ Entra tenant/issuer/app registrations, PSP sandbox credentials/contract evidence, SMS vendor, identity mappings, backup/recovery evidence และ production authorization
- Verify commands ใน `tasks.md` เป็นเกณฑ์ของ implementation หลังมี target projects; integration ที่ต้องใช้ SQL Server หรือ external provider ต้องบันทึกข้อจำกัดตามจริง
- ห้าม commit, push, deploy, เปลี่ยน protected branch หรือทำ production action จาก handoff นี้

## Tests Run

- `python3 scripts/tests/test_repo_policy_alignment.py` -> `Ran 61 tests in 0.577s`, `OK`, exit `0`
- `python3 -m unittest discover -s scripts/tests -p 'test_*.py'` -> `Ran 350 tests in 37.586s`, `OK`, exit `0`
- `python3 scripts/repo_policy_alignment.py --check --json` -> `{"diagnostics": [], "schemaVersion": 1, "verdict": "allow"}`, exit `0`
- `python3 scripts/spec_contract.py check --feature platform-restructure-v1 --strict` -> `OK: 'platform-restructure-v1' เกณฑ์ 126 ข้อ ถูกอ้างครบใน design.md และ tasks.md, EARS lint ผ่านทุกข้อ`, exit `0`
- `bash scripts/spec-trace.sh platform-restructure-v1` -> `OK: 'platform-restructure-v1' เกณฑ์ 126 ข้อ ถูกอ้างครบใน design.md และ tasks.md, EARS lint ผ่านทุกข้อ`, exit `0`
- `git diff --check` -> output ว่าง, exit `0`

คำสั่งข้างต้นเป็น checks ที่มีอยู่ใน repository และใช้ตรวจ handoff/spec ที่ commit นี้อ้างถึง; ไม่มี temporary checker หรือ evidence ของ Task 1 ใน handoff นี้

## Known Issues

- ยังไม่มี implementation task ใดเริ่ม ณ commit `470e0b1c`
- ต้องพิสูจน์ SQL Server race, tenant guards, transaction serialization, PSP callback/recovery, notification delivery และ migration rehearsal ตาม task-specific evidence
- ถ้า filter ของ `dotnet test` ไม่พบ tests ให้ถือว่าไม่ผ่าน แม้ runner จะคืน exit code 0
- งาน production ต้องรอ authorization, backup/recovery plan และ staging verification ตาม project rules

## Next Recommended Agent

ให้ orchestrator รับต่อเมื่อผู้ใช้สั่งเริ่ม implementation แล้ว route Task 1 ตาม dependency และใช้ `architect`, `coder`, `auditor`, `verifier` และ `reviewer` ตาม gate ของ repository

## Next Steps

1. รัน `git status --short` แล้วอ่าน `requirements.md`, `design.md`, `tasks.md`, `api-scope.json` และตรวจ filesystem เทียบกับ handoff นี้
2. ตรวจ capability และ external prerequisites ก่อนเริ่ม task ที่ต้องใช้ SQL Server, Entra, PSP หรือ SMS
3. เลือก task ที่ได้รับคำสั่งเริ่ม ทำ tests พร้อม implementation และบันทึก Evidence ของ Verify command ก่อนส่ง review
