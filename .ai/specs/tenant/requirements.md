# Requirements: Tenant Provisioning (control-plane)
> Status: approved 2026-06-22
> Notes:, amended 2026-06-22

## Overview

แพลตฟอร์มเป็น payment orchestration แบบ captive multi-tenant (vPrivilege / vCommerce / vSouvenir)
ที่มีชั้น tenant isolation (RLS) ครบแล้ว แต่ `TenantId` เป็นแค่ Guid ลอยๆ ไม่มี master record.
ฟีเจอร์นี้เพิ่มเอนทิตี `Tenant` (control-plane) + flow provisioning ที่ทีมกลาง (admin) ใช้ลงทะเบียน
บริษัทใหม่ ตาม `docs/reference/payment-orchestration-modules.md` section 2.4: submit config JSON ->
validate -> เขียน `Tenant` + `PspConnection` (ต่อ PSP) + `VaultSecret` (เข้ารหัส) ใน transaction เดียว
-> status `Active`. ปลดล็อกการอ่าน config ต่อ tenant ตอน runtime และ `TenantUser.TenantId` FK ของสเปก
identity-rbac (ภายหลัง). ขอบเขตนี้ครอบเฉพาะ entity `Tenant` (องค์กร) ไม่รวม `TenantUser`/identity.

## REQ-1: Tenant master record (data model)

**User Story:** As แพลตฟอร์ม owner, I want master record ของแต่ละบริษัทผู้ใช้บริการ, so that ทุกตาราง
ที่อ้าง `TenantId` มี record ต้นทางจริง และอ่าน config ต่อ tenant ได้.

**Acceptance Criteria (EARS):**
- 1.1 THE SYSTEM SHALL เก็บ `Tenant` เป็น aggregate ในตาราง `producer.Tenants` โดย PK `Id` (uniqueidentifier) เป็นตัวแทน tenant identity
- 1.2 THE SYSTEM SHALL เก็บเป็น column: `Code`, `DisplayName`, `LegalEntityId`, `Status`, `Country`, `Currency`, `EnabledChannels`, `CreatedAtUtc`
- 1.3 THE SYSTEM SHALL เก็บ `branding` / `routing` / `session` / `timezone` / `locale` / `createdByAdmin` ใน `Metadata` (json) ไม่เป็น column แยก
- 1.4 THE SYSTEM SHALL บังคับ `Code` ไม่ซ้ำ (unique index)
- 1.5 THE SYSTEM SHALL จำกัด `Status` อยู่ในเซ็ต `{ Active }` ใน scope นี้ (provisioning เซ็ต `Active` ตรง; เพิ่ม state เมื่อมี suspend/saga — finding F1)
- 1.6 IF `Code` (หลัง normalize ตาม 1.7) ไม่อยู่ใน allowlist `{ vprivilege, vcommerce, vsouvenir }` THEN
  THE SYSTEM SHALL ปฏิเสธการสร้าง `Tenant` (HTTP 400)
- 1.7 THE SYSTEM SHALL normalize `Code` เป็น lowercase ตอนรับ input ก่อน validate / เทียบ allowlist / เขียน DB; unique index และ lookup ใช้ค่า normalized (finding F2)

## REQ-2: Admin-driven provisioning

**User Story:** As ทีมกลาง (admin), I want submit config ของบริษัทเดียวจบในครั้งเดียว, so that tenant
พร้อมใช้ทันทีโดยไม่ต้องตั้งค่าหลายขั้น.

**Acceptance Criteria (EARS):**
- 2.1 WHEN admin submit provisioning config ที่ valid THE SYSTEM SHALL สร้าง `Tenant` 1 record
- 2.2 WHEN provisioning THE SYSTEM SHALL สร้าง `PspConnection` 1 record ต่อ 1 รายการใน `pspConnections[]`
- 2.3 WHEN provisioning THE SYSTEM SHALL เก็บ secret ทุกตัวของแต่ละ connection ลง vault เป็น ciphertext
- 2.4 WHEN provisioning สำเร็จ THE SYSTEM SHALL ตั้ง `Tenant.Status = Active`
- 2.5 WHEN provisioning สำเร็จ THE SYSTEM SHALL ตอบ HTTP 201 + header `Location: /admin/tenants/{code}`
  + body (`TenantId`, รายการ `PspConnectionId`, secret ที่ mask) (finding F8)
- 2.6 THE SYSTEM SHALL ไม่รองรับ update / re-provision / suspend ใน scope นี้ (create-only)

## REQ-3: Pre-write validation

