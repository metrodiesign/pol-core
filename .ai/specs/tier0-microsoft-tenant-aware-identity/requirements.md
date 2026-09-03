# Requirements: Tier 0 Microsoft Tenant-Aware Immutable Identity

> Status: approved 2026-09-02
> Status-Note: amended 2026-09-02 — P1-P4 and Q1-Q10 approved
> Workflow: Requirements-First

## Overview

ฟีเจอร์นี้เปลี่ยน Admin Microsoft workforce identity จาก mutable corporate email ไปเป็น immutable,
tenant-aware key `(Provider, TenantId, Subject)` โดย runtime ใช้ `Provider=microsoft`, `TenantId` จาก validated
Microsoft Entra claim `tid` และ `Subject` จาก validated claim `oid` หลัง OIDC protocol validation สำเร็จเท่านั้น

Email เป็น optional profile/contact attribute และไม่มีอำนาจใน authentication, identity continuity, candidate
selection, bind, conflict, recovery, JIT ownership หรือ authorization decision ส่วน `EmployeeId` ยังคงเป็น HR
profile key แยกจาก authentication ระบบคง current single-workforce-tenant boundary, internal `AdminId`, RBAC,
`MerchantAccess`, employee profile, sessions และ audit behavior เดิมโดยไม่เปิด multi-tenant admission

## Scope และการ supersede

ข้อกำหนดนี้ supersede identity-key, email eligibility/ownership, invitation และ migration behavior ตาม matrix
ด้านล่างใน `tier-0-microsoft-canonical-email`, `admin-workforce-jit`, `entra-scoped-preprovision` และ
`tier0-graph-employee-profile` ข้อกำหนด OIDC protocol, HR mapping, RBAC, session, CSRF และ audit ที่ไม่อยู่ใน
matrix ยังคงเดิม

### Supersession matrix

| Source spec | Clauses ที่ supersede/absorb | ผลหลังฟีเจอร์นี้ |
|---|---|---|
| `tier-0-microsoft-canonical-email` | REQ-2.23-2.25; REQ-3.1-3.6; REQ-4.1, 4.7, 4.11, 4.22, 4.24; REQ-5; REQ-6; REQ-9.8-9.10, 9.20, 9.24-9.25, 9.28 | `oid` เป็น canonical subject ภายใต้ tenant dimension; email ไม่ใช่ eligibility หรือ identity; legacy conversion ใช้ verified offline manifest |
| `admin-workforce-jit` | REQ-2.5-2.6, 2.24; REQ-3.1-3.2; REQ-4.1, 4.4; REQ-5.2, 5.7-5.13 | รับ exact-one canonical `tid`/`oid`; key เปลี่ยนจาก pair เป็น triple; email-less JIT และ pre-bound invite แทน email bind |
| `entra-scoped-preprovision` | absorb tenant/UUID invariants จาก REQ-1.17-1.20 และ login identity concepts จาก REQ-4.1, 4.4-4.5 | ไม่ reactivate retired pre-provision endpoint; invitation ใช้ existing Admin create flow ที่รับ `oid` และ derive pinned tenant |
| `tier0-graph-employee-profile` | Overview identity-key sentence; REQ-7.6; identity portions ของ REQ-10.1, 10.3-10.5; REQ-11.8 | profile transaction ใช้ tenant-aware identity; email ไม่มีผลต่อ `EmployeeId`; Graph/HR mapping clauses อื่นคงเดิม |

| เรื่อง | ก่อนฟีเจอร์นี้ | หลังฟีเจอร์นี้ |
|---|---|---|
| Microsoft Admin identity key | `(microsoft, canonical corporate email)` | `(microsoft, validated tid, canonical validated oid)` |
| `TenantId` บน `admin.Users` | ไม่มี | validated `tid` สำหรับ runtime writes หรือ verified manifest tenant สำหรับ legacy backfill |
| `Subject` ของ Microsoft | canonical corporate email | canonical Entra `oid` |
| `Email` | required unique invite/identity value | nullable non-unique profile/contact attribute |
| `WorkforceEmailKey` | ownership lookup และ identity bridge | ลบ property, column และ unique index; historical tool derive temporary migration value จาก legacy `Email` โดยไม่ใช้ใน runtime auth |
| `EmployeeId` | immutable HR profile key | เหมือนเดิม; ไม่ใช่ authentication identity |
| Microsoft invite | email-keyed และ bind ตอน first login | pre-bound ด้วย pinned tenant + required `oid`; email optional |
| Production tenant admission | tenant-pinned Authority + persisted singleton | เหมือนเดิม; ยังไม่เปิด multi-tenant onboarding |

### In scope

- Admin Microsoft workforce OIDC callback, optional email profile extraction และ exact tuple resolution
- `src/Hosts/Api/Program.cs` เฉพาะ `POST /api/v1/admins`, `CreateAdminRequest` และ Admin response contracts ที่ต้องรองรับ nullable email
- Admin invite contract ที่รับ required Microsoft `oid` จาก verified Entra export/evidence และ derive `TenantId` จาก persisted/configured tenant pin
- Admin domain/application/persistence model สำหรับ `TenantId`, canonical `oid` และ nullable non-unique `Email`
- runtime EF configuration และ migration-owner EF configuration
- forward schema migration, model snapshot, generated schema script และ fresh/upgrade/rollback assertions
- verified offline manifest validation/backfill สำหรับ existing Microsoft rows และ legacy unbound invites
- `src/Tools/WorkforceIdentityMigrator/Program.cs` สำหรับ mandatory historical conversion, manifest apply และ final verifier
- tests สำหรับ claims, email-less identity resolution/JIT, manifest, concurrency, employee-profile rollback และ SQL Server migration
- Admin Microsoft OIDC/cutover runbooks และ static gate ที่ห้าม `WorkforceEmailKey` ใน auth source

### Out of scope

- tenant registry, tenant allowlist management หรือ multi-tenant production onboarding
- platform user หนึ่งรายเชื่อมหลาย external identities
- email-based runtime fallback หรือ controlled first-login email bind
- invitation token/UI ใหม่ หรือ tenant-management UI/API
- การเปลี่ยน Admin `Tier`, Role, Permission, `MerchantAccess` หรือ authorization policy
- การเปลี่ยน HR semantics ของ `EmployeeId`, `VibEmp`, Office, Division, Position หรือ Level
- การเปลี่ยน Merchant-user authentication นอกจาก compile-only adaptation ที่คง behavior เดิม
- การแก้ `docker/migrate-entrypoint.sh` หรือเปลี่ยน invocation order
- dependency ใหม่, deploy, production query, commit, push หรือ PR

## Selected migration strategy

ใช้ verified offline mapping/backfill เพื่อ preserve existing `AdminId` และ authorization โดยไม่เปิด runtime email
fallback Operator ต้องสร้าง ephemeral manifest จาก authoritative Entra directory และ internal Admin inventory ผ่าน
controlled process นอกแอป Manifest entry มีเพียง `AdminId`, `TenantId` และ `ObjectId`; ห้ามมี email, `EmployeeId`,
token หรือ secret และห้าม commit ลง repository

First run ของ mandatory `WorkforceIdentityMigrator` จะ capture required `AdminId` snapshot ภายใต้ identity lock,
verify manifest SHA-256, exact database target และ approval evidence ก่อนเชื่อ file จากนั้น tool insert/verify
`WorkforceTenantBinding` จาก configured tenant, validate manifest coverage แบบ exact แล้ว set `Provider=microsoft`,
`TenantId=manifest TenantId` และ `Subject=canonical manifest ObjectId` พร้อม binding auditที่ใช้ system actor,
`Version` bump และ completion state ใน serializable transactionเดียวกัน Failure ใดต้อง rollbackและคืน non-zero
ก่อน API startup

เมื่อ completion สำเร็จ manifest ถูกนำออกได้ Tool rerun ไม่ require manifest, verify captured AdminId snapshot กับ
final states และยอมรับ valid JIT/pre-bound invite ที่เกิดหลัง completion โดยไม่เพิ่ม rowเหล่านั้นเข้าชุด migration
snapshot

ไม่เลือก controlled transitional first-login bind เพราะมันให้อำนาจ email ใน authentication path และมี
email-reassignment takeover risk ขัด final contract โดยตรง Runtime binary หลัง cutover จึงไม่มี candidate lookup,
fallback หรือ recovery ด้วย email/`WorkforceEmailKey`

### Manifest coverage และ final persisted states

First-run required snapshot มีทุก pre-cutover row ที่ `Provider=microsoft` หรือ `Subject IS NULL`; bound
non-Microsoft historical row ที่ `Provider!=microsoft` และ `Subject IS NOT NULL` ไม่ถูกเปลี่ยน Manifest ต้องตรง
snapshot นี้แบบ exact Extra entry, missing required row หรือหลาย entry ต่อ `AdminId` เป็น invalid manifest
Post-completion JIT/pre-bound invite ไม่ใช่สมาชิก snapshot และ completed rerunตรวจด้วย final-state policyเท่านั้น

| Phase/state | `Provider` | `TenantId` | `Subject` | Runtime admission |
|---|---|---|---|---|
| Pre-tool legacy Microsoft | `microsoft` | NULL | legacy email หรือ historical value | migration-only; API startup ต้อง fail |
| Pre-tool legacy invite | historical provider | NULL | NULL | migration-only; API startup ต้อง fail |
| Final Microsoft | `microsoft` | pinned tenant | canonical `oid` | exact tuple resolution เท่านั้น |
| Final historical non-Microsoft | provider อื่น | NULL | non-null provider subject | generic non-Microsoft behavior เดิม |

New Microsoft invites อยู่ใน Final Microsoft state ตั้งแต่ create สำเร็จ โดย required `oid` ต้องมาจาก verified
Entra exportและมี approval evidence reference จึงไม่มี unbound email-keyed invite state หลัง cutover `Subject` ยัง
nullable ทาง physical schemaชั่วคราวเพื่อรองรับ pre-tool upgrade แต่ final startupไม่ยอมรับ NULL-subject row

Optional token email ใช้เป็น contact ได้เมื่อมี claim เดียว หลัง trim แล้ว non-empty และยาวไม่เกิน 320 characters
เท่านั้น ไม่มี corporate-domain gate ไม่มี syntax-based authorization และไม่ fallback ไป `preferred_username`/UPN

### `WorkforceEmailKey` disposition

Repository inventory ไม่พบ non-auth business caller ที่จำเป็นต้องเก็บ column เดิม `WorkforceIdentityMigrator` สามารถ
complete historical pending state โดย derive canonical migration value จาก legacy `Email` ใน memory ก่อน apply
verified `AdminId + tid + oid` manifest โดยไม่อ่านหรือเขียน `WorkforceEmailKey` ดังนั้นงานนี้ลบ property, column,
filtered unique index และทุก current-source reference ออกจาก domain, EF models, application, repository, startup
validation และ tool

Historical migration source `20260823132337_Tier0WorkforceEmailIdentity` และ generated SQL segment เดิมยังคงชื่อ
column ตาม immutable migration history แต่ final schema ไม่มี column และ static auth gateต้องไม่ whitelist current
runtime sourceใด

หลัก claim semantics อ้างอิง Microsoft Learn และบันทึกไว้ที่
`microsoft-entra-claim-research.md`

## REQ-1: Validated Microsoft workforce claims และ tenant pin

**User Story:** As a security owner, I want เชื่อ `tid` และ `oid` หลัง OIDC validation ครบเท่านั้น, so that forged, mutable หรือ cross-tenant claims เปลี่ยน identity ไม่ได้

**Acceptance Criteria (EARS):**

