# Requirements: Entra Scoped Pre-provision

> Status: approved 2026-08-19

## Overview

ฟีเจอร์นี้ทำให้ Super ผูกบัญชี Scoped admin ที่สร้างไว้แล้วเข้ากับ Microsoft Entra Workforce identity
โดยใช้ tenant ID และ Object ID ที่ Entra รับรอง แทนการจับคู่ด้วยอีเมลที่แก้ไขได้

เป้าหมายคือให้พนักงานเข้า Admin Console ด้วยสิทธิ์และ merchant scope ที่เตรียมไว้ล่วงหน้า โดยไม่ผ่าน
bootstrap allowlist และไม่มีทางยกระดับเป็น Super จากขั้นตอนนี้

## REQ-1: การผูก Microsoft identity โดย Super

**User Story:** As a Super admin, I want pre-provision Microsoft identity ให้ Scoped admin, so that พนักงาน login ด้วย Entra ได้โดยไม่พึ่ง email binding

**Acceptance Criteria (EARS):**

- 1.1 WHEN Active Super ส่งคำขอ pre-provision THE SYSTEM SHALL รับ target admin ID, workforce tenant ID, Entra Object ID และเหตุผลการดำเนินการ
- 1.2 THE SYSTEM SHALL บันทึก identity provider ของ target เป็น `microsoft`
- 1.3 THE SYSTEM SHALL ใช้ Entra Object ID เป็น subject ของ Microsoft identity
- 1.4 THE SYSTEM SHALL ตรวจ workforce tenant ID จากคำขอตรงกับ tenant-pinned Microsoft Authority ของ Admin Console
- 1.5 THE SYSTEM SHALL ตรวจ workforce tenant ID เป็น UUID ที่ถูกต้อง
- 1.6 THE SYSTEM SHALL ตรวจ Entra Object ID เป็น UUID ที่ถูกต้อง
- 1.7 THE SYSTEM SHALL บังคับ CSRF protection บนคำขอ pre-provision
- 1.8 THE SYSTEM SHALL บังคับ `If-Match` ด้วย resource version ปัจจุบันของ target admin
- 1.9 THE SYSTEM SHALL บังคับ `Idempotency-Key` ที่ไม่ว่างและยาวไม่เกิน 200 ตัวอักษร
- 1.10 IF caller ไม่มี Admin session ที่ถูกต้อง THEN THE SYSTEM SHALL ตอบ `401`
- 1.11 IF caller ไม่ใช่ Active Super THEN THE SYSTEM SHALL ตอบ `403`
- 1.12 IF Microsoft provider ของ Admin Console ไม่ได้เปิดใช้งาน THEN THE SYSTEM SHALL ตอบ `409` โดยไม่เปลี่ยนข้อมูล
- 1.13 IF workforce tenant ID หรือ Entra Object ID ไม่ใช่ UUID THEN THE SYSTEM SHALL ตอบ `400`
- 1.14 IF workforce tenant ID ไม่ตรงกับ tenant-pinned Microsoft Authority THEN THE SYSTEM SHALL ตอบ `400`
- 1.15 IF `If-Match` หรือ `Idempotency-Key` ขาดหายหรือไม่ถูกต้อง THEN THE SYSTEM SHALL ตอบ `400`
- 1.16 WHEN Microsoft provider เปิดใช้งาน THE SYSTEM SHALL บังคับให้ tenant segment ใน Admin Microsoft Authority เป็น UUID และ fail fast ตอน boot หากไม่ถูกต้อง
- 1.17 THE SYSTEM SHALL normalize workforce tenant ID, Entra Object ID และ token claim `tid`/`oid` เป็น UUID รูปแบบ lowercase `D` ก่อนเปรียบเทียบ บันทึก หรือคำนวณ idempotency intent
- 1.18 WHEN database ยังไม่มี persisted workforce tenant binding THE SYSTEM SHALL initialize binding จาก tenant-pinned Authority ที่ผ่าน validation ก่อนรับ Microsoft login หรือ pre-provision
- 1.19 THE SYSTEM SHALL ถือ persisted workforce tenant binding เป็น immutable ต่อ database และการเปลี่ยน tenant ต้องผ่าน migration/runbook แยก
- 1.20 IF tenant ใน Admin Microsoft Authority ต่างจาก persisted workforce tenant binding THEN THE SYSTEM SHALL fail fast ตอน boot และไม่รับ Microsoft login หรือ pre-provision
- 1.21 THE SYSTEM SHALL trim เหตุผลการดำเนินการและบังคับให้มีความยาว `1..1000` ตัวอักษร
- 1.22 WHEN mutation เริ่ม transaction THE SYSTEM SHALL revalidate ว่า caller ยังเป็น Active Super ด้วย authorization version ปัจจุบันก่อนเขียน binding หรือ audit
- 1.23 IF transactional revalidation พบว่า caller ไม่ใช่ Active Super แล้ว THEN THE SYSTEM SHALL ตอบ `403` โดยไม่เปลี่ยนข้อมูล
- 1.24 THE SYSTEM SHALL คืน error ของ endpoint นี้เป็น RFC 9457 Problem Details พร้อม stable `code` ตาม wire contract ด้านล่างและ safe correlation ID