**User Story:** As ทีมกลาง, I want config ผิดถูกปฏิเสธก่อนแตะ DB, so that ไม่เกิด record ครึ่งๆ จาก
input ที่ใช้ไม่ได้.

**Acceptance Criteria (EARS):**
- 3.1 THE SYSTEM SHALL validate config ครบก่อนเขียน DB ใดๆ (ปฏิเสธก่อนเกิด side-effect)
- 3.2 IF psp code ไม่รู้จัก (`PspCodes.FromCode` ไม่ผ่าน) THEN THE SYSTEM SHALL ปฏิเสธทั้ง request (400)
- 3.3 IF `currency` ไม่ถูกรองรับใน `Iso4217` THEN THE SYSTEM SHALL ปฏิเสธทั้ง request (400)
- 3.4 THE SYSTEM SHALL เก็บ `enabledChannels` / `routing` / `branding` / `session` แบบ verbatim โดย ไม่ validate semantics ภายใน (scope นี้)
- 3.5 IF `pspConnections[]` ว่าง THEN THE SYSTEM SHALL ปฏิเสธ (400) — tenant ต้องมี PSP connection อย่างน้อย 1 (finding F5)
- 3.6 IF `pspConnections[]` มี psp code ซ้ำกัน THEN THE SYSTEM SHALL ปฏิเสธ (400) ก่อนเขียน (กันชน unique `(TenantId, Psp)`) (finding F6)
- 3.7 IF connection ใดขาด secret key ที่ "จำเป็น" ต่อ psp นั้น (ขั้นต่ำ: ทั้ง 2C2P และ Omise ต้องมี
  `secretKey`) THEN THE SYSTEM SHALL ปฏิเสธ (400); secret field อื่นที่ส่งมา (เช่น Omise `publicKey` /
  `webhookSecret`) THE SYSTEM SHALL เก็บตามที่ส่ง (store-as-provided). validation นี้อยู่หลัง Payments
  secret-envelope port ซึ่งเป็นเจ้าของ shape (finding F7; amended จาก design review S1/S7 — เดิมระบุ shape
  ที่ขัด envelope จริง)

## REQ-4: Atomicity — no partial provision

**User Story:** As security owner, I want provisioning เป็น all-or-nothing, so that ไม่มี tenant ที่มี
config แต่ไม่มี secret (หรือกลับกัน).

**Acceptance Criteria (EARS):**
- 4.1 THE SYSTEM SHALL เขียน `Tenant` + `PspConnection`(s) + `VaultSecret`(s) + ตั้ง `Active` ภายใน transaction เดียว
- 4.2 IF ขั้นใดขั้นหนึ่งใน provisioning ล้มเหลว THEN THE SYSTEM SHALL rollback ทั้งหมด (ไม่เหลือ `Tenant` / `PspConnection` / `VaultSecret` บางส่วน)
- 4.3 WHILE provisioning ยังไม่ commit THE SYSTEM SHALL ไม่เปิดเผย tenant ที่ค้างให้ runtime path เห็น
- 4.4 THE SYSTEM SHALL ดำเนินการเขียนทั้งหมดของ provisioning (`Tenant` + `PspConnection` + `VaultSecret` + audit) บน DbContext/connection เดียว (`pol_admin`-scoped) เพราะ atomic transaction ข้าม connection ไม่ได้ (finding F4)

## REQ-5: Idempotency by tenant code

**User Story:** As ทีมกลาง, I want กดสร้างซ้ำแล้วไม่พัง, so that ระบบไม่สร้าง tenant ซ้ำหรือทับของเดิม.

**Acceptance Criteria (EARS):**
- 5.1 THE SYSTEM SHALL ใช้ `Tenant.Code` (unique index) เป็น idempotency key ของ provisioning
- 5.2 IF submit ด้วย `Code` ที่ provision แล้ว THEN THE SYSTEM SHALL ตอบ 409 Conflict โดยไม่ overwrite record เดิม
- 5.3 IF สอง request `Code` เดียวกันเข้ามาพร้อมกัน (race) THEN THE SYSTEM SHALL ให้สำเร็จได้เพียง 1 และ อีก request ได้ 409 (unique index เป็น backstop)

## REQ-6: Secret confidentiality

**User Story:** As security owner, I want secret ไม่รั่วทุกเส้นทาง, so that credential จ่ายเงินของ
ทุก tenant ปลอดภัย.