- 1.1 WHEN client เริ่ม Admin Microsoft login THE SYSTEM SHALL ใช้ Authorization Code flow ปัจจุบัน
- 1.2 WHEN client เริ่ม Admin Microsoft login THE SYSTEM SHALL ใช้ PKCE ปัจจุบัน
- 1.3 THE SYSTEM SHALL ตรวจ OIDC state ตาม framework flow ปัจจุบัน
- 1.4 THE SYSTEM SHALL ตรวจ token signature ก่อนอ่าน workforce claims
- 1.5 THE SYSTEM SHALL ตรวจ token issuer ก่อนอ่าน workforce claims
- 1.6 THE SYSTEM SHALL ตรวจ token audience ก่อนอ่าน workforce claims
- 1.7 THE SYSTEM SHALL ตรวจ nonce ก่อนอ่าน workforce claims
- 1.8 THE SYSTEM SHALL ตรวจ token lifetime ก่อนอ่าน workforce claims
- 1.9 WHEN protocol validation ทุกชั้นสำเร็จ THE SYSTEM SHALL อ่าน workforce claims จาก validated principal ของ callback นั้นเท่านั้น
- 1.10 THE SYSTEM SHALL บังคับให้ validated token มี claim `tid` หนึ่งค่า
- 1.11 THE SYSTEM SHALL บังคับให้ claim `tid` parse เป็น non-empty GUID
- 1.12 THE SYSTEM SHALL บังคับให้ validated token มี claim `oid` หนึ่งค่า
- 1.13 THE SYSTEM SHALL บังคับให้ claim `oid` parse เป็น non-empty GUID
- 1.14 WHEN `tid` parse สำเร็จ THE SYSTEM SHALL normalize ค่าเป็น lowercase GUID รูปแบบ `D`
- 1.15 WHEN `oid` parse สำเร็จ THE SYSTEM SHALL normalize ค่าเป็น lowercase GUID รูปแบบ `D`
- 1.16 THE SYSTEM SHALL บังคับให้ validated `tid` ตรง configured tenant-pinned Authority แบบ exact
- 1.17 THE SYSTEM SHALL คง exact configured-tenant versus persisted `WorkforceTenantBinding` singleton check ก่อนรับ Admin Microsoft traffic
- 1.18 THE SYSTEM SHALL ไม่บังคับ corporate-email eligibility สำหรับ Admin Microsoft authentication
- 1.19 IF `tid` missing THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `workforce-access-denied`
- 1.20 IF `oid` missing THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `workforce-access-denied`
- 1.21 IF validated `tid` ไม่ตรง current workforce tenant pin THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `workforce-access-denied`
- 1.22 IF workforce claim validation ล้มเหลว THEN THE SYSTEM SHALL ไม่อ่าน identity/profile data จาก database
- 1.23 IF workforce claim validation ล้มเหลว THEN THE SYSTEM SHALL ไม่ mutate Admin identity หรือ profile
- 1.24 IF workforce claim validation ล้มเหลว THEN THE SYSTEM SHALL ไม่สร้าง Admin session
- 1.25 WHERE employee profile switch เปิด THE SYSTEM SHALL ไม่เรียก Microsoft Graph ก่อน REQ-1.9 ถึง REQ-1.21 และ REQ-1.28 ถึง REQ-1.33 ผ่านครบ
- 1.26 IF signature audience nonce lifetime state หรือ code exchange validation ล้มเหลว THEN THE SYSTEM SHALL คง browser reason `auth-failed` ตาม current flow
- 1.27 IF issuer validation ล้มเหลว THEN THE SYSTEM SHALL คง browser reason `workforce-access-denied` ตาม current flow
- 1.28 IF `tid` duplicate THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `workforce-access-denied`
- 1.29 IF `tid` malformed THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `workforce-access-denied`
- 1.30 IF `tid` empty THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `workforce-access-denied`
- 1.31 IF `oid` duplicate THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `workforce-access-denied`
- 1.32 IF `oid` malformed THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `workforce-access-denied`
- 1.33 IF `oid` empty THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `workforce-access-denied`
- 1.34 THE SYSTEM SHALL validate every `admin.Users` row against exactly one final persisted state before accepting Admin Microsoft traffic
- 1.35 IF startup validation พบ row ที่ไม่อยู่ใน final persisted states THEN THE SYSTEM SHALL fail boot โดยไม่เปิดรับ Admin Microsoft traffic
- 1.36 THE SYSTEM SHALL คง validation ว่า prior `WorkforceIdentityMigrations` state complete ก่อนรับ Admin Microsoft traffic
- 1.37 IF workforce claim validation ล้มเหลว THEN THE SYSTEM SHALL อนุญาตเฉพาะ generic denied-auth audit write บน fresh scope
- 1.38 THE SYSTEM SHALL ไม่อ่าน `WorkforceEmailKey` ระหว่าง startup identity validation
- 1.39 THE SYSTEM SHALL validate ว่า tenant-aware offline manifest state complete และ counts ตรงก่อนรับ Admin Microsoft traffic
- 1.40 IF validated token ไม่มี email claim THEN THE SYSTEM SHALL ดำเนิน Microsoft identity resolution ต่อด้วย validated `tid + oid`
- 1.41 THE SYSTEM SHALL ไม่ใช้ `preferred_username` หรือ UPN เป็น fallback เมื่อ email claim ไม่มี
- 1.42 IF validated token มี email claim มากกว่าหนึ่งค่า THEN THE SYSTEM SHALL ignore email profile attribute โดยไม่ปฏิเสธ `tid + oid` authentication
- 1.43 IF validated token มี email claim ที่หลัง trim ยาวเกิน 320 characters THEN THE SYSTEM SHALL ignore email profile attribute โดยไม่ปฏิเสธ `tid + oid` authentication
- 1.44 IF validated token มี email claim ที่หลัง trim เป็นค่าว่าง THEN THE SYSTEM SHALL treat email profile attribute เป็น absent
- 1.45 WHEN validated token มี email claimหนึ่งค่าที่หลัง trim non-empty และยาวไม่เกิน 320 characters THE SYSTEM SHALL ไม่ให้ค่านั้นเปลี่ยน identity resolution decision
- 1.46 THE SYSTEM SHALL ไม่ใช้ corporate-domain validation ตัดสินว่า optional email มีผลต่อ authenticationหรือไม่

## REQ-2: Persisted tenant-aware identity contract

**User Story:** As a platform maintainer, I want Microsoft identity มี tenant dimension ที่ชัดเจน, so that subject เดียวกันจากคนละ tenant ไม่ชนหรือ resolve ข้ามกัน

**Acceptance Criteria (EARS):**

- 2.1 THE SYSTEM SHALL ใช้ `(Provider, TenantId, Subject)` เป็น persisted external identity key
- 2.2 THE SYSTEM SHALL ใช้ค่า `microsoft` เป็น `Provider` ของ Admin Microsoft identity
- 2.3 THE SYSTEM SHALL persist validated `tid` ลง `TenantId` สำหรับ Microsoft runtime identity ใหม่
- 2.4 THE SYSTEM SHALL persist canonical validated `oid` ลง `Subject` สำหรับ Microsoft runtime identity ใหม่
- 2.5 THE SYSTEM SHALL map `admin.Users.TenantId` เป็น SQL Server `uniqueidentifier` nullable
- 2.6 WHILE Microsoft identity อยู่ใน final persisted state THE SYSTEM SHALL มี `TenantId` ที่ไม่เป็น NULL
- 2.7 WHILE Microsoft identity อยู่ใน final persisted state THE SYSTEM SHALL มี `Subject` ที่ไม่เป็น NULL
- 2.8 WHEN Microsoft workforce invite ถูกสร้างหลังฟีเจอร์นี้ THE SYSTEM SHALL persist current singleton `TenantId`
- 2.9 WHEN Microsoft workforce invite ถูกสร้างหลังฟีเจอร์นี้ THE SYSTEM SHALL persist canonical required `oid` ลง `Subject`
- 2.10 WHILE non-Microsoft Admin ใช้ identity contract เดิม THE SYSTEM SHALL คง `TenantId` เป็น NULL
- 2.11 WHILE mandatory offline mapper ยังไม่ complete THE SYSTEM SHALL อนุญาต legacy Microsoft `TenantId` เป็น NULL ใน migration-only state
- 2.12 THE SYSTEM SHALL enforce unique index บน `(Provider, TenantId, Subject)` สำหรับ row ที่ `Subject IS NOT NULL`
- 2.13 THE SYSTEM SHALL ยกเว้น migration-only row ที่ `Subject IS NULL` จาก tenant-aware unique index
- 2.14 THE SYSTEM SHALL แทนที่ unique index เดิมบน `(Provider, Subject)` ด้วย tenant-aware unique index
- 2.15 IF insert หรือ update ทำให้ `(Provider, TenantId, Subject)` ซ้ำ THEN THE SYSTEM SHALL ให้ database ปฏิเสธ write
- 2.16 THE SYSTEM SHALL ถือ same `Subject` ภายใต้คนละ non-null `TenantId` เป็น external identity คนละรายการใน identity policy และ triple-index semantics
- 2.17 THE SYSTEM SHALL ถือ `Email` เป็น optional profile/contact attribute ไม่ใช่ส่วนของ external identity key
- 2.18 THE SYSTEM SHALL ไม่มี persisted `WorkforceEmailKey` ใน final Admin identity model
- 2.19 THE SYSTEM SHALL ถือ `EmployeeId` เป็น HR identity/profile key ไม่ใช่ส่วนของ authentication identity key
- 2.20 THE SYSTEM SHALL ไม่ใช้ `email`, `preferred_username`, UPN, `WorkforceEmailKey` หรือ `EmployeeId` เป็น Microsoft `Subject`
- 2.21 THE SYSTEM SHALL คง model ที่หนึ่ง `admin.Users` row มี external identity ได้หนึ่งรายการตาม current aggregate
- 2.22 WHILE Microsoft identity มี non-null `TenantId` และ non-null `Subject` THE SYSTEM SHALL ถือ `Provider`, `TenantId` และ `Subject` เป็น immutable identity tuple
- 2.23 THE SYSTEM SHALL ไม่มี endpoint หรือ runtime command ที่เปลี่ยน tenant-aware identity tuple หลัง invite, JIT หรือ offline mapping สำเร็จ
- 2.24 WHEN offline mapper persist tenant-aware identity tuple THE SYSTEM SHALL persist `Provider`, `TenantId` และ `Subject` แบบ atomic
- 2.25 WHILE legacy invited Admin ยังไม่ผ่าน mandatory offline mapping THE SYSTEM SHALL อนุญาต `Subject` เป็น NULL ใน migration-only state
- 2.26 WHEN Microsoft workforce invite ถูกสร้างหลังฟีเจอร์นี้ THE SYSTEM SHALL persist `Provider=microsoft`
- 2.27 THE SYSTEM SHALL enforce uniqueness ของ `admin.WorkforceTenantBindings.TenantId`
- 2.28 THE SYSTEM SHALL enforce nullable FK จาก `admin.Users.TenantId` ไป `admin.WorkforceTenantBindings.TenantId`
- 2.29 THE SYSTEM SHALL enforce CHECK ว่า non-null `admin.Users.TenantId` ใช้ได้เฉพาะ row ที่ `Provider=microsoft`
- 2.30 THE SYSTEM SHALL คง uniqueness ของ non-Microsoft `(Provider, NULL, Subject)` ผ่าน tenant-aware index
- 2.31 THE SYSTEM SHALL ให้ migration-only rows ที่ `Subject=NULL` อยู่ร่วมกันได้ระหว่าง schema upgrade ก่อน mapper complete
- 2.32 THE SYSTEM SHALL map `admin.Users.Email` เป็น SQL Server `nvarchar(320)` nullable
- 2.33 THE SYSTEM SHALL ไม่มี unique index หรือ unique constraint บน `admin.Users.Email` หลัง migration
- 2.34 THE SYSTEM SHALL ลบ `WorkforceEmailKey` property ออกจาก Admin `User` domain model
- 2.35 THE SYSTEM SHALL ลบ `WorkforceEmailKey` mapping ออกจาก runtime `ControlPlaneDbContext`
- 2.36 WHEN runtime สร้างหรือแก้ Admin row THE SYSTEM SHALL ไม่มี `WorkforceEmailKey` value ให้เขียน
- 2.37 IF Microsoft callback ไม่มี usable email THEN THE SYSTEM SHALL ไม่สร้าง placeholder email
- 2.38 THE SYSTEM SHALL อนุญาต Admin สองรายมี non-null `Email` ค่าเดียวกันเมื่อ tenant-aware identity tuple ต่างกัน
- 2.39 THE SYSTEM SHALL ลบ `admin.Users.WorkforceEmailKey` column ผ่าน forward migration
- 2.40 THE SYSTEM SHALL ลบ `IX_Users_WorkforceEmailKey` ผ่าน forward migration
- 2.41 THE SYSTEM SHALL ให้ application และ wire contracts ที่แสดง Admin email รองรับค่า NULL
- 2.42 WHEN Microsoft invite รับ `ObjectId` จาก authenticated Super flow THE SYSTEM SHALL require ค่าอ้างอิงจาก verified Entra export
- 2.43 WHEN pre-bound invite login ครั้งแรก THE SYSTEM SHALL require validated callback tuple ตรง persisted tuple แบบ exact
- 2.44 WHILE runtime ยังใช้ singleton tenant pin THE SYSTEM SHALL ให้ FK ปฏิเสธ persisted Microsoft identity จาก tenant ที่สอง
- 2.45 WHEN Microsoft invite ถูกสร้าง THE SYSTEM SHALL require non-sensitive approval evidence reference สำหรับ supplied `ObjectId`
- 2.46 WHEN optional email ถูก persist THE SYSTEM SHALL trim ค่าและบังคับ maximum length 320 characters โดยไม่ใช้ corporate-domain gate
- 2.47 WHEN Microsoft invite รับ `ObjectId` THE SYSTEM SHALL validate ค่าเป็น non-empty canonical GUID ก่อน persist

