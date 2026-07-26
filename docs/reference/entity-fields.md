# Entity Field Reference (persisted model)

> Generated 2026-07-26 from `PolDbContextModelSnapshot.cs` (the authoritative EF model) + the entity
> configurations under `src/Persistence/Persistence.{ControlPlane,MerchantUsers,MerchantRuntime}/**/`,
> the domain enums, และ raw-SQL migrations (grant matrix / check constraints / seed). ครอบคลุม **42 ตาราง**
> ใน 7 schema. แก้ entity/migration เมื่อไหร่ regenerate ไฟล์นี้ตามด้วย.
>
> ขอบเขต: เฉพาะ entity ที่ persist ลง DB. Value object ที่ไม่มีตารางของตัวเอง (`Money` = `Amount:decimal` +
> `Currency:string`) ถูก map เป็นคอลัมน์คู่ของ entity เจ้าของ (เช่น `PriceAmount`/`PriceCurrency`).

## Legend

- **Type** = SQL Server column type. `nvarchar(n)` = Unicode string ยาวสุด n; `nvarchar(max)` = ไม่จำกัด;
  `char(3)` = fixed-length non-Unicode (ใช้กับ ISO-4217 currency เท่านั้น); `decimal(19,4)` = Money
  (มาตรฐานเดียวทั้งระบบ — ห้าม float/double/minor-units); `datetime2` = UTC timestamp (เก็บเป็น UTC เสมอ;
  field/column **ไม่ใส่** suffix `Utc`); `date` = DateOnly; `uniqueidentifier` = Guid; `bigint` = `long`;
  `bit` = bool; `varbinary` = bytes; `rowversion` = optimistic-concurrency token.
- **Null** = Y ถ้า nullable, N ถ้า NOT NULL.
- **Key** = PK / AK (alternate key) / FK / UQ (unique index) / IX (non-unique index) / UQ\* หรือ IX\* =
  filtered index / CK = check constraint.
