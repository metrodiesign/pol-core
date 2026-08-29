# Requirements: Microsoft Workforce JIT Provisioning

> Status: approved 2026-08-22
> Notes:, amended 2026-08-22

## Overview

ฟีเจอร์นี้เปลี่ยน Admin Console ให้รับ Microsoft Entra Workforce เป็น identity provider เดียว
และสร้างบัญชีพนักงานที่ผ่านนโยบายแบบ Just-In-Time โดยเริ่มจากสิทธิ์ต่ำสุด

เป้าหมายคือให้พนักงาน `@viriyah.co.th` ที่องค์กร assign App Role แล้ว login ได้ทันที
โดยยังแยก authentication ออกจาก RBAC และไม่เปลี่ยน Merchant authentication

## Scope และการ supersede

ข้อกำหนดนี้แทนที่เฉพาะพฤติกรรมที่ขัดกันใน spec `entra-scoped-preprovision`
ส่วน Microsoft pre-provision endpoint และ invariant อื่นของ spec เดิมยังคงอยู่

| พฤติกรรมเดิม | พฤติกรรมใหม่ | ผลต่อ spec เดิม |
|---|---|---|
| Microsoft identity ที่ยังไม่ bound ได้ `not-provisioned` | identity ที่ผ่าน workforce policy ถูก JIT provision | Supersede REQ-4.10 และ REQ-4.11 เฉพาะ eligible identity |
| Google verified-email first-login binding ยังทำงาน | Admin Google authentication ปิดทั้งหมด | Supersede REQ-6.1 |
| Microsoft bootstrap allowlist สร้าง Super ได้ | Microsoft callback ไม่ใช้ bootstrap allowlist | Supersede REQ-6.3 |
| Microsoft identity ที่ bound แล้ว login ได้ | ยัง login ได้เมื่อผ่าน workforce policy และ Active | Preserve REQ-6.4 พร้อมเพิ่ม eligibility gate |
| Super pre-provision Microsoft identity ได้ | endpoint และ wire contract เดิมยังอยู่ | Preserve |

## REQ-1: Microsoft-only Admin authentication

**User Story:** As a platform employee, I want Admin Console ใช้ Microsoft Entra เพียง provider เดียว, so that identity policy อยู่ภายใต้ workforce tenant ขององค์กร

**Acceptance Criteria (EARS):**

- 1.1 WHEN client เรียก `GET /api/v1/admins/auth/microsoft/login` THE SYSTEM SHALL เริ่ม Microsoft OIDC login ตาม contract ปัจจุบัน
- 1.2 THE SYSTEM SHALL คง callback URL `/api/v1/admins/auth/microsoft/callback`
- 1.3 WHEN client เรียก Admin Google login endpoint THE SYSTEM SHALL ตอบ `404`
- 1.4 WHEN client เรียก Admin Google callback endpoint THE SYSTEM SHALL ตอบ `404`
- 1.5 THE SYSTEM SHALL ไม่ register Google OIDC handler สำหรับ Admin Console
- 1.6 THE SYSTEM SHALL ไม่เรียก Google invite-binding จาก Admin authentication path
- 1.7 THE SYSTEM SHALL ไม่เรียก bootstrap allowlist จาก Microsoft Admin callback
- 1.8 THE SYSTEM SHALL คง Google และ Microsoft authentication ของ Merchant Console ตามเดิม

## REQ-2: Workforce eligibility gate

**User Story:** As a security owner, I want รับเฉพาะ Entra identity ที่ tenant, App Role และ email domain ถูกต้อง, so that บัญชีภายนอกสร้าง Admin session ไม่ได้

**Acceptance Criteria (EARS):**