## REQ-3: Exact identity resolution และ optional email

**User Story:** As an existing employee, I want `tid + oid` เดิม resolve Admin เดิมโดยไม่ขึ้นกับ email, so that mutable หรือ absent profile data ไม่สร้าง JIT account ใหม่หรือย้ายสิทธิ์

**Acceptance Criteria (EARS):**

- 3.1 WHEN exact `(microsoft, TenantId, Subject)` ตรง Admin เดิม THE SYSTEM SHALL treat exact tuple เป็น terminal identity authority
- 3.2 WHEN exact identity ตรง Active Admin เดิม THE SYSTEM SHALL resolve internal `AdminId` เดิม
- 3.3 WHEN email เปลี่ยนแต่ exact `tid + oid` เดิม THE SYSTEM SHALL resolve internal `AdminId` เดิม
- 3.4 WHEN email เปลี่ยนแต่ exact `tid + oid` เดิม THE SYSTEM SHALL ไม่สร้าง JIT Admin ใหม่
- 3.5 WHEN exact identity ตรง Admin เดิม THE SYSTEM SHALL ไม่บังคับให้ token email ตรง stored `Email`
- 3.6 WHEN exact identity ตรง Admin เดิม THE SYSTEM SHALL ไม่อ่าน stored `WorkforceEmailKey`
- 3.7 WHEN exact identity ตรง Admin เดิม THE SYSTEM SHALL ไม่ auto-refresh stored `Email`
- 3.8 WHEN exact identity ตรง Admin เดิม THE SYSTEM SHALL ไม่เขียน physical `WorkforceEmailKey`
- 3.9 IF `oid` เท่ากันแต่ `tid` ต่างกัน THEN THE SYSTEM SHALL ไม่ resolve Admin ของ tenant อื่น
- 3.10 IF `oid` เท่ากันแต่ `tid` ต่างกัน THEN THE SYSTEM SHALL ไม่ bind Admin ของ tenant อื่น
- 3.11 THE SYSTEM SHALL scope exact Microsoft identity lookup ด้วย validated `TenantId`
- 3.12 THE SYSTEM SHALL scope Microsoft race recovery lookup ด้วย validated `TenantId`
- 3.13 THE SYSTEM SHALL scope Microsoft identity conflict determination ด้วย validated `TenantId`
- 3.14 THE SYSTEM SHALL ไม่ fallback identity ด้วย email, `preferred_username`, UPN, `WorkforceEmailKey` หรือ `EmployeeId`
- 3.15 WHEN exact identity ตรง Active Admin เดิม THE SYSTEM SHALL คง `Tier` เดิม
- 3.16 WHEN exact identity ตรง Active Admin เดิม THE SYSTEM SHALL คง Role assignments เดิม
- 3.17 WHEN exact identity ตรง Active Admin เดิม THE SYSTEM SHALL resolve effective Permissions สดตาม current flow
- 3.18 WHEN exact identity ตรง Active Admin เดิม THE SYSTEM SHALL คง `MerchantAccess` เดิม
- 3.19 IF exact identity ตรง Suspended Admin THEN THE SYSTEM SHALL ปฏิเสธการสร้าง session
- 3.20 IF exact identity ตรง Suspended Admin THEN THE SYSTEM SHALL ไม่สร้าง JIT Admin ใหม่
- 3.21 IF identity ใหม่มี email ตรง Admin อื่นแต่ tenant-aware tuple ต่างกัน THEN THE SYSTEM SHALL ไม่ classify เป็น identity conflict เพราะ email
- 3.22 IF identity ใหม่มี email ตรง Admin อื่นแต่ tenant-aware tuple ต่างกัน THEN THE SYSTEM SHALL ไม่โอน ownership ด้วย email
- 3.23 WHEN exact identity ตรง Admin เดิมและ callback ไม่มี email THE SYSTEM SHALL resolve internal `AdminId` เดิม
- 3.24 THE SYSTEM SHALL ใช้ exact `(Provider, TenantId, Subject)` เป็น Microsoft repository identity lookup เพียงรูปแบบเดียว
- 3.25 THE SYSTEM SHALL ใช้ exact `(Provider, TenantId, Subject)` เป็น Microsoft identity conflict comparison เพียงรูปแบบเดียว
- 3.26 THE SYSTEM SHALL ใช้ exact `(Provider, TenantId, Subject)` เป็น Microsoft race recovery lookup เพียงรูปแบบเดียว
- 3.27 THE SYSTEM SHALL ไม่ให้ optional email มีผลต่อ authorization result หลัง exact identity resolve สำเร็จ
- 3.28 WHERE email search ยังใช้ใน Admin directory THE SYSTEM SHALL แยก caller นั้นออกจาก authentication resolver และ ownership policy

## REQ-4: Verified offline legacy และ invite mapping

**User Story:** As an existing or invited Admin, I want verified offline mapping bind immutable Entra identity เข้าบัญชีเดิมก่อน cutover, so that runtime ไม่ต้องเชื่อ email และ internal identity/authorization เดิมไม่สูญหาย

**Acceptance Criteria (EARS):**

- 4.1 WHEN tenant-aware schema ถูก apply THE SYSTEM SHALL require mandatory offline mapping complete ก่อนรับ Admin Microsoft traffic
- 4.2 THE SYSTEM SHALL จำกัด manifest entry เป็น `AdminId`, `TenantId` และ `ObjectId`
- 4.3 WHEN mapper resolve target Admin row THE SYSTEM SHALL ใช้ exact manifest `AdminId` เท่านั้น
- 4.4 THE SYSTEM SHALL ไม่ใช้ email หรือ `WorkforceEmailKey` เพื่อเลือก target row ของ offline mapping
- 4.5 WHEN first-run snapshot row มี `Provider=microsoft` THE SYSTEM SHALL require exactly one manifest entry สำหรับ row นั้น
- 4.6 WHEN first-run snapshot row มี `Subject=NULL` THE SYSTEM SHALL require exactly one manifest entry สำหรับ row นั้น
- 4.7 IF manifest `ObjectId` ไม่ใช่ non-empty GUID THEN THE SYSTEM SHALL fail mapping ก่อนเขียน
- 4.8 IF manifest `TenantId` ไม่ตรง configured tenant หรือ singleton ที่ tool ensure แล้ว THEN THE SYSTEM SHALL fail mapping ก่อนเขียน
- 4.9 IF manifest มี `AdminId` ซ้ำ THEN THE SYSTEM SHALL fail mapping ก่อนเขียน
- 4.10 WHEN legacy mapping สำเร็จ THE SYSTEM SHALL set `Provider=microsoft`
- 4.11 WHEN legacy mapping สำเร็จ THE SYSTEM SHALL set `TenantId` จาก verified manifest tenant
- 4.12 WHEN legacy mapping สำเร็จ THE SYSTEM SHALL replace legacy/unbound `Subject` ด้วย canonical manifest `ObjectId` รูปแบบ GUID `D`
- 4.13 WHEN legacy mapping สำเร็จ THE SYSTEM SHALL คง internal `AdminId` เดิม
- 4.14 WHEN legacy mapping สำเร็จ THE SYSTEM SHALL คง Status เดิม
- 4.15 WHEN legacy mapping สำเร็จ THE SYSTEM SHALL คง `Tier` เดิม
- 4.16 WHEN legacy mapping สำเร็จ THE SYSTEM SHALL คง Role assignments เดิม
- 4.17 WHEN legacy mapping สำเร็จ THE SYSTEM SHALL คง effective Permissions เดิม
- 4.18 WHEN legacy mapping สำเร็จ THE SYSTEM SHALL คง `MerchantAccess` เดิม
- 4.19 WHEN legacy mapping สำเร็จ THE SYSTEM SHALL ไม่สร้าง Admin row ใหม่
- 4.20 WHEN legacy mapping สำเร็จ THE SYSTEM SHALL append current `microsoft-email-bind` action หนึ่งรายการเพื่อ audit compatibility
- 4.21 WHEN legacy mapping สำเร็จ THE SYSTEM SHALL ใช้ migration/system actor และ internal Admin ID เป็น audit target
- 4.22 WHILE row อยู่ใน final tenant-aware state THE SYSTEM SHALL ไม่ map identity tuple ซ้ำ
- 4.23 IF final tenant-aware identity ของ target ขัดกับ manifest tuple THEN THE SYSTEM SHALL ไม่ overwrite identity เดิม
- 4.24 IF manifest มีหลาย tuple สำหรับ `AdminId` เดียวกัน THEN THE SYSTEM SHALL fail mapping ก่อนเขียน
- 4.25 IF manifest ทำให้ `(Provider, TenantId, Subject)` ซ้ำ THEN THE SYSTEM SHALL fail mapping ก่อนเขียน
- 4.26 THE SYSTEM SHALL มี measurable remaining count ครอบทุก AdminId ใน captured first-run snapshot
- 4.27 IF remaining count ไม่เป็นศูนย์ THEN THE SYSTEM SHALL fail mandatory mapper และ block API startup
- 4.28 WHEN mapper bind Suspended Admin THE SYSTEM SHALL คง Status เป็น Suspended
- 4.29 IF exact identity ตรง Suspended Admin หลัง mapping THEN THE SYSTEM SHALL ปฏิเสธการสร้าง session ด้วย current `suspended` behavior
- 4.30 WHEN legacy invited row มี `Subject=NULL` THE SYSTEM SHALL map row นั้นผ่าน verified manifest เท่านั้น
- 4.31 IF manifest อ้าง `AdminId` ที่ไม่มีใน `admin.Users` THEN THE SYSTEM SHALL fail mapping ก่อนเขียน
- 4.32 WHEN offline mapping ทำงาน THE SYSTEM SHALL ไม่อ่านหรือเปลี่ยน `EmployeeId`
- 4.33 WHEN legacy mapping เปลี่ยน identity state สำเร็จ THE SYSTEM SHALL bump Admin `Version` หนึ่งครั้ง
- 4.34 WHEN offline mapping ทำงาน THE SYSTEM SHALL ไม่เปลี่ยน employee profile หรือเพิ่ม profile `Version` bump
- 4.35 WHEN legacy mapping สำเร็จ THE SYSTEM SHALL ไม่ bump `AuthorizationVersion`
- 4.36 IF manifest validation หรือ mapping row ใดล้มเหลว THEN THE SYSTEM SHALL rollback identity, audit, version และ completion-state writes ทั้งหมด
- 4.37 THE SYSTEM SHALL require operator evidence ว่า manifest mapping มาจาก authoritative Entra directory และ reviewed Admin inventory
- 4.38 THE SYSTEM SHALL ไม่ persist manifest file หรือ manifest contents ลง repository
- 4.39 THE SYSTEM SHALL require first-run manifest coverage ตรง captured AdminId snapshot แบบ exact
- 4.40 THE SYSTEM SHALL require ทุก manifest entry ใช้ workforce tenant เดียวกับ current tenant pin
- 4.41 THE SYSTEM SHALL ไม่เขียน `TenantId` หรือ `ObjectId` ลง binding audit payload
- 4.42 WHEN completed mapper ถูก rerunโดยไม่มี manifest THE SYSTEM SHALL ไม่ append binding audit ซ้ำ
- 4.43 THE SYSTEM SHALL serialize offline mapping ด้วย transaction-owned `admin-user-identity-mutation` lock
- 4.44 WHEN final runtime resolve Microsoft identity THE SYSTEM SHALL ไม่อ่าน manifest หรือ migration-only bridge data
- 4.45 WHEN first-run mapper เริ่มภายใต้ identity lock THE SYSTEM SHALL capture immutable required `AdminId` snapshot ก่อน validate manifest coverage
- 4.46 WHEN completed mapper rerun THE SYSTEM SHALL ยอมรับ valid final-state JIT และ pre-bound invite ที่ไม่อยู่ใน first-run snapshot
- 4.47 IF `WorkforceTenantBinding` singleton ไม่มีตอน first-run mapping THEN THE SYSTEM SHALL insert configured tenant binding ภายใต้ tenant-binding lock
- 4.48 IF existing `WorkforceTenantBinding` ไม่ตรง configured tenant THEN THE SYSTEM SHALL fail mapping ก่อน identity write
- 4.49 WHEN offline mapper append binding audit THE SYSTEM SHALL include non-sensitive approval correlation reference
- 4.50 THE SYSTEM SHALL verify manifest SHA-256 ก่อนเปิด mapping transaction
- 4.51 THE SYSTEM SHALL verify approved exact database target ก่อน mapping write
- 4.52 THE SYSTEM SHALL verify non-empty approval evidence ก่อน mapping write
- 4.53 IF manifest digest, target หรือ approval evidence validation ล้มเหลว THEN THE SYSTEM SHALL ไม่เขียน database
- 4.54 WHILE historical row มี `Provider!=microsoft` และ non-null `Subject` THE SYSTEM SHALL exclude row นั้นจาก Microsoft manifest snapshot

