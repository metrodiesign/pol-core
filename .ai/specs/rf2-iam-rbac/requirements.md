# Requirements: rf2-iam-rbac — Central IAM catalog แทน 2 catalog ซ้ำ

> Status: approved 2026-07-12
> Roadmap: spec #2 ของ v5 restructure (depends rf1-schema-reset — merged). Catalog เดิมถูก accept
> เป็น throwaway (D13) ตั้งแต่ approve master plan — rf2 replace ทั้งชุดได้โดยไม่ต้อง migrate data.
> Decision set: วิเคราะห์ 10 ข้อ + คำตอบ user "เลือกตามคำแนะนำทั้งหมด" (2026-07-12, recon อ้าง
> code จริงต่อข้อ)

## Overview

ระบบมี RBAC 2 catalog ซ้ำกันโดยเจตนา (deliberate debt, marker `// ponytail: DUPLICATE`):
ฝั่ง admin (`admin.Permissions/PermissionGroups/Roles/RolePermissions`, 16 keys/6 groups/5 roles)
และฝั่ง merchant-user (`merch.*` ชุดเดียวกัน, 7 keys/3 groups/2 roles) — โครงสร้าง entity,
กฎ CRUD, การ resolve permission ต่อ request และ parity guard เขียนซ้ำสองที่. rf2 ยุบเป็น
catalog กลางเดียวใน schema `iam` ตาม v5 doc §3 (Permission/Role/RolePermission + Scope),
seed 4 roles ตามแผน, รวม `RequirePermission` + parity guard เหลือกลไกเดียว, และปิด wart
เดิมที่ merchant custom role เป็น global ข้าม merchant. การมองเห็นข้อมูล (Tier + RLS) ไม่แตะ —
นั่นคือแกน orthogonal ที่คงไว้ และเป็นงาน rf6.

## REQ-1: Central catalog ใน schema `iam`

**User Story:** As a platform engineer, I want permission/role catalog เดียวใน schema `iam`,
so that vocabulary, กฎ, และ enforcement ไม่ถูกเขียนซ้ำสองที่แล้ว drift จากกัน

**Acceptance Criteria (EARS):**
- 1.1 THE SYSTEM SHALL มีตาราง `iam.PermissionGroups` (PK = `Key`, คอลัมน์ `Scope` ∈
  {Platform, Merchant} — ทุก key ในกลุ่มสืบทอด side จาก group), `iam.Permissions` (PK = `Key`,
  dot-notation string เดียว — ไม่แยกคอลัมน์ Resource/Action; FK `GroupKey` →
  `iam.PermissionGroups.Key`), `iam.Roles`, `iam.RolePermissions` (FK `PermissionKey` →
  `iam.Permissions.Key`, delete Restrict)
- 1.2 THE SYSTEM SHALL มี code-canonical vocabulary เดียว (module `Iam` ใหม่) เป็นแหล่งเดียวของ
  permission keys/groups — แทน `Admins.Domain.Permissions.Keys` + `Merchants.Domain.Users.Permissions.Keys`
- 1.3 THE SYSTEM SHALL ใช้ permission key รูป dot-notation string เดียวเป็น contract ทุกชั้น
  (code const / wire / DB PK) — key ที่ carry มาต้องเป็น literal เดิมทุกตัว (stable strings)
- 1.4 THE SYSTEM SHALL drop ตาราง catalog เดิมทั้ง 8: `admin.Permissions`, `admin.PermissionGroups`,
  `admin.Roles`, `admin.RolePermissions` และ `merch.Permissions`, `merch.PermissionGroups`,
  `merch.Roles`, `merch.RolePermissions`
- 1.5 WHEN rf2 เสร็จ THE SYSTEM SHALL ไม่เหลือ marker `ponytail: DUPLICATE` ของ RBAC duplication
  ใน `src/` (grep = 0)
- 1.6 IF entity ใน module `Iam` ถูก map นอก schema `iam` THEN Architecture test SHALL fail
  (ขยาย allowed-schema set ของ rf1 ด้วย `iam`)

## REQ-2: Seed catalog — 20 keys / 8 groups / 4 roles

**User Story:** As a security owner, I want seed catalog ที่ตรงกับ endpoint จริงและแผน v5,
so that ไม่มี dead permission ที่ชนกับ Non-Goals และ role ตั้งต้นพร้อมใช้ทั้งสองฝั่ง