### Wire error contract

| Status | Code | Condition |
|---|---|---|
| `400` | `invalid_entra_tenant_id` | workforce tenant ID ไม่ใช่ UUID |
| `400` | `invalid_entra_object_id` | Entra Object ID ไม่ใช่ UUID |
| `400` | `entra_tenant_mismatch` | tenant ในคำขอไม่ตรง persisted/Authority tenant |
| `400` | `invalid_reason` | เหตุผลว่างหรือยาวเกินกำหนด |
| `400` | `invalid_etag` | `If-Match` ขาดหายหรือไม่ใช่ strong resource ETag |
| `400` | `invalid_idempotency_key` | `Idempotency-Key` ขาดหายหรือไม่ถูกต้อง |
| `401` | `admin_session_required` | ไม่มี Admin session ที่ถูกต้อง |
| `403` | `csrf_failed` | CSRF cookie/header ขาดหายหรือไม่ตรงกัน |
| `403` | `super_required` | caller ไม่ใช่ Active Super ตอน authorize หรือ transactional revalidation |
| `404` | `admin_not_found` | ไม่พบ target admin |
| `409` | `microsoft_provider_disabled` | Admin Microsoft provider ไม่ได้เปิดใช้งาน |
| `409` | `target_not_scoped` | target มี tier เป็น Super |
| `409` | `admin_identity_already_bound` | target ผูก identity อื่นแล้ว |
| `409` | `microsoft_identity_already_bound` | Microsoft identity ผูก target อื่นแล้ว |
| `409` | `state_conflict` | resource version stale หรือแพ้ concurrent write |
| `409` | `idempotency_key_reused` | key เดิมถูกใช้กับ logical intent อื่น |
| `409` | `operation_in_progress` | operation key เดิมยัง in-progress หรือ outcome ยังไม่ทราบ |

## REQ-2: Invariant ของบัญชีและ identity

**User Story:** As a security owner, I want identity binding เป็น one-time operation, so that ไม่มีผู้ดูแลสลับเจ้าของบัญชีหรือยกระดับสิทธิ์ผ่าน provisioning

**Acceptance Criteria (EARS):**

- 2.1 WHEN target เป็น Scoped admin ที่ยังไม่ผูก subject THE SYSTEM SHALL ผูก Microsoft identity แบบ atomic
- 2.2 WHEN binding สำเร็จ THE SYSTEM SHALL เพิ่ม resource version ของ target admin หนึ่งครั้ง
- 2.3 WHEN binding สำเร็จ THE SYSTEM SHALL คืน resource version ใหม่ผ่าน `ETag`
- 2.4 WHEN target ถูก suspend THE SYSTEM SHALL ยอมให้จอง identity โดยคงสถานะ Suspended ไว้
- 2.5 THE SYSTEM SHALL คง email ของ target admin ไว้โดยไม่เปลี่ยนแปลง
- 2.6 THE SYSTEM SHALL คง tier ของ target admin ไว้โดยไม่เปลี่ยนแปลง
- 2.7 THE SYSTEM SHALL คง role assignments ของ target admin ไว้โดยไม่เปลี่ยนแปลง
- 2.8 THE SYSTEM SHALL คง merchant assignments ของ target admin ไว้โดยไม่เปลี่ยนแปลง
- 2.9 THE SYSTEM SHALL คงข้อมูล org profile ของ target admin ไว้โดยไม่เปลี่ยนแปลง
- 2.10 IF target admin ไม่มีอยู่ THEN THE SYSTEM SHALL ตอบ `404`
- 2.11 IF target admin มี tier เป็น Super THEN THE SYSTEM SHALL ตอบ `409`
- 2.12 IF target admin ผูกกับ identity อื่นแล้ว THEN THE SYSTEM SHALL ตอบ `409`
- 2.13 IF Microsoft identity เดียวกันผูกกับ admin คนอื่นแล้ว THEN THE SYSTEM SHALL ตอบ `409`
- 2.14 IF `If-Match` ไม่ตรงกับ resource version ปัจจุบัน THEN THE SYSTEM SHALL ตอบ `409`
- 2.15 IF เกิด concurrent binding ของ identity เดียวกัน THEN THE SYSTEM SHALL ให้สำเร็จได้ไม่เกินหนึ่ง target admin
- 2.16 IF binding ล้มเหลว THEN THE SYSTEM SHALL ไม่เขียน identity หรือ audit บางส่วนค้างไว้
- 2.17 IF เกิด concurrent binding ของ identity ต่างกันไปยัง target admin เดียวกัน THEN THE SYSTEM SHALL ให้สำเร็จได้ไม่เกินหนึ่ง identity
- 2.18 WHEN binding สำเร็จหรือเป็น no-op THE SYSTEM SHALL ตอบ `200` พร้อม `{adminId, provider, subjectBound, version}` และ `ETag` ของ version ที่คืน
- 2.19 THE SYSTEM SHALL คืน `provider="microsoft"`, `subjectBound=true` และไม่คืน raw workforce tenant ID หรือ Entra Object ID ใน success body