- enum-backed column เก็บเป็น `int` (ดูค่าใน [Enums](#enums)) ยกเว้น `Carts.Status` ที่เก็บเป็น
  **string ชื่อ enum** (`HasConversion<string>`).
- **Context** = runtime DbContext ที่เป็นเจ้าของตารางนั้นตอน runtime. มี 3 ตัว (ทั้งหมด `internal sealed`,
  ไม่ประกาศ migration): `ControlPlane` (admin.\* + iam.\* + cfg.\* + dbo.DataProtectionKeys),
  `MerchantUsers` (merch.Users/Sessions/ExternalLogins/AuthAudits/RegistrationAudits/RegistrationNotices/
  RoleAssignments/UserOutbox), `MerchantRuntime` (shop.\* + txn.\* + merch.Merchants/VaultSecrets/
  VaultRevealAudits/ProvisioningAudits — เป็น isolation floor: ทุก entity มี global query filter
  `MerchantId == CurrentMerchant`).
  `PolDbContext` **ไม่ใช่** runtime context — มันเป็น migration owner อย่างเดียว (ถือ relational model เต็ม
  รวม cross-context FK จริง) และ discover entity config จาก `ModuleAssemblies` ผ่าน
  `ApplyConfigurationsFromAssembly`.
- **ไม่มี RLS**: security policy / predicate function / EXECUTE-AS bypass proc ถูกรื้อทิ้งทั้งหมดใน migration
  `20260719081817_RlsTeardownAndOnePrincipal` — isolation ย้ายไปอยู่ที่ EF global query filter + write
  authorizer ใน app layer. เหลือ DB principal เดียวคือ `pol_app` (ดู
  [Schema objects](#schema-objects-beyond-tables)).

## Schema map (7 schemas — `SchemaNames.cs`)

| Schema | เนื้อหา | Runtime context |
|---|---|---|
| `shop` | funnel: Products, Carts, CartItems, CheckoutSessions(+Items), Orders(+Items, policies, audits) | MerchantRuntime |
| `txn` | payment (interim): PaymentSessions, PspConnections, OutboxMessages, IdempotencyRecords | MerchantRuntime |
| `admin` | control plane: platform users, session/auth/audit, role assignment, provisioning ops | ControlPlane |
| `merch` | merchant + merchant-user + vault | MerchantUsers · MerchantRuntime |
| `iam` | central RBAC catalog (rf2) — vocabulary เดียวแทน catalog เดิมที่เคยซ้ำสองฝั่ง | ControlPlane |
| `cfg` | config/reference data: Positions, Offices, Levels, Divisions (masterdata-split) | ControlPlane |
| `dbo` | framework-owned — **ข้อยกเว้นเดียว** ของ schema guard: `DataProtectionKeys` | ControlPlane |

> ทุก entity configuration ต้องเรียก `ToTable(name, schema: SchemaNames.X)` เอง — ไม่มี `HasDefaultSchema`
> fallback, entity ที่ลืม schema จะ fail Architecture.Tests guard แทนที่จะตกลง `dbo` เงียบๆ.
> `VCentralPay` คือชื่อ **catalog** (database) ไม่ใช่ schema — ห้ามเขียน `VCentralPay.<Table>`.

---

## admin schema (context: ControlPlane) — 7 ตาราง

### User -> `admin.Users`
Platform user ของ control plane. `Super` = unrestricted; `Scoped` = เห็นเฉพาะ merchant ที่อยู่ใน
`admin.MerchantAccess`. `Subject` เป็น null จนกว่า login ครั้งแรกจะ bind (invite-by-email).
FK 4 ตัวไป `cfg.*` เป็น cross-schema จริง (`OnDelete: Restrict`) — master data ลบไม่ได้ถ้ายังมีคนอ้างอยู่.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| Subject | nvarchar(256) | Y | UQ* | OIDC `sub`; unique เฉพาะตอน NOT NULL (`[Subject] IS NOT NULL`) |
| Email | nvarchar(320) | N | UQ | |
| Tier | int | N | | `Tier` (Scoped=0, Super=1) |
| Status | int | N | | `UserStatus` (Active=0, Suspended=1) |
| AuthorizationVersion | bigint | N | | concurrency token — bump ทุกครั้งที่สิทธิ์เปลี่ยน |
| PositionId | uniqueidentifier | Y | FK, IX | -> `cfg.Positions.Id` (Restrict) |
| OfficeId | uniqueidentifier | Y | FK, IX | -> `cfg.Offices.Id` (Restrict) |
| LevelId | uniqueidentifier | Y | FK, IX | -> `cfg.Levels.Id` (Restrict) |
| DivisionId | uniqueidentifier | Y | FK, IX | -> `cfg.Divisions.Id` (Restrict) |
| CreatedAt | datetime2 | N | | |

### MerchantAccess -> `admin.MerchantAccess`
M:N ระหว่าง Scoped platform user กับ merchant ที่เข้าถึงได้ (accessible set). unassign = hard delete.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| PlatformUserId | uniqueidentifier | N | UQ | unique กับ MerchantId |
| MerchantId | uniqueidentifier | N | UQ | unique กับ PlatformUserId |
| AssignedByAdminId | uniqueidentifier | N | | admin ที่สั่ง assign (Super) |
| AssignedAt | datetime2 | N | | |

### Session -> `admin.Sessions`
server-side session ของ admin BFF. cookie value (opaque 256-bit) **ไม่เคยเก็บ** — เก็บแค่ SHA-256 hash.
session รวมเป็น rotation family (`FamilyId`): rotate = ออก successor ใน family เดิม + mark ตัวเก่า
`Superseded` พร้อม link ไป successor (กัน replay = reuse detection). prune ลบ row ที่เลย absolute expiry.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| FamilyId | uniqueidentifier | N | IX | rotation family; family-wide revoke |
| TokenHash | varbinary(32) | N | UQ | SHA-256 ของ cookie token (lookup O(1)) |
| PlatformUserId | uniqueidentifier | N | IX | -> `admin.Users.Id`; logout-all |
| Status | int | N | | `SessionStatus` (Active=0, Superseded=1, Revoked=2) |
| IssuedAt | datetime2 | N | | |
| IdleExpiresAt | datetime2 | N | | idle sliding (~30m), slide lazy |
| AbsoluteExpiresAt | datetime2 | N | IX | hard cap (~8h); prune sweep key |
| SupersededAt | datetime2 | Y | | เวลาที่ถูก rotate |
| SupersededBySessionId | uniqueidentifier | Y | | successor (immediate-predecessor / reuse check) |
| CreatedIp | nvarchar(45) | Y | | |
| UserAgent | nvarchar(256) | Y | | |

### AuthAudit -> `admin.AuthAudits`  (append-only)
audit ของ auth lifecycle (login-success/logout/logout-all/rotated/family-revoked-reuse/auth-denied).
แยกจาก `admin.UserAudits` เพราะ auth event อาจไม่มี user id ที่ resolve ได้ (denial ก่อน resolve).
ไม่เก็บ secret/token/raw session id.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| EventType | nvarchar(32) | N | | login-success/logout/logout-all/rotated/family-revoked-reuse/auth-denied |
| PlatformUserId | uniqueidentifier | Y | IX | null เมื่อยังไม่ resolve user |
| Subject | nvarchar(256) | Y | | OIDC `sub` |
| Reason | nvarchar(128) | Y | | label สั้น ไม่ sensitive (เหตุผล deny) |
| CorrelationId | nvarchar(128) | N | | |
| OccurredAt | datetime2 | N | | |

### Audit -> `admin.UserAudits`  (append-only)
audit ของทุก admin action (account lifecycle: self-provision/create-scoped/assign/unassign/suspend/
reactivate/session-revoke; role lifecycle: role create/update/delete/assign/unassign).

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| Action | nvarchar(64) | N | | ชื่อ action |
| ActorId | uniqueidentifier | N | | user ที่ทำ |
| ActorType | nvarchar(16) | N | | `"admin"` |
| TargetAdminId | uniqueidentifier | Y | | platform user เป้าหมาย (ถ้ามี) |
| TargetRoleId | uniqueidentifier | Y | | role เป้าหมาย (role action เท่านั้น) |
| MerchantId | uniqueidentifier | Y | | merchant ที่เกี่ยว (assign/unassign) |
| CorrelationId | nvarchar(128) | N | | |
| OccurredAt | datetime2 | N | | |

### RoleAssignment -> `admin.RoleAssignments`
ผูก platform user กับ role ใน `iam.Roles` — **ไม่มี** `MerchantId` (global, ต่างจากฝั่ง merch ที่ผูก merchant).
effective permission = union ของ `PermissionKey` จากทุก role ที่ `Status = Active`.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| PlatformUserId | uniqueidentifier | N | UQ | unique กับ RoleId |
| RoleId | uniqueidentifier | N | FK, IX, UQ | -> `iam.Roles.Id` (Restrict) |
| AssignedById | uniqueidentifier | N | | |
| AssignedAt | datetime2 | N | | |

### ProvisioningOperation -> `admin.ProvisioningOperations`
idempotency ledger ของ merchant provisioning (multi-context coordinator). `OperationKey` unique = replay
ตัวเดิมคืนผลเดิม; `ExpectedAuthorizationVersion` ล็อกกับ `admin.Users.AuthorizationVersion` ตอนเริ่ม.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| OperationKey | nvarchar(200) | N | UQ | index name `UX_ProvisioningOperations_Key` |
| CallerAdminId | uniqueidentifier | N | | |
| ExpectedAuthorizationVersion | bigint | N | | snapshot ของสิทธิ์ผู้เรียก |
| RequestHash | nvarchar(64) | N | | กัน key ซ้ำแต่ payload ต่าง |
| MerchantId | uniqueidentifier | N | | merchant ที่ provision |
| Result | nvarchar(max) | Y | | JSON ผลลัพธ์ (null = ยังไม่จบ) |
| CreatedAt | datetime2 | N | | |

---

## iam schema (context: ControlPlane) — 4 ตาราง

catalog กลางของ rf2 — vocabulary เดียวที่แทน catalog เดิมซึ่งเคยซ้ำกันสองชุด (admin/merch). ไม่มี RLS
predicate; per-merchant visibility บน `Roles`/`RolePermissions` เป็น app-layer floor.
`pol_app` ได้แค่ **SELECT** บน `PermissionGroups`/`Permissions` (catalog seed โดย migration, immutable at
runtime) แต่ได้ SELECT/INSERT/UPDATE/DELETE บน `Roles`/`RolePermissions`.

### PermissionGroup -> `iam.PermissionGroups`  (10 seed rows)

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Key | nvarchar(32) | N | PK | เช่น `txn`, `merchant`, `user`, `system`, `merchants.users`, `catalog`, `payment`, `roles`, `merchants.policies`, `policies` |
| LabelTh | nvarchar(128) | N | | |
| Scope | int | N | | `Scope` (Platform=0, Merchant=1) |
| SortOrder | int | N | | |

### Permission -> `iam.Permissions`  (24 seed rows)

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Key | nvarchar(64) | N | PK | เช่น `txn.view`, `roles.manage`, `payment.redirect` |
| GroupKey | nvarchar(32) | N | FK, IX | -> `iam.PermissionGroups.Key` (Restrict) |
| LabelTh | nvarchar(160) | N | | |
| SortOrder | int | N | | |

### Role -> `iam.Roles`  (4 seed rows)
seed 4 role ด้วย fixed id: `platform_admin` (anchor, ทุก Platform key), `platform_auditor`,
`merchant_manager` (anchor, ทุก Merchant key), `merchant_staff` — ทั้งหมด `Status = Active`,
`MerchantId = NULL` (shared/seed). anchor role ห้าม deactivate/delete (บังคับใน Role aggregate).

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| Code | nvarchar(64) | N | UQ | unique กับ MerchantId (unfiltered) |
| Name | nvarchar(128) | N | | |
| Description | nvarchar(256) | Y | | |
| Color | nvarchar(16) | Y | | |
| Status | int | N | | `RoleStatus` (Active=0, Inactive=1) |
| Scope | int | N | | `Scope` (Platform=0, Merchant=1) |
| MerchantId | uniqueidentifier | Y | UQ, CK | null = shared/seed role |
| — | — | — | CK | `CK_Roles_ScopeMerchant`: `([Scope] = 0 AND [MerchantId] IS NULL) OR [Scope] = 1` |

### RolePermission -> `iam.RolePermissions`  (34 seed rows)

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| RoleId | uniqueidentifier | N | FK, UQ | -> `iam.Roles.Id` (Cascade); unique กับ PermissionKey |
| PermissionKey | nvarchar(64) | N | FK, IX, UQ | -> `iam.Permissions.Key` (Restrict) — กัน phantom key |

---

## cfg schema (context: ControlPlane) — 4 ตาราง

reference data ของฝ่ายบุคคล เจ้าของคือ 4 โมดูล standalone (Divisions/Levels/Offices/Positions,
masterdata-split 2026-07-19). ทั้ง 4 ตารางมีรูปเดียวกันเป๊ะ และถูกอ้างเป็น FK จาก `admin.Users`.

| Table | Seed rows |
|---|---|
| `cfg.Divisions` | 10 |
| `cfg.Levels` | 10 |
| `cfg.Offices` | 8 |
| `cfg.Positions` | 12 |

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| Code | nvarchar(64) | N | UQ | |
| Name | nvarchar(200) | N | | |
| IsActive | bit | N | | |

---

## dbo schema (context: ControlPlane) — 1 ตาราง

### DataProtectionKey -> `dbo.DataProtectionKeys`
ASP.NET Core Data Protection key ring (plumbing, ไม่ใช่ domain entity) — ให้ OIDC correlation/state/nonce
cookies รอด restart + shared ข้าม instance. **ข้อยกเว้นเดียว** ของ schema guard ที่ยอมให้อยู่ `dbo`
(framework-owned). `pol_app` มีแค่ SELECT/INSERT (key ring เป็น append-only).

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | int (identity) | N | PK | |
| FriendlyName | nvarchar(256) | Y | | |
| Xml | nvarchar(max) | N | | key-ring element ที่ framework เข้ารหัสมาแล้ว (opaque) |

---

## merch schema — 12 ตาราง (8 = MerchantUsers, 4 = MerchantRuntime)

### User -> `merch.Users`  (context: MerchantUsers)
merchant-user identity + person details. `MerchantId` เป็น column บน user เอง (nullable — bind ตอน admin
approve; ก่อนหน้านั้น user ยัง `PendingApproval` และไม่ผูก merchant). ไม่มี column role
(อยู่ใน `merch.RoleAssignments`).

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| Subject | nvarchar(256) | N | UQ | OIDC `sub`; unique = 1 record/subject (replay/dedup guard ตอน submit) |
| Email | nvarchar(320) | N | | จาก id_token (informational) |
| Status | int | N | | `UserStatus` (PendingApproval=0, Active=1, Rejected=2, Suspended=3) |
| MerchantId | uniqueidentifier | Y | | merchant ที่ทำงานแทน (bind ตอน approve) |
| DisplayName | nvarchar(200) | N | | server-compute จาก FirstName+LastName |
| FirstName | nvarchar(200) | N | | |
| LastName | nvarchar(200) | N | | |
| PersonType | int | Y | | `PersonType` (Individual=0, Juristic=1) |
| IdNumber | nvarchar(64) | Y | | |
| ProducerCode | nvarchar(64) | Y | | |
| LicenseNumber | nvarchar(64) | Y | | |
| Phone | nvarchar(32) | Y | | |
| PhotoObjectKey | nvarchar(256) | Y | | opaque key (server-gen); bytes อยู่นอก DB |
| PhotoContentType | nvarchar(128) | Y | | |
| CreatedAt | datetime2 | N | | |

### Session -> `merch.Sessions`  (context: MerchantUsers)
server-side session ของ merchant-user BFF — โครงเหมือน `admin.Sessions` เป๊ะ (owner `MerchantUserId`
แทน `PlatformUserId`): opaque token เก็บแค่ SHA-256, rotation family + reuse detection, prune by
absolute expiry.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| FamilyId | uniqueidentifier | N | IX | rotation family |
| TokenHash | varbinary(32) | N | UQ | SHA-256 ของ cookie token |
| MerchantUserId | uniqueidentifier | N | IX | -> `merch.Users.Id`; logout-all/suspend revoke |
| Status | int | N | | `SessionStatus` (Active=0, Superseded=1, Revoked=2) |
| IssuedAt | datetime2 | N | | |
| IdleExpiresAt | datetime2 | N | | idle sliding (~30m) |
| AbsoluteExpiresAt | datetime2 | N | IX | hard cap (~8h); prune key |
| SupersededAt | datetime2 | Y | | |
| SupersededBySessionId | uniqueidentifier | Y | | reuse check |
| CreatedIp | nvarchar(45) | Y | | |
| UserAgent | nvarchar(256) | Y | | |

### AuthAudit -> `merch.AuthAudits`  (context: MerchantUsers, append-only)

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| EventType | nvarchar(32) | N | | login-success/logout/logout-all/rotated/family-revoked-reuse/auth-denied |
| MerchantUserId | uniqueidentifier | Y | IX | null เมื่อยังไม่ resolve |
| Subject | nvarchar(256) | Y | | OIDC `sub` |
| Reason | nvarchar(128) | Y | | label สั้น ไม่ sensitive |
| CorrelationId | nvarchar(128) | N | | |
| OccurredAt | datetime2 | N | | |

### ExternalLogin -> `merch.ExternalLogins`  (context: MerchantUsers)
map external identity (Google / Entra) -> merchant user. unique `(Provider, Subject)`.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| Provider | nvarchar(32) | N | UQ | unique กับ Subject; เช่น `"google"` |
| Subject | nvarchar(256) | N | UQ | unique กับ Provider |
| MerchantUserId | uniqueidentifier | N | | -> `merch.Users.Id` |

### RegistrationAudit -> `merch.RegistrationAudits`  (context: MerchantUsers, append-only)
audit ของ register/resubmit/approve/reject/suspend.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| Action | nvarchar(64) | N | | registered/resubmitted/approved/rejected/suspended |
| ActorSubject | nvarchar(256) | Y | | admin ที่ทำ (NULL = self-service) |
| TargetSubject | nvarchar(256) | N | | merchant user เป้าหมาย |
| Role | nvarchar(64) | Y | | role codes ตอน approve (joined) |
| Reason | nvarchar(1024) | Y | | เหตุผล (rejection reason ฯลฯ) |
| MerchantId | uniqueidentifier | Y | | merchant ตอน approve |
| CorrelationId | nvarchar(128) | N | | |
| OccurredAt | datetime2 | N | | |

### RegistrationNotice -> `merch.RegistrationNotices`  (context: MerchantUsers)
notice "awaiting approval" ที่ dispatcher เขียน idempotent ต่อ outbox event. ตารางนี้ **`ExcludeFromMigrations`**
— EF ไม่เคย diff/create ให้; สร้างด้วย raw SQL ใน migration `20260712185646_SecurityObjects` และ
`docker/bootstrap/assert-fresh-db.sql` เช็คว่ามันมีอยู่จริงบน fresh DB.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| MerchantUserId | uniqueidentifier | N | UQ | one notice per registration (idempotent) |
| Subject | nvarchar(256) | N | | |
| Email | nvarchar(320) | N | | |
| DisplayName | nvarchar(200) | N | | |
| HostedDomain | nvarchar(256) | Y | | |
| OccurredAt | datetime2 | N | | event time |
| CreatedAt | datetime2 | N | | notice time |

### RoleAssignment -> `merch.RoleAssignments`  (context: MerchantUsers)
ผูก merchant user กับ role ใน `iam.Roles`. ต่างจากฝั่ง admin ตรงที่ **มี** `MerchantId`
(assignment ผูก merchant). effective permission = union ของ key ทุก role ที่ Active ของ user ใน merchant นั้น.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| MerchantUserId | uniqueidentifier | N | UQ, IX | unique กับ RoleId; อีก index `(MerchantUserId, MerchantId)` |
| MerchantId | uniqueidentifier | N | IX | merchant ที่ approve |
| RoleId | uniqueidentifier | N | FK, IX, UQ | -> `iam.Roles.Id` (Restrict) |
| AssignedById | uniqueidentifier | N | | |
| AssignedAt | datetime2 | N | | |

### MerchantUserOutbox -> `merch.UserOutbox`  (context: MerchantUsers)
transactional outbox ของฝั่ง merchant-user (แยกจาก `txn.OutboxMessages` — event registration ย้ายมาที่นี่
ตอน RlsTeardown, และ `txn.OutboxMessages` ถูก CHECK constraint ห้ามถือ sentinel merchant id อีก).
index `(ProcessedAt, LeaseExpiresAt)` สำหรับ poll.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| MerchantId | uniqueidentifier | N | | |
| Type | nvarchar(256) | N | | ชนิด message |
| Payload | nvarchar(max) | N | | JSON |
| OccurredAt | datetime2 | N | | |
| ProcessedAt | datetime2 | Y | IX | null = ยังไม่ส่ง |
| Attempts | int | N | | |
| Error | nvarchar(2048) | Y | | error ล่าสุด |
| LeaseExpiresAt | datetime2 | Y | IX | |
| LeaseOwner | nvarchar(256) | Y | | dispatcher ที่ถือ lease |

### Merchant -> `merch.Merchants`  (context: MerchantRuntime)
ร้านค้า/บริษัทในเครือ 1 ราย. scalar เป็นคอลัมน์; key อื่นเก็บ verbatim ใน `Metadata` (JSON).

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| Code | nvarchar(64) | N | UQ | merchant code (มนุษย์อ่าน, ใช้ใน route) |
| DisplayName | nvarchar(200) | N | | |
| LegalEntityId | nvarchar(64) | N | | |
| Country | nvarchar(2) | N | | ISO 3166-1 alpha-2 |
| Currency | nvarchar(3) | N | | ISO 4217 |
| EnabledChannels | nvarchar(256) | N | | CSV ของช่องทาง |
| Metadata | nvarchar(max) | N | | JSON verbatim (branding/routing/session/...) |
| Status | int | N | | `MerchantStatus` (Active=0) |
| CreatedAt | datetime2 | N | | |

### VaultSecretBlob -> `merch.VaultSecrets`  (context: MerchantRuntime)
envelope encryption ต่อ secret. PK = (MerchantId, Name). secret write-only, อ่านกลับ mask.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| MerchantId | uniqueidentifier | N | PK | |
| Name | nvarchar(128) | N | PK | ชื่อ secret (= `PspConnections.SecretRefName`) |
| EncryptedSecret | varbinary(max) | N | | ciphertext (เข้ารหัสด้วย DEK) |
| EncryptedDek | varbinary(max) | N | | DEK ห่อด้วย per-merchant KEK |
| KeyId | nvarchar(64) | N | | key id+version ที่ใช้ห่อ DEK |
| Hint | nvarchar(16) | N | | mask hint (ไม่ใช่ตัว secret) |
| CreatedAt | datetime2 | N | | |
| UpdatedAt | datetime2 | N | | |

### VaultRevealAudit -> `merch.VaultRevealAudits`  (context: MerchantRuntime, append-only, tamper-evident)
chain hash ต่อ merchant (`Seq` + `Hash`/`PrevHash`). หลัง 1-principal collapse `pol_app` อ่าน head
ได้ตรงจากตาราง (proc `usp_vault_audit_head` ถูกลบไปพร้อม RLS).

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | bigint (identity) | N | PK | |
| MerchantId | uniqueidentifier | N | IX | index `(MerchantId, Id)` |
| Seq | bigint | N | UQ | unique `(MerchantId, Seq)` |
| Hash | varbinary(32) | N | | hash ของ entry นี้ |
| PrevHash | varbinary(32) | N | | hash ของ entry ก่อนหน้า (chain) |
| SecretName | nvarchar(128) | N | | |
| RevealedAt | datetime2 | N | | |

### ProvisioningAudit -> `merch.ProvisioningAudits`  (context: MerchantRuntime, append-only)
audit ของการ provision merchant.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| MerchantId | uniqueidentifier | N | | |
| MerchantCode | nvarchar(64) | N | | |
| AdminSubject | nvarchar(256) | N | | `sub` ของ admin ผู้ provision |
| CorrelationId | nvarchar(128) | N | | |
| OccurredAt | datetime2 | N | | |

---

## shop schema (context: MerchantRuntime) — 10 ตาราง

ทุกตารางในนี้อยู่ใต้ global query filter `MerchantId == CurrentMerchant`. actor ที่ยัง unbound
resolve เป็น `Guid.Empty` ซึ่งไม่มี row จริงถืออยู่ → เห็นศูนย์แถวทุกตาราง.

### Product -> `shop.Products`
สินค้าประกัน. `Price`/`SumInsured` เป็น `Money` complex property (แตกเป็น 2 คอลัมน์ต่อชุด).

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| MerchantId | uniqueidentifier | N | IX | index `(MerchantId, IsActive)` |
| Name | nvarchar(200) | N | | |
| InsurerName | nvarchar(200) | N | | property ชื่อ `Insurer` |
| CoverageDurationDays | int | N | | ระยะคุ้มครอง |
| PriceAmount | decimal(19,4) | N | | `Money.Amount` |
| PriceCurrency | char(3) | N | | `Money.Currency` (ISO 4217, fixed-length) |
| SumInsuredAmount | decimal(19,4) | N | | ทุนประกัน |
| SumInsuredCurrency | char(3) | N | | |
| IsActive | bit | N | IX | |
| CreatedAt | datetime2 | N | | |

### Cart -> `shop.Carts`

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| MerchantId | uniqueidentifier | N | AK | alternate key `AK_Carts_Id_MerchantId` (composite FK target) |
| Status | nvarchar(16) | N | | `CartStatus` เก็บเป็น **ชื่อ string** (Open/CheckedOut) |
| CreatedAt | datetime2 | N | | |

### Item -> `shop.CartItems`
FK **composite** `(CartId, MerchantId)` -> `Carts (Id, MerchantId)` cascade — merchant key เดินทางไปกับ FK
เอง จึงไม่ต้องพึ่ง predicate แยกแบบสมัย RLS. ราคา snapshot จาก catalog ตอนเพิ่ม (ไม่ใช่ราคา client).

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| CartId | uniqueidentifier | N | FK, IX | composite กับ MerchantId (Cascade) |
| MerchantId | uniqueidentifier | N | FK, IX | index `(CartId, MerchantId)` |
| ProductId | uniqueidentifier | N | | |
| Quantity | int | N | | |
| UnitPriceAmount | decimal(19,4) | N | | snapshot จาก Product |
| UnitPriceCurrency | char(3) | N | | |

### Session -> `shop.CheckoutSessions`
ล็อกยอดจาก subtotal ของ cart (ไม่ใช่ค่าจาก client). Confirm -> emit CheckoutConfirmed -> Orders เปิด order.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| MerchantId | uniqueidentifier | N | AK | alternate key `AK_CheckoutSessions_Id_MerchantId` |
| CartId | uniqueidentifier | N | | |
| AmountAmount | decimal(19,4) | N | | |
| AmountCurrency | char(3) | N | | |
| NotificationRecipient | nvarchar(320) | Y | | email ผู้รับลิงก์สรุป (optional) |
| Status | int | N | | `SessionStatus` (Started=0, Confirmed=1, Abandoned=2) |
| CreatedAt | datetime2 | N | | |

### Item -> `shop.CheckoutSessionItems`
1 บรรทัด = 1 ผู้เอาประกัน. field ผู้เอาประกัน + ราคา/ทุน/ผู้รับประกัน เป็น **snapshot ณ เวลาซื้อ**
(ไม่ตามการแก้ catalog ทีหลัง). FK composite `(SessionId, MerchantId)` -> `CheckoutSessions (Id, MerchantId)`
cascade.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| SessionId | uniqueidentifier | N | FK, IX | composite กับ MerchantId (Cascade) |
| MerchantId | uniqueidentifier | N | FK, IX | index `(SessionId, MerchantId)` |
| ProductId | uniqueidentifier | N | | |
| Quantity | int | N | | |
| UnitPriceAmount | decimal(19,4) | N | | |
| UnitPriceCurrency | char(3) | N | | |
| SumInsuredAmount | decimal(19,4) | N | | |
| SumInsuredCurrency | char(3) | N | | |
| InsurerName | nvarchar(200) | N | | property ชื่อ `Insurer` |
| CoverageDurationDays | int | N | | |
| InsuredFirstName | nvarchar(200) | N | | PII — อ่านแบบ mask, การเปิดจริงถูก audit |
| InsuredLastName | nvarchar(200) | N | | PII |
| InsuredIdNumber | nvarchar(20) | N | | PII |
| InsuredDateOfBirth | datetime2 | N | | PII |

### Order -> `shop.Orders`
`Id` ไม่ใช่ value-generated (แอป assign). `SummaryToken` = capability opaque สำหรับลูกค้าเปิดหน้าสรุปแบบ
anonymous (อ่านตรงจากตาราง — proc `usp_resolve_order_summary` ถูกลบไปพร้อม RLS).

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | app-assigned |
| MerchantId | uniqueidentifier | N | IX, AK | alternate key `AK_Orders_Id_MerchantId` |
| CheckoutSessionId | uniqueidentifier | Y | UQ* | unique เมื่อ NOT NULL (1 order ต่อ session) |
| PaymentSessionId | uniqueidentifier | Y | IX* | index เมื่อ NOT NULL |
| AmountAmount | decimal(19,4) | N | | |
| AmountCurrency | char(3) | N | | |
| Status | int | N | | `OrderStatus` (AwaitingPayment=0, Paid=1, Cancelled=2) |
| SummaryToken | nvarchar(64) | N | UQ | opaque capability token |
| SummaryTokenExpiresAt | datetime2 | N | | TTL ของลิงก์สรุป |
| NotificationRecipient | nvarchar(320) | Y | | |
| PaidAt | datetime2 | Y | | set ตอน webhook ยืนยัน Paid |
| CreatedAt | datetime2 | N | | |

### Item -> `shop.OrderItems`
เดิมชื่อ `OrderLines` — rename ด้วย `sp_rename` ใน migration `20260723122929_RenameOrderLinesToOrderItems`
(rows/GRANT/PK/FK คงอยู่). โครงเหมือน `CheckoutSessionItems` เป๊ะ ต่างที่ parent เป็น Order.
INSERT-only (ค่าที่ต้องแก้ทีหลังไปอยู่ `OrderItemPolicies` แทน).

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| OrderId | uniqueidentifier | N | FK, IX | composite กับ MerchantId -> `Orders (Id, MerchantId)` (Cascade) |
| MerchantId | uniqueidentifier | N | FK, IX | index `(OrderId, MerchantId)` |
| ProductId | uniqueidentifier | N | | |
| Quantity | int | N | | |
| UnitPriceAmount | decimal(19,4) | N | | |
| UnitPriceCurrency | char(3) | N | | |
| SumInsuredAmount | decimal(19,4) | N | | |
| SumInsuredCurrency | char(3) | N | | |
| InsurerName | nvarchar(200) | N | | property ชื่อ `Insurer` |
| CoverageDurationDays | int | N | | |
| InsuredFirstName | nvarchar(200) | N | | PII |
| InsuredLastName | nvarchar(200) | N | | PII |
| InsuredIdNumber | nvarchar(20) | N | | PII |
| InsuredDateOfBirth | datetime2 | N | | PII |

### ItemPolicy -> `shop.OrderItemPolicies`
policy-reference record ต่อ OrderItem — aggregate **mutable** 1:1 กับ OrderItem (ต่างจาก OrderItem เองที่
INSERT-only) สำหรับกรอกเลขกรมธรรม์/เบี้ย/สถานะหักส่งหลังการขาย. invariant บังคับใน `Apply` ของ aggregate.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| OrderItemId | uniqueidentifier | N | UQ | 1:1 กับ `shop.OrderItems.Id` |
| MerchantId | uniqueidentifier | N | IX | |
| InsuranceCategory | int | Y | | `InsuranceCategory` (Voluntary=0, Compulsory=1) |
| ReferenceNumberType | int | Y | | `ReferenceNumberType` (PolicyNumber=0, NotificationNumber=1) |
| ReferenceNumber | nvarchar(100) | Y | | |
| RenewalReminderNumber | nvarchar(100) | Y | | |
| EndorsementNumber | nvarchar(100) | Y | | |
| InsuredObjectReference | nvarchar(100) | Y | | |
| GrossPremiumAmount | decimal(19,4) | Y | | |
| GrossPremiumCurrency | char(3) | Y | | |
| NetPremiumAmount | decimal(19,4) | Y | | |
| NetPremiumCurrency | char(3) | Y | | |
| PremiumRemittanceStatus | int | N | | `PremiumRemittanceStatus` (NotApplicable=0, Deducted=1) |
| DeductedAt | date | Y | | `DateOnly` — วันที่หักส่ง |
| CreatedAt | datetime2 | N | | |
| UpdatedAt | datetime2 | N | | |

### ItemPolicyAudit -> `shop.OrderItemPolicyAudits`  (append-only)
audit ของทุกการเขียน `OrderItemPolicies`.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| OrderItemId | uniqueidentifier | N | IX | |
| MerchantId | uniqueidentifier | N | IX | index `(MerchantId, OccurredAt)` |
| Operation | int | N | | `AuditOperation` (Created=0, Updated=1) |
| ActorKind | int | N | | `ActorKind` (Admin=0, Merchant=1) |
| ActorId | nvarchar(200) | N | | |
| ChangeSummary | nvarchar(500) | N | | |
| CorrelationId | nvarchar(200) | N | | |
| OccurredAt | datetime2 | N | | |

### RevealAudit -> `shop.OrderItemRevealAudits`  (append-only)
audit ของการเปิดอ่าน PII ผู้เอาประกันแบบไม่ mask (unmask reveal).

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| OrderItemId | uniqueidentifier | N | IX | |
| MerchantId | uniqueidentifier | N | IX | index `(MerchantId, RevealedAt)` |
| ActorType | nvarchar(32) | N | | |
| ActorId | nvarchar(200) | N | | |
| CorrelationId | nvarchar(200) | N | | |
| RevealedAt | datetime2 | N | | |

---

## txn schema (context: MerchantRuntime) — 4 ตาราง

### Session -> `txn.PaymentSessions`
แตะ PSP ครั้งแรกตอนสร้าง redirect. `RowVersion` กัน concurrent claim. `(Psp, PspExternalChargeId)` unique
กัน webhook ซ้ำ.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| MerchantId | uniqueidentifier | N | | |
| OrderId | uniqueidentifier | N | IX | |
| AmountAmount | decimal(19,4) | N | | |
| AmountCurrency | char(3) | N | | |
| Method | nvarchar(32) | N | | payment method code (card/promptpay/installment) |
| Psp | int | N | | `Code` (TwoCTwoP=0, Omise=1) |
| PspExternalChargeId | nvarchar(256) | Y | UQ* | unique กับ Psp เมื่อ NOT NULL |
| RedirectUrl | nvarchar(2048) | Y | | `authorize_uri` ของ PSP |
| Status | int | N | | `SessionStatus` (Created=0, Redirected=1, Paid=2, Failed=3, Expired=4) |
| RowVersion | rowversion | N | | concurrency token |
| CreatedAt | datetime2 | N | | |
| UpdatedAt | datetime2 | N | | |

### Connection -> `txn.PspConnections`
config การเชื่อม PSP ต่อ merchant. secret จริงอยู่ใน vault (`SecretRefName` ชี้ไป `merch.VaultSecrets.Name`).
webhook resolve merchant จากตารางนี้ตรงๆ (proc `usp_resolve_webhook_merchant` ถูกลบไปพร้อม RLS).

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| MerchantId | uniqueidentifier | N | UQ | unique `(MerchantId, Psp)` |
| Psp | int | N | UQ | `Code` |
| EnabledMethods | nvarchar(256) | N | | CSV ของ method |
| SecretRefName | nvarchar(128) | N | | -> `merch.VaultSecrets.Name` (write-only secret) |
| Metadata | nvarchar(max) | Y | | non-secret PSP config verbatim |
| IsEnabled | bit | N | | |
| CreatedAt | datetime2 | N | | |

### OutboxMessage -> `txn.OutboxMessages`
transactional outbox + lease สำหรับ dispatcher. index `(ProcessedAt, LeaseExpiresAt)` สำหรับ poll.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| MerchantId | uniqueidentifier | N | CK | `CK_OutboxMessages_NoSentinel`: `MerchantId <> 'f0f0f0f0-0000-4000-8000-00000000ad17'` — sentinel row ย้ายไป `merch.UserOutbox` แล้ว |
| Type | nvarchar(256) | N | | ชนิด message |
| Payload | nvarchar(max) | N | | JSON |
| OccurredAt | datetime2 | N | | |
| ProcessedAt | datetime2 | Y | IX | null = ยังไม่ส่ง |
| Attempts | int | N | | |
| Error | nvarchar(2048) | Y | | error ล่าสุด |
| LeaseExpiresAt | datetime2 | Y | IX | |
| LeaseOwner | nvarchar(256) | Y | | dispatcher ที่ถือ lease |

### IdempotencyRecord -> `txn.IdempotencyRecords`
idempotency key store (PK = Key string). กัน replay/duplicate.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Key | nvarchar(400) | N | PK | idempotency key |
| Context | nvarchar(256) | N | | scope/handler ของ key |
| MerchantId | uniqueidentifier | N | | |
| CreatedAt | datetime2 | N | | |

---

## Schema objects beyond tables

### DB principal — `pol_app` (ตัวเดียว)

`docker/bootstrap/01-principals.sql` สร้าง login+user `pol_app`. migration
`20260719081817_RlsTeardownAndOnePrincipal` ยุบ principal เดิมทั้งหมด (`pol_admin`, `pol_worker`,
`pol_resolver`, `pol_vault_auditor` + role `pol_rls_bypass`) เข้ามาเป็น `pol_app` ตัวเดียว, และให้ grant
เป็น union ของสิทธิ์เดิมทั้งหมด. `docker/bootstrap/assert-fresh-db.sql` บังคับสถานะปลายทางนี้บน fresh DB
(fail ถ้ามี legacy principal / RLS object โผล่กลับมา).

| ชั้น | สิทธิ์ที่ `pol_app` ถือ |
|---|---|
| `shop.Products` · `shop.Carts` · `shop.CartItems` · `shop.CheckoutSessions` · `shop.Orders` | SELECT, INSERT, UPDATE, DELETE |
| `shop.OrderItems` · `shop.CheckoutSessionItems` · `shop.OrderItemRevealAudits` | SELECT, INSERT |
| `shop.OrderItemPolicies` | SELECT, INSERT, UPDATE |
| `shop.OrderItemPolicyAudits` | SELECT, INSERT |
| `txn.PaymentSessions` · `txn.OutboxMessages` | SELECT, INSERT, UPDATE |
| `txn.PspConnections` · `txn.IdempotencyRecords` | SELECT, INSERT |
| `merch.Merchants` · `merch.VaultSecrets` · `merch.UserOutbox` · `merch.Users` | SELECT, INSERT, UPDATE |
| `merch.VaultRevealAudits` · `merch.RegistrationNotices` · `merch.ExternalLogins` · `merch.RegistrationAudits` · `merch.AuthAudits` · `merch.ProvisioningAudits` | SELECT, INSERT |
| `merch.Sessions` · `merch.RoleAssignments` | SELECT, INSERT, UPDATE, DELETE |
| `admin.Users` · `admin.ProvisioningOperations` | SELECT, INSERT, UPDATE |
| `admin.UserAudits` · `admin.AuthAudits` | SELECT, INSERT |
| `admin.MerchantAccess` · `admin.Sessions` · `admin.RoleAssignments` | SELECT, INSERT, UPDATE, DELETE |
| `cfg.Positions` · `cfg.Offices` · `cfg.Levels` · `cfg.Divisions` | SELECT, INSERT, UPDATE |
| `iam.PermissionGroups` · `iam.Permissions` | SELECT (catalog immutable at runtime) |
| `iam.Roles` · `iam.RolePermissions` | SELECT, INSERT, UPDATE, DELETE |
| `dbo.DataProtectionKeys` | SELECT, INSERT (key ring append-only) |

> grant ที่ authoritative อยู่ใน migration: `RlsTeardownAndOnePrincipal` (matrix หลัก),
> `GrantInsuranceLineTables`, `GrantOrderItemPolicyTables`. ตารางที่ rename ผ่าน `sp_rename` เก็บ GRANT เดิม
> ไว้อัตโนมัติ ไม่ต้อง re-grant.
>
> Blast radius ที่รับไว้โดยตั้งใจ (signed-off tradeoff ของการยุบเหลือ principal เดียว): แอปถูกเจาะ =
> อ่าน vault plaintext + audit chain ได้ระดับ DB. เดิมมี principal แยกกันช่วยกันไว้ — ตอนนี้ isolation
> ย้ายไปอยู่ที่ app layer (EF global query filter + write authorizer) แทน.

### ไม่มีอยู่แล้ว (ห้ามเขียนอ้างถึงอีก)

`sec` schema ทั้ง schema, security policy `MerchantIsolationPolicy`, predicate function
`fn_merchant_predicate`/`fn_cartitem_predicate`/`fn_outbox_predicate`, EXECUTE-AS proc
`usp_resolve_webhook_merchant`/`usp_resolve_order_summary`/`usp_vault_audit_head`, และ principal
`pol_admin`/`pol_worker`/`pol_resolver`/`pol_vault_auditor`/`pol_rls_bypass` — ทั้งหมดถูกรื้อใน
`RlsTeardownAndOnePrincipal` (+ `DropEmptySecSchema` เก็บ container ที่ว่างแล้วทิ้ง). โค้ดที่เคยเรียก proc
เหล่านี้อ่านตารางตรงแทนแล้ว.

### ตารางที่ EF ไม่ได้สร้าง

`merch.RegistrationNotices` (`ExcludeFromMigrations`) — สร้างด้วย raw SQL ใน
`20260712185646_SecurityObjects`. EF map มันไว้อ่าน/เขียนได้ แต่ไม่เคย diff เพื่อ generate DDL ให้.

### Check constraints (2 ตัวทั้งระบบ)

| Constraint | ตาราง | นิยาม |
|---|---|---|
| `CK_Roles_ScopeMerchant` | `iam.Roles` | `([Scope] = 0 AND [MerchantId] IS NULL) OR [Scope] = 1` — Platform role ห้ามผูก merchant |
| `CK_OutboxMessages_NoSentinel` | `txn.OutboxMessages` | `MerchantId <> 'f0f0f0f0-0000-4000-8000-00000000ad17'` — sentinel อยู่ `merch.UserOutbox` เท่านั้น |

---

## Enums

ค่าจริงของคอลัมน์ `int` ที่ enum-backed (ค่า stable, แยกจากชื่อ enum). ทุกตัวใช้ `HasConversion<int>()`
ยกเว้น `CartStatus` ที่ `HasConversion<string>()`.

| Enum | คอลัมน์ที่ใช้ | ค่า |
|---|---|---|
| `Admins.Domain.Users.Tier` | `admin.Users.Tier` | Scoped=0, Super=1 |
| `Admins.Domain.Users.UserStatus` | `admin.Users.Status` | Active=0, Suspended=1 |
| `Admins.Domain.Users.SessionStatus` | `admin.Sessions.Status` | Active=0, Superseded=1, Revoked=2 |
| `Merchants.Domain.Users.UserStatus` | `merch.Users.Status` | PendingApproval=0, Active=1, Rejected=2, Suspended=3 |
| `Merchants.Domain.Users.PersonType` | `merch.Users.PersonType` | Individual=0, Juristic=1 |
| `Merchants.Domain.Users.SessionStatus` | `merch.Sessions.Status` | Active=0, Superseded=1, Revoked=2 |
| `Merchants.Domain.MerchantStatus` | `merch.Merchants.Status` | Active=0 (suspend/pending เพิ่มภายหลัง — YAGNI) |
| `Iam.Domain.Roles.RoleStatus` | `iam.Roles.Status` | Active=0, Inactive=1 |
| `Iam.Domain.Permissions.Scope` | `iam.Roles.Scope`, `iam.PermissionGroups.Scope` | Platform=0, Merchant=1 |
| `Carts.Domain.CartStatus` | `shop.Carts.Status` (string) | Open, CheckedOut (เก็บเป็นชื่อ ไม่ใช่ int) |
| `Checkouts.Domain.SessionStatus` | `shop.CheckoutSessions.Status` | Started=0, Confirmed=1, Abandoned=2 |
| `Orders.Domain.OrderStatus` | `shop.Orders.Status` | AwaitingPayment=0, Paid=1, Cancelled=2 |
| `Orders.Domain.Items.InsuranceCategory` | `shop.OrderItemPolicies.InsuranceCategory` | Voluntary=0, Compulsory=1 |
| `Orders.Domain.Items.ReferenceNumberType` | `shop.OrderItemPolicies.ReferenceNumberType` | PolicyNumber=0, NotificationNumber=1 |
| `Orders.Domain.Items.PremiumRemittanceStatus` | `shop.OrderItemPolicies.PremiumRemittanceStatus` | NotApplicable=0, Deducted=1 |
| `Orders.Domain.Items.AuditOperation` | `shop.OrderItemPolicyAudits.Operation` | Created=0, Updated=1 |
| `Orders.Domain.Items.ActorKind` | `shop.OrderItemPolicyAudits.ActorKind` | Admin=0, Merchant=1 |
| `Payments.Domain.SessionStatus` | `txn.PaymentSessions.Status` | Created=0, Redirected=1, Paid=2, Failed=3, Expired=4 |
| `Payments.Domain.Psp.Code` | `txn.PaymentSessions.Psp`, `txn.PspConnections.Psp` | TwoCTwoP=0, Omise=1 (wire code: `"2c2p"`/`"omise"`) |