## REQ-5: Least-privilege email-optional JIT และ employee-profile atomicity

**User Story:** As an eligible new employee, I want email-optional JIT และ employee profile ทำงานกับ immutable identity แบบ atomic, so that failure ไม่ทิ้งบัญชีหรือ profile ครึ่งชุด

**Acceptance Criteria (EARS):**

- 5.1 WHEN exact tenant-aware identity ไม่พบ THE SYSTEM SHALL JIT-create Admin ใหม่โดยไม่ค้น email candidate
- 5.2 WHEN JIT-create Admin THE SYSTEM SHALL persist `TenantId=validated tid`
- 5.3 WHEN JIT-create Admin THE SYSTEM SHALL persist `Subject=canonical validated oid`
- 5.4 WHEN JIT-create Admin พร้อม exact-one optional email ที่ผ่าน trim/length policy THE SYSTEM SHALL persist trimmed email เป็น contact attribute
- 5.5 WHEN JIT-create Admin โดยไม่มี usable email THE SYSTEM SHALL persist `Email=NULL`
- 5.6 WHEN JIT-create Admin THE SYSTEM SHALL กำหนด Status เป็น `Active`
- 5.7 WHEN JIT-create Admin THE SYSTEM SHALL กำหนด `Tier` เป็น `Scoped`
- 5.8 WHEN JIT-create Admin THE SYSTEM SHALL ไม่ assign Role ใด
- 5.9 WHEN JIT-create Admin THE SYSTEM SHALL ไม่ assign `MerchantAccess` ใด
- 5.10 WHEN JIT-create Admin THE SYSTEM SHALL ไม่ grant Permission ใดโดยนัย
- 5.11 WHILE employee profile switch ปิด THE SYSTEM SHALL คง Graph/HR/profile behavior เดิมของ `tier0-graph-employee-profile` REQ-12
- 5.12 WHILE employee profile switch เปิด THE SYSTEM SHALL คง Graph `employeeId` validation และ HR mapping semantics เดิม
- 5.13 THE SYSTEM SHALL commit JIT identity tuple ใน transaction เดียวกับ employee profile mutation ที่เปิดใช้
- 5.14 THE SYSTEM SHALL commit related `UserAudits` ใน transaction เดียวกับ identity/profile mutation
- 5.15 IF employee profile resolution ล้มเหลว THEN THE SYSTEM SHALL rollback JIT Admin creation
- 5.16 IF employee profile resolution ล้มเหลว THEN THE SYSTEM SHALL ไม่เปลี่ยน existing tenant-aware identity tuple
- 5.17 IF employee profile resolution ล้มเหลว THEN THE SYSTEM SHALL rollback employee profile mutation
- 5.18 IF employee profile resolution ล้มเหลว THEN THE SYSTEM SHALL rollback related success/JIT/profile `UserAudits`
- 5.19 IF employee profile resolution ล้มเหลว THEN THE SYSTEM SHALL ไม่สร้าง Admin session
- 5.20 IF employee profile resolution ล้มเหลว THEN THE SYSTEM SHALL คง denied-auth audit behavior บน fresh scope ปัจจุบัน
- 5.21 WHEN identity/profile transaction สำเร็จสำหรับ Active Admin THE SYSTEM SHALL สร้าง Admin session ตาม current flow
- 5.22 THE SYSTEM SHALL ไม่ใช้ `EmployeeId` เพื่อ resolve Microsoft authentication identity
- 5.23 THE SYSTEM SHALL ไม่เปลี่ยน `EmployeeId` uniqueness หรือ immutability semantics ในงานนี้
- 5.24 THE SYSTEM SHALL ไม่เปลี่ยน `VibEmp`, Office หรือ Division mapping semantics ในงานนี้
- 5.25 WHILE production ยัง pin workforce tenant เดียว THE SYSTEM SHALL คง `EmployeeId` conflict lookup และ unique index แบบ global
- 5.26 THE SYSTEM SHALL classify `EmployeeId` collision เป็น HR-profile conflict ไม่ใช่ Microsoft authentication identity lookup
- 5.27 WHEN valid `tid + oid` ไม่มี email และ employee-profile prerequisites ผ่าน THE SYSTEM SHALL JIT-create Admin สำเร็จ
- 5.28 WHEN identity ใหม่มี email ตรง Admin อื่นและ tenant-aware tuple ต่างกัน THE SYSTEM SHALL JIT-create identity คนละรายการโดยไม่ bind Admin เดิม
- 5.29 IF optional email ยาวเกิน 320 characters หลัง trim THEN THE SYSTEM SHALL ไม่ให้ email profile parsing ปฏิเสธ JIT identity
- 5.30 WHEN JIT-create Admin THE SYSTEM SHALL ไม่มี `WorkforceEmailKey` field ให้ populate
- 5.31 WHEN JIT-create Admin สำเร็จ THE SYSTEM SHALL append `jit-provision` audit ตาม current behavior
- 5.32 IF optional email ว่างหลัง trim THEN THE SYSTEM SHALL ไม่ให้ email profile parsing ปฏิเสธ JIT identity
- 5.33 IF optional email duplicated ใน claims THEN THE SYSTEM SHALL ไม่ให้ email profile parsing ปฏิเสธ JIT identity

## REQ-6: Conflict, concurrency และ recovery safety

**User Story:** As a security owner, I want race และ divergent identity fail closed ภายใต้ tenant scope, so that callback พร้อมกันไม่ merge คนละ external identity

**Acceptance Criteria (EARS):**

- 6.1 THE SYSTEM SHALL serialize Microsoft identity mutation ผ่าน identity mutation lock ปัจจุบัน
- 6.2 IF callback หลายรายการ JIT exact `(Provider, TenantId, Subject)` เดียวกันพร้อมกัน THEN THE SYSTEM SHALL สร้าง Admin ได้ไม่เกินหนึ่ง row
- 6.3 IF callback หลายรายการ JIT exact identity เดียวกันพร้อมกัน THEN THE SYSTEM SHALL append `jit-provision` audit ได้ไม่เกินหนึ่งรายการ
- 6.4 IF mandatory mapper ถูกเรียกพร้อมกันสำหรับ legacy row เดียวกัน THEN THE SYSTEM SHALL map สำเร็จได้ไม่เกินหนึ่งครั้ง
- 6.5 IF mandatory mapper ถูกเรียกซ้ำสำหรับ legacy row เดียวกัน THEN THE SYSTEM SHALL append binding audit ได้ไม่เกินหนึ่งรายการ
- 6.6 WHEN concurrent winner commit สำเร็จสำหรับ exact identity เดียวกัน THE SYSTEM SHALL ให้ callback ที่ตามมา resolve Admin รายเดียวกัน
- 6.7 IF tenant-aware unique constraint race เกิดขึ้น THEN THE SYSTEM SHALL re-resolve ด้วย exact `(Provider, TenantId, Subject)` เท่านั้น
- 6.8 IF tenant-aware unique constraint race เกิดขึ้น THEN THE SYSTEM SHALL ไม่ recovery ด้วย email หรือ `WorkforceEmailKey`
- 6.9 IF race recovery ไม่ resolve exact identity เดิม THEN THE SYSTEM SHALL คืน `identity-conflict`
- 6.10 IF tenant-aware identity tuple เป็น duplicate หรือ divergent THEN THE SYSTEM SHALL ไม่ mutate identity ใด
- 6.11 IF identity conflict เกิดขึ้น THEN THE SYSTEM SHALL ไม่สร้าง Admin session
- 6.12 IF identity conflict เกิดขึ้น THEN THE SYSTEM SHALL ไม่เปิดเผย `tid`, `oid`, email หรือ record ที่ชนใน browser reason
- 6.13 THE SYSTEM SHALL ให้ database unique constraint เป็น final concurrency guard ของ tenant-aware key
- 6.14 IF callbacks ใช้ email เดียวกันแต่ exact tuples ต่างกัน THEN THE SYSTEM SHALL ไม่ serialize ownership ด้วย email
- 6.15 IF unique conflict เกิดจาก optional email persistence THEN THE SYSTEM SHALL treat เหตุการณ์นั้นเป็น schema regression ไม่ใช่ identity conflict policy
- 6.16 THE SYSTEM SHALL ไม่ใช้ `GetByEmail`, email candidate list หรือ equivalent API ใน Microsoft resolver
- 6.17 THE SYSTEM SHALL ไม่ใช้ `WorkforceEmailKey` ใน recovery reader หรือ recovery policy

## REQ-7: Schema migration, offline cutover และ rollback

**User Story:** As an operator, I want upgrade จาก email identity ไป tenant-aware identity ด้วย verified offline mapping, so that deploy และ rollback ไม่สร้าง `oid` เท็จหรือทำลาย account/profile data เดิม

**Acceptance Criteria (EARS):**