**Acceptance Criteria (EARS):**
- 6.1 THE SYSTEM SHALL เก็บ secret เป็น ciphertext ใน vault เท่านั้น (ไม่เก็บ plaintext ใน `Tenants` / `PspConnections`)
- 6.2 WHEN API ตอบกลับข้อมูลที่มี secret THE SYSTEM SHALL mask เสมอ (เช่น `****hint`) ไม่คืน plaintext
- 6.3 THE SYSTEM SHALL ไม่ log ค่า secret หรือ provisioning payload ที่มี secret
- 6.4 IF เกิด error ระหว่าง provisioning THEN THE SYSTEM SHALL ไม่ใส่ secret ลงใน error response/detail
- 6.5 WHEN provisioning THE SYSTEM SHALL คำนวณ masked hint จาก input ใน memory แล้วเก็บ hint (last-4 ต่อ secret field) ไว้กับ `PspConnection` (non-secret data); WHEN read-back (REQ-9) THE SYSTEM SHALL อ่าน hint จาก `PspConnection` ไม่อ่าน vault — `pol_admin` ได้สิทธิ์ INSERT-only บน `VaultSecrets` (write-only, ไม่มี SELECT) (finding F3; amended จาก design review B3 — เดิมจะ `MaskedAsync` ซึ่ง materialize ciphertext column ที่ pol_admin ไม่ได้ grant)

## REQ-7: Admin-only authorization

**User Story:** As security owner, I want เฉพาะทีมกลางเรียก provisioning ได้, so that tenant SPA แตะ
control-plane ไม่ได้.

**Acceptance Criteria (EARS):**
- 7.1 THE SYSTEM SHALL จำกัด endpoint provisioning + read-back ให้เฉพาะ principal ที่มี role `admin`
- 7.2 IF request มาด้วย token role `tenant` THEN THE SYSTEM SHALL ตอบ 403
- 7.3 IF request ไม่มี auth THEN THE SYSTEM SHALL ตอบ 401
- 7.4 THE SYSTEM SHALL ไม่ทำ provisioning command เป็น `ITenantScoped` (เป็น cross-tenant ไม่ผูก tenant context)
- 7.5 THE SYSTEM SHALL อาศัย Google OAuth audience ของ role `admin` ที่ถูก config ไว้ (prerequisite: เพิ่ม admin client ใน `Google:Audiences` + appsettings — เป็น task ใน design/tasks) (finding F9)

## REQ-8: RLS isolation for the Tenants table

**User Story:** As security owner, I want `Tenants` ถูกคุมที่ data layer, so that tenant อ่านแถวของ
tenant อื่นไม่ได้แม้ app เผลอ.

**Acceptance Criteria (EARS):**
- 8.1 THE SYSTEM SHALL ใส่ RLS FILTER + BLOCK predicate บน `producer.Tenants` ด้วย `producer.fn_tenant_predicate(Id)`
- 8.2 WHILE connection ผูก `SESSION_CONTEXT('TenantId') = T` THE SYSTEM SHALL ให้อ่าน `Tenants` ได้ เฉพาะแถวที่ `Id = T`
- 8.3 IF tenant principal (`pol_app`) พยายาม INSERT/UPDATE `Tenants` ด้วย `Id` ที่ไม่ตรง session THEN
  THE SYSTEM SHALL block การเขียนนั้น
- 8.4 WHERE principal เป็นสมาชิก `pol_rls_bypass` (`pol_admin`) THE SYSTEM SHALL อนุญาตอ่าน/เขียน cross-tenant
- 8.5 THE SYSTEM SHALL รัน provisioning ใต้ `pol_admin` connection (แยกจาก `pol_app`)

## REQ-9: Masked read-back

**User Story:** As ทีมกลาง, I want ดู config ของ tenant ที่ provision แล้ว, so that ตรวจสอบได้โดยไม่
เห็น secret ดิบ.

**Acceptance Criteria (EARS):**
- 9.1 WHEN admin เรียก `GET /admin/tenants/{code}` THE SYSTEM SHALL คืน config + secret แบบ mask (lookup ด้วย code ที่ normalize ตาม REQ-1.7)
- 9.2 IF ไม่พบ tenant ตาม `code` THEN THE SYSTEM SHALL ตอบ 404
- 9.3 THE SYSTEM SHALL ไม่คืน plaintext secret ใน read-back ทุกกรณี

## REQ-10: Runtime per-tenant config read

**User Story:** As tenant runtime path, I want อ่าน config ของ tenant ตัวเอง, so that adapter/method
router ใช้ branding/routing/session ได้.