- 2.1 THE SYSTEM SHALL ตรวจ signature, issuer, audience และ lifetime ของ Microsoft OIDC token ตาม validation ปัจจุบัน
- 2.2 WHEN Microsoft OIDC token ผ่าน token validation THE SYSTEM SHALL ตรวจ workforce eligibility ก่อน resolve หรือสร้าง Admin user
- 2.3 THE SYSTEM SHALL บังคับให้ token มี claim `tid` เพียงค่าเดียว
- 2.4 THE SYSTEM SHALL บังคับให้ claim `tid` ตรงกับ configured workforce tenant
- 2.5 THE SYSTEM SHALL บังคับให้ token มี claim `oid` เพียงค่าเดียว
- 2.6 THE SYSTEM SHALL บังคับให้ claim `oid` เป็น UUID ที่ถูกต้อง
- 2.7 THE SYSTEM SHALL บังคับให้ claim `roles` มีค่า exact `vcp.employee`
- 2.8 THE SYSTEM SHALL เปรียบเทียบ App Role `vcp.employee` แบบ case-sensitive
- 2.9 WHEN token มี claim `email` เพียงค่าเดียว THE SYSTEM SHALL ใช้ค่านั้นตรวจ domain และใช้เป็นข้อมูลแสดงผล
- 2.10 WHEN token ไม่มี claim `email` THE SYSTEM SHALL fallback ไปใช้ claim `preferred_username` เพียงค่าเดียว
- 2.11 IF token มี claim `email` มากกว่าหนึ่งค่า THEN THE SYSTEM SHALL ปฏิเสธ workforce eligibility
- 2.12 IF token ไม่มี `email` และมี `preferred_username` มากกว่าหนึ่งค่า THEN THE SYSTEM SHALL ปฏิเสธ workforce eligibility
- 2.13 IF selected email identifier ไม่มี local part หนึ่งค่าและ domain หนึ่งค่าคั่นด้วย `@` หนึ่งตัว THEN THE SYSTEM SHALL ปฏิเสธ workforce eligibility
- 2.14 THE SYSTEM SHALL เปรียบเทียบ selected email domain กับ `viriyah.co.th` แบบ exact และ case-insensitive
- 2.15 IF selected email domain เป็น subdomain ของ `viriyah.co.th` THEN THE SYSTEM SHALL ปฏิเสธ workforce eligibility
- 2.16 IF claim `email` มีอยู่แต่ไม่ผ่าน domain policy THEN THE SYSTEM SHALL ไม่ fallback ไปใช้ `preferred_username`
- 2.17 IF token ขาด claim ที่บังคับหรือมี scalar claim กำกวม THEN THE SYSTEM SHALL redirect ด้วย reason `workforce-access-denied`
- 2.18 IF `tid`, App Role หรือ email domain ไม่ผ่าน policy THEN THE SYSTEM SHALL redirect ด้วย reason `workforce-access-denied`
- 2.19 IF workforce eligibility ไม่ผ่าน THEN THE SYSTEM SHALL ไม่สร้าง Admin user
- 2.20 IF workforce eligibility ไม่ผ่าน THEN THE SYSTEM SHALL ไม่สร้าง Admin session
- 2.21 WHERE Entra identity เป็น Guest THE SYSTEM SHALL ใช้ eligibility policy เดียวกับ Member
- 2.22 THE SYSTEM SHALL กำหนด `viriyah.co.th` และ `vcp.employee` เป็น code-level invariants ที่ไม่มี configuration override
- 2.23 THE SYSTEM SHALL ไม่เรียก Microsoft Graph ใน runtime login path
- 2.24 THE SYSTEM SHALL normalize valid `tid` และ `oid` เป็น UUID lowercase รูปแบบ `D` ก่อนเปรียบเทียบ, identity lookup หรือ persistence
- 2.25 IF Microsoft callback ถูกปฏิเสธเพราะ issuer หรือ tenant อยู่นอก workforce tenant ที่กำหนด THEN THE SYSTEM SHALL redirect ด้วย reason `workforce-access-denied`
- 2.26 IF Microsoft callback ถูกปฏิเสธเพราะ signature, audience, nonce, lifetime, state หรือ code exchange validation THEN THE SYSTEM SHALL redirect ด้วย reason `auth-failed`

## REQ-3: Stable identity resolution

**User Story:** As an employee, I want บัญชี local ติดตาม Entra Object ID ที่คงที่, so that การเปลี่ยนอีเมลไม่สร้างตัวตนใหม่หรือสลับเจ้าของบัญชี

**Acceptance Criteria (EARS):**