- 7.1 THE SYSTEM SHALL เพิ่ม migration ใหม่ต่อจาก `20260830172117_Tier0EmployeeProfile`
- 7.2 WHEN migration `Up()` ทำงาน THE SYSTEM SHALL เพิ่ม nullable `TenantId uniqueidentifier` บน `admin.Users`
- 7.3 WHEN migration `Up()` ทำงาน THE SYSTEM SHALL ไม่ backfill `TenantId` ของ legacy row ก่อน manifest validation
- 7.4 WHEN migration `Up()` หรือ mapper ทำงาน THE SYSTEM SHALL ไม่ derive หรือ fabricate `oid` จาก email
- 7.5 WHEN migration `Up()` ทำงาน THE SYSTEM SHALL ไม่เปลี่ยน `Provider` ของ existing Admin row
- 7.6 WHEN migration `Up()` ทำงาน THE SYSTEM SHALL ไม่เปลี่ยน `Subject` ของ existing Admin row
- 7.7 WHEN migration `Up()` ทำงาน THE SYSTEM SHALL replace identity uniqueness จาก `(Provider, Subject)` เป็น `(Provider, TenantId, Subject)` ตาม REQ-2.12 ถึง REQ-2.14
- 7.8 THE SYSTEM SHALL คง migration-owner EF model ตรงกับ migration ที่ generated
- 7.9 THE SYSTEM SHALL คง runtime `ControlPlaneDbContext` EF configuration ตรงกับ migration-owner final identity shape
- 7.10 THE SYSTEM SHALL sync `PolDbContextModelSnapshot` กับ migration HEAD
- 7.11 THE SYSTEM SHALL sync `docker/migrations/schema.sql` กับ migration HEAD
- 7.12 THE SYSTEM SHALL sync fresh-database bootstrap assertions กับ migration HEAD
- 7.13 THE SYSTEM SHALL คง existing profile columns, indexes และ FKs ของ employee-profile work เดิม
- 7.14 THE SYSTEM SHALL ไม่เปลี่ยนข้อมูลใน `dbo.VibEmp` หรือ `dbo.branch`
- 7.15 THE SYSTEM SHALL ไม่เปลี่ยนข้อมูลใน `cfg.Offices` หรือ `cfg.Divisions` ระหว่าง identity migration
- 7.16 THE SYSTEM SHALL require production backup ก่อน apply identity migration
- 7.17 WHILE schema ถูก apply แต่ offline mapping ยังไม่ complete THE SYSTEM SHALL ห้าม Admin Microsoft application instance รับ traffic
- 7.18 WHEN rollback เกิดก่อนมี row ใด mapped/JIT ด้วย non-null `TenantId` THE SYSTEM SHALL restore pre-feature identity columns, indexes และ nullability โดยไม่เปลี่ยน existing identity/profile values
- 7.19 IF automatic rollback พบ non-null `TenantId` ที่ไม่มี verified reverse mapping THEN THE SYSTEM SHALL abort rollback ก่อนทิ้ง tenant identity data
- 7.20 IF post-cutover rollback ต้อง restore legacy email subject THEN THE SYSTEM SHALL require verified mapping manifest หรือ approved backup restore
- 7.21 WHEN migration `Down()` ทำงานใน safe state THE SYSTEM SHALL ไม่เปลี่ยน `EmployeeId`, `FirstName`, `LastName`, `OfficeId` หรือ `DivisionId`
- 7.22 WHEN migration `Down()` ทำงานใน safe state THE SYSTEM SHALL ไม่เปลี่ยน unrelated Provider/Subject rows
- 7.23 THE SYSTEM SHALL มี fresh-database migration test จาก empty database ถึง HEAD
- 7.24 THE SYSTEM SHALL มี upgrade migration test จาก `20260830172117_Tier0EmployeeProfile` ถึง HEAD
- 7.25 THE SYSTEM SHALL มี rollback migration test ที่พิสูจน์ fail-closed guard และ preservation ของ unrelated identity/profile data
- 7.26 THE SYSTEM SHALL assert identity index columns และ column order ด้วย SQL Server metadata
- 7.27 THE SYSTEM SHALL assert `TenantId` SQL type และ nullability ด้วย SQL Server metadata
- 7.28 THE SYSTEM SHALL assert existing profile FK shape หลัง upgrade และ rollback
- 7.29 THE SYSTEM SHALL ให้ generated idempotent migration script ผ่าน `scripts/check-migration-script.sh`
- 7.30 THE SYSTEM SHALL assert identity index uniqueness ด้วย SQL Server metadata
- 7.31 THE SYSTEM SHALL assert identity index filter ด้วย SQL Server metadata
- 7.32 THE SYSTEM SHALL assert tenant/provider CHECK shape ด้วย SQL Server metadata
- 7.33 THE SYSTEM SHALL ไม่แก้ไฟล์ migration `20260830172117_Tier0EmployeeProfile` หรือ migration history ก่อนหน้า
- 7.34 THE SYSTEM SHALL assert `WorkforceTenantBindings.TenantId` uniqueness ด้วย SQL Server metadata
- 7.35 THE SYSTEM SHALL assert nullable `Users.TenantId` FK shape ด้วย SQL Server metadata
- 7.36 WHILE prior `WorkforceIdentityMigrations` state ยัง pending THE SYSTEM SHALL ให้ mandatory tool complete historical email migration จาก legacy `Email` โดยไม่ใช้ `WorkforceEmailKey`
- 7.37 WHILE prior `WorkforceIdentityMigrations` state complete THE SYSTEM SHALL ให้ mandatory tool เริ่ม tenant-aware manifest validation โดยไม่ rewrite completed history
- 7.38 IF mandatory tool พบ persisted identity state หรือ manifest นอก approved contract THEN THE SYSTEM SHALL คืน non-zero โดยไม่เปลี่ยน identity/profile data
- 7.39 THE SYSTEM SHALL คง `docker/migrate-entrypoint.sh` invocation order เดิม
- 7.40 WHEN migration `Up()` ทำงาน THE SYSTEM SHALL alter `admin.Users.Email` เป็น nullable
- 7.41 WHEN migration `Up()` ทำงาน THE SYSTEM SHALL drop unique `IX_Users_Email`
- 7.42 WHEN migration `Up()` ทำงาน THE SYSTEM SHALL drop `IX_Users_WorkforceEmailKey`
- 7.43 WHEN migration `Up()` ทำงาน THE SYSTEM SHALL drop `admin.Users.WorkforceEmailKey`
- 7.44 THE SYSTEM SHALL ให้ mandatory tool รับ first-run manifest, SHA-256, exact target และ approval evidence ผ่าน ephemeral operator-controlled inputs
- 7.45 IF first-run AdminId snapshot ไม่ว่างและ manifest input ไม่มี THEN THE SYSTEM SHALL คืน non-zero ก่อน mapping write
- 7.46 THE SYSTEM SHALL commit historical-state completion, tenant-aware mapping, binding audits, version bumps และ manifest completion state ใน transaction เดียวกัน
- 7.47 THE SYSTEM SHALL persist aggregate manifest counts และ captured AdminId snapshot โดยไม่ persist `TenantId`, `ObjectId`, email หรือ `EmployeeId` ใน completion state
- 7.48 THE SYSTEM SHALL require completed tenant-aware manifest state ก่อน API startup validation ผ่าน
- 7.49 WHEN historical pending conversion derive canonical email จาก legacy `Email` THE SYSTEM SHALL จำกัดค่านั้นไว้ใน migration transaction เท่านั้น
- 7.50 WHEN tenant-aware manifest mapping target rows THE SYSTEM SHALL ไม่ใช้ historical email conversion result เพื่อเลือก `AdminId` หรือ `ObjectId`
- 7.51 THE SYSTEM SHALL ให้ mandatory tool output เฉพาะ aggregate counts, category และ generic failure reason
- 7.52 WHEN safe `Down()` restore `WorkforceEmailKey` THE SYSTEM SHALL reconstruct exact pre-feature nullable values จาก pre-validated historical completion state
- 7.53 THE SYSTEM SHALL คง migration-owner และ runtime EF models โดยไม่มี `WorkforceEmailKey` property หรือ shadow property ที่ HEAD
- 7.54 WHEN migration `Up()` ทำงาน THE SYSTEM SHALL preserve every existing non-null `Email` value
- 7.55 WHEN migration `Up()` ทำงาน THE SYSTEM SHALL ไม่เปลี่ยน existing Admin session หรือ audit row
- 7.56 WHEN migration `Up()` เริ่ม THE SYSTEM SHALL validate historical `WorkforceIdentityMigrations` state กับ every existing `WorkforceEmailKey` ก่อน destructive DDL
- 7.57 IF historical migration state completeและ existing keyไม่ตรง canonical legacy `Email` THEN THE SYSTEM SHALL abort `Up()` ก่อน drop columnหรือ index
- 7.58 IF historical migration state pendingและ existing `WorkforceEmailKey` มี non-null value THEN THE SYSTEM SHALL abort `Up()` ก่อน drop columnหรือ index
- 7.59 WHEN completed mandatory tool rerun THE SYSTEM SHALL ไม่ require manifest, digest, target หรือ approval inputsอีก
- 7.60 WHEN completed mandatory tool rerun THE SYSTEM SHALL validate captured AdminId snapshotยังเป็น final tenant-aware states
- 7.61 WHEN completed mandatory tool rerun THE SYSTEM SHALL validate post-completion Microsoft rowsด้วย final-state policyโดยไม่เพิ่มเข้า migration snapshot
- 7.62 THE SYSTEM SHALL ให้ mandatory tool ensure configured `WorkforceTenantBinding` ก่อน insert mapped TenantId ที่อยู่ใต้ FK
- 7.63 THE SYSTEM SHALL ไม่ persist manifest SHA-256, exact target credential หรือ approval evidence valueใน identity/audit tables
- 7.64 WHEN safe `Down()` restore pre-feature Email nullabilityและ unique index THE SYSTEM SHALL abortก่อน DDLหาก current dataไม่รองรับ old constraints
- 7.65 WHEN migrationหรือ mapperทำงาน THE SYSTEM SHALL preserve bound non-Microsoft historical rowsแบบ byte-equivalent

## REQ-8: Current tenant boundary และ future multi-tenant blockers

**User Story:** As a platform owner, I want schema รองรับ tenant-aware identity แต่ runtime ยัง pin tenant เดิม, so that foundation ดีขึ้นโดยไม่เปิด security boundary ใหม่ก่อนมี registry

**Acceptance Criteria (EARS):**

- 8.1 THE SYSTEM SHALL รับ Admin Microsoft runtime traffic จาก configured workforce tenant เดียวเท่านั้น
- 8.2 THE SYSTEM SHALL คง tenant-pinned Microsoft Authority requirement ปัจจุบัน
- 8.3 THE SYSTEM SHALL คง `WorkforceTenantBinding` singleton เป็น deployment-level tenant pin ในงานนี้
- 8.4 IF configured tenant ไม่ตรง persisted singleton THEN THE SYSTEM SHALL fail boot ตาม current behavior
- 8.5 THE SYSTEM SHALL ไม่เพิ่ม production tenant registry หรือ onboarding path ในงานนี้
- 8.6 THE SYSTEM SHALL ไม่ใช้ global `Email` uniqueness เป็น identity หรือ admission boundary
- 8.7 THE SYSTEM SHALL ไม่มี `WorkforceEmailKey` index ที่ migration HEAD
- 8.8 THE SYSTEM SHALL คง filtered unique `EmployeeId` แบบ global ใน current single-tenant runtime
- 8.9 THE SYSTEM SHALL document ว่า optional `Email` อาจ absent, mutable, reused หรือซ้ำข้าม tenant-aware identities
- 8.10 THE SYSTEM SHALL document ว่า `WorkforceEmailKey` ถูกลบและห้ามสร้าง tenant-aware bridge ทดแทนใน auth path
- 8.11 THE SYSTEM SHALL document ว่า `EmployeeId` uniqueness ต้องผ่าน HR-domain review และอาจต้อง scope ด้วย tenant ก่อนเปิดหลาย workforce tenant
- 8.12 THE SYSTEM SHALL document ว่า `WorkforceTenantBinding` singleton ต้องถูกแทนด้วย approved tenant registry/allowlist ก่อนเปิดหลาย workforce tenant
- 8.13 THE SYSTEM SHALL document ว่า issuer discovery, authority selection และ tenant onboarding ต้อง fail closed ต่อ registry ก่อนเปิดหลาย workforce tenant
- 8.14 THE SYSTEM SHALL document ว่า runtime ไม่มี legacy email fallback หลัง cutover
- 8.15 THE SYSTEM SHALL ไม่ใช้ tenant-aware DB index เป็นเหตุผลให้ยอมรับ tenant เพิ่มใน production
- 8.16 THE SYSTEM SHALL ไม่ scope optional email ด้วย tenantเพื่อสร้าง secondary identity key
- 8.17 THE SYSTEM SHALL require separately approved tenant-registry design ก่อน runtime ยอมรับ manifest หรือ token จาก tenant ที่สอง

