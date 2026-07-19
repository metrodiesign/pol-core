# Requirements: RLS → EF Core cluster-aligned runtime contexts (tenant isolation floor migration)
> Status: approved 2026-07-19

## Overview

pol-core เป็น multi-merchant payment platform (backend/DB เดียวร่วมกัน — [PROJECT_CONTEXT.md](../../shared/PROJECT_CONTEXT.md)).
ปัจจุบัน isolation floor = SQL Server RLS. spec นี้ย้าย floor ลง app layer: **1 migration-owner `PolDbContext`** (CLR
name คงไว้, ไม่ register runtime) + **runtime contexts 3 ตัวจัดตาม co-commit cluster** (`ControlPlaneDbContext` =
admin.*/iam.*/cfg.*, `MerchantUserDbContext` = merch user/role/registration, `MerchantRuntimeDbContext` = shop.*/txn.*/
merch provisioning) — isolation เป็น **compile-time** (assembly boundary); merchant-scoped context ใช้ query filter +
sealed write guard. ยุบ DB principal เหลือ 1, ถอด RLS ทั้งชุด — โดย**ไม่เปิดช่องข้อมูลรั่วข้าม merchant**. requirements
derive จาก [design.md](design.md) v7 (Design-First; inventory 21 transaction พิสูจน์ 20/21 single-context, cross-context
เหลือ ProvisionMerchant ตัวเดียว — ดู PLAN-REVIEW-LOG.md); แต่ละ REQ อ้าง design section ต้นทาง.