**Acceptance Criteria (EARS):**
- 2.1 THE SYSTEM SHALL seed permissions 20 keys ใน 8 groups:
  `txn` (txn.view, txn.refund, txn.export), `merchant` (merchant.view, merchant.manage),
  `user` (user.view, user.manage, user.roles), `system` (audit.view, settings.manage, apikey.manage),
  `merchants.users` (merchants.users.approve, merchants.users.reject),
  `catalog` (product.create, product.update), `payment` (payment.create, payment.redirect),
  `roles` (roles.view, roles.manage, users.roles) — group `Scope`: {txn, merchant, user,
  system, merchants.users} = Platform, {catalog, payment, roles} = Merchant
- 2.2 THE SYSTEM SHALL ไม่ seed keys เดิม `invoice.view`, `invoice.manage`, `settlement.run`
  และ group `finance` — เหตุผล 2 เงื่อนไขประกอบ: ungated (ไม่ gate endpoint ใด) **และ** ชน
  Non-Goals ห้าม billing/settlement; ส่วน `product.update`/`roles.view` ที่ ungated เช่นกัน
  ให้คงไว้เพราะ reserved ใช้งานใกล้ (rf5 PUT /products, FE visibility toggle)
- 2.3 THE SYSTEM SHALL seed 4 roles: `platform_admin` (Scope=Platform — ทุก platform key 13 ตัว),
  `platform_auditor` (Scope=Platform — txn.view, merchant.view, user.view, audit.view),
  `merchant_manager` (Scope=Merchant — ทุก merchant key 7 ตัว),
  `merchant_staff` (Scope=Merchant — product.create, product.update, payment.create, payment.redirect)
- 2.4 THE SYSTEM SHALL ให้ `platform_admin` และ `merchant_manager` เป็น seed anchor:
  deactivate หรือ delete SHALL ถูกปฏิเสธ (409/throw) — แทน anchor เดิม `super_admin`/`merchant_owner`
- 2.5 WHEN สร้าง DB จากศูนย์ (docker bootstrap → `dotnet ef database update`) THE SYSTEM SHALL
  ได้ catalog + seed ครบโดยไม่พึ่ง manual step
- 2.6 IF insert `iam.RolePermissions` อ้าง key ที่ไม่อยู่ใน `iam.Permissions` THEN DB SHALL reject (FK)

## REQ-3: Role scoping — Scope + merchant visibility

**User Story:** As a security owner, I want role แยก scope Platform/Merchant และ custom role
ของ merchant ไม่รั่วข้าม merchant, so that assign ผิดฝั่งเป็นไปไม่ได้และปิด cross-tenant wart เดิม

**Acceptance Criteria (EARS):**
- 3.1 THE SYSTEM SHALL มีคอลัมน์ `Scope` บน `iam.Roles` ค่า ∈ {Platform, Merchant} NOT NULL
  และ immutable หลังสร้าง (เหมือน code slug)
- 3.2 THE SYSTEM SHALL มีคอลัมน์ `MerchantId uniqueidentifier NULL` บน `iam.Roles` —
  NULL = shared/seed role, มีค่า = custom role ของ merchant นั้น
- 3.3 IF role มี `Scope = Platform` และ `MerchantId` ไม่เป็น NULL THEN THE SYSTEM SHALL reject
  (invariant — enforce ที่ domain + CHECK constraint)
- 3.4 WHEN assign role ให้ PlatformUser IF `role.Scope != Platform` THEN THE SYSTEM SHALL ตอบ 400
- 3.5 WHEN assign role ให้ MerchantUser IF `role.Scope != Merchant` หรือ (`role.MerchantId` ไม่เป็น
  NULL และไม่เท่ากับ MerchantId ของ target) THEN THE SYSTEM SHALL ตอบ 400
- 3.6 THE SYSTEM SHALL ให้ merchant-side role list/read เห็นเฉพาะ role ที่ `Scope = Merchant` และ
  (`MerchantId` IS NULL หรือ = merchant ของ caller)
- 3.7 WHEN merchant-side สร้าง role THE SYSTEM SHALL ตั้ง `MerchantId` = merchant ของ caller เสมอ
- 3.8 IF merchant-side update/delete role ที่ `MerchantId` ไม่ใช่ของ caller (รวม shared seed)
  THEN THE SYSTEM SHALL ตอบ 409