## REQ-9: Authorization, session, audit, privacy และ regression boundaries

**User Story:** As an existing Admin and auditor, I want identity foundation เปลี่ยนโดยไม่เปลี่ยนสิทธิ์หรือเปิดเผย claims, so that security behavior รอบข้างคงเดิม

**Acceptance Criteria (EARS):**

- 9.1 THE SYSTEM SHALL ไม่เปลี่ยน Admin `Tier` จาก Microsoft claims
- 9.2 THE SYSTEM SHALL ไม่เปลี่ยน internal Role assignment จาก Microsoft claims
- 9.3 THE SYSTEM SHALL ไม่เปลี่ยน internal Permission จาก Microsoft claims
- 9.4 THE SYSTEM SHALL ไม่เปลี่ยน `MerchantAccess` จาก Microsoft claims
- 9.5 THE SYSTEM SHALL คง session rotation contract ปัจจุบัน
- 9.6 THE SYSTEM SHALL คง session reuse-detection contract ปัจจุบัน
- 9.7 THE SYSTEM SHALL คง session revocation contract ปัจจุบัน
- 9.8 THE SYSTEM SHALL คง CSRF contract ปัจจุบัน
- 9.9 THE SYSTEM SHALL คง login-success audit transaction behavior ปัจจุบัน
- 9.10 THE SYSTEM SHALL คง existing audit rows แบบ append-only โดยไม่ rewrite ประวัติ
- 9.11 THE SYSTEM SHALL ไม่บันทึก `tid` ใน application log หรือ audit
- 9.12 THE SYSTEM SHALL ไม่บันทึก `oid` ใน application log หรือ audit
- 9.13 THE SYSTEM SHALL ไม่บันทึก email ใน application log หรือ identity audit
- 9.14 THE SYSTEM SHALL ไม่บันทึก `employeeId` ใน application log หรือ identity audit
- 9.15 THE SYSTEM SHALL ไม่บันทึก authorization code ใน application log หรือ audit
- 9.16 THE SYSTEM SHALL ไม่บันทึก ID token ใน application log หรือ audit
- 9.17 THE SYSTEM SHALL ไม่บันทึก access token ใน application log หรือ audit
- 9.18 THE SYSTEM SHALL ไม่บันทึก session token หรือ cookie ใน application log หรือ audit
- 9.19 THE SYSTEM SHALL ไม่บันทึก Microsoft Graph response body ใน application log หรือ audit
- 9.20 THE SYSTEM SHALL ไม่ใส่ `tid`, `oid`, email หรือ `employeeId` ใน browser reason หรือ query string
- 9.21 THE SYSTEM SHALL ไม่เปลี่ยน Merchant Google authentication behavior
- 9.22 THE SYSTEM SHALL ไม่เปลี่ยน Merchant Microsoft authentication behavior
- 9.23 THE SYSTEM SHALL ไม่เปลี่ยน Admin Google retirement behavior
- 9.24 THE SYSTEM SHALL ไม่เพิ่ม dependency ใหม่
- 9.25 THE SYSTEM SHALL คง denied-auth audit transaction behavior บน fresh scope ปัจจุบัน
- 9.26 WHEN identity migration ถูก apply THE SYSTEM SHALL ไม่ revoke existing Admin sessions โดยอัตโนมัติ
- 9.27 WHILE existing Admin session ยัง valid THE SYSTEM SHALL คง session ownership ผ่าน internal `AdminId`
- 9.28 THE SYSTEM SHALL ให้ existing Admin sessions ใช้ expiry, rotation, reuse-detection และ revocation policy เดิม
- 9.29 THE SYSTEM SHALL ไม่บันทึก manifest contents ใน application, migration-tool หรือ CI output
- 9.30 THE SYSTEM SHALL ไม่บันทึก manifest filesystem path ใน non-operator application log
- 9.31 WHEN mandatory mapper รายงานผล THE SYSTEM SHALL แสดงเฉพาะ aggregate counts และ non-sensitive category
- 9.32 WHEN existing Admin ถูก offline-map THE SYSTEM SHALL preserve existing non-null `Email` value
- 9.33 WHEN existing Admin ถูก offline-map THE SYSTEM SHALL preserve existing employee profile values
- 9.34 WHEN Microsoft invite แบบ pre-bound ถูกสร้าง THE SYSTEM SHALL คง `create-scoped` audit behavior ปัจจุบัน
- 9.35 THE SYSTEM SHALL ไม่เปลี่ยน session ownership จาก internal `AdminId` เป็น external tuple
- 9.36 THE SYSTEM SHALL ไม่ใส่ optional email ลง server-side session token หรือ cookie
- 9.37 WHEN offline mapping append per-row audit THE SYSTEM SHALL identify actorเป็น migration/systemไม่ใช่ target Admin
- 9.38 THE SYSTEM SHALL ไม่ใส่ raw manifest approval evidence ลง audit payload

## REQ-10: Documentation และ verification

**User Story:** As an operator and reviewer, I want tests และ runbook พิสูจน์ทั้ง runtime กับ migration path จริง, so that cutover ไม่อาศัย assumption หรือ false-green check

**Acceptance Criteria (EARS):**

