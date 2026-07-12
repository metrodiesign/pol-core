# Requirements: rf1-schema-reset — Big-bang foundation ของ v5 restructure

> Status: approved 2026-07-11
> Derived from: design.md (approved 2026-07-11) — Design-First; ทุก REQ อ้าง design section ต้นทาง

## Overview

pol-core เป็น payment orchestration platform ของบริษัทในเครือ 3 ระบบ (vPrivilege/vCommerce/vSouvenir) ที่กำลัง restructure ตาม `Payment_Orchestration_Schema_Design-v5.md` (master plan approved 2026-07-11). rf1 คือ foundation: ย้ายจาก schema เดียว `VCentralPay` + RLS ชั้นเดียว ไป multi-schema + RLS layer 1 แบบ 3 branch (merchant / platform Super / platform Scoped), rename actor model ทั้งระบบ (Tenant→Merchant, AdminAccount→PlatformUser, ProducerAccount→MerchantUser), Money → DECIMAL(19,4), และ reset migration chain — โดย business behavior ฝั่ง server คงเดิมทั้งหมด (funnel + payment เดิมใช้งานได้ตลอด) ยกเว้น change โดยเจตนา 4 อย่าง: (ก) wire rename (ข) funnel auth เปลี่ยนเป็น session cookie (ค) Money wire เป็น string (ง) provisioning merchant กลายเป็น Super-only ที่ DB floor (REQ-3.7 — control ใหม่)

## REQ-1: Persistence layer หลาย schema

**User Story:** As a platform engineer, I want ทุก entity ถูก map ลง schema ตาม bounded context, so that security boundary/workload แยกชัดตาม v5 และไม่มีตารางหลุดไป schema ผิดโดยเงียบ

**Acceptance Criteria (EARS):** *(design: Architecture Overview ข้อ 1-2, Schema map, T1-T3, T10)*
- 1.1 THE SYSTEM SHALL ใช้ DbContext เดียวชื่อ `PolDbContext` (rename จาก `ProducerDbContext`) โดยคง keyed `"admin"` registration บน connection `pol_admin`
- 1.2 THE SYSTEM SHALL map ทุก entity ด้วย `ToTable(name, schema)` ตาม schema map ใน design (shop / txn / admin / merch / dbo) โดยไม่มี `HasDefaultSchema`
- 1.3 THE SYSTEM SHALL คงชื่อตารางแบบพหูพจน์ตาม convention เดิม
- 1.4 IF entity ใดไม่ระบุ schema หรือระบุ schema นอกเซ็ต {shop, txn, admin, merch} และไม่อยู่ใน named exception list ราย entity (มีตัวเดียว: `DataProtectionKey` → dbo) THEN Architecture test SHALL fail
- 1.5 THE SYSTEM SHALL คง DB catalog ชื่อ `VCentralPay`

## REQ-2: Actor model rename + module merge

**User Story:** As a developer, I want domain language ตรงกับ v5 (Merchant/PlatformUser/MerchantUser) ทั้ง code/DB/config, so that ไม่มี vocabulary เก่าปนใหม่ให้สับสนหรือ grep พลาด