- 3.9 THE SYSTEM SHALL ให้ admin-side role CRUD สร้าง/จัดการเฉพาะ role ที่ `Scope = Platform`
  (`MerchantId` NULL ตาม invariant 3.3) — ไม่มี Scope input ตอน create; shared Merchant-scope
  roles (`merchant_manager`/`merchant_staff`) เกิดจาก seed migration เท่านั้น

## REQ-4: Unified permission enforcement

**User Story:** As a developer, I want `RequirePermission` กลไกเดียวใช้ได้ทั้ง admin และ
merchant endpoints, so that fail-closed semantics เขียนที่เดียวและ gate ใหม่ในอนาคตไม่ต้องเลือกฝั่ง

**Acceptance Criteria (EARS):**
- 4.1 THE SYSTEM SHALL มี `RequirePermission` extension + metadata type เดียว แทนคู่เดิม
  (`RequirePermission`/`RequireMerchantUserPermission`)
- 4.2 THE SYSTEM SHALL resolve permission set ตอน authenticate ต่อ request จาก DB — union ของ
  `iam.RolePermissions` ของ roles ที่ assigned และ `Status = Active`; ฝั่ง merchant นับเฉพาะแถว
  assignment ที่ `MerchantId` ตรงกับ merchant ของ session — ไม่ cache ข้าม request, ไม่ใช้ claims
- 4.3 IF request ไม่มี actor scope ที่ bound (`IAdminScope`/`IUserScope` ต่อ request) หรือ
  permission set ไม่มี key ที่ endpoint ต้องการ THEN THE SYSTEM SHALL ตอบ 403
  (fail-closed — ไม่ throw 500)
- 4.4 WHEN role ถูก deactivate หรือ assignment ถูกถอน THE SYSTEM SHALL ให้ผล enforcement
  เปลี่ยนภายใน request ถัดไป
- 4.5 THE SYSTEM SHALL คง gate site เดิมทั้ง 20 จุด (admin 13 + merchant 7) ด้วย key literal
  เดิม และ SHALL NOT เพิ่ม
  gate ใหม่บน funnel endpoints ใน rf2 (carts/checkouts/orders คง policy `merchant-user` ล้วน —
  gating ใหม่เป็นงาน rf5/rf6)
- 4.6 WHILE role มี `Status = Inactive` THE SYSTEM SHALL ไม่นับ permissions ของ role นั้น
  ใน effective set

## REQ-5: Parity guard เดียว

**User Story:** As a developer, I want boot guard เดียวตรวจทุก gated key กับ catalog,
so that key หลุด catalog ทำให้ boot fail ทันทีไม่ว่า endpoint ฝั่งไหน

**Acceptance Criteria (EARS):**
- 5.1 THE SYSTEM SHALL มี parity guard เดียว scan `EndpointDataSource` ทั้งหมด แล้ว assert
  ทุก key ใน `RequiredPermission` metadata ⊆ vocabulary `AllKeys` (in-memory, ไม่แตะ DB)
- 5.2 IF gated key ใดไม่อยู่ใน catalog THEN THE SYSTEM SHALL throw ก่อน `app.Run()` (boot fail)
- 5.3 THE SYSTEM SHALL ลบ guard เดิมทั้งสอง (`PermissionParity`, `UserPermissionParity`)
- 5.4 IF endpoint ใต้ scheme `AdminSession` ถูก gate ด้วย key ที่ group `Scope != Platform`
  หรือ endpoint ใต้ scheme `MerchantUserSession` ถูก gate ด้วย key ที่ group `Scope != Merchant`
  THEN parity guard SHALL throw ก่อน `app.Run()` (side-awareness — ทดแทนสิ่งที่เดิมได้ฟรี
  จากการแยก 2 guard)

## REQ-6: Role management endpoints บน catalog ใหม่

**User Story:** As an admin / merchant manager, I want endpoint จัดการ role เดิมทำงานต่อบน
catalog ใหม่, so that console ทั้งสองใช้งานได้โดย behavior และ wire shape ไม่ regress