- 3.1 THE SYSTEM SHALL ใช้คู่ `(provider=microsoft, subject=oid)` เป็น identity key
- 3.2 THE SYSTEM SHALL ไม่ใช้ `email` หรือ `preferred_username` เพื่อ bind Microsoft identity
- 3.3 WHEN identity key ตรงกับ Admin user เดิม THE SYSTEM SHALL resolve Admin user เดิม
- 3.4 WHEN identity key ตรงกับ Active Admin user เดิม THE SYSTEM SHALL คง Tier เดิม
- 3.5 WHEN identity key ตรงกับ Active Admin user เดิม THE SYSTEM SHALL คง role assignments เดิม
- 3.6 WHEN identity key ตรงกับ Active Admin user เดิม THE SYSTEM SHALL คง merchant assignments เดิม
- 3.7 WHEN identity key ตรงกับ Active Admin user เดิม THE SYSTEM SHALL อ่าน effective permissions จากสถานะปัจจุบัน
- 3.8 WHEN identity key ตรงกับ Active Admin user เดิม THE SYSTEM SHALL อ่าน accessible merchants จากสถานะปัจจุบัน
- 3.9 IF identity key ตรงกับ Suspended Admin user THEN THE SYSTEM SHALL ปฏิเสธการสร้าง session
- 3.10 IF identity key ตรงกับ Suspended Admin user THEN THE SYSTEM SHALL ไม่สร้าง JIT user ใหม่
- 3.11 IF identity เดิมไม่ผ่าน workforce eligibility ปัจจุบัน THEN THE SYSTEM SHALL ปฏิเสธการสร้าง session
- 3.12 THE SYSTEM SHALL เก็บ Admin records เดิมที่มี non-corporate email ไว้เพื่อ audit

## REQ-4: Atomic JIT provisioning

**User Story:** As an eligible first-time employee, I want ระบบสร้างบัญชีขั้นต่ำให้อัตโนมัติ, so that เข้า Admin Console ได้โดยไม่รอ pre-provision รายคน

**Acceptance Criteria (EARS):**

- 4.1 WHEN eligible identity ยังไม่มี identity binding และ selected email identifier ไม่ชนกับ Admin record อื่น THE SYSTEM SHALL สร้าง Admin user ใหม่
- 4.2 WHEN JIT สร้าง Admin user THE SYSTEM SHALL กำหนด Status เป็น `Active`
- 4.3 WHEN JIT สร้าง Admin user THE SYSTEM SHALL กำหนด Tier เป็น `Scoped`
- 4.4 WHEN JIT สร้าง Admin user THE SYSTEM SHALL bind provider `microsoft` กับ canonical `oid`
- 4.5 WHEN JIT สร้าง Admin user THE SYSTEM SHALL ใช้ selected email identifier เป็นข้อมูล email/display ของ user
- 4.6 WHEN JIT สร้าง Admin user THE SYSTEM SHALL ไม่ assign Role ใด
- 4.7 WHEN JIT สร้าง Admin user THE SYSTEM SHALL ไม่ assign merchant ใด
- 4.8 WHEN JIT สร้าง Admin user THE SYSTEM SHALL ไม่ให้ permission โดยนัยจาก Tier
- 4.9 THE SYSTEM SHALL commit user, identity binding และ JIT audit ใน transaction เดียวกัน
- 4.10 WHEN JIT transaction commit สำเร็จ THE SYSTEM SHALL สร้าง Admin session ให้ user ที่สร้างแล้ว
- 4.11 IF JIT transaction ล้มเหลว THEN THE SYSTEM SHALL ไม่เหลือ Admin user บางส่วน
- 4.12 IF JIT transaction ล้มเหลว THEN THE SYSTEM SHALL ไม่สร้าง Admin session

## REQ-5: Conflict และ concurrency safety

**User Story:** As a security owner, I want first-login race และ email collision fail closed, so that identity หนึ่งไม่สร้างหลายบัญชีหรือ bind ผิดคน

**Acceptance Criteria (EARS):**