## REQ-3: Idempotency

**User Story:** As an operator, I want retry provisioning ได้อย่างปลอดภัย, so that network failure ไม่สร้าง binding หรือ audit ซ้ำ

**Acceptance Criteria (EARS):**

- 3.1 WHEN `Idempotency-Key` เดิมถูกส่งซ้ำด้วย actor, operation และ logical intent เดิม THE SYSTEM SHALL ตรวจ replay หลัง authentication, authorization, CSRF และ header syntax แต่ก่อน provider/current-state/version gates แล้วคืนผลสำเร็จเดิมโดยไม่ bind ซ้ำ
- 3.2 WHEN `Idempotency-Key` เดิมภายใต้ actor และ operation เดิมถูกส่งซ้ำด้วย logical intent ต่างจากเดิม THE SYSTEM SHALL ตอบ `409`
- 3.3 WHEN `If-Match` ตรงกับ resource version และ target ผูกกับ Microsoft identity ค่าเดียวกับคำขออยู่แล้ว THE SYSTEM SHALL คืนผลสำเร็จแบบ no-op
- 3.4 WHEN binding เป็น no-op THE SYSTEM SHALL ไม่เพิ่ม resource version
- 3.5 WHEN binding เป็น no-op THE SYSTEM SHALL ไม่เพิ่ม audit event ของการเปลี่ยนสถานะ
- 3.6 IF operation record ของ key เดิมยัง in-progress หรือ outcome ยังไม่ทราบ THEN THE SYSTEM SHALL ตอบ `409` ด้วย code `operation_in_progress`
- 3.7 THE SYSTEM SHALL คำนวณ logical intent จาก canonical target admin ID, workforce tenant ID, Entra Object ID และเหตุผล โดยไม่รวม `If-Match`
- 3.8 WHEN exact replay ส่ง `If-Match` ต่างจากคำขอแรก THE SYSTEM SHALL คืนผลสำเร็จเดิมเพราะ `If-Match` ไม่ใช่ส่วนของ logical intent
- 3.9 WHEN exact replay หรือ natural no-op สำเร็จ THE SYSTEM SHALL คืน body และ `ETag` ตาม REQ-2.18

## REQ-4: Login และ authorization หลัง pre-provision

**User Story:** As a pre-provisioned employee, I want login ด้วย Entra account ของฉัน, so that ได้สิทธิ์ Scoped admin ที่องค์กรกำหนดไว้

**Acceptance Criteria (EARS):**