- 10.1 THE SYSTEM SHALL มี automated test สำหรับ missing `tid`
- 10.2 THE SYSTEM SHALL มี automated test สำหรับ missing `oid`
- 10.3 THE SYSTEM SHALL มี automated test ยืนยัน wrong tenant ถูกปฏิเสธก่อน identity/profile mutation
- 10.4 THE SYSTEM SHALL มี automated test ยืนยัน invalid workforce claims ถูกปฏิเสธก่อน session write
- 10.5 THE SYSTEM SHALL มี automated test ยืนยัน exact `(Provider, TenantId, Subject)` resolve Admin เดิม
- 10.6 THE SYSTEM SHALL มี automated test ยืนยัน email rename ภายใต้ `tid + oid` เดิมไม่สร้าง JIT Admin
- 10.7 THE SYSTEM SHALL มี automated test ยืนยัน same `oid` คนละ `tid` ไม่ resolve ข้าม tenant
- 10.8 THE SYSTEM SHALL มี automated SQL Server test ยืนยัน duplicate `(Provider, TenantId, Subject)` ถูก unique constraint ปฏิเสธ
- 10.9 THE SYSTEM SHALL มี automated test ยืนยัน legacy invited account ถูก offline-map ได้ครั้งเดียว
- 10.10 THE SYSTEM SHALL มี automated test ยืนยัน divergent final identity กับ manifest ทำให้ mapper fail closed
- 10.11 THE SYSTEM SHALL มี automated test ยืนยัน identity conflict ไม่ overwrite identity เดิม
- 10.12 THE SYSTEM SHALL มี automated test ยืนยัน JIT Admin ใหม่เป็น `Active`
- 10.13 THE SYSTEM SHALL มี automated test ยืนยัน JIT Admin ใหม่ไม่มี Role
- 10.14 THE SYSTEM SHALL มี automated test ยืนยัน JIT Admin ใหม่ไม่มี `MerchantAccess`
- 10.15 THE SYSTEM SHALL มี automated test ครอบ employee profile switch เปิด
- 10.16 THE SYSTEM SHALL มี automated test ยืนยัน profile failure rollback JIT Admin creation
- 10.17 THE SYSTEM SHALL มี SQL Server integration test ที่เรียก handler/repository/identity lock จริงสำหรับ same identity race
- 10.18 THE SYSTEM SHALL มี SQL Server integration test ที่เรียก mandatory mapper และ identity lock จริงสำหรับ concurrent manifest apply
- 10.19 THE SYSTEM SHALL มี migration tests ตาม REQ-7.23 ถึง REQ-7.55 บน SQL Server จริง
- 10.20 THE SYSTEM SHALL มี regression test ยืนยัน Merchant-user authentication behavior เดิม
- 10.21 THE SYSTEM SHALL ไม่มี test ที่ใช้ `.Skip` หรือ equivalent
- 10.22 THE SYSTEM SHALL ใช้ synthetic GUID, email และ employee profile fixture เท่านั้น
- 10.23 THE SYSTEM SHALL update Admin Microsoft OIDC runbook ด้วย offline-manifest preflight และ cutover order
- 10.24 THE SYSTEM SHALL update Admin Microsoft OIDC runbook ด้วย rollback cutoff และ forward-recovery path
- 10.25 THE SYSTEM SHALL update Admin Microsoft OIDC runbook ด้วย no-email-fallback startup gate
- 10.26 THE SYSTEM SHALL update Admin Microsoft OIDC runbook ด้วย multi-tenant blockers ตาม REQ-8.9 ถึง REQ-8.17
- 10.27 THE SYSTEM SHALL รัน `dotnet build pol-core.slnx --no-restore -warnaserror` และรายงานผล observed จริง
- 10.28 THE SYSTEM SHALL รัน `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` และรายงานผล observed จริง
- 10.29 THE SYSTEM SHALL รัน `dotnet test pol-core.slnx --filter "Category=Integration"` และรายงานผล observed จริง
- 10.30 THE SYSTEM SHALL รัน `scripts/check-migration-script.sh` และรายงานผล observed จริง
- 10.31 THE SYSTEM SHALL รัน `.ai/bin/check-secrets.sh --all` และรายงานผล observed จริง
- 10.32 THE SYSTEM SHALL รัน `scripts/spec-trace.sh tier0-microsoft-tenant-aware-identity` และรายงานผล observed จริง
- 10.33 IF verification command ไม่ได้รันหรือ infrastructure ล้มก่อน assertion THEN THE SYSTEM SHALL รายงานผลเป็น unverified หรือ blocked
- 10.34 THE SYSTEM SHALL มี automated test สำหรับ duplicate `tid`
- 10.35 THE SYSTEM SHALL มี automated test สำหรับ malformed `tid`
- 10.36 THE SYSTEM SHALL มี automated test สำหรับ empty `tid`
- 10.37 THE SYSTEM SHALL มี automated test สำหรับ duplicate `oid`
- 10.38 THE SYSTEM SHALL มี automated test สำหรับ malformed `oid`
- 10.39 THE SYSTEM SHALL มี automated test สำหรับ empty `oid`
- 10.40 THE SYSTEM SHALL มี automated test ยืนยัน same `oid` คนละ `tid` ไม่ bind ข้าม tenant
- 10.41 THE SYSTEM SHALL มี automated test ยืนยัน offline-mapped legacy invite คง authorization state เดิม
- 10.42 THE SYSTEM SHALL มี automated test ยืนยัน JIT Admin ใหม่เป็น `Scoped`
- 10.43 THE SYSTEM SHALL มี automated test ครอบ employee profile switch ปิด
- 10.44 THE SYSTEM SHALL มี automated test ยืนยัน profile failure ไม่เปลี่ยน existing tenant-aware identity tuple
- 10.45 THE SYSTEM SHALL มี automated test ยืนยัน profile failure rollback employee profile mutation
- 10.46 THE SYSTEM SHALL มี automated test ยืนยัน profile failure rollback related success/JIT/profile `UserAudits`
- 10.47 THE SYSTEM SHALL มี deterministic unit tests สำหรับ exact identity และ race-recovery policy
- 10.48 THE SYSTEM SHALL มี automated SQL Server test ยืนยัน duplicate non-Microsoft `(Provider, NULL, Subject)` ถูก tenant-aware index ปฏิเสธ
- 10.49 THE SYSTEM SHALL มี automated SQL Server test ยืนยัน migration-only rows ที่ `Subject=NULL` ไม่ชน tenant-aware identity index
- 10.50 THE SYSTEM SHALL มี automated SQL Server test ยืนยัน tenant/provider CHECK ปฏิเสธ invalid state
- 10.51 THE SYSTEM SHALL document preflight สำหรับ authoritative Entra export, Admin inventory review และ manifest approval evidence
- 10.52 THE SYSTEM SHALL document ว่า controlled transitional email bind ไม่ถูกเลือกและ runtime fallback เป็น prohibited state
- 10.53 THE SYSTEM SHALL มี automated test ยืนยัน offline mapping bump Admin `Version` หนึ่งครั้ง
- 10.54 THE SYSTEM SHALL มี automated test ยืนยัน offline mapping และ profile login mutation ไม่ bump `AuthorizationVersion`
- 10.55 THE SYSTEM SHALL มี automated test ยืนยัน restart หลัง offline mapping ผ่าน final persisted-state validation
- 10.56 THE SYSTEM SHALL มี automated test ยืนยัน invalid persisted identity state ทำให้ startup fail closed
- 10.57 THE SYSTEM SHALL มี automated test ยืนยัน existing Admin session ไม่ถูก revoke โดย identity migration
- 10.58 THE SYSTEM SHALL มี automated SQL Server test ยืนยัน nullable `Users.TenantId` FK ปฏิเสธ tenant ที่ไม่มี persisted binding
- 10.59 THE SYSTEM SHALL มี automated SQL Server test ยืนยัน duplicate `WorkforceTenantBindings.TenantId` ถูก unique constraint ปฏิเสธ
- 10.60 THE SYSTEM SHALL มี automated test ยืนยัน same email กับ different tenant-aware tuples อยู่ร่วมกันได้โดยไม่ resolve หรือ bind ข้ามกัน
- 10.61 THE SYSTEM SHALL มี automated SQL Server test ยืนยัน mandatory tool complete pending historical migration หลัง `WorkforceEmailKey` ถูก drop
- 10.62 THE SYSTEM SHALL มี automated SQL Server test ยืนยัน mandatory tool rerun สำเร็จหลัง tenant-aware manifest completion โดยไม่เขียน audit ซ้ำ
- 10.63 THE SYSTEM SHALL มี automated SQL Server test ยืนยัน mandatory tool fail closed บน invalid tenant-aware persisted state
- 10.64 THE SYSTEM SHALL มี automated callback test ยืนยัน email-less valid `tid + oid` login สำเร็จ
- 10.65 THE SYSTEM SHALL มี automated JIT test ยืนยัน email-less user persist `Email=NULL`
- 10.66 THE SYSTEM SHALL มี automated test ยืนยัน `preferred_username` ไม่ถูกใช้เป็น identity fallback
- 10.67 THE SYSTEM SHALL มี automated test ยืนยัน duplicate email claims ไม่เปลี่ยน valid `tid + oid` authentication decision
- 10.68 THE SYSTEM SHALL มี automated test ยืนยัน malformed email claim ไม่เปลี่ยน valid `tid + oid` authentication decision
- 10.69 THE SYSTEM SHALL มี automated test ยืนยัน new Microsoft invite require non-empty canonical `oid`
- 10.70 THE SYSTEM SHALL มี automated test ยืนยัน new Microsoft invite derive tenant จาก persisted singleton
- 10.71 THE SYSTEM SHALL มี automated test ยืนยัน new Microsoft invite ยอมรับ absent email
- 10.72 THE SYSTEM SHALL มี static test ยืนยัน Admin Microsoft auth sourceไม่มี `WorkforceEmailKey` reference
- 10.73 THE SYSTEM SHALL มี static test ยืนยัน Admin Microsoft resolverไม่มี email candidate lookup
- 10.74 THE SYSTEM SHALL มี automated SQL Server metadata test ยืนยัน `admin.Users.Email` nullable
- 10.75 THE SYSTEM SHALL มี automated SQL Server metadata test ยืนยันไม่มี unique `IX_Users_Email`
- 10.76 THE SYSTEM SHALL มี automated SQL Server metadata test ยืนยันไม่มี `admin.Users.WorkforceEmailKey` column
- 10.77 THE SYSTEM SHALL มี automated SQL Server metadata test ยืนยันไม่มี `IX_Users_WorkforceEmailKey`
- 10.78 THE SYSTEM SHALL มี automated test ยืนยัน missing manifest block required legacy mapping
- 10.79 THE SYSTEM SHALL มี automated test ยืนยัน incomplete manifest rollback ทุก mapping write
- 10.80 THE SYSTEM SHALL มี automated test ยืนยัน duplicate manifest `AdminId` ถูกปฏิเสธ
- 10.81 THE SYSTEM SHALL มี automated test ยืนยัน foreign-tenant manifest ถูกปฏิเสธ
- 10.82 THE SYSTEM SHALL มี automated test ยืนยัน mapper output ไม่มี manifest identity values
- 10.83 THE SYSTEM SHALL มี automated migration test ยืนยัน existing non-null `Email` ถูก preserve
- 10.84 THE SYSTEM SHALL มี static test ยืนยันเฉพาะ immutable historical migration และ new drop/rollback migration source เท่านั้นที่ยังมี retired `WorkforceEmailKey` token
- 10.85 THE SYSTEM SHALL มี automated migration test ยืนยัน existing Status และ `Tier` ถูก preserve
- 10.86 THE SYSTEM SHALL มี automated migration test ยืนยัน existing Role assignments ถูก preserve
- 10.87 THE SYSTEM SHALL มี automated migration test ยืนยัน existing `MerchantAccess` ถูก preserve
- 10.88 THE SYSTEM SHALL มี automated migration test ยืนยัน existing employee profile ถูก preserve
- 10.89 THE SYSTEM SHALL มี automated migration test ยืนยัน existing sessions ถูก preserve
- 10.90 THE SYSTEM SHALL มี automated migration test ยืนยัน existing audit rows ถูก preserve
- 10.91 THE SYSTEM SHALL มี automated migration test ยืนยัน existing internal `AdminId` ถูก preserve
- 10.92 THE SYSTEM SHALL มี automated test ยืนยัน nullable email ผ่าน application และ host contracts โดยไม่สร้าง placeholder
- 10.93 THE SYSTEM SHALL มี automated test ยืนยัน completed tool rerunโดยไม่มี manifest inputสำเร็จ
- 10.94 THE SYSTEM SHALL มี automated test ยืนยัน valid JITและpre-bound inviteหลัง completionไม่ทำให้ tool rerunล้ม
- 10.95 THE SYSTEM SHALL มี automated SQL Server test ยืนยัน toolสร้าง missing workforce tenant singletonก่อน mapping
- 10.96 THE SYSTEM SHALL มี automated SQL Server test ยืนยัน toolปฏิเสธ mismatched workforce tenant singletonก่อน mapping
- 10.97 THE SYSTEM SHALL มี automated test ยืนยัน offline binding auditใช้ migration/system actorและ target AdminId
- 10.98 THE SYSTEM SHALL มี automated test ยืนยัน inviteที่ไม่มี approval evidenceถูกปฏิเสธก่อน write
- 10.99 THE SYSTEM SHALL มี automated test ยืนยัน manifest SHA-256 mismatchถูกปฏิเสธก่อน database write
- 10.100 THE SYSTEM SHALL มี automated test ยืนยัน exact database target mismatchถูกปฏิเสธก่อน mapping write
- 10.101 THE SYSTEM SHALL มี automated test ยืนยัน missing manifest approval evidenceถูกปฏิเสธก่อน mapping write
- 10.102 THE SYSTEM SHALL มี automated migration test ยืนยัน completed historical key driftทำให้ `Up()` abortก่อน DDL
- 10.103 THE SYSTEM SHALL มี automated migration test ยืนยัน pending historical non-null keyทำให้ `Up()` abortก่อน DDL
- 10.104 THE SYSTEM SHALL มี automated migration test ยืนยัน unsafe old Email constraintsทำให้ `Down()` abortก่อน DDL
- 10.105 THE SYSTEM SHALL มี automated test ยืนยัน bound non-Microsoft historical rowถูก excludeจาก manifestและไม่ถูก mutate
- 10.106 THE SYSTEM SHALL มี automated test ยืนยัน optional email trim/length policyโดยไม่ใช้ corporate-domain eligibility
- 10.107 THE SYSTEM SHALL มี automated test ยืนยัน captured AdminId snapshotไม่ขยายเมื่อมี post-completion JIT

## Verification matrix

| กลุ่ม | Scenario ขั้นต่ำ | ครอบคลุม |
|---|---|---|
| OIDC protocol | code, PKCE, state, signature, issuer, audience, nonce, lifetime | REQ-1 |
| Claims | exact-one non-empty GUID `tid`/`oid`, canonical `D`, wrong tenant, optional email | REQ-1 |
| Identity model | nullable `TenantId`/`Email`, canonical oid subject, triple unique index, no email-key column | REQ-2 |
| Resolution | exact tuple only, email absent/rename/duplicate, same oid/different tenant, suspended | REQ-3 |
| Offline mapping | exact manifest coverage, legacy/invite mapping, divergent/duplicate/foreign rejection, atomic audit | REQ-4 |
| JIT/profile | email-less Active Scoped roleless JIT, switch on/off, full rollback | REQ-5 |
| Race/recovery | same-key JIT, concurrent mapper, exact-only conflict recovery | REQ-6 |
| Migration | empty-to-HEAD, pre-feature-to-HEAD, old pending state, metadata, guarded rollback | REQ-7 |
| Tenant readiness | current pin retained, no email bridge, future blockers documented | REQ-8 |
| Regression/privacy | RBAC/session/audit unchanged, no identity/manifest logging, Merchant auth unchanged | REQ-9 |
| Repository gates | build, unit, integration, static auth scan, migration script, secret scan, spec trace | REQ-10 |

## Edge Cases & Open Questions

### Decisions applied; pending requirements approval

