# Handoff: แบบเรียบง่ายสำหรับทีมเล็ก
> From: spec-authoring session   To: orchestrator หรือ any   Date: 2026-09-09

## Task Summary

ข้อเสนอนี้กำหนดแนวทางลดความซับซ้อนของ POL Platform สำหรับทีมเล็ก โดยอ้างอิง active spec `platform-restructure-v1` และข้อกำหนด `REQ-1` ถึง `REQ-12` การบันทึกนี้คงสถานะเอกสาร ณ commit `470e0b1c` และยังไม่ถือว่าเริ่ม implementation หรือ refactor backend/database แล้ว

## Current Status

เอกสารข้อเสนอและ spec สำหรับเริ่มพัฒนาเป็นช่วงเสร็จแล้ว ณ commit `470e0b1c` มี blueprint, source review, ERD v3 และ handoff เป็นชุดเอกสารที่ใช้อ้างอิงต่อ เป้าหมายลดเหลือ 4 source projects และ 3 test projects เป็น target ที่เสนอ ยังไม่ใช่ผล refactor ที่ทำแล้ว และ implementation tasks ทั้ง 10 ช่วงยังไม่เริ่ม

## Files Changed

- `docs/proposals/payment-platform-redesign-2026-09-08/latest-blueprint.md` — เอกสารแบบหลักและ MVP defaults — คงใช้
- `docs/proposals/payment-platform-redesign-2026-09-08/latest-source-review.md` — ผลตรวจ source และประเด็นที่ต้องล็อก — คงใช้
- `outputs/diagrams/2026-09-08_payment-platform-latest-blueprint_v3.md` — conceptual model ของ scope ทั้งหมด — คงใช้
- `docs/proposals/payment-platform-redesign-2026-09-08/handoff.md` — สถานะส่งต่องานและข้อจำกัด — แก้ schema ให้ตรง protocol
- `.ai/specs/platform-restructure-v1/requirements.md` — ข้อกำหนด EARS — คงใช้เป็น source of truth
- `.ai/specs/platform-restructure-v1/design.md` — design ที่ approved — คงใช้เป็น source of truth
- `.ai/specs/platform-restructure-v1/tasks.md` — ลำดับ implementation 10 ช่วง — คงใช้เป็น source of truth

เอกสารเก่าที่ถูกแทนแล้ว ได้แก่ `overview.md`, `analysis.md`, `target-structure.md`, `payment-platform-business_v1.md`, `payment-platform-target-structure_v1.md` และ `payment-platform-latest-blueprint_v2.md` ไม่อยู่ในชุดที่ต้องนำกลับมา

## Important Decisions

- คง 7 เจ้าของงานและไม่มี Payment aggregate แต่ลดการแยก project ซ้ำต่อโมดูล
- เป้าหมายคือ 4 source projects: Domain, Application, Infrastructure, Api และ 3 test projects: Unit, Architecture, Integration
- เสนอ 2 runtime contexts คือ Control Plane และ Commerce หลังพิสูจน์ isolation กับ transaction ครบ โดย migration composition มี mapping เจ้าของเดียว
- ใช้ backend, database และ process หลักชุดเดียว งานเบื้องหลังใช้ `BackgroundService` และ outbox ที่มีอยู่ ไม่เพิ่ม broker, Redis หรือ framework เผื่ออนาคต
- จำกัดหนึ่ง active `MerchantAccess` ต่อ Account/Merchant และหนึ่ง Client ต่อ SYSTEM Account ตาม design
- คง auth/tenant guards, vault, idempotency, snapshots, audit, inbox/outbox, retry, 2C2P/Omise และ Email/SMS พร้อม capability gate
- ใช้ OAuth/OIDC component ที่ดูแล ไม่สร้าง authorization server หรือ crypto เอง และไม่เก็บ session/token state ซ้ำ
- API inventory 116 รายการเป็น scope สำหรับ trace ไปยัง use case/consumer ก่อน implement ไม่ใช่จำนวน CRUD ที่ต้องสร้าง
- ก่อน retire source ต้องตรวจ consumer, DI/reflection/generated wiring, jobs/config, ข้อมูล และ callback ที่ค้าง แล้วรัน gate ที่เกี่ยวข้อง

## Constraints

- repository คือ `/Users/king_developer/Desktop/Project/pol-core` และสถานะอ้างอิงคือ commit `470e0b1c`
- handoff นี้บันทึกสถานะการเขียน spec เท่านั้น ห้ามดึง handoff หรือ implementation ที่เกิดหลัง commit 470e0b1c เข้ามาปะปน
- ไม่มีการแก้ source/config/database หรือ production data ในรอบสร้าง spec; การลด project/context ยังเป็น target ที่ต้องพิสูจน์
- ห้าม commit, push, deploy, ลบตาราง, ลบ PSP reference หรือทำลายข้อมูลผู้ใช้จากขอบเขตนี้
- ค่าเริ่มต้นในเอกสารเป็นข้อเสนอสำหรับ v1 ยังไม่ใช่ approval ของ Physical DDL หรือ deployment

## Tests Run

- `python3 scripts/tests/test_repo_policy_alignment.py` -> `Ran 61 tests in 0.577s`, `OK`, exit `0`
- `python3 -m unittest discover -s scripts/tests -p 'test_*.py'` -> `Ran 350 tests in 37.586s`, `OK`, exit `0`
- `python3 scripts/repo_policy_alignment.py --check --json` -> `{"diagnostics": [], "schemaVersion": 1, "verdict": "allow"}`, exit `0`
- `python3 scripts/spec_contract.py check --feature platform-restructure-v1 --strict` -> `OK: 'platform-restructure-v1' เกณฑ์ 126 ข้อ ถูกอ้างครบใน design.md และ tasks.md, EARS lint ผ่านทุกข้อ`, exit `0`
- `bash scripts/spec-trace.sh platform-restructure-v1` -> `OK: 'platform-restructure-v1' เกณฑ์ 126 ข้อ ถูกอ้างครบใน design.md และ tasks.md, EARS lint ผ่านทุกข้อ`, exit `0`
- `git diff --check` -> output ว่าง, exit `0`

คำสั่งทั้งหมดเป็น checks ที่มีอยู่ใน repository; ไม่มีการอ้าง checker จาก temporary path ภายนอก

## Known Issues

- ต้องทำ physical model และ state matrix ของ create/issue/cancel ก่อนแก้ code
- การลด project/context ต้องมี architecture และ integration evidence โดยเฉพาะ tenant filters, write guard และ cross-module transaction
- การใช้งานจริงของ legacy APIs, jobs, consumers และ production data ยังต้อง inventory ก่อนลบ source
- Entra tenant, PSP sandbox credentials, SMS vendor, identity mappings, backup/recovery evidence และ production authorization ยังเป็น prerequisite ของงานภายนอกตาม `tasks.md`

## Next Recommended Agent

ให้ orchestrator รับต่อเมื่อผู้ใช้สั่งเริ่ม implementation แล้ว route ตาม task dependency โดยใช้ `architect`, `coder`, `auditor`, `verifier` และ `reviewer` ตาม gate ของ repository

## Next Steps

1. รัน `git status --short` แล้วอ่าน `../../../.ai/specs/platform-restructure-v1/requirements.md`, `design.md`, `tasks.md` และตรวจ filesystem เทียบกับ handoff นี้
2. เลือก task ที่ผู้ใช้อนุมัติให้เริ่ม แล้วทำ inventory consumer, ownership และ external prerequisites ก่อนแก้ source
3. รัน verify commands ของ task นั้น บันทึก Evidence และผ่าน review/test gate ก่อน retire legacy หรือ deploy