**Acceptance Criteria (EARS):**
- 10.1 WHEN runtime path (tenant-scoped) อ่าน config ของ tenant ตัวเอง THE SYSTEM SHALL คืนแถว `Tenant` ของ tenant นั้น
- 10.2 THE SYSTEM SHALL ไม่ให้ tenant หนึ่งอ่านแถว `Tenant` ของ tenant อื่น (บังคับด้วย RLS ตาม REQ-8)

## REQ-11: Provisioning audit (bypass accountability)

**User Story:** As security/compliance owner, I want บันทึกทุกครั้งที่ provisioning ใช้ `pol_admin`
bypass, so that ตรวจย้อนได้ว่าใครสร้าง tenant ใด เมื่อไร.

**Acceptance Criteria (EARS):**
- 11.1 WHEN provisioning สำเร็จ THE SYSTEM SHALL เขียน audit row 1 แถว (admin identity จาก principal, tenant `Code`, `TenantId`, timestamp UTC, correlation id)
- 11.2 THE SYSTEM SHALL เขียน audit row ใน transaction เดียวกับ provisioning (REQ-4.1) เพื่อ commit/ rollback พร้อมกัน
- 11.3 THE SYSTEM SHALL ไม่ใส่ secret ใดๆ ลงใน audit row

## Edge Cases & Open Questions

### /spec-analyze findings log — anchor `9bdfc52` (HEAD; requirements.md ยัง untracked) · 2026-06-22

ทั้ง 11 finding เลือก option **a** (ไม่มี finding ใด dismissed):
- F1 [REQ-1.5] status -> `{ Active }` (YAGNI; เพิ่ม state เมื่อมี suspend/saga)
- F2 [REQ-1.6/1.7/9.1] Code normalize lowercase ก่อน validate/lookup
- F3 [REQ-6.5] masked hint: provisioning คำนวณจาก input; read-back อ่าน `Hint` column
- F4 [REQ-4.4] provisioning เขียนบน `pol_admin`-scoped DbContext เดียว (atomicity)
- F5 [REQ-3.5] `pspConnections[]` ว่าง -> 400
- F6 [REQ-3.6] psp ซ้ำใน submit -> 400 (pre-validate)
- F7 [REQ-3.7] ขาด required secret ต่อ psp -> 400
- F8 [REQ-2.5] success -> 201 + `Location`
- F9 [REQ-7.5] admin OAuth audience = prerequisite (config task)
- F10 [REQ-2.4] maker-checker ใช้กับ `TenantUser` (2.5) เท่านั้น; provisioning activate ทันที (REQ-2.4 คงเดิม)
- F11 [REQ-11] provisioning audit row (REQ ใหม่)

**Amended 2026-06-22 (design-review spec-architect):** REQ-3.7 (secret shape ขัด envelope จริง -> secretKey
required + store-extras, validate ผ่าน Payments port) และ REQ-6.5 (masked read-back: เก็บ hint ที่
`PspConnection` ไม่อ่าน vault; `pol_admin` INSERT-only บน VaultSecrets). รายละเอียดเต็มใน design.md ส่วน
"Design Review Resolution".

### Standing notes (design-level — แก้ตอน /spec-design)

- **ADR single-tx vs saga (`SECURITY_RULES.md:190`):** ยืนยัน single-tx (REQ-4.1); valid เฉพาะตอน vault
  DB-backed (`LocalEnvelopeVaultStore` ใช้ `ProducerDbContext` เดียวกัน); vault -> external KMS/HSM =
  trigger กลับ saga. **ต้องเขียน ADR ก่อน implement** (task)
- **VaultSecret granularity:** 1 envelope ต่อ connection (`PspSecretEnvelope`) ตามของจริง ไม่ใช่ 1 row
  ต่อ field (doc 2.4)
- **`PspConnection.Metadata` ขนาด:** 4000 chars อาจไม่พอ non-secret config Omise -> พิจารณา `nvarchar(max)`
- **non-secret PSP config:** เก็บใน `PspConnection.Metadata` json (merchantId/accountId/webhookPath/
  returnUrls/card/installment/enabledSources)
- **Cross-module seam:** `Tenant.Application` -> `Payments.Application` (เพิ่ม `IPspConnectionRepository.Add`);
  Tenant อยู่นอก peer-ban `Modules[]` (Architecture.Tests)
- **audit table RLS:** ตาราง provisioning-audit เป็น control-plane (admin-written) ไม่อยู่ใต้ tenant
  predicate — ตัดสิน policy ตอน design
- **Down migration:** revoke grants ที่เพิ่มให้ `pol_admin` + drop policy clause ของ `Tenants`