**Acceptance Criteria (EARS):**
- 6.1 THE SYSTEM SHALL ให้ endpoints ฝั่ง admin เดิมทำงานบน `iam` catalog ด้วย gate key เดิม:
  GET `/admins/permissions`, GET `/admins/roles`, GET `/admins/roles/{code}` (auth เท่านั้น),
  POST/PUT/DELETE `/admins/roles*` + PUT `/admins/{id}/roles` (gate `user.roles`)
- 6.2 THE SYSTEM SHALL ให้ endpoints ฝั่ง merchant เดิมทำงานบน `iam` catalog ด้วย gate key เดิม:
  GET `/merchants/users/permissions|roles|roles/{code}` (auth เท่านั้น), POST/PUT/DELETE
  `.../roles*` (gate `roles.manage`), PUT `/merchants/users/{id}/roles` (gate `users.roles`)
- 6.3 THE SYSTEM SHALL คงกฎ role เดิมทุกข้อ: code slug `^[a-z0-9_]+$` ยาว ≤64 + immutable,
  status parse แบบ strict (ค่าแปลก = 400), dup code = 409, delete role ที่ยังมี assignment = 409,
  grant key นอก catalog = 400, unknown role = 404
- 6.4 THE SYSTEM SHALL ให้ GET permissions ของแต่ละ console คืนเฉพาะ vocabulary ฝั่งตน
  (filter ด้วย group `Scope`): admin console = Platform keys (13/5 groups), merchant console =
  Merchant keys (7/3 groups) — wire shape เดิม
- 6.5 IF deactivate หรือ delete `platform_admin` หรือ `merchant_manager` THEN THE SYSTEM SHALL
  ตอบ 409 (seed anchor ตาม REQ-2.4)
- 6.6 IF grant key ให้ role โดย group `Scope` ของ key ไม่ตรงกับ `role.Scope` THEN THE SYSTEM
  SHALL ตอบ 400 (cross-side grant เป็นไปไม่ได้ — ทดแทน fail-closed เชิงโครงสร้างของ
  2 catalog เดิม)

## REQ-7: Assignment flows

**User Story:** As an admin / merchant manager, I want การ assign role ทำงานตามจังหวะเดิม
(admin ตอน approve, merchant self-service หลังจากนั้น) บน catalog ใหม่, so that onboarding
ตัวแทนไม่เปลี่ยน flow แต่ scope ถูก validate fail-closed

**Acceptance Criteria (EARS):**
- 7.1 THE SYSTEM SHALL คงตาราง assignment 2 ตารางแยก schema (`admin.*` สำหรับ PlatformUser,
  `merch.*` สำหรับ MerchantUser + MerchantId) โดย FK `RoleId` ชี้ `iam.Roles`
- 7.2 WHEN admin approve merchant user พร้อม `RoleCodes` THE SYSTEM SHALL validate ทุก code
  ตาม REQ-3.5 (Scope=Merchant + visibility ของ merchant target) ก่อนสร้าง assignment
- 7.3 THE SYSTEM SHALL คง merchant self-service เดิม: PUT `/merchants/users/{id}/roles`
  scoped merchant ของ caller — target นอก merchant = 404 (ไม่ leak การมีอยู่)
- 7.4 IF assignment อ้าง role code ที่ไม่มี THEN THE SYSTEM SHALL ตอบ 400
- 7.5 THE SYSTEM SHALL NOT เพิ่ม endpoint ให้ admin จัดการ role ใดที่ `Scope = Merchant` ใน rf2
  (ทั้ง shared seed และ merchant-specific `MerchantId` != NULL — สอดคล้อง REQ-3.9)
- 7.6 THE SYSTEM SHALL มี integration drift guard: ทุกแถว assignment ฝั่ง `admin.*` ชี้ role ที่
  `Scope = Platform` และทุกแถวฝั่ง `merch.*` ชี้ role ที่ `Scope = Merchant` (จับ write path
  ที่หลุด validation 3.4/3.5 — FK เดียวไป `iam.Roles` ไม่ constrain scope ที่ DB แล้ว)

## REQ-8: Bootstrap + Tier orthogonality คงเดิม

**User Story:** As a security owner, I want bootstrap first-admin ทำงานต่อและ Tier ไม่ถูกแตะ,
so that แกน action (role) กับแกน visibility (Tier + RLS) ยัง orthogonal ตามหลัก no-bypass