**Acceptance Criteria (EARS):** *(design: Schema map, C# rename map, Architecture Overview ข้อ 3-4)*
- 2.1 THE SYSTEM SHALL rename `Tenant`→`Merchant`, `TenantCode`→`MerchantCode` (allowlist `vprivilege`/`vcommerce`/`vsouvenir` เดิม + CHECK constraint ที่ตาราง), `TenantStatus`→`MerchantStatus`
- 2.2 THE SYSTEM SHALL rename `AdminAccount`→`PlatformUser`, `AdminTenantAssignment`→`PlatformMerchantAccess` (unique (PlatformUserId, MerchantId)) และตาราง admin session/audit ตาม rename map
- 2.3 THE SYSTEM SHALL rename `ProducerAccount`→`MerchantUser` พร้อมคอลัมน์ `MerchantId uniqueidentifier NULL` และ drop ตาราง `ProducerTenantAssignments` (ดูดซับความสัมพันธ์ 1 merchant/account)
- 2.4 THE SYSTEM SHALL merge module `Tenant` + `Producer` เป็น `Merchants` (Domain/Application/Infrastructure + test project เดียว) และลบ `src/Modules/Identity` shells
- 2.5 THE SYSTEM SHALL rename คอลัมน์/property `TenantId`→`MerchantId` ทุกตาราง รวม `VaultSecrets` composite key (MerchantId, Name) และ `VaultRevealAudits` unique (MerchantId, Seq)
- 2.6 THE SYSTEM SHALL คงตาราง RBAC 2 catalog (admin + merchant-user) พฤติกรรมเดิม — เปลี่ยนเฉพาะชื่อ/schema ตาม map รวมถึง rename permission key `producer.approve`→`merchant_user.approve`, `producer.reject`→`merchant_user.reject` (seed + gate + parity guard พร้อมกัน; รวมเข้า iam เป็นงาน rf2)
- 2.7 WHEN rename sweep เสร็จ THE SYSTEM SHALL ไม่เหลือ token `Tenant`/`tenant`/`Producer`/`producer` ใน `src/ tests/ docker/ .github/` นอกรายการยกเว้นตามหมวดใน design (PSP vocabulary, principal names, comment อ้างประวัติ) — tasks.md SHALL แตกหมวดเป็น concrete exception list ตอน implement (filesystem = ground truth)

## REQ-3: RLS layer 1 — merchant isolation + scoped admin ที่ DB

**User Story:** As a security owner, I want tenant isolation + scoped-admin scope บังคับที่ DB จริง (ไม่ใช่ app layer อย่างเดียว), so that mis-scoped query ของ admin หรือ merchant รั่วข้ามกันไม่ได้แม้ code ผิด

**Acceptance Criteria (EARS):** *(design: RLS section, Sequence 1-2, T4-T5, Error Handling)*
- 3.1 WHEN `SESSION_CONTEXT('MerchantId')` ตรงกับคอลัมน์ MerchantId ของแถว THE SYSTEM SHALL ให้แถวนั้นผ่าน predicate (merchant branch)
- 3.2 WHEN `SESSION_CONTEXT('MerchantId') = Guid.Empty` และ `UserId` เป็น `admin.PlatformUsers` ที่ `Tier = Super` THE SYSTEM SHALL ให้เห็นทุกแถว (platform Super — **เช็ค tier จริง ไม่ใช่ absence ใน PMA**; deviate จาก doc §8 โดยเจตนาเพื่อปิด fail-open F2)
- 3.3 WHEN `SESSION_CONTEXT('MerchantId') = Guid.Empty` และ `UserId` ไม่ใช่ Super และมีแถว `admin.PlatformMerchantAccess` ตรงกับ merchant ของแถว THE SYSTEM SHALL ให้เห็นเฉพาะแถวของ merchant ที่ assigned (platform Scoped)
- 3.4 IF `SESSION_CONTEXT('MerchantId') = Guid.Empty` และ `SESSION_CONTEXT('UserId')` IS NULL THEN THE SYSTEM SHALL คืน 0 แถว (sentinel เปล่า = deny — guard ห้ามถอด)
- 3.11 IF platform actor ที่ `Tier = Scoped` ไม่มีแถวใน `admin.PlatformMerchantAccess` เลย THEN THE SYSTEM SHALL คืน 0 แถว (fail-closed — actor ใหม่ที่ยังไม่ assign เห็นศูนย์ ไม่ใช่เห็นหมด)
- 3.5 IF ไม่มี SESSION_CONTEXT เลย THEN THE SYSTEM SHALL คืน 0 แถวและ BLOCK ทุก insert บนตารางใต้ policy
- 3.6 THE SYSTEM SHALL ครอบ policy `sec.MerchantIsolationPolicy` (STATE=ON, SCHEMABINDING=ON): FILTER+BLOCK บน shop.Products/Carts/CartItems(parent)/CheckoutSessions/Orders, txn.PaymentSessions/PspConnections/IdempotencyRecords, merch.VaultSecrets, merch.Merchants(self-row) และ BLOCK-insert บน txn.OutboxMessages, merch.VaultRevealAudits
- 3.7 IF platform Scoped INSERT แถว `merch.Merchants` ใหม่ THEN THE SYSTEM SHALL BLOCK (provisioning merchant = Super-only ที่ DB — control ใหม่โดยเจตนา)
- 3.8 THE SYSTEM SHALL ถอด `pol_admin` ออกจาก role `pol_rls_bypass` (bootstrap ลบ ADD MEMBER + guarded DROP MEMBER กัน stale volume)
- 3.9 THE SYSTEM SHALL คง `pol_rls_bypass` เฉพาะ login-less EXECUTE AS users และ procs 3 ตัวทำงานเดิม: `sec.usp_resolve_webhook_merchant` (user `pol_resolver`), `sec.usp_resolve_order_summary`, `sec.usp_vault_audit_head`
- 3.10 THE SYSTEM SHALL สร้างทุก schema ด้วย owner `dbo` (ownership chaining ให้ predicate อ้าง `admin.PlatformMerchantAccess` ข้าม schema ได้โดยผู้ query ไม่ต้องมี SELECT)

## REQ-4: Session context stamping 2 key

**User Story:** As a security owner, I want ทุก connection stamp MerchantId+UserId แบบ fail-safe, so that RLS branch ทำงานถูกตาม actor จริงและไม่มี stale context ข้าม pooled connection

**Acceptance Criteria (EARS):** *(design: Interceptor contract, T13-T14, Error Handling)*
- 4.1 WHEN connection เปิดและ actor context bound THE SYSTEM SHALL stamp ทั้ง `MerchantId` และ `UserId` เสมอ (UserId = NULL explicit เมื่อไม่มี) ด้วย `@read_only=1`
- 4.2 WHEN ไม่มี actor context (รวม unbound `AdminActorContext`) THE SYSTEM SHALL ไม่ stamp key ใดเลย และห้าม throw
- 4.3 IF bound actor มี `MerchantId == Guid.Empty` และ `UserId == null` THEN THE SYSTEM SHALL throw `InvalidOperationException` ก่อนถึง DB
- 4.4 THE SYSTEM SHALL ใช้ actor source แยกต่อ registration: default = `IActorContext` (`HttpActorContext`/`WorkerActorContext`), keyed "admin" = `AdminActorContext` (`{Guid.Empty, PlatformUserId}` จาก admin session middleware)
- 4.5 THE SYSTEM SHALL register keyed "admin" options ผ่าน DI callback โดยคง EF model cache (ห้าม hand-build `DbContextOptions` ต่อ request)
- 4.6 THE SYSTEM SHALL แทน `ITenantContext`/`ITenantScoped`/`TenantGuardBehavior`/`AmbientTenant` ด้วย `IActorContext`/`IMerchantScoped`/`MerchantGuardBehavior`/`AmbientActor` โดย semantics เดิม และ `AmbientActor.Begin` reject `Guid.Empty`
- 4.7 WHILE Worker ประมวลผล message THE SYSTEM SHALL bind actor ต่อ message ผ่าน `IActorScope.Begin(merchantId)` (ไม่มี UserId)

## REQ-5: Auth surface — ตัด Bearer, funnel ใช้ session

**User Story:** As a security owner, I want auth เหลือ BFF session 2 ชุด (admin, merchant-user) + anon token paths, so that ไม่มี Google id-token Bearer surface ค้างและ default scheme ชัดเจน

**Acceptance Criteria (EARS):** *(design: Auth policies + default scheme note)*
- 5.1 THE SYSTEM SHALL ถอด `AddGoogleIdTokenAuthentication` และ policy `tenant` ออกทั้งหมด (ไม่มี JwtBearer scheme ใน app)
- 5.2 THE SYSTEM SHALL ตั้ง default authentication scheme เป็น MerchantUserSession scheme แบบ explicit
- 5.3 THE SYSTEM SHALL ให้ policy `merchant-user` เป็น single-scheme (session cookie เท่านั้น — เอา `JwtBearerDefaults` ออกจาก `AddAuthenticationSchemes`)
- 5.4 THE SYSTEM SHALL ให้ funnel endpoints ทั้งหมด (/products, /carts, /checkouts, /orders ที่ protected รวม `POST /orders/{orderId}/summary/resend`, /payments/sessions, `GET /reports/reconciliation`) `RequireAuthorization("merchant-user")` — ครบทุก endpoint ที่เคยใช้ policy `tenant` (REQ-5.1 ลบ policy นั้น — endpoint ที่ตกหล่นจะ fail ตอน boot/test)
- 5.5 THE SYSTEM SHALL rename cookie `__Host-prd_session`→`__Host-mch_session` และ csrf `prd_csrf`→`mch_csrf`
- 5.6 THE SYSTEM SHALL คง admin BFF flow เดิมทำงานครบ (login → session → RBAC resolve สดต่อ request → CSRF)
- 5.7 THE SYSTEM SHALL คง endpoints anonymous เดิม: order summary token, `POST /webhooks/{pspConnectionId}` เดิม, `/merchant-users/register`, `/merchant-users/auth/login`

## REQ-6: Money DECIMAL(19,4) ทุกชั้น

**User Story:** As a platform engineer, I want Money เป็น decimal exact ทุกชั้น + wire เป็น string, so that ไม่มี precision loss จาก minor-unit conversion หรือ IEEE754 double (ปิด gap 22 / ADR 16)

**Acceptance Criteria (EARS):** *(design: Money section, T8)*
- 6.1 THE SYSTEM SHALL ใช้ `Money { decimal Amount, string Currency }` ใน SharedKernel โดยไม่มี `MinorUnits` เหลือใน src/tests
- 6.2 IF สร้าง Money ด้วย scale > 4 หรือค่าติดลบ THEN THE SYSTEM SHALL reject ด้วย `ArgumentException` (ไม่ silent round)
- 6.3 THE SYSTEM SHALL map Money เป็น EF complex type คอลัมน์ `{Prop}Amount decimal(19,4)` + `{Prop}Currency char(3)` (override ชื่อ default `{Prop}_Amount`)
- 6.4 THE SYSTEM SHALL serialize Money บน wire เป็น `{"amount": "<string fixed 4 decimals เช่น 1500.0000>", "currency": "<ISO4217>"}` และ register converter ใน**ทุก serializer ที่ contract ผ่าน**: `ConfigureHttpJsonOptions` + outbox/worker serializer options (event contracts ถือ Money ตาม 6.6)
- 6.5 IF JSON amount เป็น number (ไม่ใช่ string) THEN THE SYSTEM SHALL reject เป็น 400 RFC 9457
- 6.6 THE SYSTEM SHALL rewrite DTO + event contracts (`CheckoutConfirmed`, `PaymentPaid`, `CustomerOrderNotification`) ให้ถือ Money object แทน flat `*MinorUnits` + `Currency`
- 6.7 THE SYSTEM SHALL ให้ PSP adapters (2C2P/Omise) format ยอด major-unit จาก decimal ตรง (ตัดโค้ดหาร 100)
- 6.8 IF บวก Money ต่างสกุล THEN THE SYSTEM SHALL throw (semantics เดิม)

## REQ-7: Migration reset

**User Story:** As a platform engineer, I want migration chain ใหม่ 3 ไฟล์ที่สร้าง DB สมบูรณ์จากศูนย์, so that fresh environment ได้ schema + security floor + seed ครบโดยไม่พึ่ง chain ประวัติ 25 ตัว

**Acceptance Criteria (EARS):** *(design: Architecture Overview ข้อ 1, Sequence 3, T9, Error Handling)*
- 7.1 THE SYSTEM SHALL ลบ migrations เดิมทั้ง 25 ไฟล์ + `ProducerDbContextModelSnapshot`
- 7.2 THE SYSTEM SHALL มี `InitialSchema` (generated) ครอบทุกตาราง/index/EnsureSchema ตาม model ใหม่
- 7.3 THE SYSTEM SHALL มี `SecurityObjects` (hand SQL) บรรจุ: ตาราง `merch.RegistrationNotices` (raw, นอก EF model) + index/grants ของมัน, `sec.fn_merchant_predicate`, `sec.fn_cartitem_predicate`, procs 3 ตัว, security policy, GRANT matrix ทั้งหมด — สร้าง clause จาก tuple list (schema, table) ห้าม interpolate prefix
- 7.4 THE SYSTEM SHALL มี `SeedData` (hand SQL) บรรจุ seed RBAC 2 catalog เดิมครบ (key rename ตาม REQ-2.6: `merchant_user.approve`/`merchant_user.reject`) + master data (Positions/Offices/Levels/Divisions) จาก seed เดิม
- 7.5 THE SYSTEM SHALL คง index/constraint พิเศษเดิมครบ: Outbox lease index, VaultRevealAudits unique (MerchantId, Seq), PaymentSessions RowVersion + unique indexes เดิม, CHECK allowlist บน merch.Merchants
- 7.6 IF migration `Down()` ถูกเรียก THEN THE SYSTEM SHALL DROP policy/fn/proc แบบ `IF EXISTS` ย้อนลำดับ (ALTER SECURITY POLICY ไม่ transactional)
- 7.7 WHEN รัน fresh container → bootstrap → `ef database update` จากศูนย์ THE SYSTEM SHALL ได้ DB ที่ raw objects ครบ (assert: RegistrationNotices, fn 2, procs 3, policy, grants, seeds) และ test suite ทั้งหมดเขียว
- 7.8 THE SYSTEM SHALL แก้ bootstrap `01-principals.sql` ตาม REQ-3.8/3.9 (rename `pol_webhook_resolver`→`pol_resolver`)

## REQ-8: Route + wire contract rename

**User Story:** As an API consumer (FE/ระบบต้นทาง), I want ชื่อ route/field/event สอดคล้อง merchant vocabulary ทั้งชุดในครั้งเดียว, so that ไม่มี mixed vocabulary หรือ alias ค้างให้ maintain

**Acceptance Criteria (EARS):** *(design: Auth policies + API surface, C# rename map, T11)*
- 8.1 THE SYSTEM SHALL rename routes: `/api/v1/producers/*`→`/api/v1/merchant-users/*`, `/api/v1/admins/tenants*`→`/admins/merchants*`, `/admins/tenant-users/*`→`/admins/merchant-users/*` โดยไม่มี alias/redirect เก่า
- 8.2 THE SYSTEM SHALL rename JSON field `tenantId`→`merchantId` ทุก DTO (camelCase เดิม)
- 8.3 THE SYSTEM SHALL rename event `TenantUserRegistrationSubmitted`→`MerchantUserRegistrationSubmitted` และ field `TenantId`→`MerchantId` ในทุก contract
- 8.4 THE SYSTEM SHALL rename config keys: `Tenant:DevTenantId`→`Merchant:DevMerchantId`, `ConnectionStrings__Producer`→`ConnectionStrings__App` พร้อม update ทุก read site (Program.cs fail-fast, Worker, docker-compose, CI workflows, IntegrationDb fallback, design-time factory) รวมไฟล์ตัวอย่างที่ commit: `.env.example`, `.env.prod.example`
- 8.5 THE SYSTEM SHALL update Scalar/OpenAPI security annotations + operation metadata ตามชื่อใหม่
- 8.6 WHERE dev environment ไม่มี session THE SYSTEM SHALL ใช้ fallback `Merchant:DevMerchantId` (พฤติกรรม DevTenantId เดิม)

## REQ-9: Onboarding ตัวแทนคงเดิมบนชื่อใหม่

**User Story:** As a merchant user (ตัวแทนขาย), I want register → admin approve → login ทำงานเหมือนเดิม, so that การ onboarding ไม่สะดุดจาก restructure

**Acceptance Criteria (EARS):** *(design: Interceptor table (unbound), T14, Error Handling, Schema map MerchantUsers)*
- 9.1 WHEN ผู้ใช้ register แบบ anonymous THE SYSTEM SHALL สร้าง `MerchantUser` ที่ `MerchantId = NULL` ผ่าน keyed pol_admin context แบบ unbound (ไม่ stamp — ตาราง identity อยู่นอก policy)
- 9.2 WHEN admin approve THE SYSTEM SHALL ตั้ง `MerchantId` ให้ account (ดูดซับ semantics ProducerTenantAssignment เดิม)
- 9.3 WHILE `MerchantUser.MerchantId == NULL` THE SYSTEM SHALL ตอบ 403 บน protected merchant endpoints ผ่าน `MerchantBoundFilter` (พฤติกรรม `ProducerBoundProducerFilter` เดิม)
- 9.4 THE SYSTEM SHALL คง registration audit + notice + BFF session flow เดิม (rotation, reuse-detection, instant revoke, CSRF)

## REQ-10: Docs + coordination

**User Story:** As a team member (dev/FE/operator), I want canon docs และคู่มือสะท้อนโครงใหม่ + mapping ที่ FE ใช้ตามแก้ได้, so that ไม่มีเอกสารโกหกหลัง big-bang

**Acceptance Criteria (EARS):** *(design: Non-Functional Considerations)*
- 10.1 THE SYSTEM SHALL update `.ai/shared/ARCHITECTURE.md` + `.ai/shared/CODING_STANDARDS.md`: schema layout ใหม่, actor names ใหม่, ถอดกฎ freeze `tenant-user(s)`, Money as-built = DECIMAL(19,4)
- 10.2 THE SYSTEM SHALL update `docs/runbooks/local-dev-run.md` (ขั้น reset + `ConnectionStrings__App`)
- 10.3 THE SYSTEM SHALL ใส่ stale banner ชี้ master plan บน `docs/reference/*.md` ที่ยังอ้างชื่อเก่า (rewrite เต็มเป็นงาน spec ปลายทาง)
- 10.4 THE SYSTEM SHALL มี FE mapping table (route เก่า→ใหม่, JSON field, cookie, Money wire) ใน spec folder
- 10.5 THE SYSTEM SHALL มี operator note: gitignored `.env` / `.env.integration` / `appsettings.Development.json` ต้องแก้มือ (key ใหม่ + down -v)

## Edge Cases & Open Questions

- **Spike ก่อน implement (task แรก):** (ก) `sp_set_session_context @read_only=1` สอง key บน pooled connection — `sp_reset_connection` ล้างจริงและ re-stamp ได้; (ข) cross-schema predicate (sec → admin.PlatformMerchantAccess) ownership chaining บน SQL Server 2025 จริง. IF spike ล้ม THEN หยุดและปรับ design (fallback ยังไม่กำหนดโดยเจตนา — ห้ามด้นสด)
- **FE timing:** merge rf1 = FE 2 ตัวพังทันที — ต้องส่ง mapping table (REQ-10.4) และนัด cutover ก่อน merge
- **Hosts.Tests race เดิม:** bootstrap + migrate ก่อน `dotnet test` เสมอ (DB หายหลัง down -v → parallel CREATE DATABASE race)
- **ชื่อชั่วคราว:** ตาราง RBAC merchant-user (`MerchantUserRoleDefinitions` ฯลฯ) มีอายุถึง rf2 เท่านั้น — ยอม churn เพื่อ grep gate สะอาด
- **PaymentSessions interim:** อยู่ schema txn จนกว่า rf6; PspConnections จนกว่า rf3 — ห้าม refactor เพิ่มใน rf1

### /spec-analyze findings log — anchor: b37eed7 (2026-07-11; ไฟล์ยัง untracked ตอน audit — anchor = HEAD ณ เวลานั้น)

| # | Finding | Decision |
|---|---|---|
| F1 | REQ-1.2 ขัด REQ-1.4 — DataProtectionKeys (dbo) ทำ schema guard fail | **แก้แล้ว (ก)**: named exception list ราย entity ใน 1.4 |
| F2 | REQ-3.2 fail-open — Scoped ที่ไม่มีแถว PMA ถูก predicate มองเป็น Super | **แก้แล้ว (ก)**: Super branch เช็ค `PlatformUsers.Tier = Super` จริง + เพิ่ม 3.11 fail-closed (deviate doc §8 โดยเจตนา) |
| F3 | REQ-3.7 เป็น behavior change ข้อ 4 ที่ไม่อยู่ใน Overview | **แก้แล้ว (ก)**: เพิ่มข้อยกเว้น (ง) ใน Overview |
| F4 | permission key `producer.*` — rename เป็นอะไร ขัด "พฤติกรรมเดิม" ใน 2.6 | **แก้แล้ว (ก)**: `merchant_user.approve`/`merchant_user.reject` ระบุใน 2.6 + 7.4 |
| F5 | Money converter register แค่ HTTP ไม่พอ (outbox/worker serializer แยก) | **แก้แล้ว (ก)**: 6.4 ครอบทุก serializer ที่ contract ผ่าน |
| F6 | REQ-5.4 ขาด `/orders/{orderId}/summary/resend` + `/reports/reconciliation` | **แก้แล้ว (ก)**: เพิ่มทั้งสอง + note ครบทุก endpoint ที่เคยใช้ policy `tenant` |
| F7 | amount string ขาออก — scale ไม่กำหนด | **แก้แล้ว (ก)**: fixed 4 decimals |
| F8 | REQ-8.4 ขาดไฟล์ตัวอย่าง `.env.example`/`.env.prod.example` | **แก้แล้ว (ก)**: เพิ่มเข้า 8.4 |
| F9 | grep exception เป็นหมวด ไม่ใช่ลิสต์ไฟล์ | **แก้แล้ว (ก)**: คงหมวดใน REQ; tasks.md แตก concrete list ตอน implement |