- 5.1 THE SYSTEM SHALL serialize JIT identity mutation ผ่าน identity mutation lock ปัจจุบัน
- 5.2 THE SYSTEM SHALL รักษา unique constraint ของคู่ provider และ subject ปัจจุบัน
- 5.3 THE SYSTEM SHALL รักษา unique constraint ของ Admin email ปัจจุบัน
- 5.4 IF callback หลายรายการ JIT identity key เดียวกันพร้อมกัน THEN THE SYSTEM SHALL สร้าง Admin user ได้ไม่เกินหนึ่งรายการ
- 5.5 IF callback หลายรายการ JIT identity key เดียวกันพร้อมกัน THEN THE SYSTEM SHALL เขียน `jit-provision` audit ได้ไม่เกินหนึ่งรายการ
- 5.6 WHEN concurrent callback ทำงานหลัง JIT winner commit THE SYSTEM SHALL resolve Admin user รายการเดียวกัน
- 5.7 IF selected email identifier ชนกับ Admin record อื่นก่อน JIT THEN THE SYSTEM SHALL redirect ด้วย reason `identity-conflict`
- 5.8 IF selected email identifier ชนกับ Admin record อื่นก่อน JIT THEN THE SYSTEM SHALL ไม่ bind identity ด้วย email
- 5.9 IF selected email identifier ชนกับ Admin record อื่นก่อน JIT THEN THE SYSTEM SHALL ไม่สร้าง Admin user
- 5.10 IF selected email identifier ชนกับ Admin record อื่นก่อน JIT THEN THE SYSTEM SHALL ไม่สร้าง Admin session
- 5.11 IF race ทำให้ unique constraint conflict หลัง eligibility ผ่าน THEN THE SYSTEM SHALL re-resolve identity แบบ fail closed
- 5.12 IF conflict ยังไม่สามารถ resolve เป็น identity key เดิมได้ THEN THE SYSTEM SHALL redirect ด้วย reason `identity-conflict`
- 5.13 THE SYSTEM SHALL คง Microsoft pre-provision endpoint ปัจจุบันไว้สำหรับ operator แก้ identity conflict

## REQ-6: Session และ least-privilege authorization

**User Story:** As a newly provisioned employee, I want login สำเร็จด้วยสิทธิ์ขั้นต่ำ, so that Admin กำหนด Role ภายหลังตามหน้าที่จริง

**Acceptance Criteria (EARS):**

- 6.1 WHEN JIT user login สำเร็จ THE SYSTEM SHALL รักษา Admin session แม้ effective permissions เป็นค่าว่าง
- 6.2 WHEN JIT user เรียก `GET /api/v1/admins/me` THE SYSTEM SHALL ตอบ success ตาม wire contract ปัจจุบัน
- 6.3 WHEN JIT user ยังไม่มี Active Role THE SYSTEM SHALL คืน `permissions=[]` จาก `GET /api/v1/admins/me`
- 6.4 WHEN JIT user ยังไม่มี merchant assignment THE SYSTEM SHALL คืน accessible merchant set ว่างจาก `GET /api/v1/admins/me`
- 6.5 WHEN `/api/v1/admins/me` สำเร็จและ `permissions=[]` THE SYSTEM SHALL แสดงหน้า `403` ปัจจุบันใน Admin Console
- 6.6 WHILE JIT user ไม่มี effective permission THE SYSTEM SHALL ไม่แสดง protected Admin content
- 6.7 WHEN Admin assign Active Role ให้ user THE SYSTEM SHALL ใช้ role assignment API ปัจจุบัน
- 6.8 WHEN user refresh หลัง Role assignment THE SYSTEM SHALL resolve effective permissions ใหม่
- 6.9 WHEN refreshed effective permissions ไม่ว่าง THE SYSTEM SHALL ออกจาก zero-permission `403` state ใน Admin Console
- 6.10 IF session เรียก endpoint โดยขาด permission ที่กำหนด THEN THE SYSTEM SHALL ตอบ `403` ตาม RBAC contract ปัจจุบัน

## REQ-7: Audit และ privacy

**User Story:** As an auditor, I want เห็นเหตุการณ์ JIT โดยไม่เก็บ external identity หรือ PII เกินจำเป็น, so that ตรวจย้อนหลังได้โดยลดข้อมูลอ่อนไหวใน audit trail

**Acceptance Criteria (EARS):**