- 4.1 WHEN Microsoft OIDC callback ผ่าน token validation, `tid` ตรงกับ tenant-pinned Authority และ `oid` ตรงกับ bound subject THE SYSTEM SHALL resolve target admin
- 4.2 WHEN target admin เป็น Active THE SYSTEM SHALL สร้าง Admin session ตาม flow ปัจจุบัน
- 4.3 WHEN target admin เป็น Suspended THE SYSTEM SHALL ปฏิเสธการสร้าง Admin session
- 4.4 THE SYSTEM SHALL resolve Microsoft identity จาก token claim `oid`
- 4.5 THE SYSTEM SHALL ตรวจ token claim `tid` ผ่าน tenant isolation ของ Admin Microsoft provider
- 4.6 THE SYSTEM SHALL ไม่ใช้ `email` หรือ `preferred_username` เพื่อ bind Microsoft identity
- 4.7 THE SYSTEM SHALL resolve effective permissions สดจาก role assignments ของ target admin
- 4.8 THE SYSTEM SHALL resolve accessible merchants สดจาก merchant assignments ของ target admin
- 4.9 THE SYSTEM SHALL คง target admin เป็น Scoped หลัง login
- 4.10 IF Microsoft identity ไม่ตรงกับ binding ใด THEN THE SYSTEM SHALL ปฏิเสธด้วย reason `not-provisioned`
- 4.11 IF email ตรงกับ unbound invite แต่ Microsoft identity ยังไม่ถูก pre-provision THEN THE SYSTEM SHALL ปฏิเสธด้วย reason `not-provisioned`
- 4.12 THE SYSTEM SHALL ไม่ self-provision Super จาก flow Scoped pre-provision

## REQ-5: Audit และการไม่เปิดเผย identity

**User Story:** As an auditor, I want ตรวจสอบการผูก identity ได้, so that การเปลี่ยนสิทธิ์เข้าถึงมี actor และหลักฐานย้อนหลัง

**Acceptance Criteria (EARS):**

- 5.1 WHEN binding เปลี่ยนสถานะจาก unbound เป็น bound THE SYSTEM SHALL เขียน append-only audit event ใน transaction เดียวกัน
- 5.2 THE SYSTEM SHALL บันทึก acting admin ID ใน audit event
- 5.3 THE SYSTEM SHALL บันทึก target admin ID ใน audit event
- 5.4 THE SYSTEM SHALL บันทึก correlation ID ใน audit event
- 5.5 THE SYSTEM SHALL บันทึกเวลาที่เกิดเหตุการณ์เป็น UTC ใน audit event
- 5.6 THE SYSTEM SHALL ใช้ stable audit action สำหรับ Microsoft identity pre-provision
- 5.7 THE SYSTEM SHALL ไม่บันทึก ID token, access token หรือ session token ใน audit และ application log
- 5.8 THE SYSTEM SHALL ไม่ใส่ raw Entra Object ID หรือ email ใน error response
- 5.9 WHEN binding สำเร็จ THE SYSTEM SHALL แสดง `subjectBound=true` ผ่าน admin detail contract ปัจจุบัน
- 5.10 THE SYSTEM SHALL บันทึกเหตุผลการดำเนินการที่ trim แล้วใน audit event
- 5.11 THE SYSTEM SHALL บันทึก before/after binding state เป็น `subjectBound=false` และ `subjectBound=true` ใน audit event
- 5.12 THE SYSTEM SHALL บันทึก non-reversible fingerprint ที่ derive จาก canonical workforce tenant ID และ Entra Object ID เพื่อ correlation โดยไม่บันทึก raw identity
- 5.13 THE SYSTEM SHALL ไม่บันทึก raw workforce tenant ID, Entra Object ID หรือ email ใน audit event และ application log

## REQ-6: Backward compatibility และ verification

**User Story:** As a maintainer, I want เพิ่ม Entra pre-provision โดยไม่ทำให้ login เดิมเสีย, so that rollout ไม่ตัดสิทธิ์ผู้ใช้ปัจจุบัน

**Acceptance Criteria (EARS):**