## REQ-1: Merchant read isolation (READ floor)
**User Story:** As the platform, I want ทุก merchant-scoped read ถูก filter ด้วย merchant ปัจจุบันอัตโนมัติ, so that ไม่มี merchant เห็นข้อมูลของ merchant อื่นโดยไม่ต้องเขียน filter เอง.
**Acceptance Criteria (EARS):** (design: §PolDbContext READ floor, §Per-table treatment)
- 1.1 THE SYSTEM SHALL apply EF Core global query filter `<tenantKey> == CurrentMerchant` กับ **ทุก entity ใน `MerchantRuntimeDbContext`** (uniform — shop.*, txn.*, merch.Merchants/VaultSecrets/VaultRevealAudits/ProvisioningAudits) และกับ merch.Users/RoleAssignments ใน `MerchantUserDbContext`
- 1.7 THE SYSTEM SHALL ใช้ **1 migration-owner `PolDbContext`** (CLR name คงไว้เพื่อคง migration lineage, map ทุก table + real FK, ไม่ register runtime) + **runtime contexts 3 ตัวจัดตาม co-commit cluster** (`ControlPlaneDbContext`/`MerchantUserDbContext`/`MerchantRuntimeDbContext`, scalar FK only, filter + sealed guard ต่อ context); merchant code reference control-plane context ไม่ได้ (assembly boundary = compile-time) [R3 pivot, R4 hybrid, R5 cluster]
- 1.2 THE SYSTEM SHALL filter `merch.Merchants` ด้วย `Id == CurrentMerchant` (self-row)
- 1.3 WHEN merchant-facing actor โหลด merchant-scoped row ด้วย primary key ที่เป็นของ merchant อื่น THE SYSTEM SHALL คืน 0 row (IDOR ปิด)
- 1.4 THE SYSTEM SHALL derive `CurrentMerchant` จาก authenticated actor เท่านั้น ไม่จาก client input (path/body/header)
- 1.5 THE SYSTEM SHALL ให้ query filter อ้าง DbContext instance member เพื่อให้ค่า re-evaluate ต่อ query (รองรับ worker late-bind) ไม่ bake เข้า cached model
- 1.6 THE SYSTEM SHALL apply read filter กับ `txn.OutboxMessages` (MerchantRuntimeOutbox), `merch.UserOutbox` (MerchantUserOutbox) และ `merch.VaultRevealAudits` ด้วย (fail-closed) และ suppress เฉพาะที่ narrow per-operation port (outbox drain ต่อ owner / vault-audit path) เท่านั้น [Codex-R1 #2, R3-v7 #5]

## REQ-2: Merchant write isolation (WRITE floor)
**User Story:** As the platform, I want ทุก merchant-scoped write ถูก validate กับ merchant ปัจจุบัน, so that ไม่มี actor เขียนข้อมูลของ merchant อื่น (query filter คุมแค่ read).
**Acceptance Criteria (EARS):** (design: §WRITE guard interceptor)
- 2.1 WHEN SaveChanges persist entity `IMerchantFiltered` ใน state Added/Modified/Deleted THE SYSTEM SHALL verify ว่า `MerchantId` เขียนได้โดย actor ปัจจุบัน (`CanWrite`)
- 2.2 IF `MerchantId` ของ entity เขียนไม่ได้โดย actor ปัจจุบัน THEN THE SYSTEM SHALL throw และ abort การ save ทั้งชุด (ไม่มี partial write)
- 2.3 IF entity `IMerchantFiltered` ถูกเขียนด้วย `MerchantId == Guid.Empty` (actor ใดก็ตาม รวม platform) THEN THE SYSTEM SHALL reject (กัน deny-sentinel poisoning — ไม่มี legit row ที่ MerchantId ว่าง) [F7]
- 2.4 THE SYSTEM SHALL reject การ Modified หรือ Deleted ของ `VaultRevealAudit` ทุกกรณี (append-only)
- 2.5 THE SYSTEM SHALL stamp `MerchantId` ตอน insert จาก authenticated actor เท่านั้น ไม่จาก client input
- 2.6 THE SYSTEM SHALL configure the tenant key (`MerchantId`, หรือ `Id` สำหรับ `Merchant`) เป็น EF concurrency token บนทุก merchant-scoped entity (`MerchantRuntimeDbContext` + merch.Users/RoleAssignments ใน `MerchantUserDbContext`) → UPDATE/DELETE emit `WHERE <key>=@id AND <key>=@original` (forged detached stub = 0 rows → `DbUpdateConcurrencyException`) [Codex-R1 #1, R3]
- 2.7 THE SYSTEM SHALL วาง `Merchant` root (`merch.Merchants`) ไว้ใน `MerchantRuntimeDbContext` โดย tenant-key descriptor = `Id` (self-row filter + guard ด้วย `Id`) [Codex-R1 #7, R3]
- 2.8 WHERE เป็น anonymous registration flow THE SYSTEM SHALL dispatch ด้วย **system actor `CurrentMerchant=Empty`** ที่ `CanWrite` อนุญาตเฉพาะ registration `OutboxMessage` (insert), consumer เขียนได้เฉพาะ control-plane — **ห้ามเขียน business filtered table**; registration event → `merch.UserOutbox` (owner-scoped); DB CHECK: หลัง legacy atomic-move, sentinel อนุญาตเฉพาะ `merch.UserOutbox` (ห้ามบน `txn.OutboxMessages` + business filtered table) เป็น floor [Codex-R1 #6, R2 #6, R3-v7 #5, R4-v7 #3]
- 2.9 THE SYSTEM SHALL ทำให้ `MerchantId` (tenant key) immutable หลัง insert (EF after-save = Throw) และ guard reject ถ้า original ≠ current ตอน Modified/Deleted (กันย้าย row ข้าม merchant); **carve-out เดียว**: pending `merch.Users` (MerchantId NULL) transition NULL→verified merchant ได้ครั้งเดียวตอน approval ผ่าน approve write port — merchant→merchant ยังห้าม [Codex-R2, R1-v7 #1]
- 2.10 THE SYSTEM SHALL วาง write guard ไว้ใน sealed override ทั้ง **4 save overload** ของ **ทุก runtime context** (ControlPlane/MerchantUser/MerchantRuntime) ผ่าน save-core ต่อ context (ไม่ใช่ interceptor) + reflection test ทุก virtual save entrypoint ถูก seal; write เข้า context ได้เฉพาะผ่าน narrow port (assembly-enforced) [Codex-R2 #4, R3 #1, R4 #1]
- 2.11 THE SYSTEM SHALL ให้ `CanWrite` เป็น operation-aware `CanWrite(entityType, state, targetMerchant)` **default-deny** ผ่าน explicit capability ต่อ flow (merchant / worker / provisioning-Super); admin/platform **read-only บน business table** (Order/PaymentSession/IdempotencyRecord/OutboxMessage/vault) — mirror pol_admin GRANT เดิม [Codex-R2 #3]
- 2.12 THE SYSTEM SHALL enumerate ทุก transaction เป็น transaction inventory (design §Transaction inventory) — gate scan ทุก transaction API (`ExecuteInTransactionAsync` + `BeginTransaction`/`UseTransaction`/`TransactionScope`/raw — 22 sites รวม `VaultRevealAuditWriter`); inventory พิสูจน์ 20/21 (+#22) เป็น single-context; **cross-context write เหลือ `ProvisionMerchant` ตัวเดียว** ซึ่งรันใน internal provisioning UoW (1 SqlConnection + 1 transaction, สร้าง context ผูก connection เดียว ภายใน execution-strategy delegate, `SaveChanges(acceptAllChangesOnSuccess:false)`→commit→`AcceptAllChanges`, idempotency key — ไม่ใช่ generic coordinator ใน Application contract) + failpoint test (rollback atomic) + transaction-enlistment test (write ใช้ transaction id เดียว); inventory รวม fan-out writes (row 1/5/7/9/13 → `admin.Users.AuthorizationVersion`, row 16 → `admin.ProvisioningOperations`) — ControlPlane-single ทั้งหมด, ProvisionMerchant ยังข้าม context flow เดียว [Codex-R4 #3, R5 #1/#2/#3, R4-v7 #5]

## REQ-3: Deny-by-default & unbound actor
**User Story:** As the platform, I want context ที่ไม่มี actor bound มองไม่เห็นอะไรเลย, so that การลืม bind ไม่ทำให้ข้อมูลรั่ว.
**Acceptance Criteria (EARS):** (design: §Actor abstraction, §Error Handling)
- 3.1 WHILE ไม่มี actor bound THE SYSTEM SHALL resolve `CurrentMerchant` เป็น `Guid.Empty` ให้ merchant-scoped read คืน 0 row
- 3.2 IF `IMerchantScoped` message ถูก dispatch โดยไม่มี actor bound THEN THE SYSTEM SHALL throw `MerchantBindingException` (map เป็น opaque 500)
- 3.3 IF authenticated principal ของ merchant-facing scheme มี `merchant_id` claim เป็น all-zeros/`Guid.Empty` THEN THE SYSTEM SHALL reject การ authenticate (401/403) — platform admin ไม่เคยมี `merchant_id` claim จึงแยกด้วย auth scheme (Empty ของ admin legit ตาม REQ-4.1) [F4]
- 3.4 THE SYSTEM SHALL NOT เปิดเผย binding state หรือ cross-tenant existence ใน error response ใดๆ
- 3.5 THE SYSTEM SHALL enforce DB `CHECK (MerchantId <> '00000000-0000-0000-0000-000000000000')` ทุก `IMerchantFiltered` table + `Merchants.Id` (invariant ที่รอดแม้ interceptor พลาด/raw SQL/bulk DML) + `CHECK (MerchantId <> registration-sentinel)` ทุก filtered table ยกเว้น outbox table + **atomic move legacy registration-sentinel rows** จาก `txn.OutboxMessages` → `merch.UserOutbox` (preserve Id/state, quiescence) **แล้ว sentinel ถูกห้ามบน `txn.OutboxMessages` หลัง move** (อนุญาตเฉพาะ `merch.UserOutbox`) + fresh-DB assertion [Codex-R1 #3, R2 #6, R3-v7 #5, R4-v7 #3]

## REQ-4: Admin cross-merchant access (Super/Scoped)
**User Story:** As a platform admin, I want อ่าน/เขียนข้าม merchant ได้ตาม tier/accessible set, so that admin ทำงานได้แต่ Scoped admin ไม่หลุดขอบเขต.
**Acceptance Criteria (EARS):** (design: §Admin cross-merchant seam, §Actor CanWrite)
- 4.1 WHILE actor เป็น platform admin THE SYSTEM SHALL resolve `CurrentMerchant` เป็น `Guid.Empty` (admin อ่าน merchant data ผ่าน `IAdminQuery` seam เท่านั้น)
- 4.2 WHERE ต้อง read ข้าม merchant, only `IAdminQuery` seam SHALL ออก query นั้น ด้วย `IgnoreQueryFilters()` + `WHERE MerchantId ∈ Accessible`
- 4.3 WHILE admin เป็น Super THE SYSTEM SHALL ให้เข้าถึงทุก merchant; WHILE Scoped ให้เฉพาะ merchant ที่มีแถวใน `admin.MerchantAccess`
- 4.4 IF Scoped admin ไม่มีแถวใน `admin.MerchantAccess` THEN THE SYSTEM SHALL คืน 0 row (fail-closed ไม่ใช่เห็นหมด)
- 4.5 WHEN platform admin เขียน entity `IMerchantFiltered` THE SYSTEM SHALL อนุญาตผ่าน operation-aware default-deny capability เท่านั้น — admin **read-only บน business table**; write เฉพาะ capability ที่ระบุ (เช่น provisioning=Super เขียน Merchant/PspConnection/VaultSecret ของ merchant ใหม่) ตาม tier/accessible set [Codex-R2 #3]
- 4.6 THE SYSTEM SHALL ป้องกัน handler อื่นนอก `IAdminQuery` ไม่ให้ออก cross-merchant query ตรง (arch-enforced)
- 4.7 THE SYSTEM SHALL บังคับ keyed-admin read ทุกจุดผ่าน `IAdminQuery` (ห้ามเรียก tenant-bound repo ตรง เช่น `GetMerchantHandler`); revalidate-in-write-transaction (authorization lease) จำกัดเฉพาะ **lease-covered flows** (revoke/demote-sensitive ControlPlane writes + provisioning) — approve/reject authorization linearize ที่ request boundary (RBAC), SQL predicate คุมแค่ target/state (race test: authorize-ก่อน-revoke commit ได้ / start-หลัง-revoke deny) [Codex-R1 #14, R3-v7 #6]
- 4.8 THE SYSTEM SHALL enforce isolation ด้วย **context boundary (compile-time)** แทน runtime classifier: control-plane (admin.*/iam.*/cfg.*) → `ControlPlaneDbContext` (merchant code reference ไม่ได้), merch user/role/registration → `MerchantUserDbContext`, shop.*/txn.*/merch provisioning → `MerchantRuntimeDbContext`; `iam.Roles` mixed-audience อยู่ `ControlPlaneDbContext` — merchant auth อ่านผ่าน `IMerchantRoleReader` port (visibility: shared MerchantId=NULL + own); tenant-key descriptor (`Merchant` key=`Id`) [Codex-R2 #1/#2, R3 pivot, R5 cluster]
- 4.9 THE SYSTEM SHALL ให้ authorization ผ่าน host-owned port `IWriteAuthorizer` (BuildingBlocks.Application, host implement ด้วย `IAdminScope`); revoke/demote race ปิดด้วย **authorization lease แยกจาก business DML**: lease = conditional no-op update บน caller authorization row (`UPDATE admin.Users SET AuthorizationVersion=AuthorizationVersion WHERE Id=@caller AND AuthorizationVersion=@expected`) **exactly-one row หรือ deny+rollback** ถือใน tx เดียวกับ business write; business revoke คง row-count ธรรมชาติ (session-family = 0..N idempotent — ไม่ใช่ authz signal); demote/suspend/revoke bump version; timeout/deadlock → bounded retry แล้ว deny. **lease scope**: revoke/demote-sensitive admin write ที่อยู่ ControlPlane (single-context) + provisioning (cross-context); approve/reject ไม่ใช้ lease (request-boundary RBAC + status conditional DML, single-context) [Codex-R2 #tx-race, R3 #revoke, R5 #9, R2-v7 #1/#4]
- 4.11 THE SYSTEM SHALL bump `admin.Users.AuthorizationVersion` ของ user ที่ได้รับผล **ใน transaction เดียวกับ** การเปลี่ยน effective authorization ทุก source: Status (suspend/reactivate), Tier (promote/demote), Session revoke, MerchantAccess grant/revoke (Assign/**Unassign**Merchant), RoleAssignment add/remove, RolePermission update/delete (bump ทุก admin ที่ถือ role นั้น) — ตกหล่น source ใด = revoked admin ถือ lease ค้าง; barrier test ต่อ source [R3-v7 #3]
- 4.10 WHERE actor เป็น merchant เขียน merchant-owned role (Create/Update/Delete บน `iam.Roles`) THE SYSTEM SHALL ผ่าน narrow `IMerchantRoleWriter` capability (merchant code inject `ControlPlaneDbContext` ตรงไม่ได้) ที่บังคับ `Role.MerchantId == CurrentMerchant` (reject shared/null/foreign), `MerchantId` immutable+concurrency token, `RolePermission` scope ผ่าน tracked parent + negative SQL Server matrix (A แก้ role ของ B/shared/platform ไม่ได้) [Codex-R5 #4]

## REQ-5: Sanctioned cross-merchant escape hatches
**User Story:** As the platform, I want cross-merchant path ที่จำเป็นถูกจำกัดเป็น allowlist, so that การ bypass filter ไม่ลามและตรวจสอบได้.
**Acceptance Criteria (EARS):** (design: §Escape-hatch allowlist)
- 5.1 THE SYSTEM SHALL จำกัดการใช้ `IgnoreQueryFilters()` เฉพาะ allowlist (outbox drain, merchant uniqueness, admin directory seam)
- 5.2 THE SYSTEM SHALL ban `ExecuteUpdate`/`ExecuteDelete` บน **ทุก runtime entity** by default (bypass change tracker + sealed guard) — อนุญาตเฉพาะใน named operation port ที่ DML `WHERE` มี tenant/target/state predicate (authorization แยก = lease หรือ boundary-RBAC ไม่บังคับใน WHERE) + static scan + SQL integration test ต่อ port [R1-v7 #3, R4-v7 #6]
- 5.3 WHERE query จำเป็นต้องเข้าถึงข้าม merchant ก่อนรู้ merchant (webhook resolve, public order-summary token) THE SYSTEM SHALL ใช้ raw SQL / `IgnoreQueryFilters()` แบบ explicit
- 5.4 THE SYSTEM SHALL NOT ใช้ `DbSet.Find`/`FindAsync` กับ merchant-scoped entity (bypass query filter) — ใช้ `.Where(MerchantId==).FirstOrDefault` แทน
- 5.5 THE SYSTEM SHALL filter `txn.OutboxMessages`/`merch.UserOutbox`/`merch.VaultRevealAudits` (fail-closed ตาม REQ-1.6) และ suppress เฉพาะที่ narrow per-operation port (worker drain ต่อ owner / vault-audit) — arch-enforced [F2, Codex-R1 #2, R3-v7 #5]
- 5.6 THE SYSTEM SHALL แยก raw/filter-suppressed operation เป็น **narrow port ต่อ operation** (`IWebhookMerchantResolver`/`IOrderSummaryReader`/`IOutboxDrain`/`IVaultAuditAppender`/`IMerchantDirectory`) — ไม่มี generic gateway (จะกลายเป็น universal bypass หรือ reverse dependency); arch-allowlist ต่อ implementation + capability/audit กลาง [Codex-R2 #5]

## REQ-6: CartItems isolation (denormalized)
**User Story:** As the platform, I want `shop.CartItems` ถูก isolate ต่อ merchant, so that cart item ไม่รั่วข้าม merchant แม้ไม่มี navigation `Cart`.
**Acceptance Criteria (EARS):** (design: §CartItems denormalized `MerchantId` column)
- 6.1 THE SYSTEM SHALL มี column `MerchantId` บน `shop.CartItems` denormalized จาก parent Cart
- 6.2 WHEN cart item ถูกสร้าง THE SYSTEM SHALL stamp `MerchantId` จาก merchant ของ parent Cart
- 6.3 THE SYSTEM SHALL filter `Item` ด้วย own column `MerchantId == CurrentMerchant`
- 6.4 THE SYSTEM SHALL ป้องกันการ query `Set<Item>()` ตรงนอก Cart aggregate (arch-enforced)
- 6.5 THE SYSTEM SHALL enforce composite FK `Item(CartId, MerchantId) → Cart(Id, MerchantId)` (+ alt key บน Cart) → `Item.MerchantId` ต้องตรง parent Cart (ปิด denormalization drift); stamp เฉพาะใน aggregate ctor; update raw seed/test inserts ให้มี column ใหม่ [Codex-R1 #8]

## REQ-7: Vault reveal-audit serialization (แทน EXECUTE-AS proc)
**User Story:** As the platform, I want vault reveal ยังทำงานหลังถอด `usp_vault_audit_head`, so that payment path ไม่พังและ hash-chain ไม่ fork.
**Acceptance Criteria (EARS):** (design: §Proc caller rewrites, blocker #2)
- 7.1 WHEN vault reveal-audit ถูกเขียน THE SYSTEM SHALL append audit head โดยไม่พึ่ง stored procedure `usp_vault_audit_head` (ที่ถูกถอด)
- 7.2 THE SYSTEM SHALL serialize reveal-audit concurrent ของ merchant เดียวกันใน **transaction เดียว**: BeginTransaction → `sp_getapplock`(Exclusive, transaction-owned) → ตรวจ return code (<0 = abort write) → read head → insert → commit — ผ่าน narrow per-operation port; พิสูจน์ด้วย concurrent-N-writer test (Seq ต่อเนื่อง, ไม่ fork/drop, lock-fail aborts) [F3, Codex-R1 #12]
- 7.3 THE SYSTEM SHALL คง unique `(MerchantId, Seq)` เป็น backstop; IF applock ล้มเหลว/ถูก bypass และเกิด concurrent violation THEN THE SYSTEM SHALL fail การเขียนนั้น (ไม่ fork chain)
- 7.4 THE SYSTEM SHALL คง vault reveal ให้ทำงานได้บน SQL Server (payment path ต้องไม่พัง)

## REQ-8: RLS removal & principal collapse
**User Story:** As the platform, I want ถอดกลไก RLS ทั้งชุดและยุบ principal, so that ระบบง่ายลงและ isolation unit-testable.
**Acceptance Criteria (EARS):** (design: §สิ่งที่ถอดออก, §Technology Decisions)
- 8.1 THE SYSTEM SHALL ถอด `sec.MerchantIsolationPolicy`, predicate functions, และ merchant GRANT/BLOCK matrix
- 8.2 THE SYSTEM SHALL ถอด SESSION_CONTEXT stamping และ `SessionContextConnectionInterceptor`
- 8.3 THE SYSTEM SHALL ถอด role `pol_rls_bypass` และ EXECUTE-AS procs ทั้ง 3
- 8.4 THE SYSTEM SHALL ทำงานด้วย runtime DB principal เดียว (DDL/migrator แยกต่างหาก)
- 8.5 THE SYSTEM SHALL NOT register DbContext ใดๆ (ทั้ง 3) ผ่าน context-pooling API (`AddDbContextPool`, `AddPooledDbContextFactory`) — filter value จะ stale [Codex-R1 #13]
- 8.6 THE SYSTEM SHALL ให้ migration คง **migration chain เดียวใน migration-owner `PolDbContext`** (CLR name คงไว้ → designer/snapshot `[DbContext(typeof(PolDbContext))]` ยัง resolve chain เดิม; rename class = orphan lineage): **gate `dotnet ef migrations list --context PolDbContext` เห็น migration IDs เดิมครบก่อนเพิ่ม forward migration**; forward migration drop security object explicit, preserve `merch.RegistrationNotices`, add CartItems column+backfill + CHECK/composite-FK/alt-key + `admin.ProvisioningOperations` (named-unique operationKey + CallerAdminId + expectedAuthorizationVersion + request hash + **non-FK** pre-minted MerchantId + serialized result) + `AuthorizationVersion` column บน admin.Users (default 0); outbox physical: `MerchantRuntimeOutbox` reuse `txn.OutboxMessages` (ไม่ rename), `MerchantUserOutbox` = new `merch.UserOutbox`, **atomic move legacy registration-sentinel rows (preserve Id/state) แล้ว sentinel forbidden บน `txn.OutboxMessages` หลัง move**; **upgrade test จาก current migrated DB** (ไม่ใช่แค่ fresh) + schema fingerprint assert [Codex-R1 #11, R4 #6, R5 #7, R2-v7 #4/#7, R4-v7 #3]
- 8.8 THE SYSTEM SHALL แยก persistence เป็น assembly ตาม cluster + context `internal` → merchant code inject control-plane context ไม่ compile; compile-negative **build** test (forbidden project reference → build fail, custom MSBuild ไม่ใช่แค่ arch-lint); unified `Api.csproj` (ref ทุก module) = declared trusted composition root + arch test ว่า merchant endpoint adapter resolve control-plane port ไม่ได้ + privileged port reauthorize ทุก call [Codex-R4 #8, R5 #8]
- 8.7 THE SYSTEM SHALL update deployment/bootstrap inventory ทั้งหมดเป็น 1 principal: `docker-compose.prod.yml`, `docker/entrypoint.sh`, `docker/migrate-entrypoint.sh`, `docker/bootstrap/01-principals.sql`, `.env.example`, `.github/workflows/ci.yml`, local compose, Worker settings, `assert-fresh-db.sql` + CI assertion ว่าไม่มี legacy principal/RLS object/bypass role เหลือหลัง migration [Codex-R1 #11, R2 #7]

## REQ-9: User/session isolation (control-plane, in scope)
**User Story:** As the platform, I want control-plane session/user data ถูก isolate ต่อ owner, so that การถอด RLS/SESSION_CONTEXT ไม่ทำให้ user เห็น session/data ของ user อื่น.
**Acceptance Criteria (EARS):** (design: §User/session isolation)
- 9.1 THE SYSTEM SHALL วาง merch user/session/registration audit ใน `MerchantUserDbContext` (filter `MerchantId==CurrentMerchant`) และ admin user/session/audit ใน `ControlPlaneDbContext` (admin scope, ไม่ merchant-filter); isolate ด้วย owner identity/tenant key per-entity descriptor แยกจาก merchant business filter [R3, R5 cluster]
- 9.5 THE SYSTEM SHALL แยก pre-owner-bind **read** ports (`ISessionByTokenHash`, `IResolveMerchantLoginBySubject`, `IResolveAdminLoginBySubject` — projection จำกัด + `AsNoTracking` + audit + arch-allowlist) จาก pre-owner-bind **write** ports (`IRegistrationWriter`/`IBindInvitedAdminIdentity`/`ISelfProvisionSuperWriter`/`IApproveRegistrationWriter`/`IRejectRegistrationWriter` — แต่ละตัว verify trust root เอง: registration ticket / invited-admin email allowlist / bootstrap Subject allowlist / approval lookup pending `merch.Users` by Subject under suppression + conditional DML `MerchantId IS NULL AND Status=Pending`, เขียน exact entity/state allowlist, conditional write, atomic 1 context, opaque capability); ห้าม generic unfiltered Identity repository; `AuthAudit`/`RegistrationNotice` = audit/queue policy [R3 #IUserOwned, R5 #5]
- 9.6 THE SYSTEM SHALL ให้ `MerchantUserDbContext` มี OutboxMessages ของตัวเอง + drain port → registration UoW (merch.Users + ExternalLogins + RegistrationAudits + event) commit atomic ใน context เดียว (ไม่ cross-context) [Codex-R4 #2]
- 9.2 WHEN session/user record ถูก lookup ด้วย id อย่างเดียว (เช่น `FindByIdAsync`) THE caller SHALL verify ownership ก่อน act; the spec SHALL enumerate caller by-id ล้วนทุกจุด + มี unit test ownership-check ต่อจุด [F5]
- 9.3 THE SYSTEM SHALL NOT apply merchant query filter กับ control-plane identity tables (admin จัดการข้าม merchant)
- 9.4 THE SYSTEM SHALL NOT regress user/session isolation เมื่อถอด SESSION_CONTEXT (ยืนยัน: มีแค่ predicate ที่ถูกถอดอ่าน `SESSION_CONTEXT('UserId')`)

## REQ-10: Provisioning Super-only (แทน DB BLOCK)
**User Story:** As the platform, I want provision merchant จำกัดเฉพาะ Super admin, so that control ที่เคยเป็น DB BLOCK ไม่หายเงียบหลังถอด RLS.
**Acceptance Criteria (EARS):** (design: §Error Handling provision, Open item 1)
- 10.1 WHEN merchant ถูก provision (insert `merch.Merchants`) THE SYSTEM SHALL อนุญาตเฉพาะ Super admin
- 10.2 IF Scoped admin พยายาม provision merchant THEN THE SYSTEM SHALL reject (แทน DB BLOCK เดิม rf1 REQ-3.7)
- 10.3 THE SYSTEM SHALL ให้ provisioning รันใน internal provisioning UoW: **lock+recheck FULL authorization จาก `admin.Users` ใน tx เดียวกับ write**: `SELECT 1 FROM admin.Users WITH (UPDLOCK,HOLDLOCK) WHERE Id=@caller AND Tier=Super AND Status=Active AND AuthorizationVersion=@expected` (hint หลัง table; `@expected` = port arg `expectedAuthorizationVersion` pinned ที่ request boundary ไม่ re-read ใน tx; bare Tier ไม่พอ — suspended Super ยังมี Tier.Super), zero rows → rollback; port คืน `ProvisionMerchantResult` เต็ม (merchant id + connection ids) ให้ replay คืน body เดิม; mint merchant id เอง, Added set exact (Merchant/PspConnection/VaultSecret/ProvisioningAudit); **idempotency ledger `admin.ProvisioningOperations` (ControlPlane): required `operationKey` (named unique index) + `CallerAdminId` + `expectedAuthorizationVersion` + request hash + pre-minted MerchantId (NON-FK ledger value) + serialized result; authz recheck รันก่อนเสมอทุก attempt (ไม่คืนผลก่อน authz); INSERT เป็น immediate parameterized SQL ใน try/catch เช็ค named index เจาะจง (ไม่ใช่ deferred Add / ไม่เดาจาก 2601/2627 ลอย); duplicate → rollback + verify requesting `CallerAdminId` == stored **AND canonical request hash match** (caller อื่น/payload ต่าง → reject ก่อน deserialize) → คืน stored result; commit-unknown → execution-strategy `verifySucceeded` hook เรียก verifier (key+caller+snapshot+hash+result) ก่อน retry, match = committed แล้วคืนผลโดยไม่ re-run auth-first** (ไม่โผล่ 500/409 ดิบ), opaque one-shot capability, execution-strategy fresh conn+contexts ต่อ attempt [Codex-R4 #5, R5 #2/#6, R1-v7 #2, R2-v7 #2/#3]

## REQ-11: Testing & CI guardrails
**User Story:** As the platform, I want isolation floor พิสูจน์ได้และ guardrail กัน regression, so that floor ที่อยู่ app layer ล้วนไม่ถูกทำพังเงียบ.
**Acceptance Criteria (EARS):** (design: §Testing Strategy, §Non-Functional)
- 11.1 THE SYSTEM SHALL พิสูจน์ merchant isolation ด้วย unit test บน SQLite (ไม่ต้อง live SQL Server)
- 11.2 THE SYSTEM SHALL คง integration test บน real SQL Server ครอบ matrix (DB invariant พิสูจน์บน SQLite ไม่ได้): forged detached write (concurrency token), Empty CHECK, CartItems composite-FK reject mismatched parent, outbox drain via `IOutboxDrain` port, admin write authz, vault concurrent-N serialization, generated-DML parameterization, constraint/migration presence; **provisioning UoW: failpoint หลัง ControlPlane save + หลัง MerchantRuntime save → rollback atomic; retry ไม่ double-provision (idempotency); demote-during-provision; transaction-enlistment (transaction id เดียว)**; **upgrade-from-current-migrated-DB + `dotnet ef migrations list` lineage gate** [Codex-R1 #16, R5 #10]
- 11.3 THE SYSTEM SHALL enforce ด้วย arch test: **default-ban** raw `SqlConnection` ใน production infra (coverage รวม `Admins`/`Iam`/`MasterData`.Infrastructure) + **exact allowlist** เฉพาะ provisioning-integration UoW + sanctioned raw-operation ports [R3-v7 #8]
- 11.4 THE SYSTEM SHALL enforce ด้วย arch test: ban bypass primitive (`IgnoreQueryFilters`, `SqlQueryRaw`, `FromSql*`, `ExecuteSql*`, `GetDbConnection`, `ExecuteUpdate`/`ExecuteDelete` บน **ทุก runtime entity** — ไม่ใช่แค่ `IMerchantFiltered`) นอก narrow per-operation port (DML WHERE มี tenant/target/state predicate; authorization แยกเป็น lease หรือ boundary-RBAC ไม่บังคับอยู่ใน WHERE + SQL/ordering test ต่อ port) — ทุก production assembly [Codex-R1 #9, R2-v7 #3, R4-v7 #6]
- 11.5 THE SYSTEM SHALL enforce ด้วย model-build + arch test: ทุก runtime-mapped entity ถูก assign ไป context เดียวตาม cluster (ControlPlane/MerchantUser/MerchantRuntime) ไม่ overlap; model-build fail ถ้า entity ไม่อยู่ context ใดเลย [R3 pivot, Codex-R1 #4, R5 cluster]
- 11.6 THE SYSTEM SHALL มี test assert generated SQL/parameters ของ 2 scope `MerchantRuntimeDbContext` ที่ actor ต่างกัน (filter value ต่างกัน; พิสูจน์ instance-member parameterization ไม่ bake เข้า cached model) [Codex-R1 #13]
- 11.8 THE SYSTEM SHALL มี compile-time/arch test: merchant assembly ไม่ reference `ControlPlaneDbContext` entity (control-plane) — isolation เป็น compile-time [R3 pivot, R5 cluster]
- 11.7 THE SYSTEM SHALL enforce ด้วย arch test + model-build: ทุก merchant-scoped entity (`MerchantRuntimeDbContext` + merch.Users/RoleAssignments ใน `MerchantUserDbContext`) มี read filter + concurrency token + immutable บน tenant key (resolve ด้วย `FindProperty`, reject shadow/typo/wrong-type); **carve-out: `merch.Users.MerchantId` nullable ได้ตอน pending (NULL) + one-time NULL→value transition ตอน approval** — immutable หลัง bound [F1, Codex-R1 #1, R3, R2-v7 #1]

## REQ-12: Canon / documentation supersede
**User Story:** As a future maintainer, I want canon docs สะท้อน floor ใหม่, so that ไม่มี agent/คน drift กลับไปคาดหวัง RLS.
**Acceptance Criteria (EARS):** (design: §Non-Functional canon)
- 12.1 THE SYSTEM SHALL update canon docs (`ARCHITECTURE.md`, `SECURITY_RULES.md`, `CODING_STANDARDS.md`, `PROJECT_CONTEXT.md`, `docs/reference/db-connection-and-rls.md`) ให้บรรยาย app-layer floor
- 12.2 THE SYSTEM SHALL mark superseded ต่อ RLS requirements เดิม (rf1 REQ-3.2/3.3/3.7/3.8, admin-actor-rename REQ-7.4)

## REQ-13: Observability (security-critical telemetry)
**User Story:** As an operator, I want telemetry ของ guard/isolation events, so that ตรวจจับ attack/bug ได้ (โดยเฉพาะเมื่อ 1 principal ทำ DB attribution หาย).
**Acceptance Criteria (EARS):** (design: §Observability) [Codex-R1 #15]
- 13.1 THE SYSTEM SHALL emit structured audit/metric สำหรับ: guard denial, `CanWrite` denial, `DbUpdateConcurrencyException`, DB CHECK/FK violation, unbound actor, `MerchantId=Empty`/sentinel hit, port (suppressed-op) use + cardinality anomaly, applock timeout, admin cross-merchant action, admin revalidation denial, registration-sentinel misuse [Codex-R2 #9]
- 13.2 THE SYSTEM SHALL ให้ telemetry มี actor kind/id, target merchant, entity, operation, reason, correlation id — ห้าม log PII/secret
- 13.3 THE SYSTEM SHALL ตั้ง per-host connection-string `Application Name` (partial DB attribution แม้ใช้ 1 principal)
- 13.4 THE SYSTEM SHALL ส่ง telemetry ไป tamper-resistant/external sink พร้อม alert + retention; มี test พิสูจน์ redaction (ไม่หลุด PII/secret) ทุก denial path [Codex-R2 #9]

## Edge Cases & Open Questions
- **CartItems denormalize เป็น schema change** — big-bang reset ทำให้ไม่ต้อง data migration แต่ต้องแน่ใจ stamp ครบทุก insert path (REQ-6.2)
- **Vault serialization** (REQ-7.2): **decided (F3) = app-level `sp_getapplock` ผ่าน raw SQL** (คงพฤติกรรม proc เดิม); unique `(MerchantId,Seq)` = backstop (REQ-7.3); ต้องมี test concurrent
- **Admin write blast radius** (REQ-4.5): ต้อง audit ทุก admin write path ที่ผ่าน keyed context ว่าผ่าน `CanWrite` — ไม่ใช่แค่ provisioning
- **ctor stub actor** (design §PolDbContext): migration bootstrap + design-time ไม่มี actor — ต้อง inject unbound stub, ยืนยัน `dotnet ef`/boot ผ่าน
- **1 principal residual risk**: least-privilege belt (vault plaintext readback, append-only DB grant) หาย — user รับแล้ว, belt เหลือ app layer (REQ-2.4 + REQ-2.1)
- **repo ที่ผูก keyed admin (Empty) context อ่าน `IMerchantFiltered`** (`ConnectionRepository`/`ProvisioningAuditWriter`) — ต้อง audit ว่าอยู่ allowlist ถ้ามี read (ไม่งั้น fail-closed 0 row, feature พัง)

### Analyze findings log — anchor 417962b (2026-07-18, full audit; ทุก finding เลือก option (a) recommended)
- **F1** [gap/leak] read-filter set ↔ `IMerchantFiltered` marker drift → **fixed**: เพิ่ม REQ-11.7 (arch test coverage)
- **F2** [gap/leak] outbox/audit read ไม่มี floor หลังยุบ principal → **fixed**: เพิ่ม REQ-5.5 (arch test ban read นอก allowlist)
- **F3** [decision] vault serialization → **decided**: app-level `sp_getapplock` raw SQL (REQ-7.2), unique `(MerchantId,Seq)` = backstop (REQ-7.3)
- **F4** [ambiguity] zero `merchant_id` claim reject + แยกจาก admin → **fixed**: REQ-3.3 reject 401/403 ที่ merchant scheme, admin แยกด้วย scheme (ไม่มี claim)
- **F5** [gap] REQ-9.2 caller ownership enforcement → **fixed**: REQ-9.2 enumerate caller by-id + unit test ต่อจุด
- **F6** [interaction] EF seed เขียน `IMerchantFiltered` ด้วย stub actor → **dismissed**: ยืนยันไม่มี (grep `InsertData`/`HasData` บน shop/txn/merch data = 0; seed-demo=raw SQL, SeedData=iam/cfg) → no action
- **F7** [inconsistency] REQ-2.3 "non-platform" แคบไป → **fixed**: broaden REQ-2.3 reject Empty ทุก actor (ตรง design guard)