- 7.1 WHEN JIT provision สำเร็จ THE SYSTEM SHALL append audit action `jit-provision`
- 7.2 WHEN JIT provision สำเร็จ THE SYSTEM SHALL อ้าง target ด้วย internal Admin ID ใน audit
- 7.3 THE SYSTEM SHALL ไม่บันทึก raw `oid` ใน JIT audit
- 7.4 THE SYSTEM SHALL ไม่บันทึก raw `tid` ใน JIT audit
- 7.5 THE SYSTEM SHALL ไม่บันทึก email หรือ `preferred_username` ใน JIT audit
- 7.6 THE SYSTEM SHALL ไม่บันทึก ID token, access token หรือ session token ใน JIT audit
- 7.7 THE SYSTEM SHALL ไม่บันทึก ID token, access token หรือ session token ใน application log
- 7.8 IF workforce eligibility ไม่ผ่าน THEN THE SYSTEM SHALL ไม่เปิดเผย claim ที่ไม่ผ่านใน browser error reason
- 7.9 IF identity conflict เกิดขึ้น THEN THE SYSTEM SHALL ไม่เปิดเผย identity ของ record ที่ชนใน browser error reason
- 7.10 WHEN JIT audit ถูกเขียน THE SYSTEM SHALL ใช้ created Admin internal ID เป็นทั้ง actor และ target

## REQ-8: Admin Console experience

**User Story:** As an employee, I want หน้า login และ error สื่อ Microsoft workforce flow ตรงจริง, so that เลือกบัญชีและแก้ปัญหาได้ถูกทาง

**Acceptance Criteria (EARS):**

- 8.1 THE SYSTEM SHALL แสดงปุ่ม Microsoft เพียงปุ่มเดียวในการ์ดพนักงานของ Admin Console
- 8.2 THE SYSTEM SHALL ไม่แสดงปุ่ม Google ในการ์ดพนักงานของ Admin Console
- 8.3 THE SYSTEM SHALL คงปุ่มและพฤติกรรม Merchant login ตามเดิม
- 8.4 THE SYSTEM SHALL ไม่มี Admin Google login helper ที่เรียกใช้งานได้
- 8.5 WHEN URL มี reason `workforce-access-denied` THE SYSTEM SHALL แสดงข้อความว่าบัญชีไม่ผ่านนโยบายพนักงาน
- 8.6 WHEN URL มี reason `identity-conflict` THE SYSTEM SHALL แสดงข้อความให้ติดต่อผู้ดูแลเพื่อแก้ identity binding
- 8.7 WHEN URL มี legacy auth reason THE SYSTEM SHALL ใช้ข้อความ provider-neutral
- 8.8 WHEN user กลับไปหน้า login หลัง auth error THE SYSTEM SHALL ให้เริ่ม Microsoft login ใหม่ได้

## REQ-9: Production configuration safety

**User Story:** As an operator, I want production fail fast เมื่อ workforce authentication ตั้งค่าไม่ครบ, so that ระบบไม่เปิดด้วย provider หรือ policy ที่ผิด

**Acceptance Criteria (EARS):**

- 9.1 WHILE environment เป็น Production THE SYSTEM SHALL บังคับ Microsoft Admin provider ให้ enabled
- 9.2 WHILE environment เป็น Production THE SYSTEM SHALL บังคับให้ Microsoft Admin `Authority`, `ClientId`, `ClientSecret` และ `CallbackPath` ไม่ว่าง
- 9.3 WHILE environment เป็น Production THE SYSTEM SHALL บังคับ Microsoft Authority ให้ pin กับ workforce tenant UUID
- 9.4 IF Production Microsoft Admin provider ตั้งค่าไม่ครบ THEN THE SYSTEM SHALL fail fast ก่อนรับ request
- 9.5 IF Production configuration เปิด Admin Google provider THEN THE SYSTEM SHALL fail fast ก่อนรับ request
- 9.6 THE SYSTEM SHALL ไม่เพิ่ม configuration key สำหรับเปลี่ยน workforce email domain
- 9.7 THE SYSTEM SHALL ไม่เพิ่ม configuration key สำหรับเปลี่ยน workforce App Role

## REQ-10: Contract และ regression boundaries

**User Story:** As a maintainer, I want JIT เป็นการเปลี่ยน auth behavior ขนาดเล็ก, so that API และ persistence contract อื่นไม่ regress

**Acceptance Criteria (EARS):**

