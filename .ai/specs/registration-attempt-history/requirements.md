# Requirements: Registration Attempt History

> Status: approved 2026-08-02

## Overview

merchant user ที่ถูก admin ปฏิเสธการลงทะเบียนสามารถ resubmit ได้แล้ว (Correction flow, PR #137) แต่ข้อมูลฟอร์มของแต่ละครั้งถูกเขียนทับบน `merch.Users` ทำให้ admin ไม่เห็นว่าผู้สมัครเคยกรอกอะไรมาก่อนถูกปฏิเสธ feature นี้เก็บ snapshot ฟอร์มต่อการ submit หนึ่งครั้งแบบ append-only ผูกกับ user รายคน และเปิด endpoint ให้ admin ดูประวัติการดำเนินงานเต็ม (submit → rejected(reason) → resubmit → approved) โดยคุ้มครอง PII ด้วย masking + reveal audit — สอดคล้องกับหลัก audit/control-plane ของ platform (ดู `.ai/shared/PROJECT_CONTEXT.md`)

## REQ-1: Attempt Snapshot Capture

**User Story:** As a platform, I want ทุกการ submit ฟอร์มลงทะเบียนถูกบันทึกเป็น snapshot ถาวรต่อครั้ง, so that ประวัติการสมัครของ user แต่ละคนไม่สูญหายเมื่อมีการ resubmit ทับ

**Acceptance Criteria (EARS):**

- 1.1 WHEN `SubmitRegistrationHandler` ประมวลผลสำเร็จ (ทั้ง branch Registration และ Correction) THE SYSTEM SHALL เขียนแถวใหม่ลงตาราง `merch.RegistrationAttempts` ภายใน transaction เดียวกันกับการเขียน `User`
- 1.2 THE SYSTEM SHALL เก็บใน snapshot อย่างน้อย: `FirstName`, `LastName`, `PersonType`, `IdNumber`, `ProducerCode`, `LicenseNumber`, `Phone`, `Email`, `PhotoObjectKey`, `PhotoContentType`, `SubmittedAt`, `Purpose` (Registration/Correction) — โดย `Email` คือค่าจาก verified ticket ของ attempt นั้น (`command.Email`) ไม่ใช่ค่าปัจจุบันบน `User` (A3)
- 1.3 THE SYSTEM SHALL ผูกทุก attempt กับ user ผ่าน FK `MerchantUserId` อ้าง `merch.Users(Id)`
- 1.4 THE SYSTEM SHALL กำหนด `AttemptNo` เป็นลำดับต่อ user เริ่มที่ 1 และเพิ่มทีละ 1 ต่อการ submit สำเร็จ
- 1.5 THE SYSTEM SHALL บังคับ `UNIQUE(MerchantUserId, AttemptNo)` ที่ระดับฐานข้อมูล
- 1.6 THE SYSTEM SHALL เก็บรูปถ่ายเป็น reference (`PhotoObjectKey`) เท่านั้น ไม่ copy ไฟล์รูปต่อ attempt
- 1.7 THE SYSTEM SHALL ไม่อนุญาตให้ UPDATE หรือ DELETE แถวใน `merch.RegistrationAttempts` จาก application code path ใด (append-only)
- 1.8 IF การเขียน snapshot ล้มเหลว THEN THE SYSTEM SHALL rollback การ submit ทั้ง transaction (ไม่มีสถานะที่ `User` ถูกแก้แต่ snapshot หาย)
- 1.9 IF สอง request สร้าง `AttemptNo` ชนกันบน user เดียวกัน (race) THEN THE SYSTEM SHALL ให้ฝ่ายแพ้ได้รับ 409 ผ่าน unique index ตามกลไก unit-of-work เดิม

## REQ-2: Admin Registration History Endpoint

**User Story:** As an admin, I want ดูประวัติการลงทะเบียนทุกครั้งของ merchant user รายคน, so that ตัดสินใจ approve/reject รอบใหม่ได้โดยเห็นว่าเคยกรอกอะไรและถูกปฏิเสธด้วยเหตุผลอะไร

**Acceptance Criteria (EARS):**

- 2.1 THE SYSTEM SHALL เปิด endpoint `GET /api/v1/admins/merchants/users/{subject}/registrations`
- 2.2 THE SYSTEM SHALL คืน attempts ทั้งหมดของ user นั้นเรียงตาม `AttemptNo` จากน้อยไปมาก โดยไม่มี cap/pagination (ตั้งใจ — ปริมาณ attempt ต่อ user bounded ด้วยพฤติกรรมจริง, G3)
- 2.3 THE SYSTEM SHALL ประกอบ timeline เหตุการณ์ของ user จาก `merch.RegistrationAudits` (query ด้วย `TargetSubject`) รวมไว้ใน response เพื่อให้เห็น submit → rejected(reason) → resubmitted → approved ครบ
- 2.4 THE SYSTEM SHALL อ่านข้อมูล user ผ่าน read path แบบ filter-free (pre-bind seam) เพราะ target row อาจมี `MerchantId` เป็น NULL
- 2.5 IF ไม่พบ user ของ `subject` นั้น THEN THE SYSTEM SHALL คืน 404
- 2.6 WHEN user ยังไม่มี attempt (ลงทะเบียนก่อน feature นี้ deploy และยังไม่ resubmit) THE SYSTEM SHALL คืน list ว่างพร้อม timeline จาก `RegistrationAudits` ตามปกติ (ไม่ error)

## REQ-3: PII Masking and Reveal Audit

**User Story:** As a platform, I want PII ในประวัติถูก mask โดย default และการเปิดดูค่าเต็มถูก audit, so that ข้อมูลส่วนบุคคลไม่รั่วเกินจำเป็นและตรวจย้อนได้ว่าใครเปิดดู

**Acceptance Criteria (EARS):**

- 3.1 THE SYSTEM SHALL mask field ต่อไปนี้ใน response โดย default: `IdNumber`, `LicenseNumber`, `Phone` — rule เดียวกัน: ค่ายาวกว่า 4 ตัว → `****` ตามด้วย 4 ตัวท้าย, ค่ายาว ≤ 4 ตัว → mask ทั้งหมด (`****`), NULL → NULL
- 3.2 THE SYSTEM SHALL mask `Email` โดย default ด้วยรูปแบบ: ตัวอักษรแรกของ local part + `***@` + domain เต็ม (เช่น `s***@example.com`), NULL → NULL
- 3.3 THE SYSTEM SHALL แสดง `FirstName`, `LastName`, `DisplayName` เต็มเสมอ (admin ต้องใช้ระบุตัวผู้สมัคร)
- 3.4 WHERE request ระบุ query `?reveal=true` THE SYSTEM SHALL คืนค่า PII เต็มโดยไม่ mask ครอบทั้ง response (ทุก attempt ใน list)
- 3.5 WHEN request ที่ `reveal=true` ได้ response 200 (รวมกรณี attempts list ว่าง) THE SYSTEM SHALL เขียน `RegistrationAudit` action `revealed` หนึ่งแถวต่อ request ระบุ `ActorSubject` (admin ผู้เรียก) และ `TargetSubject`
- 3.6 IF `reveal=true` แต่ response เป็น 404 THEN THE SYSTEM SHALL ไม่เขียน audit `revealed`
- 3.7 IF การเขียน audit `revealed` ล้มเหลว THEN THE SYSTEM SHALL ล้มเหลวทั้ง request (5xx) — ห้ามคืนค่า PII เต็มโดยไม่มี audit (fail-closed)

## REQ-4: Permission Gate

**User Story:** As a platform operator, I want endpoint ประวัติถูก gate ด้วย permission key เฉพาะ, so that สิทธิ์เปิดดูข้อมูลผู้สมัครควบคุมผ่าน RBAC กลางได้

**Acceptance Criteria (EARS):**

- 4.1 THE SYSTEM SHALL เพิ่ม permission key ใหม่ `merchants.users.view` ใน catalog `iam.*` ผ่าน seed migration โดย grant ให้ role ชุดเดียวกับที่ถือ `Keys.MerchantUserApprove`/`Reject` อยู่แล้ว
- 4.2 THE SYSTEM SHALL gate endpoint REQ-2 ด้วย `merchants.users.view` (fail-closed ตามกลไก `RequirePermission` เดิม)
- 4.3 IF admin ผู้เรียกไม่มี permission `merchants.users.view` THEN THE SYSTEM SHALL คืน 403
- 4.4 THE SYSTEM SHALL อัปเดตจำนวนใน `assert-fresh-db` ให้ตรงกับ catalog หลังเพิ่ม key (นับจริงจากไฟล์ปัจจุบัน ไม่ใช้ตัวเลขจากแผนเก่า)

## Edge Cases & Open Questions

- **ไม่ backfill** (ตัดสินแล้ว): user ที่ลงทะเบียนก่อน deploy จะเริ่มมี attempt เมื่อ submit ครั้งถัดไป — ประวัติเก่าดูจาก `RegistrationAudits` (REQ-2.6 รองรับ)
- **Photo reference dangle** (ยอมรับแล้ว): ถ้า resubmit อัปโหลดรูปใหม่ รูปเก่าอาจถูก orphan ตามพฤติกรรม `IPhotoStore` เดิม — snapshot เก็บ key + content-type ไว้เป็น metadata แม้ blob จะหาย
- Concurrency: resubmit สองแท็บพร้อมกัน — REQ-1.9 ให้ unique index ตัดสิน สอดคล้อง concurrency token บน `Status` ที่มีอยู่แล้ว

### Findings log (/spec-analyze, anchor: HEAD `060a6aa` — ไฟล์ยังไม่ commit ณ ตอน audit, 2026-08-02)

| # | REQ | ประเด็น | Decision |
|---|-----|---------|----------|
| A1 | 3.1/3.2 | รูปแบบ mask ไม่ระบุ (ค่าสั้น, email, NULL) | กำหนด rule ชัดใน REQ-3.1/3.2: >4 ตัว → `****`+4 ท้าย, ≤4 → `****`, email → ตัวแรก+`***@`+domain, NULL → NULL |
| A2 | 3.4/3.5 | ขอบเขต reveal + granularity audit | reveal ครอบทั้ง response, audit 1 แถว/request |
| A3 | 1.2 | แหล่งค่า `Email` ใน snapshot | ใช้ `command.Email` จาก verified ticket ของ attempt นั้น |
| G1 | 3.7 | audit `revealed` เขียน fail | fail-closed — request ล้ม 5xx ห้ามคืน PII เต็มโดยไม่มี audit |
| G2 | 3.5 | reveal บน attempts list ว่าง | เขียน audit เสมอเมื่อ 200 รวมกรณี list ว่าง |
| G3 | 2.2 | ไม่มี cap/pagination | ไม่ cap — ตั้งใจ ระบุในเกณฑ์แล้ว |
| G4 | 2.3 | reject `Reason` free text ไม่ mask | ไม่ mask — admin เขียนให้ admin อ่าน ความเสี่ยงยอมรับ |
| U1 | 1.2/2 | ไม่มี endpoint ดูรูปย้อนหลัง | out of scope — snapshot เก็บ photo metadata เท่านั้น FE ไม่ render รูปจากประวัติ |
| — | 3.1 | `Phone` mask หรือไม่ (open question เดิม) | mask ด้วย rule เดียวกับ `IdNumber` |
| — | 4.1 | role ไหนได้ `merchants.users.view` ใน seed (open question เดิม) | ชุดเดียวกับ role ที่ถือ `MerchantUserApprove`/`Reject` |