**Acceptance Criteria (EARS):**
- 8.1 WHEN self-provision Super สำเร็จ THE SYSTEM SHALL auto-assign role `platform_admin`
  ใน transaction เดียวกัน แบบ idempotent (แทน `super_admin` เดิม — semantics เดิมทุกข้อ)
- 8.2 THE SYSTEM SHALL คงพฤติกรรม Tier ทั้งหมดไม่เปลี่ยน: enum `Tier`, Super-only endpoints
  8 จุด, scoped floor ใน `IAdminQuery`, SQL `fn_merchant_predicate` branch บน Tier
- 8.3 THE SYSTEM SHALL ไม่มี code path ที่ให้สิทธิ์ action จาก Tier (Tier ไม่ imply permission —
  no-bypass เดิม)

## REQ-9: DB security objects + grants

**User Story:** As a security owner, I want grant matrix ของ `iam` schema ชัดและ catalog
อยู่นอก RLS, so that runtime resolve ได้พอดีสิทธิ์และ catalog กลางไม่โดน filter เงียบ

**Acceptance Criteria (EARS):**
- 9.1 THE SYSTEM SHALL grant `iam` schema ตาม least privilege: write ได้เฉพาะ path จัดการ role
  (mirror pattern เดิม: `pol_admin` CRUD บน Roles/RolePermissions + SELECT บน
  Permissions/PermissionGroups) และ principal ที่ resolve permission ต่อ request ทั้งสอง path
  ต้อง SELECT ได้ — matrix ราย principal ระบุใน design
- 9.2 THE SYSTEM SHALL ไม่มี RLS policy ครอบตาราง `iam.*` ใน rf2 — `Permissions`/
  `PermissionGroups` เป็น vocabulary ล้วน; `Roles`/`RolePermissions` มี tenant data
  (`MerchantId`) แต่ใช้ app-layer scoped read (REQ-3.6) เป็น floor เพราะ RLS บนตารางที่ถูก
  resolve ระหว่าง authenticate เสี่ยง chicken-and-egg — residual risk ต้องบันทึกใน design
  และ read path ทุกตัวต้องมี test ครอบ (edge case merchant A/B ด้านล่าง)
- 9.3 WHEN รัน migration บน fresh DB THE SYSTEM SHALL ผ่านจากศูนย์ (bootstrap → `ef database
  update`) โดยลำดับ drop เดิม → create `iam` → seed ไม่ชน FK ของ assignment tables

## REQ-10: Contract pins + drift guards

**User Story:** As a developer, I want literal pins ของ contract ใหม่, so that rename/แก้เงียบ
ใน catalog โดน test จับ (บทเรียน L8 จาก hierarchical-naming)

**Acceptance Criteria (EARS):**
- 10.1 THE SYSTEM SHALL มี test pin จำนวนและชุด literal ของ catalog ใหม่: 20 keys / 8 groups /
  4 role codes + scope ของแต่ละ role (แทน pin เดิม 16/6/5 และ 7/3/2)
- 10.2 THE SYSTEM SHALL มี integration drift guard: seeded rows ใน DB SetEquals vocabulary
  ใน code (แทน guard เดิมทั้งสองฝั่ง)
- 10.3 THE SYSTEM SHALL คง pin auth scheme ids `["AdminSession", "MerchantUserSession"]` ไม่เปลี่ยน
- 10.4 THE SYSTEM SHALL มี test pin ราย endpoint↔key mapping ของ gate site ทั้ง 20 จุด —
  จับทั้ง count drift และ swap `user.roles`↔`users.roles` (2 key เกือบเหมือนกันที่ตอนนี้อยู่
  catalog เดียว — guard เชิง set มองไม่เห็น)

## Edge Cases & Open Questions

**Edge cases (ต้องมี test):**
- Scoped-tier admin ที่ถือ `platform_admin` role: ทำ action ได้ทุก key แต่เห็นข้อมูลเฉพาะ merchant
  ที่ assigned (orthogonality ทำงานสองแกนอิสระ — REQ-8)
- Merchant A สร้าง custom role แล้ว merchant B list roles: ต้องไม่เห็นของ A (REQ-3.6)
- Assign `platform_auditor` ให้ MerchantUser: 400 (REQ-3.5)
- Grant `settings.manage` (Platform-side key) ให้ Merchant-scope role: 400 (REQ-6.6)
- Approve merchant user ด้วย role code ของ merchant อื่น: 400 (REQ-7.2)
- Role deactivate ระหว่าง session ยัง active: request ถัดไปสิทธิ์หาย (REQ-4.4)
- Merchant แก้ shared seed `merchant_staff`: 409 (REQ-3.8) — เดิมแก้ได้ (wart)