- 10.1 THE SYSTEM SHALL คง wire shape ของ `GET /api/v1/admins/me`
- 10.2 THE SYSTEM SHALL คง wire shape ของ Admin role APIs
- 10.3 THE SYSTEM SHALL คง wire shape ของ Microsoft pre-provision API
- 10.4 THE SYSTEM SHALL ไม่เพิ่ม REST endpoint สำหรับ JIT provisioning
- 10.5 THE SYSTEM SHALL ไม่เพิ่ม database table สำหรับ JIT provisioning
- 10.6 THE SYSTEM SHALL ไม่เพิ่ม database migration สำหรับ JIT provisioning
- 10.7 THE SYSTEM SHALL ไม่เพิ่ม runtime dependency สำหรับ Microsoft Graph
- 10.8 THE SYSTEM SHALL ไม่เปลี่ยน Merchant user provisioning, approval หรือ authentication behavior
- 10.9 THE SYSTEM SHALL รักษา logout และ session-management contracts ปัจจุบัน

## External Entra prerequisites

สิ่งต่อไปนี้เป็น deployment prerequisites นอก runtime ของ `pol-core`

| รายการ | ค่าที่ต้องใช้ | เกณฑ์ยืนยัน |
|---|---|---|
| App Role | Value `vcp.employee` | ID token มี `roles` ที่รวมค่านี้ |
| Enterprise App | `Assignment required = Yes` | ผู้ไม่ได้ assign เข้า app ไม่ได้ |
| Employee access | security group แบบ direct membership | สมาชิกที่ assign ได้ App Role ใน token |
| Conditional Access | policy ขององค์กร | MFA และเงื่อนไขการเข้าใช้ถูก Entra บังคับ |
| Workforce tenant | tenant UUID เดียวกับ Admin Microsoft Authority | token `tid` ตรง configured tenant |

## Rollout requirements

1. ตั้ง App Role, Assignment required, employee group และ Conditional Access บน staging
2. ใช้ Super ปัจจุบันปรับ `supachaip@viriyah.co.th` เป็น `Tier.Super` และเพิ่ม `platform_admin` โดยรักษา Role เดิม
3. ยืนยัน corporate Super login Microsoft ได้และ token มี `vcp.employee`
4. Deploy `pol-admin` ก่อน `pol-core` ภายใน maintenance window
5. ใช้ session-management APIs เดิม enumerate และ revoke Admin sessions ทุกบัญชี โดย revoke session ผู้ดำเนินการท้ายสุด
6. Login ใหม่ด้วย corporate Super แล้วทดสอบ JIT user ใหม่จาก login ถึง zero-permission `403`
7. Assign Role ให้ JIT user แล้ว refresh เพื่อยืนยัน effective permissions ใหม่
8. เก็บ non-domain Admin records เดิมไว้และยืนยันว่า login ใหม่สร้าง session ไม่ได้

## Verification matrix

| กลุ่ม | Scenario ขั้นต่ำ | ครอบคลุม |
|---|---|---|
| OIDC policy | tenant, role และ domain ผ่าน/ไม่ผ่าน | REQ-2.1-2.23 |
| Claim parsing | mixed case domain, subdomain, missing และ duplicate scalar claims | REQ-2.3-2.26 |
| Guest | assigned Guest ที่ tenant, role และ domain ผ่าน | REQ-2.21 |
| JIT | Active Scoped, no Role, no merchant, session สำเร็จ | REQ-4.1-4.10, REQ-6.1-6.5 |
| Race | concurrent first-login สร้าง user/audit ครั้งเดียว | REQ-5.1-5.6 |
| Existing identity | authorization state คงเดิมและ refresh เป็นค่าปัจจุบัน | REQ-3.3-3.8 |
| Denial | Suspended, Hotmail, onmicrosoft และ identity collision ไม่มี partial write/session | REQ-2.18-2.20, REQ-3.9-3.12, REQ-5.7-5.12 |
| Privacy | audit ไม่มี external identity, email หรือ token | REQ-7.1-7.10 |
| Provider regression | Admin Google `404`; Merchant login ไม่เปลี่ยน | REQ-1.3-1.8, REQ-10.8 |
| Admin UI | Microsoft-only employee card, zero-permission `403`, Role refresh | REQ-6.5-6.9, REQ-8.1-8.8 |
| Production guard | incomplete Microsoft หรือ enabled Google ทำให้ boot fail | REQ-9.1-9.7 |

### Automated gates