| ID | Decision | Alternative rejected | Rationale |
|---|---|---|---|
| A1 | `(tid, oid)` ต่างกันคือ external identity คนละราย และหนึ่ง `admin.Users` row มี identity เดียว | identity collection ต่อ user | ตรง current aggregate และ objective ไม่ขยาย account-linking |
| A2 | Production ยังรับเพียง tenant ที่ pin อยู่ | tenant registry/allowlist ในงานเดียวกัน | ยังไม่มี tenant admission governance |
| A3 | ใช้ verified offline manifest keyed ด้วย `AdminId + tid + oid` | controlled first-login email bind | ไม่มี runtime email takeover window และ preserve AdminId ได้ |
| A4 | Exact tuple login ไม่ auto-refresh optional `Email` | refresh ทุก login | กัน profile mutation/collision แฝงใน authentication |
| A5 | `Email` nullable/non-unique และ `WorkforceEmailKey` ถูกลบทั้งหมด | คง global email ownership/index | email-less และ same-email/different-identity ต้องไม่ถูก DB block |
| A6 | New Microsoft invite pre-bound ด้วย pinned tenant + required oid | email-only unbound invite | ไม่มี secure runtime candidate keyอื่นโดยไม่สร้าง invite-token featureใหม่ |
| A7 | Mandatory tool derive legacy canonical emailใน memoryเฉพาะ old pending conversion | คง `WorkforceEmailKey` column | ทำให้ final model/schemaไม่มี retired bridgeโดยยังคง entrypoint orderเดิม |
| A8 | Offline mapping bump `Version` หนึ่งครั้งแต่ไม่ bump `AuthorizationVersion` | preserve Version byte-for-byte | identity resource เปลี่ยนจริงแต่ effective authorizationไม่เปลี่ยนและ sessionsต้องอยู่ต่อ |
| A9 | Capture first-run AdminId snapshot; completed rerunไม่ใช้ manifestและยอมรับ post-completion rows | persist/update manifestทุก deployment | manifestต้อง ephemeralและ JITไม่ใช่ migration residue |
| A10 | Optional token email usableเมื่อ exact-one, trim non-empty, lengthไม่เกิน 320 | ไม่ persist token emailหรือใช้ corporate canonicalizer | deterministic contact policyโดยไม่มี eligibility power |
| A11 | คง singleton FK; same-oid/different-tenant separationเป็น policy/index semanticsจนมี registry | remove FKตอนนี้ | current tenant pinยังเป็น DB boundary |
| A12 | Mandatory tool insert/verify tenant singletonก่อน mapping | require pre-existing singleton | entrypointรัน toolก่อน APIที่เคยเป็น initializer |
| A13 | Offline auditใช้ migration/system actorและ target AdminId | targetเป็น actor | audit attributionต้องตรงผู้กระทำ |
| A14 | Invite oidมาจาก verified Entra exportพร้อม approval evidence | trust Super inputอย่างเดียวหรือเพิ่ม Graph directory permission | ป้องกัน pre-bindผิด objectโดยไม่เพิ่ม runtime permission |
| A15 | Verify manifest SHA-256, exact DB targetและ approval evidence | trust mounted fileอย่างเดียวหรือ signed manifest | bind artifactกับ approval/targetโดยใช้ BCLเดิม |
| A16 | Validate historical completion/key invariantก่อน dropและ reconstructใน safe Down | per-row PII snapshotหรือ no Down | restore exact old shapeโดยไม่เพิ่ม email snapshot |
| A17 | Bound non-Microsoft historical rowsถูก excludeและคงเดิม | opt-inหรือ mapทั้งหมดเป็น Microsoft | preserve Admin Google retirement/non-Microsoft contract |
| A18 | ขยาย scopeเฉพาะ Admin create route/requestและ nullable email responsesใน `src/Hosts/Api/Program.cs` | คง Programเดิมหรือตัด invite preservation | pre-bound inviteและ nullable Emailเปลี่ยน wire contractอย่างหลีกเลี่ยงไม่ได้ |

ไม่มี open decision ค้าง P1-P4 และ Q1-Q10 ได้รับคำตอบครบเมื่อ 2026-09-02

### Code/domain fit check

Current `Admins.Domain.Users.User` เก็บ external identity inline เป็น `Provider + Subject`, บังคับ `Email` non-null,
สร้าง Microsoft JIT subjectจาก canonical email และมี `WorkforceEmailKey` property Current repository, candidate policy,
startup validator และ mandatory toolอ่าน bridgeนี้ จึงต้องเปลี่ยนเป็น typed `TenantId/ObjectId` path พร้อม nullable
contact emailและ exact tuple lookup ส่วน existing `WorkforceIdentityMigrator` อยู่ใน scopeเพื่อรักษา mandatory
post-schema invocation โดยไม่แก้ `docker/migrate-entrypoint.sh`

### Edge cases ที่ requirement ครอบแล้ว

- token มี `tid` หรือ `oid` ซ้ำ แม้ค่าทุกตัวเท่ากัน (REQ-1.28, 1.31)
- GUID เป็น empty, malformed หรือ representation ต่างกัน (REQ-1.11-1.15, 1.29-1.30, 1.32-1.33)
- token ไม่มี email หรือมี unusable/duplicate email แต่ `tid + oid` valid (REQ-1.40-1.45)
- exact identity เดิมมาพร้อม email ใหม่หรือไม่มี email (REQ-3.3-3.8, 3.23)
- email เดียวกันแต่ identity tuples ต่างกัน (REQ-2.38, 3.21-3.22, 5.28, 6.14)
- same oid จาก tenant อื่น (REQ-3.9-3.14)
- legacy Microsoft, unbound invite, Suspended row และ divergent manifest (REQ-4)
- incomplete, duplicate, extra, malformed หรือ foreign-tenant manifest (REQ-4.7-4.9, 4.24-4.31, 4.39-4.40)
- profile switch เปิดแล้ว HR/Graph/profile mutation ล้มหลัง JIT (REQ-5.13-5.20)
- unique-index race หลัง lock/repository race หรือ direct concurrent writer (REQ-6)
- old workforce migration pendingหลัง final schemaไม่มี `WorkforceEmailKey` (REQ-7.36-7.37, 7.49-7.50)
- rollback หลัง offline mapping ซึ่งไม่มี safe automatic reverse identity (REQ-7.18-7.20, 7.52)
- schema รองรับ same oid/different tenant แต่ runtime ยัง deny tenant ที่ไม่ pin (REQ-2.16, REQ-8)

### Findings log

| ID | Category | Finding | Decision | Resolution |
|---|---|---|---|---|
| F1 | logical inconsistency | startup validator เดิมบังคับ Microsoft Subject เท่ากับ email | A | final startupรับเฉพาะ canonical tenant-aware Microsoft stateและไม่อ่าน bridge |
| F2 | logical inconsistency | email-only invite claimไม่ได้เมื่อห้าม email fallback | A | new invite require oidและ pre-bind pinned tuple; legacy inviteอยู่ใน manifest required set |
| F3 | logical inconsistency | employee-profile runbook ให้ unlink EmployeeId เมื่อ email เปลี่ยน | A | supersede adviceและคง EmployeeId semanticsเดิม |
| F4 | ambiguity | cross-tenant/same-email rowอาจกลายเป็น candidate | A | ลบ candidate queryทั้งหมด; exact tupleหรือ JITเท่านั้น |
| F5 | ambiguity | exact oid loginจะ refresh emailหรือไม่ | A | no automatic refresh; emailเป็น nullable contactเท่านั้น |
| F6 | ambiguity | invalid claimsห้าม DB writeอาจชน denied-auth auditเดิม | A | อนุญาต generic denied auditบน fresh scopeเท่านั้น |
| F7 | ambiguity | audit action `microsoft-email-bind` มีคำว่า email | A | คง literalเพื่อ audit compatibilityเฉพาะ offline map; payloadไม่มี emailและ runtimeไม่ bind |
| F8 | ambiguity | clausesจาก source specsที่ supersedeไม่ชัด | A | update supersession matrixให้รวม email eligibility/invite/migration |
| F9 | conflicting constraints | runtime tenant pinไม่มี DB guardบน Users.TenantId | A | เพิ่ม provider CHECK, binding unique keyและ nullable FK |
| F10 | conflicting constraints | tenant identityอาจปน global EmployeeId semantics | A | EmployeeIdยังเป็น global HR-profile conflict ไม่ใช่ auth identity |
| F11 | conflicting constraints | staged email bridgeมี one-time takeover risk | A | reject staged strategyและใช้ verified offline manifest |
| F12 | gap | migrationไม่ระบุผลต่อ existing sessions | A | sessionsคงผูก AdminIdและไม่ auto-revoke |
| F13 | gap | upgrade predecessorไม่ชัดท่ามกลาง employee-profile WIP | A | pin `20260830172117_Tier0EmployeeProfile` และห้ามแก้ migrationเดิม |
| F14 | gap | race testsอาจใช้ fakeโดยไม่พิสูจน์ SQL lock/constraint | A | require real SQL Server handler/repository/tool/lock integration |
| F15 | gap | claim error casesรวมหลาย condition | A | คงแยก missing/duplicate/malformed/empty tidและ oidเป็น stable IDs |
| F16 | unstated assumption | Version/AuthorizationVersion ของ mappingไม่กำหนด | A | mapping bump Versionครั้งเดียวและไม่ bump AuthorizationVersion |
| F17 | scope blocker | mandatory toolต้องเข้าใจ final oid state | A | include `WorkforceIdentityMigrator`; entrypointและ orderเดิม |
| F18 | logical inconsistency | email-less JITชน non-null Email column | A | Email nullableและห้าม placeholder |
| F19 | logical inconsistency | same-email/different tupleชน unique Email index | A | drop global unique Email index |
| F20 | logical inconsistency | drop WorkforceEmailKeyก่อน old pending toolรัน | A | tool derive legacy canonical valueจาก Emailใน memoryและไม่ใช้เป็น final mapping |
| F21 | gap | offline manifestอาจขาด Suspendedหรือ unbound invite | A | exact coverageครอบ Provider=microsoftหรือ Subject=NULL; missing row blockทั้งหมด |
| F22 | security | manifestมี immutable identifiersที่ห้าม log | A | ephemeral input, no repository persistence, aggregate-only output/audit |
| F23 | security | optional emailยังอาจมีอำนาจผ่าน repositoryหรือ DB conflict | A | static no-reference/no-candidate gatesและลบ unique ownership index |
| F24 | compatibility | historicalและ new rollback migration sourceยังต้องอ้าง retired token | A | allowlistเฉพาะ migration files; current domain/runtime/application/tool sourceห้ามมี token |
| F25 | logical inconsistency | current-row manifest coverageทำให้ post-completion JITบังคับใช้ manifestใหม่ทุก rerun | A | capture immutable first-run AdminId snapshot; completed rerunไม่ require manifestและยอมรับ valid new rows |
| F26 | ambiguity | usable optional emailไม่มี normalization boundary | A | exact-one, trim non-empty, maximum 320, no corporate-domain gate |
| F27 | ambiguity | same oid/different tenantชน singleton FK | A | คง FK; separationเป็น policy/triple-index semanticsและ foreign tenant persistถูกปฏิเสธใน phaseนี้ |
| F28 | conflicting constraints | toolรันก่อน API initializerแต่ mapperต้องใช้ persisted tenant FK | A | tool insert/verify singletonภายใต้ ordered locksก่อน mapping |
| F29 | conflicting constraints | offline mapใช้ target Adminเป็น actorทั้งที่ targetไม่ได้ initiate | A | migration/system actor + target AdminId + approval correlation |
| F30 | gap | invite oidตรวจแค่ GUID shapeจึง pre-bindผิด objectได้ | A | require verified Entra exportและ approval evidenceก่อน create |
| F31 | gap | manifest approvalไม่ bind fileเข้ากับ target | A | verify SHA-256, exact DB targetและ approval evidenceก่อน write |
| F32 | gap | safe Downอ้าง exact WorkforceEmailKeyแต่ไม่มี pre-drop invariant | A | validate completed/pending key stateก่อน DDLและ reconstructตาม validated state |
| F33 | unstated assumption | bound non-Microsoft historical rowsไม่อยู่ manifest set | A | preserve/exclude rowsเหล่านี้และคง Admin Google retirement behavior |
| F34 | scope blocker | pre-bound inviteและ nullable Admin email wireต้องแก้ `src/Hosts/Api/Program.cs` ซึ่งไม่อยู่ใน scopeเดิม | A | อนุมัติ narrow expansionเฉพาะ create route/requestและ nullable email response contracts |

Findings log anchor `501b1ed` (requirements pathยัง untrackedที่ anchor); F1-F24 มาจาก auditsก่อนหน้าและ F25-F34
มาจาก no-email amendment audit 2026-09-02 ทุก findingมี decisionแล้ว

## Official claim references

สรุปและแหล่ง Microsoft Learn อยู่ใน `microsoft-entra-claim-research.md` โดยอ้าง:

- https://learn.microsoft.com/en-us/entra/identity-platform/id-token-claims-reference
- https://learn.microsoft.com/en-us/entra/identity-platform/v2-protocols-oidc
- https://learn.microsoft.com/en-us/entra/identity-platform/claims-validation