**Operator note:** สภาพแวดล้อม dev ที่มี catalog/assignment เดิมต้อง reset DB
(`docker compose down -v` → bootstrap → migrate) ตาม precedent rf1 — ไม่มี data migration
(throwaway D13)

**Open questions (ตัดสินใน design):**
- ชื่อตาราง assignment หลัง re-point (`RoleAssignments` เดิม vs `UserRoles` ตาม v5 §4-5) —
  naming law L1-L8 ตัดสิน
- Grant matrix ราย principal สำหรับ per-request resolve ฝั่ง merchant (REQ-9.1) — ต้อง audit
  ว่า connection ไหนรัน resolution จริง
- Migration strategy: new migration บน chain rf1 (3 ไฟล์เดิมคง) vs regenerate InitialSchema —
  design เลือกตาม effort/ความสะอาด (pre-prod ทำได้ทั้งคู่)
- ชื่อคอลัมน์ `AssignedByAdminId` บน merch assignment — ค่าจริงเป็น merchant user ตอน
  self-service (misnomer เดิม) — design ตัดสิน rename ตอน re-point FK (F15)

### Findings log — /spec-analyze 2026-07-12 (anchor: 87c9345 = HEAD; requirements.md ยัง untracked ณ เวลา log)

Audit 2 รอบ: ผู้เขียน + spec-architect fresh context (verify กับ live code). Decision โดย user
"ตามแนะนำทั้งหมด" (2026-07-12).

| # | Finding (ย่อ) | Decision |
|---|---|---|
| F1 | key ไม่มี side attribute — root ของ cross-side grant hole, guard side-blind, partition 13/7 ทำไม่ได้ | FIXED — `Scope` บน PermissionGroups (1.1, 2.1) + grant rule (6.6) + guard side-aware (5.4) + filter (6.4) |
| F2 | gate sites อ้าง 23 — จริง 20 (verify Program.cs) | FIXED — 4.5 = 20 + pin ราย endpoint↔key (10.4) |
| F3 | admin create ไม่ระบุ scope selection + ขัด 6.4 | FIXED — 3.9 admin CRUD = Platform-scope เท่านั้น; shared Merchant roles = seed-only |
| F4 | FK assignment → iam.Roles ไม่ constrain scope ที่ DB | FIXED — app floor 3.4/3.5 + drift guard 7.6 |
| F5 | no-RLS blanket ทั้ง iam.* ทั้งที่ Roles ถือ MerchantId | FIXED — 9.2 แยกเหตุผล + residual risk ลง design |
| F6 | 4.2 กำกวมบทบาท MerchantId ใน resolution | FIXED — นับเฉพาะ assignment ที่ MerchantId ตรง session |
| F7 | rationale ตัด dead key ไม่สม่ำเสมอ (product.update/roles.view ก็ ungated) | FIXED — 2.2 สองเงื่อนไข + เหตุเก็บ 2 ตัว (reserved rf5/FE) |
| F8 | platform_auditor key set ต่างจาก auditor เดิม | CONFIRMED — ชุดใหม่ตาม 2.3 |
| F9 | 7.5 ถ้อยคำชน 3.9 | FIXED — 7.5 = ห้ามทุก role Scope=Merchant |
| F10 | "scope ที่ bound" ไม่นิยาม | FIXED — 4.3 ระบุ IAdminScope/IUserScope |
| F11 | 1.1 ขาด FK Permissions.GroupKey → PermissionGroups | FIXED — เพิ่มใน 1.1 |
| F12 | group `merchants.users` ชื่อ dotted ผิดแผงกลุ่มอื่น | DISMISSED — stable string เดิม, ตั้งใจ |
| F13 | concurrent role-permission edit = last-write-wins | DISMISSED — pre-existing, note ใน design พอ |
| F14 | ขอ verify pin เดิม 5/2 roles | VERIFIED — SeedData.cs: admin 5, merchant 2 ตรง |
| F15 | `AssignedByAdminId` misnomer ฝั่ง merch self-service | LOGGED — open question ให้ design |