```bash
dotnet test pol-core.slnx --filter "Category!=Integration"
dotnet test pol-core.slnx --filter "Category=Integration"
```

```bash
npm test
npm run typecheck
npm run lint
npm run build
```

### Browser verification

1. Login ด้วย eligible Microsoft identity ใหม่
2. ยืนยัน JIT user ได้ session และเห็น zero-permission `403`
3. Assign Active Role ด้วย Admin เดิม
4. Refresh browser และยืนยันหน้าที่ permission อนุญาตใช้งานได้
5. Login ด้วย Hotmail หรือ onmicrosoft identity และยืนยันว่าไม่มี user/session ใหม่

## Success measures

| Measure | เกณฑ์ผ่าน |
|---|---|
| Eligible first login | corporate identity ที่มี `vcp.employee` ได้ Active Scoped account และ session |
| Least privilege | JIT account เริ่มด้วย zero Role, zero merchant และ zero effective permission |
| Authorization activation | Role assignment มีผลหลัง refresh โดยไม่ต้อง provision identity ซ้ำ |
| External denial | non-corporate identity ไม่สร้าง user หรือ session |
| Identity integrity | concurrent first-login ได้ local user และ JIT audit อย่างละหนึ่ง |
| Provider boundary | Admin มี Microsoft เท่านั้นและ Merchant auth ไม่ regress |
| Rollback safety | rollback ใช้ image เดิมได้โดยไม่มี schema rollback |

## Implementation constraints

| Constraint | Decision |
|---|---|
| Identity key | ใช้ provider `microsoft` กับ canonical `oid` เท่านั้น |
| Concurrency | reuse identity mutation lock และ unique indexes เดิม |
| Persistence | reuse Admin user, identity, role, merchant และ audit models เดิม |
| API | ไม่มี endpoint ใหม่ |
| Database | ไม่มี table หรือ migration ใหม่ |
| External API | ไม่มี Microsoft Graph call หรือ SDK ใหม่ |
| Policy constants | `viriyah.co.th` และ `vcp.employee` อยู่ใน code |

## Edge cases & open questions

ไม่มี open question ที่ขวาง design; decision ต่อไปนี้ถูกล็อกจาก intake และ spec-analyze

- `email` มี precedence เหนือ `preferred_username`; fallback ใช้เมื่อ `email` ไม่มีเท่านั้น
- Scalar security claim ที่ซ้ำถูกถือว่า ambiguous แม้ค่าซ้ำกันเหมือนกัน
- `roles` เป็น multi-value claim ได้; ต้องมี exact value `vcp.employee`
- Guest ไม่ถูกปฏิเสธจาก guest status เพียงอย่างเดียว
- Existing bound identity ยังต้องผ่าน tenant, role และ domain policy ทุก login
- Email collision ไม่ auto-bind และไม่แก้ด้วย JIT
- JIT user มี session ได้แม้ไม่มี permission; frontend เป็นผู้แสดง zero-permission `403`
- Role assignment ถูกอ่านใหม่จาก backend เมื่อ refresh
- Rollback ไม่ลบ JIT accounts เพราะบัญชีเหล่านี้ไม่มี Role หรือ merchant assignment โดย default

### Findings log (spec-analyze) — anchor: `ccebf2a` (requirements.md uncommitted at analyze time)

| Finding | Category | REQ | Decision | Resolution |
|---|---|---|---|---|
| F1 | logical inconsistency | 4.1, 5.7 | A | เพิ่มเงื่อนไข no email collision ใน 4.1 และให้ collision fail closed ด้วย `identity-conflict` |
| F2 | ambiguity | 2.6, 3.1, 4.4 | A | normalize `tid`/`oid` เป็น lowercase UUID `D` ก่อน compare, lookup และ persist |
| F3 | conflicting constraints | 2.18, 2.25, 2.26 | A | issuer/tenant policy failure ใช้ `workforce-access-denied`; cryptographic/protocol failure ใช้ `auth-failed` |
| F4 | unstated assumption | 9.2 | A | ระบุ required Production settings เป็น `Authority`, `ClientId`, `ClientSecret`, `CallbackPath` |
| F5 | gap | 7.1, 7.2, 7.10 | A | ใช้ existing `Audit`; JIT account internal ID เป็นทั้ง actor และ target |