- 6.1 THE SYSTEM SHALL คง Google verified-email first-login binding ไว้ตามเดิม
- 6.2 THE SYSTEM SHALL คง Microsoft email auto-binding เป็น disabled
- 6.3 THE SYSTEM SHALL คง Microsoft bootstrap allowlist behavior ของ Super เดิมไว้ตามเดิม
- 6.4 THE SYSTEM SHALL คง login ของ Microsoft admin ที่ bound อยู่แล้วให้ทำงานต่อ
- 6.5 THE SYSTEM SHALL ไม่ hardcode workforce tenant ID หรือ Entra Object ID ลง source, migration หรือ committed configuration
- 6.6 THE SYSTEM SHALL ไม่ต้องใช้ Microsoft Graph permission หรือ Graph request ใน runtime login path
- 6.7 THE SYSTEM SHALL มี automated test ยืนยัน successful binding และการคง authorization assignments
- 6.8 THE SYSTEM SHALL มี automated test ยืนยัน authentication, Super authorization และ CSRF gate
- 6.9 THE SYSTEM SHALL มี automated test ยืนยัน UUID validation และ tenant mismatch rejection
- 6.10 THE SYSTEM SHALL มี automated test ยืนยัน bound-account conflict, identity uniqueness ข้าม target และ target one-time binding ภายใต้ concurrent request
- 6.11 THE SYSTEM SHALL มี automated test ยืนยัน idempotent replay และ idempotency payload conflict
- 6.12 THE SYSTEM SHALL มี OIDC integration test ยืนยันว่า Microsoft callback ใช้ `tid` และ `oid` เพื่อ resolve pre-provisioned Scoped admin
- 6.13 THE SYSTEM SHALL มี regression test ยืนยันว่า Microsoft email ที่ตรงกับ unbound invite ยัง bind ไม่ได้
- 6.14 THE SYSTEM SHALL มี automated test ยืนยัน UUID canonicalization, persisted workforce tenant initialization และ boot failure เมื่อ Authority tenant drift
- 6.15 THE SYSTEM SHALL มี automated test ยืนยัน transactional Active Super revalidation และไม่มี partial binding/audit เมื่อ caller ถูก suspend หรือลด tier ระหว่างคำขอ
- 6.16 THE SYSTEM SHALL มี contract test ยืนยัน success body/`ETag`, exact replay precedence และ stable Problem Details code ทุก failure branch

## Success Measures

| Measure | เกณฑ์ผ่าน |
|---|---|
| Entra provisioning | External user มี tenant-local Object ID ใน workforce tenant |
| Admin binding | Admin detail เปลี่ยนจาก `subjectBound=false` เป็น `subjectBound=true` |
| Authorization preservation | tier, roles, permissions และ merchant assignments เท่าเดิมหลัง binding |
| Login | ผู้ใช้ที่รับ invitation แล้ว login ถึง Admin Console dashboard ได้ |
| Effective access | `/api/v1/admins/me` คืน Scoped tier, expected permissions และ expected merchant scope |
| Negative control | Microsoft account ที่ไม่มี binding ได้ `not-provisioned` และไม่เกิด Super account |

## Edge Cases & Open Questions

- Entra invitation redemption เป็นขั้นตอนของ Microsoft และผู้รับ invitation ต้องดำเนินการเองก่อน login ครั้งแรก
- ระบบตรวจ tenant จาก tenant-pinned Authority ที่มีอยู่แล้ว จึงไม่เพิ่ม Microsoft Graph dependency เพื่อ lookup Object ID
- Binding ของ Suspended admin ทำได้เพื่อจอง identity แต่ session ยังถูกปฏิเสธจนกว่า Super จะ reactivate
- การเปลี่ยน identity หลัง binding อยู่นอก scope หากต้องเปลี่ยนผู้ถือบัญชีให้สร้าง workflow แยกพร้อม audit และ session revocation
- การ bulk pre-provision พนักงานหลายคนอยู่นอก scope รอบนี้ endpoint รายคนต้องเสถียรก่อน

### Findings log (spec-analyze) — anchor: `ac98acf` (requirements.md uncommitted at analyze time)

ทุก finding เลือก decision A และถูกนำไปแก้ acceptance criteria โดยคง REQ IDs เดิม

| F | Category | REQ | Decision | Resolution |
|---|---|---|---|---|
| F1 | conflicting constraint | 1.8, 2.14, 3.1–3.2 | **A** | exact replay ชนะ current-state/`If-Match`; auth, authorization, CSRF และ header syntax ยังตรวจทุกครั้ง |
| F2 | unstated assumption | 1.2–1.4, 4.1, 4.5, 6.4 | **A** | persist workforce tenant ต่อ database และ fail fast เมื่อ Authority drift |
| F3 | ambiguity | 1.4–1.6, 4.5 | **A** | Authority ต้องมี tenant GUID; canonical UUID เป็น lowercase `D` |
| F4 | gap | 5.1–5.8 | **A** | บังคับ reason; audit before/after state และ identity fingerprint โดยไม่เก็บ raw identity |
| F5 | gap/concurrency | 1.1, 2.15–2.16, 6.10 | **A** | revalidate Active Super ใน transaction และครอบ race ทั้ง identity กับ target |
| F6 | gap/ambiguity | 1.10–1.15, 2.3, 3.1–3.3, 5.9 | **A** | `200` minimal body + `ETag`; RFC 9457 stable code ทุก failure branch |
