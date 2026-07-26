# Entity Field Reference (persisted model)

> Generated 2026-07-26 from `PolDbContextModelSnapshot.cs` (the authoritative EF model) + the entity
> configurations under `src/Persistence/Persistence.{ControlPlane,MerchantUsers,MerchantRuntime}/**/`,
> the domain entities/enums (XML doc = ที่มาของช่องหมายเหตุ), raw-SQL migrations (grant matrix / check
> constraints / seed) และ `docker/bootstrap/seed-demo.sql` + `Iam.Domain/Permissions/Keys.cs`
> (= ที่มาของช่องตัวอย่าง). ครอบคลุม **42 ตาราง** ใน 7 schema. แก้ entity/migration เมื่อไหร่ regenerate
> ไฟล์นี้ตามด้วย.
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
- **ตัวอย่าง** = ค่าตัวแทน 1 ค่าของคอลัมน์นั้น. หัวข้อของแต่ละตารางบอกที่มา — `ตัวอย่าง: seed-demo.sql`
  (ค่าจริงจาก demo dataset), `ตัวอย่าง: migration <ชื่อ>` (ค่า seed จริงใน migration) หรือ
  `ตัวอย่าง: derive จาก <ไฟล์>` (ไม่มี seed — อ่านจากโค้ดที่ generate ค่าจริง). กติกาการเขียน:
  - GUID ย่อกลางด้วย `…` (`e1000000-…-0001` = `e1000000-0000-4000-8000-000000000001`) — รูปย่อเพื่ออ่านง่าย
    **ไม่ใช่ค่าที่ copy ไปวางได้ตรงๆ**; GUID ที่เป็น literal ในโค้ด/migration เขียนเต็มเมื่อสั้นพอ.
  - hash/secret ใส่แค่ **รูปทรง** ไม่ใช่ค่าจริง — เช่น `0x9f86d0…` (varbinary 32 bytes),
    `A3F1…` (SHA-256 hex 64 ตัว).
  - `NULL` เขียนตรงๆ เมื่อ null เป็นสถานะที่มีความหมาย (เงื่อนไขอยู่ในช่องหมายเหตุ).
  - PII ทุกค่าเป็นค่าปลอมจาก `seed-demo.sql` (dataset นั้นปลอมทั้งชุดโดยออกแบบ) — ห้ามยกค่าจริงจาก prod มาใส่.
  - `เวลาที่เขียน` = คอลัมน์ timestamp ที่ค่ามาจากนาฬิกา ณ ตอนเขียน (ไม่มีตัวอย่างเจาะจง เขียนรูปแบบ
    `2026-07-26T08:15:00Z` แทน).
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

> ตัวอย่าง: `seed-demo.sql` (6 demo rows `e2000000-…`) — โครงสร้าง/กติกาจาก `Admins.Domain/Users/User.cs`.

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `e2000000-…-0001` | `Guid.NewGuid()` ตอน `SelfProvision`/`CreateScoped` (แอป assign ไม่ใช่ DB) |
| Subject | nvarchar(256) | Y | UQ* | `demo-adm-1` (ของจริง: `117…` 21 หลักจาก Google) | OIDC `sub`; unique เฉพาะตอน NOT NULL (`[Subject] IS NOT NULL`). NULL = บัญชี invite ที่ยังไม่เคย login — `BindSubject` เขียนครั้งเดียว re-bind ไม่ได้ |
| Email | nvarchar(320) | N | UQ | `superadmin1@demo.pol.local` | verified email จาก id_token; **unique เสมอ** = invite key ที่ใช้ resolve บัญชีก่อนจะมี `Subject` |
| Tier | int | N | | `1` (Super) | `Tier` (Scoped=0, Super=1). Super = ข้ามทุก merchant; Scoped = เห็นเฉพาะที่อยู่ใน `admin.MerchantAccess` |
| Status | int | N | | `0` (Active) | `UserStatus` (Active=0, Suspended=1). ไม่มี PendingApproval ฝั่ง admin — สร้างโดย Super หรือ bootstrap allowlist เท่านั้น |
| AuthorizationVersion | bigint | N | | `0` (บัญชีที่ยังไม่เคยถูกแก้สิทธิ์) | concurrency token — bump ใน tx เดียวกับทุก write ที่เปลี่ยนสิทธิ์ (Status/Tier/Session/MerchantAccess/RoleAssignment); caller ที่ถือค่าเก่าจะ fail authorization lease |
| PositionId | uniqueidentifier | Y | FK, IX | `a1000000-…-0001` | -> `cfg.Positions.Id` (Restrict). ตำแหน่ง — NULL ได้ (บัญชี invite ที่ยังไม่ระบุ) |
| OfficeId | uniqueidentifier | Y | FK, IX | `b2000000-…-0001` | -> `cfg.Offices.Id` (Restrict). สถานที่ปฏิบัติงาน |
| LevelId | uniqueidentifier | Y | FK, IX | `c3000000-…-0001` | -> `cfg.Levels.Id` (Restrict). ระดับ |
| DivisionId | uniqueidentifier | Y | FK, IX | `d4000000-…-0001` | -> `cfg.Divisions.Id` (Restrict). ฝ่าย/ภาค — ทั้ง 4 FK แก้พร้อมกันทีเดียวผ่าน `UpdateProfile` (null = ล้างมิตินั้น) |
| CreatedAt | datetime2 | N | | `2026-07-26T08:15:00Z` | เวลาที่เขียน (`SYSUTCDATETIME()` ใน seed / `IClock.UtcNow` ใน handler) |

### MerchantAccess -> `admin.MerchantAccess`
M:N ระหว่าง Scoped platform user กับ merchant ที่เข้าถึงได้ (accessible set). unassign = hard delete.

> ตัวอย่าง: `seed-demo.sql` (4 rows `e3000000-…` — เฉพาะ Scoped; Super ไม่มีแถวเลย เพราะไม่ต้องใช้ตารางนี้).

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `e3000000-…-0001` | surrogate key; `Guid.NewGuid()` ตอน `MerchantAccess.Create` |
| PlatformUserId | uniqueidentifier | N | UQ | `e2000000-…-0003` (Scoped admin) | unique กับ MerchantId; soft reference ไป `admin.Users.Id` |
| MerchantId | uniqueidentifier | N | UQ | `e1000000-…-0001` (vprivilege) | unique กับ PlatformUserId. **ไม่มี DB FK** — Admins ไม่ reference โมดูล Merchants; ตรวจว่ามีจริง/active ผ่าน `IAdminMerchantDirectory` ตอน assign |
| AssignedByAdminId | uniqueidentifier | N | | `e2000000-…-0001` (Super) | admin ที่สั่ง assign (Super) |
| AssignedAt | datetime2 | N | | `2026-07-26T08:15:00Z` | เวลาที่เขียน |

### Session -> `admin.Sessions`
server-side session ของ admin BFF. cookie value (opaque 256-bit) **ไม่เคยเก็บ** — เก็บแค่ SHA-256 hash.
session รวมเป็น rotation family (`FamilyId`): rotate = ออก successor ใน family เดิม + mark ตัวเก่า
`Superseded` พร้อม link ไป successor (กัน replay = reuse detection). prune ลบ row ที่เลย absolute expiry.

> ตัวอย่าง: derive จาก `Admins.Domain/Users/Session.cs` + `Session:*` ใน `appsettings.json`
> (IdleMinutes 30 / AbsoluteHours 8 / RotationMinutes 15) — ไม่มี seed (session เกิดตอน login จริงเท่านั้น).

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `7c1f4d2e-…-9a30` | `Guid.NewGuid()` ตอน `Session.Start`/`Rotate` |
| FamilyId | uniqueidentifier | N | IX | `2b9e0a71-…-4f08` | rotation family; family-wide revoke. GUID ใหม่ตอน login, สืบทอดต่อทุก rotate |
| TokenHash | varbinary(32) | N | UQ | `0x9f86d0…` (32 bytes) | SHA-256 ของ cookie token (lookup O(1)). **cookie value จริงไม่เคยถูกเก็บ** |
| PlatformUserId | uniqueidentifier | N | IX | `e2000000-…-0001` | -> `admin.Users.Id`; logout-all |
| Status | int | N | | `0` (Active) | `SessionStatus` (Active=0, Superseded=1, Revoked=2). flip เป็น Superseded/Revoked ด้วย set-based update ใน store ไม่ใช่ tracked entity |
| IssuedAt | datetime2 | N | | `2026-07-26T08:15:00Z` | เวลาที่ออก session นี้ |
| IdleExpiresAt | datetime2 | N | | `2026-07-26T08:45:00Z` (= IssuedAt + 30m) | idle sliding (~30m), slide lazy |
| AbsoluteExpiresAt | datetime2 | N | IX | `2026-07-26T16:15:00Z` (= IssuedAt + 8h) | hard cap (~8h); prune sweep key. successor สืบทอดค่าเดิม — rotate ไม่ต่ออายุ hard cap |
| SupersededAt | datetime2 | Y | | `NULL` (session ที่ยัง Active) | เวลาที่ถูก rotate |
| SupersededBySessionId | uniqueidentifier | Y | | `NULL` / `9d33ab10-…-1c72` | successor (immediate-predecessor / reuse check). ใช้ token ของ predecessor ที่ไม่ใช่ตัวติดกัน = ถือว่าถูกขโมย revoke ทั้ง family |
| CreatedIp | nvarchar(45) | Y | | `203.0.113.24` (รองรับ IPv6 เต็ม 45 ตัว) | IP ตอน login; NULL ได้เมื่ออ่านไม่ได้ |
| UserAgent | nvarchar(256) | Y | | `Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) …` | ตัดที่ 256 ตัว |

### AuthAudit -> `admin.AuthAudits`  (append-only)
audit ของ auth lifecycle (login-success/logout/logout-all/rotated/family-revoked-reuse/auth-denied).
แยกจาก `admin.UserAudits` เพราะ auth event อาจไม่มี user id ที่ resolve ได้ (denial ก่อน resolve).
ไม่เก็บ secret/token/raw session id.

> ตัวอย่าง: derive จาก `Admins.Domain/Users/AuthAudit.cs` (`AuthEventType` constants) — ไม่มี seed.

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `f4a10c88-…-2b61` | `Guid.NewGuid()` ตอน `AuthAudit.For` |
| EventType | nvarchar(32) | N | | `login-success` | login-success/logout/logout-all/rotated/family-revoked-reuse/auth-denied — ค่าคงที่ใน `AuthEventType` |
| PlatformUserId | uniqueidentifier | Y | IX | `e2000000-…-0001` / `NULL` | null เมื่อยังไม่ resolve user (deny ก่อน resolve) |
| Subject | nvarchar(256) | Y | | `demo-adm-1` | OIDC `sub`; ยังบันทึกได้แม้ resolve บัญชีไม่เจอ |
| Reason | nvarchar(128) | Y | | `not-allowlisted` | label สั้น ไม่ sensitive (เหตุผล deny). NULL บน event ที่สำเร็จ |
| CorrelationId | nvarchar(128) | N | | `9f2c1ab34d5e4f6789012345abcdef01` | จาก header `X-Correlation-ID` ถ้า well-formed (ตัวอักษร/ตัวเลข/`-`/`_` ยาว <=128) ไม่งั้น mint เป็น `Guid` N-format |
| OccurredAt | datetime2 | N | | `2026-07-26T08:15:00Z` | เวลาที่เกิด event |

### Audit -> `admin.UserAudits`  (append-only)
audit ของทุก admin action (account lifecycle: self-provision/create-scoped/assign/unassign/suspend/
reactivate/session-revoke; role lifecycle: role create/update/delete/assign/unassign).

> ตัวอย่าง: derive จาก `Admins.Domain/Users/Audit.cs` (`AuditAction` constants) — ไม่มี seed.

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `3ce77a05-…-8d42` | `Guid.NewGuid()` ตอน `Audit.For` |
| Action | nvarchar(64) | N | | `assign-merchant` | ชื่อ action — ค่าคงที่ใน `AuditAction`: self-provision/create-scoped/assign-merchant/unassign-merchant/suspend/reactivate/session-revoke/tier-changed/update-profile/role-created/role-updated/role-deleted/role-assigned/role-unassigned |
| ActorId | uniqueidentifier | N | | `e2000000-…-0001` | user ที่ทำ — **required** (ต่างจาก `admin.AuthAudits` ที่ยอม null ได้); self-provision ใช้ id ของตัวเอง |
| ActorType | nvarchar(16) | N | | `admin` | `"admin"` ค่าเดียวตอนนี้ — เผื่อ actor แบบ system/automation ในอนาคต |
| TargetAdminId | uniqueidentifier | Y | | `e2000000-…-0003` | platform user เป้าหมาย (ถ้ามี); NULL บน role CRUD |
| TargetRoleId | uniqueidentifier | Y | | `11111111-1111-1111-1111-111111111111` | role เป้าหมาย (role action เท่านั้น) |
| MerchantId | uniqueidentifier | Y | | `e1000000-…-0001` | merchant ที่เกี่ยว (assign/unassign); NULL บน action อื่น |
| CorrelationId | nvarchar(128) | N | | `9f2c1ab34d5e4f6789012345abcdef01` | ผูก audit row กับ request เดียวกันข้ามตาราง (ดู `CorrelationIdMiddleware`) |
| OccurredAt | datetime2 | N | | `2026-07-26T08:15:00Z` | เวลาที่เกิด action |

### RoleAssignment -> `admin.RoleAssignments`
ผูก platform user กับ role ใน `iam.Roles` — **ไม่มี** `MerchantId` (global, ต่างจากฝั่ง merch ที่ผูก merchant).
effective permission = union ของ `PermissionKey` จากทุก role ที่ `Status = Active`.

> ตัวอย่าง: `seed-demo.sql` (6 rows `e4000000-…`) — RoleId ชี้ role ที่ migration `SeedData` สร้างไว้แล้ว.

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `e4000000-…-0001` | surrogate key; `Guid.NewGuid()` ตอน `RoleAssignment.Create` |
| PlatformUserId | uniqueidentifier | N | UQ | `e2000000-…-0001` | unique กับ RoleId (1 คน 1 role ได้ครั้งเดียว) |
| RoleId | uniqueidentifier | N | FK, IX, UQ | `11111111-1111-1111-1111-111111111111` (platform_admin) | -> `iam.Roles.Id` (Restrict) — role ที่ยังมีคนถืออยู่ ลบไม่ได้ |
| AssignedById | uniqueidentifier | N | | `e2000000-…-0001` | admin ที่สั่ง assign |
| AssignedAt | datetime2 | N | | `2026-07-26T08:15:00Z` | เวลาที่เขียน |

### ProvisioningOperation -> `admin.ProvisioningOperations`
idempotency ledger ของ merchant provisioning (multi-context coordinator). `OperationKey` unique = replay
ตัวเดิมคืนผลเดิม; `ExpectedAuthorizationVersion` ล็อกกับ `admin.Users.AuthorizationVersion` ตอนเริ่ม.

> ตัวอย่าง: derive จาก `BuildingBlocks.Infrastructure/Provisioning/ProvisioningOperation.cs` +
> `ProvisionMerchantHandler.cs` (คนตั้ง `OperationKey`) + `ProvisioningCoordinator.cs` — ไม่มี seed.

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `c05be1d4-…-77a9` | `Guid.NewGuid()` ตอน `ProvisioningOperation.Create` |
| OperationKey | nvarchar(200) | N | UQ | `provision-merchant:vprivilege` | index name `UX_ProvisioningOperations_Key`. handler ประกอบเป็น `provision-merchant:{code}` — INSERT ผ่าน raw SQL ก่อนทำงานจริง จึงชน unique index ได้ชัดเจนเมื่อมี request คู่ขนาน |
| CallerAdminId | uniqueidentifier | N | | `e2000000-…-0001` (ต้องเป็น Super ที่ Active) | replay ที่ caller ต่างจากเดิม = 409 ไม่คืนผลเดิมให้ |
| ExpectedAuthorizationVersion | bigint | N | | `0` | snapshot ของสิทธิ์ผู้เรียก — pin ไว้ที่ request boundary; replay เทียบกับค่าที่เก็บ ไม่ใช่ค่าที่อ่านใหม่ |
| RequestHash | nvarchar(64) | N | | `A3F1…` (SHA-256 hex ตัวใหญ่ 64 ตัว) | กัน key ซ้ำแต่ payload ต่าง = `Convert.ToHexString(SHA256(JSON ของ ProvisionSpec))` |
| MerchantId | uniqueidentifier | N | | `e1000000-…-0001` | merchant ที่ provision — pre-mint ที่นี่ก่อน แล้วใช้เป็น `merch.Merchants.Id` จริง (ตั้งใจไม่ทำ FK เพราะแถวนี้เกิดก่อน) |
| Result | nvarchar(max) | Y | | `{"MerchantId":"e1000000-…","Connections":[…]}` | JSON ผลลัพธ์ (null = ยังไม่จบ); replay ที่ match คืน body ตัวนี้ตรงๆ |
| CreatedAt | datetime2 | N | | `2026-07-26T08:15:00Z` | เวลาที่เริ่ม operation |

---

## iam schema (context: ControlPlane) — 4 ตาราง

catalog กลางของ rf2 — vocabulary เดียวที่แทน catalog เดิมซึ่งเคยซ้ำกันสองชุด (admin/merch). ไม่มี RLS
predicate; per-merchant visibility บน `Roles`/`RolePermissions` เป็น app-layer floor.
`pol_app` ได้แค่ **SELECT** บน `PermissionGroups`/`Permissions` (catalog seed โดย migration, immutable at
runtime) แต่ได้ SELECT/INSERT/UPDATE/DELETE บน `Roles`/`RolePermissions`.

### PermissionGroup -> `iam.PermissionGroups`  (10 seed rows)

> ตัวอย่าง: migration `20260712185912_SeedData` (8 กลุ่มแรก) + `20260723150000_SeedPolicyPermissions`
> (อีก 2 กลุ่ม) — vocabulary ต้นทางคือ `Iam.Domain/Permissions/Keys.cs` (integration test บังคับว่าไม่ drift).

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Key | nvarchar(32) | N | PK | `merchants.users` | เช่น `txn`, `merchant`, `user`, `system`, `merchants.users`, `catalog`, `payment`, `roles`, `merchants.policies`, `policies` — string คงที่ ห้าม rename หลัง ship |
| LabelTh | nvarchar(128) | N | | `ผู้ใช้งานร้านค้า` | ป้ายภาษาไทยที่คอนโซลใช้จัดหัวข้อ |
| Scope | int | N | | `0` (Platform) | `Scope` (Platform=0, Merchant=1). กลุ่ม Platform 6 กลุ่ม / Merchant 4 กลุ่ม — คุมว่า key ในกลุ่มนี้ให้กับ role ฝั่งไหนได้ |
| SortOrder | int | N | | `5` | ลำดับแสดงผล 1-10 (ไม่มี unique constraint) |

### Permission -> `iam.Permissions`  (24 seed rows)

> ตัวอย่าง: migration `SeedData` (20 keys) + `SeedPolicyPermissions` (4 keys) — catalog นี้ `pol_app`
> อ่านได้อย่างเดียว แก้ผ่าน migration เท่านั้น.

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Key | nvarchar(64) | N | PK | `merchants.policies.write` | เช่น `txn.view`, `roles.manage`, `payment.redirect`. ระวังคู่ที่หน้าตาคล้าย: `user.roles` (Platform) กับ `users.roles` (Merchant) เป็นคนละ key |
| GroupKey | nvarchar(32) | N | FK, IX | `merchants.policies` | -> `iam.PermissionGroups.Key` (Restrict) — `Scope` ของ key มาจากกลุ่ม ไม่ได้เก็บซ้ำที่นี่ |
| LabelTh | nvarchar(160) | N | | `แก้ไขข้อมูลกรมธรรม์ร้านค้า` | ป้ายภาษาไทยของสิทธิ์ |
| SortOrder | int | N | | `22` | ลำดับแสดงผล 1-24 เรียงข้ามกลุ่ม |

### Role -> `iam.Roles`  (4 seed rows)
seed 4 role ด้วย fixed id: `platform_admin` (anchor, ทุก Platform key), `platform_auditor`,
`merchant_manager` (anchor, ทุก Merchant key), `merchant_staff` — ทั้งหมด `Status = Active`,
`MerchantId = NULL` (shared/seed). anchor role ห้าม deactivate/delete (บังคับใน Role aggregate).

> ตัวอย่าง: migration `SeedData` (id คงที่ 4 ตัว) — กติกาจาก `Iam.Domain/Roles/Role.cs`.

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `11111111-1111-1111-1111-111111111111` (platform_admin) | seed 4 ตัวใช้ id คงที่: `1111…`=platform_admin, `5555…`=platform_auditor, `aaaa…`=merchant_manager, `bbbb…`=merchant_staff; role ที่สร้างใหม่เป็น `Guid.NewGuid()` |
| Code | nvarchar(64) | N | UQ | `platform_admin` | unique กับ MerchantId (unfiltered) — merchant สร้าง role โค้ดซ้ำกับ seed ได้เพราะคนละ bucket. บังคับ slug `^[a-z0-9_]+$` (ลงใน route `/admins/roles/{code}`) และแก้ไม่ได้หลังสร้าง |
| Name | nvarchar(128) | N | | `ผู้ดูแลแพลตฟอร์ม` | ชื่อที่แสดงในคอนโซล (แก้ได้) |
| Description | nvarchar(256) | Y | | `เข้าถึงได้ทุกส่วนของแพลตฟอร์ม รวมถึงการตั้งค่าความปลอดภัย` | คำอธิบาย; NULL ได้ |
| Color | nvarchar(16) | Y | | `red` (seed ใช้ `red`/`gray`/`blue`) | สี badge ในคอนโซล; NULL ได้ |
| Status | int | N | | `0` (Active) | `RoleStatus` (Active=0, Inactive=1). Inactive = ไม่ให้สิทธิ์อะไรเลยแม้ยังมี assignment ค้างอยู่ |
| Scope | int | N | | `0` (Platform) | `Scope` (Platform=0, Merchant=1). **immutable** ตั้งตอน Create เท่านั้น; permission ที่ grant ได้ต้อง scope ตรงกัน |
| MerchantId | uniqueidentifier | Y | UQ, CK | `NULL` (seed/shared) หรือ `e1000000-…-0001` (role ของ merchant นั้น) | null = shared/seed role |
| — | — | — | CK | — | `CK_Roles_ScopeMerchant`: `([Scope] = 0 AND [MerchantId] IS NULL) OR [Scope] = 1` |

### RolePermission -> `iam.RolePermissions`  (34 seed rows)

> ตัวอย่าง: migration `SeedData` (28 grants) + `SeedPolicyPermissions` (6 grants) — id ใช้ `NEWID()`
> ไม่คงที่ข้าม environment.

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `6f0d…` (seed ใช้ `NEWID()`) | surrogate key — ไม่ใช่ค่าที่ต้องอ้างถึง ให้ค้นด้วย (RoleId, PermissionKey) แทน |
| RoleId | uniqueidentifier | N | FK, UQ | `11111111-1111-1111-1111-111111111111` | -> `iam.Roles.Id` (Cascade); unique กับ PermissionKey — ลบ role แล้ว grant หายตาม |
| PermissionKey | nvarchar(64) | N | FK, IX, UQ | `merchants.policies.read` | -> `iam.Permissions.Key` (Restrict) — กัน phantom key. Scope ของ key ต้องตรงกับ Scope ของ role (บังคับใน `Role.Create`) |

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

> ตัวอย่าง: migration `20260712185912_SeedData` — id คงที่ (ห้ามใช้ `NEWID()` ใน migration) namespaced
> ต่อตาราง: `a1…`=Positions, `b2…`=Offices, `c3…`=Levels, `d4…`=Divisions. CI fresh-DB gate pin จำนวนแถวไว้.

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `a1000000-…-0007` (positions/manager) | id คงที่ทุก environment — `admin.Users` FK ชี้มาที่นี่จึงย้ายไม่ได้ |
| Code | nvarchar(64) | N | UQ | `manager` / `hq` / `level_3` / `customer_service` | slug lowercase snake_case ใช้อ้างอิงในโค้ด/import |
| Name | nvarchar(200) | N | | `ผู้จัดการ` / `สำนักงานใหญ่` / `ระดับ 3` / `ฝ่ายบริการลูกค้า` | ชื่อภาษาไทยที่แสดงผล |
| IsActive | bit | N | | `1` | seed ทั้งหมดเป็น 1; ปิดใช้งานด้วย 0 แทนการลบ (FK เป็น Restrict ลบไม่ได้ถ้ายังมีคนอ้าง) |

---

## dbo schema (context: ControlPlane) — 1 ตาราง

### DataProtectionKey -> `dbo.DataProtectionKeys`
ASP.NET Core Data Protection key ring (plumbing, ไม่ใช่ domain entity) — ให้ OIDC correlation/state/nonce
cookies รอด restart + shared ข้าม instance. **ข้อยกเว้นเดียว** ของ schema guard ที่ยอมให้อยู่ `dbo`
(framework-owned). `pol_app` มีแค่ SELECT/INSERT (key ring เป็น append-only).

> ตัวอย่าง: derive จาก ASP.NET Core Data Protection (`EntityFrameworkCoreXmlRepository`) — framework
> เขียนเองทั้งหมด ไม่มี seed และแอปไม่เคยเขียน/อ่านตารางนี้ตรงๆ.

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | int (identity) | N | PK | `1` | identity ของ SQL Server (ตารางเดียวในระบบที่ไม่ใช่ GUID) |
| FriendlyName | nvarchar(256) | Y | | `key-3a1f9c2e-8d44-4f10-b7c5-9e0a6d21b834` | ชื่อที่ framework ตั้งให้ key (รูปแบบ `key-<guid>`); NULL ได้ |
| Xml | nvarchar(max) | N | | `<key id="3a1f9c2e-…" version="1">…</key>` | key-ring element ที่ framework เข้ารหัสมาแล้ว (opaque) — ห้าม parse/แก้เอง |

---

## merch schema — 12 ตาราง (8 = MerchantUsers, 4 = MerchantRuntime)

### User -> `merch.Users`  (context: MerchantUsers)
merchant-user identity + person details. `MerchantId` เป็น column บน user เอง (nullable — bind ตอน admin
approve; ก่อนหน้านั้น user ยัง `PendingApproval` และไม่ผูก merchant). ไม่มี column role
(อยู่ใน `merch.RoleAssignments`).

> ตัวอย่าง: `seed-demo.sql` (12 rows `e5000000-…` — ครบทั้ง 4 Status และ 2 PersonType);
> field รูปถ่ายไม่มีใน seed — derive จาก `Merchants.Infrastructure/LocalPhotoStore.cs`.

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `e5000000-…-0001` | `Guid.NewGuid()` ตอน `User.Register` |
| Subject | nvarchar(256) | N | UQ | `demo-mch-1` | OIDC `sub`; unique = 1 record/subject (replay/dedup guard ตอน submit). ต่างจากฝั่ง admin ตรงที่ **NOT NULL** — ฝั่งนี้ผู้สมัครสมัครเองหลัง login แล้ว |
| Email | nvarchar(320) | N | | `somchai.p@demo.pol.local` | จาก id_token (informational) — ไม่ unique, ไม่ใช่ key ที่ใช้ resolve |
| Status | int | N | | `1` (Active) | `UserStatus` (PendingApproval=0, Active=1, Rejected=2, Suspended=3). เส้นทาง: Register->PendingApproval; approve->Active; reject->Rejected; resubmit->PendingApproval; suspend->Active->Suspended |
| MerchantId | uniqueidentifier | Y | | `e1000000-…-0001` (`NULL` ตอน PendingApproval) | merchant ที่ทำงานแทน (bind ตอน approve). approve ซ้ำ merchant เดิม = no-op; approve เข้า merchant อื่น = throw |
| DisplayName | nvarchar(200) | N | | `สมชาย พริวิเลจ` | server-compute จาก FirstName+LastName (ตัดที่ 200 ตัว) — ฟอร์มส่งค่านี้มาเองไม่ได้ |
| FirstName | nvarchar(200) | N | | `สมชาย` (นิติบุคคลใน seed ใช้ `-`) | required — ประกอบเป็น DisplayName |
| LastName | nvarchar(200) | N | | `พริวิเลจ` | required |
| PersonType | int | Y | | `0` (Individual) | `PersonType` (Individual=0, Juristic=1) |
| IdNumber | nvarchar(64) | Y | | `1100200300401` (บุคคล) / `0105561000045` (นิติบุคคล 13 หลัก) | เลขบัตรประชาชน/เลขนิติบุคคล — ค่าปลอมทั้งหมดใน seed |
| ProducerCode | nvarchar(64) | Y | | `PRD-VP-001` | รหัสตัวแทน; NULL ได้ (ผู้สมัครที่ยังไม่อนุมัติมัก NULL) |
| LicenseNumber | nvarchar(64) | Y | | `LIC-2024-00101` | เลขใบอนุญาตตัวแทน; NULL ได้ |
| Phone | nvarchar(32) | Y | | `0812345001` | เก็บ verbatim ไม่ normalize |
| PhotoObjectKey | nvarchar(256) | Y | | `4d9b1e77c0a34fb1a2e5c6d7e8f90123.jpg` | opaque key (server-gen); bytes อยู่นอก DB. รูปแบบ `{Guid:N}{นามสกุลตาม content-type}` — **ไม่เคยใช้ชื่อไฟล์จาก client** (กัน path traversal) |
| PhotoContentType | nvarchar(128) | Y | | `image/jpeg` | content-type ที่ผ่าน validate (type/magic byte/size) แล้ว ส่งกลับพร้อม `nosniff` |
| CreatedAt | datetime2 | N | | `2026-07-26T08:15:00Z` | เวลาที่สมัคร |

### Session -> `merch.Sessions`  (context: MerchantUsers)
server-side session ของ merchant-user BFF — โครงเหมือน `admin.Sessions` เป๊ะ (owner `MerchantUserId`
แทน `PlatformUserId`): opaque token เก็บแค่ SHA-256, rotation family + reuse detection, prune by
absolute expiry.

> ตัวอย่าง: derive จาก `Merchants.Domain/Users/Session.cs` + `MerchantAuth`/`Session:*` config — ไม่มี seed.

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `5e82ba14-…-0cd7` | `Guid.NewGuid()` ตอน Start/Rotate |
| FamilyId | uniqueidentifier | N | IX | `a71c93f0-…-6b25` | rotation family — สืบทอดข้ามทุก rotate; revoke ทั้ง family ได้ทีเดียว |
| TokenHash | varbinary(32) | N | UQ | `0x5f2e7b…` (32 bytes) | SHA-256 ของ cookie token — cookie จริงไม่ถูกเก็บ |
| MerchantUserId | uniqueidentifier | N | IX | `e5000000-…-0001` | -> `merch.Users.Id`; logout-all/suspend revoke |
| Status | int | N | | `0` (Active) | `SessionStatus` (Active=0, Superseded=1, Revoked=2) |
| IssuedAt | datetime2 | N | | `2026-07-26T08:15:00Z` | เวลาที่ออก session |
| IdleExpiresAt | datetime2 | N | | `2026-07-26T08:45:00Z` | idle sliding (~30m) |
| AbsoluteExpiresAt | datetime2 | N | IX | `2026-07-26T16:15:00Z` | hard cap (~8h); prune key |
| SupersededAt | datetime2 | Y | | `NULL` | เวลาที่ถูก rotate; NULL ตราบที่ยัง Active |
| SupersededBySessionId | uniqueidentifier | Y | | `NULL` | reuse check — ชี้ไป session ตัวถัดไปใน family |
| CreatedIp | nvarchar(45) | Y | | `203.0.113.24` | IP ตอน login |
| UserAgent | nvarchar(256) | Y | | `Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 …)` | ตัดที่ 256 ตัว |

### AuthAudit -> `merch.AuthAudits`  (context: MerchantUsers, append-only)
โครงเดียวกับ `admin.AuthAudits` ต่างที่ owner เป็น `MerchantUserId`.

> ตัวอย่าง: derive จาก `Merchants.Domain/Users/AuthAudit.cs` — ไม่มี seed.

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `b207e5cc-…-41af` | `Guid.NewGuid()` ตอนเขียน audit |
| EventType | nvarchar(32) | N | | `auth-denied` | login-success/logout/logout-all/rotated/family-revoked-reuse/auth-denied |
| MerchantUserId | uniqueidentifier | Y | IX | `e5000000-…-0001` / `NULL` | null เมื่อยังไม่ resolve |
| Subject | nvarchar(256) | Y | | `demo-mch-1` | OIDC `sub` |
| Reason | nvarchar(128) | Y | | `pending-approval` | label สั้น ไม่ sensitive — ห้ามใส่ token/secret/raw session id |
| CorrelationId | nvarchar(128) | N | | `9f2c1ab34d5e4f6789012345abcdef01` | ผูกกับ request เดียวกัน |
| OccurredAt | datetime2 | N | | `2026-07-26T08:15:00Z` | เวลาที่เกิด event |

### ExternalLogin -> `merch.ExternalLogins`  (context: MerchantUsers)
map external identity (Google / Entra) -> merchant user. unique `(Provider, Subject)`.

> ตัวอย่าง: `seed-demo.sql` (12 rows `e6000000-…`, 1:1 กับ `merch.Users`, provider `google` ทั้งหมด).

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `e6000000-…-0001` | `Guid.NewGuid()` ตอน `ExternalLogin.Create` |
| Provider | nvarchar(32) | N | UQ | `google` | unique กับ Subject; ค่าคงที่ 2 ตัวคือ `google` / `microsoft` (Entra) |
| Subject | nvarchar(256) | N | UQ | `demo-mch-1` | unique กับ Provider. Google ใช้ claim `sub`, Entra ใช้ `oid` |
| MerchantUserId | uniqueidentifier | N | | `e5000000-…-0001` | -> `merch.Users.Id`. คนหนึ่งผูกได้หลาย provider แต่ (Provider, Subject) ห้ามซ้ำ |

### RegistrationAudit -> `merch.RegistrationAudits`  (context: MerchantUsers, append-only)
audit ของ register/resubmit/approve/reject/suspend.

> ตัวอย่าง: derive จาก `Merchants.Domain/Users/RegistrationAudit.cs` (`RegistrationAuditAction`) — ไม่มี seed.

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `8ad4c0f1-…-3e69` | `Guid.NewGuid()` ตอน `RegistrationAudit.For` |
| Action | nvarchar(64) | N | | `approved` | registered/resubmitted/approved/rejected/suspended |
| ActorSubject | nvarchar(256) | Y | | `demo-adm-1` (`NULL` เมื่อ register เอง) | admin ที่ทำ (NULL = self-service). เก็บเป็น **subject string** ไม่ใช่ id — นี่คือสะพานไป `admin.Users.Subject` |
| TargetSubject | nvarchar(256) | N | | `demo-mch-3` | merchant user เป้าหมาย |
| Role | nvarchar(64) | Y | | `merchant_manager` (หลาย role คั่นด้วย comma) | role codes ตอน approve (joined); NULL บน action อื่น |
| Reason | nvarchar(1024) | Y | | `เอกสารใบอนุญาตไม่ชัดเจน` | เหตุผล (rejection reason ฯลฯ) — free text ที่ admin กรอก |
| MerchantId | uniqueidentifier | Y | | `e1000000-…-0001` | merchant ตอน approve; NULL ก่อนหน้านั้น |
| CorrelationId | nvarchar(128) | N | | `9f2c1ab34d5e4f6789012345abcdef01` | ผูกกับ request เดียวกัน |
| OccurredAt | datetime2 | N | | `2026-07-26T08:15:00Z` | เวลาที่เกิด action |

### RegistrationNotice -> `merch.RegistrationNotices`  (context: MerchantUsers)
notice "awaiting approval" ที่ dispatcher เขียน idempotent ต่อ outbox event. ตารางนี้ **`ExcludeFromMigrations`**
— EF ไม่เคย diff/create ให้; สร้างด้วย raw SQL ใน migration `20260712185646_SecurityObjects` และ
`docker/bootstrap/assert-fresh-db.sql` เช็คว่ามันมีอยู่จริงบน fresh DB.

> ตัวอย่าง: derive จาก `Merchants.Domain/Users/RegistrationNotice.cs` — ไม่มี seed (dispatcher เขียนตอน
> consume outbox event เท่านั้น).

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `d1e77b90-…-5a04` | `Guid.NewGuid()` ตอน `RegistrationNotice.For` |
| MerchantUserId | uniqueidentifier | N | UQ | `e5000000-…-0003` | one notice per registration (idempotent) — unique index คือสิ่งที่ทำให้ consumer ทน at-least-once delivery |
| Subject | nvarchar(256) | N | | `demo-mch-3` | OIDC `sub` ของผู้สมัคร (คัดลอกมาจาก event ไม่ได้ join กลับ) |
| Email | nvarchar(320) | N | | `wanida.k@demo.pol.local` | อีเมลผู้สมัคร ณ เวลาสมัคร |
| DisplayName | nvarchar(200) | N | | `วนิดา คงพริวิเลจ` | ชื่อที่จะโชว์ในรายการรออนุมัติ |
| HostedDomain | nvarchar(256) | Y | | `demo.pol.local` (`NULL` ถ้าเป็น Gmail ทั่วไป) | claim `hd` จาก Google Workspace |
| OccurredAt | datetime2 | N | | `2026-07-26T08:15:00Z` | event time — เวลาที่ผู้ใช้กดสมัคร |
| CreatedAt | datetime2 | N | | `2026-07-26T08:15:02Z` | notice time — เวลาที่ dispatcher เขียนแถวนี้ (ช้ากว่า OccurredAt เล็กน้อย) |

### RoleAssignment -> `merch.RoleAssignments`  (context: MerchantUsers)
ผูก merchant user กับ role ใน `iam.Roles`. ต่างจากฝั่ง admin ตรงที่ **มี** `MerchantId`
(assignment ผูก merchant). effective permission = union ของ key ทุก role ที่ Active ของ user ใน merchant นั้น.

> ตัวอย่าง: `seed-demo.sql` (6 rows `e7000000-…` — เฉพาะ user ที่ Active, merchant ละ 2 คน manager/staff).

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `e7000000-…-0001` | surrogate key |
| MerchantUserId | uniqueidentifier | N | UQ, IX | `e5000000-…-0001` | unique กับ RoleId; อีก index `(MerchantUserId, MerchantId)` |
| MerchantId | uniqueidentifier | N | IX | `e1000000-…-0001` | merchant ที่ approve — ต่างจาก `admin.RoleAssignments` ที่ไม่มีคอลัมน์นี้ |
| RoleId | uniqueidentifier | N | FK, IX, UQ | `aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa` (merchant_manager) | -> `iam.Roles.Id` (Restrict) — ต้องเป็น role ฝั่ง Merchant |
| AssignedById | uniqueidentifier | N | | `e5000000-…-0001` | admin ที่อนุมัติ **หรือ** merchant user เองตอน self-service (ชื่อเดิม AssignedByAdminId เรียกผิด) |
| AssignedAt | datetime2 | N | | `2026-07-26T08:15:00Z` | เวลาที่เขียน. ไม่มีคอลัมน์ status ต่อ assignment — union สิทธิ์ดูที่ status ของ role แทน |

### MerchantUserOutbox -> `merch.UserOutbox`  (context: MerchantUsers)
transactional outbox ของฝั่ง merchant-user (แยกจาก `txn.OutboxMessages` — event registration ย้ายมาที่นี่
ตอน RlsTeardown, และ `txn.OutboxMessages` ถูก CHECK constraint ห้ามถือ sentinel merchant id อีก).
index `(ProcessedAt, LeaseExpiresAt)` สำหรับ poll.

> ตัวอย่าง: derive จาก `BuildingBlocks.Infrastructure/Outbox/MerchantUserOutbox.cs` +
> `MerchantRegistrationOutboxWriter.cs` + `MerchantUserOutboxDispatcher.cs` — ไม่มี seed.

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `019820c4-…-7f31` (`Guid.CreateVersion7()`) | UUIDv7 = เรียงตามเวลา ทำให้ insert ไม่กระจายทั้ง index |
| MerchantId | uniqueidentifier | N | | `f0f0f0f0-0000-4000-8000-00000000ad17` (sentinel) | ผู้สมัครที่ยังไม่ถูก approve ยังไม่มี merchant จริง จึงใช้ sentinel — เป็นค่าเดียวที่ write authorizer ยอมให้ actor ที่ยัง unbound เขียนได้ |
| Type | nvarchar(256) | N | | `MerchantUserRegistrationSubmitted` | ชนิด message = **ชื่อคลาส** ของ notification (`type.Name` ไม่ใช่ full name) |
| Payload | nvarchar(max) | N | | `{"MerchantUserId":"e5000000-…","Subject":"demo-mch-3",…}` | JSON ของ notification object |
| OccurredAt | datetime2 | N | | `2026-07-26T08:15:00Z` | เวลาที่ enqueue (tx เดียวกับการเขียน domain) |
| ProcessedAt | datetime2 | Y | IX | `NULL` (ยังไม่ส่ง) | null = ยังไม่ส่ง; ตั้งค่าแล้ว Error/Lease ถูกล้างพร้อมกัน |
| Attempts | int | N | | `0` (เพิ่ม 1 ทุกครั้งที่ lease) | เกิน max attempts แล้ว dispatcher เลิกหยิบ |
| Error | nvarchar(2048) | Y | | `NULL` / `SqlException: timeout expired` | error ล่าสุด (ตัดที่ 2048) |
| LeaseExpiresAt | datetime2 | Y | IX | `2026-07-26T08:16:00Z` (lease 1 นาที) | หมดอายุแล้ว dispatcher ตัวอื่นหยิบต่อได้ |
| LeaseOwner | nvarchar(256) | Y | | `pol-api-7d9c4:1` (`{MachineName}:{ProcessId}`) | dispatcher ที่ถือ lease — อ่านแถวที่ lease อยู่ได้เฉพาะ owner ตัวเอง |

### Merchant -> `merch.Merchants`  (context: MerchantRuntime)
ร้านค้า/บริษัทในเครือ 1 ราย. scalar เป็นคอลัมน์; key อื่นเก็บ verbatim ใน `Metadata` (JSON).

> ตัวอย่าง: `seed-demo.sql` (3 rows `e1000000-…` = ทั้ง allowlist) — validation จาก `Merchants.Domain/Merchant.cs`.

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `e1000000-…-0001` | **คือ merchant identity เอง** — ทุกตาราง merchant-scoped อ้างค่านี้ ไม่มีคอลัมน์ owner แยก. provisioning pre-mint ค่านี้ไว้ใน `admin.ProvisioningOperations` ก่อน |
| Code | nvarchar(64) | N | UQ | `vprivilege` | merchant code (มนุษย์อ่าน, ใช้ใน route). normalize เป็น **lowercase** ตอน Create + ต้องอยู่ใน allowlist `vprivilege`/`vcommerce`/`vsouvenir` |
| DisplayName | nvarchar(200) | N | | `บริษัท วีพริวิเลจ จำกัด` | ชื่อที่แสดง |
| LegalEntityId | nvarchar(64) | N | | `0105561000011` | เลขนิติบุคคล 13 หลัก — required (seed ใช้ค่าปลอม) |
| Country | nvarchar(2) | N | | `TH` | ISO 3166-1 alpha-2 — บังคับ uppercase + ยาว 2 ตัวพอดี |
| Currency | nvarchar(3) | N | | `THB` | ISO 4217 — uppercase, validate กับ `Iso4217.IsSupported` |
| EnabledChannels | nvarchar(256) | N | | `card,promptpay,installment` | CSV ของช่องทาง; เก็บ verbatim ไม่ validate ความหมาย (`""` ได้ถ้าไม่ส่งมา) |
| Metadata | nvarchar(max) | N | | `{}` (seed) / `{"branding":{"logoUrl":"…"}}` | JSON verbatim (branding/routing/session/...) — **non-secret เท่านั้น**; default `{}` ไม่ใช่ NULL |
| Status | int | N | | `0` (Active) | `MerchantStatus` (Active=0) — ค่าเดียวตอนนี้ |
| CreatedAt | datetime2 | N | | `2026-07-26T08:15:00Z` | เวลาที่ provision |

### VaultSecretBlob -> `merch.VaultSecrets`  (context: MerchantRuntime)
envelope encryption ต่อ secret. PK = (MerchantId, Name). secret write-only, อ่านกลับ mask.

> ตัวอย่าง: derive จาก `BuildingBlocks.Infrastructure/Vault/VaultSecretBlob.cs` + `VaultOptions.cs` +
> `PspSecretEnvelopeFactory.cs` — **ไม่มี seed โดยตั้งใจ** (demo dataset ไม่เขียน secret จริงลง DB).

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| MerchantId | uniqueidentifier | N | PK | `e1000000-…-0001` | PK ส่วนแรก — 1 secret ต่อ (merchant, ชื่อ) |
| Name | nvarchar(128) | N | PK | `psp/vprivilege/2c2p` | ชื่อ secret (= `PspConnections.SecretRefName`) |
| EncryptedSecret | varbinary(max) | N | | `0x8c14fa…` | ciphertext (เข้ารหัสด้วย DEK) — envelope JSON ของ PSP ที่ adapter parse ตอน reveal |
| EncryptedDek | varbinary(max) | N | | `0x3ab902…` | DEK ห่อด้วย per-merchant KEK |
| KeyId | nvarchar(64) | N | | `local-envelope-v1` (dev) / `vault-key-2026q3` | key id+version ที่ใช้ห่อ DEK — rotate master key ได้โดยไม่ต้องเข้ารหัส secret ใหม่ทั้งหมด |
| Hint | nvarchar(16) | N | | `••••3a9f` (4 ตัวท้าย) | mask hint (ไม่ใช่ตัว secret); ค่าที่สั้นเกินไปถูก mask ทั้งตัว |
| CreatedAt | datetime2 | N | | `2026-07-26T08:15:00Z` | ตอน provision |
| UpdatedAt | datetime2 | N | | `2026-07-26T08:15:00Z` (= CreatedAt จนกว่าจะ rotate) | ขยับตอน `Rotate` |

### VaultRevealAudit -> `merch.VaultRevealAudits`  (context: MerchantRuntime, append-only, tamper-evident)
chain hash ต่อ merchant (`Seq` + `Hash`/`PrevHash`). หลัง 1-principal collapse `pol_app` อ่าน head
ได้ตรงจากตาราง (proc `usp_vault_audit_head` ถูกลบไปพร้อม RLS).

> ตัวอย่าง: derive จาก `BuildingBlocks.Infrastructure/Vault/VaultRevealAudit.cs` — ไม่มี seed.

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | bigint (identity) | N | PK | `1` | identity ของ SQL Server (ต่อเนื่องข้าม merchant) |
| MerchantId | uniqueidentifier | N | IX | `e1000000-…-0001` | index `(MerchantId, Id)` — ใช้หา head ของ chain |
| Seq | bigint | N | UQ | `1` (แถวแรกของ merchant นั้น) | unique `(MerchantId, Seq)` — ลำดับต่อ merchant เริ่มที่ 1 ไม่ใช่ต่อทั้งตาราง |
| Hash | varbinary(32) | N | | `0x7d21e9…` (SHA-256) | hash ของ entry นี้ = H(PrevHash, MerchantId, SecretName, Seq, RevealedAt) |
| PrevHash | varbinary(32) | N | | `0x0000…00` (32 zero bytes ที่ Seq=1) | hash ของ entry ก่อนหน้า (chain) — genesis ของทุก merchant คือศูนย์ 32 bytes |
| SecretName | nvarchar(128) | N | | `psp/vprivilege/2c2p` | ชื่อ secret ที่ถูกเปิดอ่าน (ไม่ใช่ค่าที่อ่านได้) |
| RevealedAt | datetime2 | N | | `2026-07-26T08:15:00Z` | เวลาที่ reveal — เป็น input ของ hash ด้วย จึงแก้ย้อนหลังไม่ได้แบบเงียบ |

### ProvisioningAudit -> `merch.ProvisioningAudits`  (context: MerchantRuntime, append-only)
audit ของการ provision merchant.

> ตัวอย่าง: derive จาก `Merchants.Domain/ProvisioningAudit.cs` — ไม่มี seed (เขียนใน tx เดียวกับการ provision).

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `2f6b8ad3-…-90c1` | `Guid.NewGuid()` ตอน `ProvisioningAudit.Create` |
| MerchantId | uniqueidentifier | N | | `e1000000-…-0001` | merchant ที่เพิ่ง provision |
| MerchantCode | nvarchar(64) | N | | `vprivilege` | code ณ เวลานั้น (denormalize ไว้ อ่าน audit ได้โดยไม่ต้อง join) |
| AdminSubject | nvarchar(256) | N | | `demo-adm-1` | `sub` ของ admin ผู้ provision |
| CorrelationId | nvarchar(128) | N | | `9f2c1ab34d5e4f6789012345abcdef01` | ผูกกับ request เดียวกัน |
| OccurredAt | datetime2 | N | | `2026-07-26T08:15:00Z` | commit/rollback พร้อมกับการ provision |

---

## shop schema (context: MerchantRuntime) — 10 ตาราง

ทุกตารางในนี้อยู่ใต้ global query filter `MerchantId == CurrentMerchant`. actor ที่ยัง unbound
resolve เป็น `Guid.Empty` ซึ่งไม่มี row จริงถืออยู่ → เห็นศูนย์แถวทุกตาราง.

### Product -> `shop.Products`
สินค้าประกัน. `Price`/`SumInsured` เป็น `Money` complex property (แตกเป็น 2 คอลัมน์ต่อชุด).

> ตัวอย่าง: migration `20260720165648_InsuranceProductSeed` (4 แผนตัวอย่าง กรอกครบทุก field) +
> `seed-demo.sql` (100 rows `e9000000-…` ที่กรอกเฉพาะราคา — field ประกันตกไปที่ default ของ migration
> `InsuranceProductFields`: `0`/`""`).

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `e9000000-…-0006` | app assign |
| MerchantId | uniqueidentifier | N | IX | `e1000000-…-0001` | index `(MerchantId, IsActive)`. **ไม่มี DB FK** ไป `merch.Merchants` — เป็นแค่ค่าที่ query filter ใช้ |
| Name | nvarchar(200) | N | | `ประกันรถยนต์ชั้น 1 Motor First Class` | ชื่อแผนที่ขาย |
| InsurerName | nvarchar(200) | N | | `Viriyah Insurance` (default `""` ถ้าไม่กรอก) | property ชื่อ `Insurer` — บริษัทผู้รับประกัน |
| CoverageDurationDays | int | N | | `365` (เดินทางสั้น = `14`) | ระยะคุ้มครอง (default `0`) |
| PriceAmount | decimal(19,4) | N | | `15900.0000` | `Money.Amount` — เบี้ยที่ลูกค้าจ่าย |
| PriceCurrency | char(3) | N | | `THB` | `Money.Currency` (ISO 4217, fixed-length) |
| SumInsuredAmount | decimal(19,4) | N | | `800000.0000` | ทุนประกัน (default `0`) |
| SumInsuredCurrency | char(3) | N | | `THB` | สกุลของทุนประกัน |
| IsActive | bit | N | IX | `1` (`0` = เลิกขาย) | ปิดการขายด้วย 0 แทนการลบ — order เก่ายังอ้าง product นี้อยู่ |
| CreatedAt | datetime2 | N | | `2026-07-26T08:15:00Z` | เวลาที่สร้างสินค้า |

### Cart -> `shop.Carts`
ตะกร้าของผู้ซื้อ 1 ใบ. อายุสั้น — จบที่ `CheckedOut` เมื่อเปิด checkout session.

> ตัวอย่าง: `seed-demo.sql` (6 rows `ea000000-…` — 2 ใบต่อ merchant, มีทั้ง Open และ CheckedOut).

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `ea000000-…-0001` | app assign |
| MerchantId | uniqueidentifier | N | AK | `e1000000-…-0001` | alternate key `AK_Carts_Id_MerchantId` (composite FK target) — ทำให้ `shop.CartItems` พก merchant key ติดไปกับ FK เอง |
| Status | nvarchar(16) | N | | `Open` (ไม่ใช่ `0`) | `CartStatus` เก็บเป็น **ชื่อ string** (Open/CheckedOut) — ตารางเดียวในระบบที่ enum ไม่ได้เก็บเป็น int |
| CreatedAt | datetime2 | N | | `2026-07-26T08:15:00Z` | เวลาที่เปิดตะกร้า |

### Item -> `shop.CartItems`
FK **composite** `(CartId, MerchantId)` -> `Carts (Id, MerchantId)` cascade — merchant key เดินทางไปกับ FK
เอง จึงไม่ต้องพึ่ง predicate แยกแบบสมัย RLS. ราคา snapshot จาก catalog ตอนเพิ่ม (ไม่ใช่ราคา client).

> ตัวอย่าง: `seed-demo.sql` (14 rows `eb000000-…`).

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `eb000000-…-0002` | app assign |
| CartId | uniqueidentifier | N | FK, IX | `ea000000-…-0001` | composite กับ MerchantId (Cascade) — ลบตะกร้าแล้วรายการหายตาม |
| MerchantId | uniqueidentifier | N | FK, IX | `e1000000-…-0001` | index `(CartId, MerchantId)`. denormalize จากตะกร้าแม่ — ค่าต้องตรงกันเสมอ (ต่าง = data bug) |
| ProductId | uniqueidentifier | N | | `e9000000-…-0002` | ต้องเป็นสินค้าของ merchant เดียวกับตะกร้า |
| Quantity | int | N | | `2` | จำนวนชิ้น |
| UnitPriceAmount | decimal(19,4) | N | | `1850.0000` | snapshot จาก Product — **ไม่ใช่ราคาที่ client ส่งมา** และไม่ขยับตามการแก้ catalog ทีหลัง |
| UnitPriceCurrency | char(3) | N | | `THB` | สกุลของราคาที่ snapshot |

### Session -> `shop.CheckoutSessions`
ล็อกยอดจาก subtotal ของ cart (ไม่ใช่ค่าจาก client). Confirm -> emit CheckoutConfirmed -> Orders เปิด order.

> ตัวอย่าง: `seed-demo.sql` (4 rows `ec000000-…` — Confirmed 2, Started 1, Abandoned 1).

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `ec000000-…-0001` | app assign |
| MerchantId | uniqueidentifier | N | AK | `e1000000-…-0001` | alternate key `AK_CheckoutSessions_Id_MerchantId` — เป้าของ composite FK จาก `shop.CheckoutSessionItems` |
| CartId | uniqueidentifier | N | | `ea000000-…-0002` | ตะกร้าที่ล็อกยอดมา (อ้างด้วย id ล้วน ไม่มี FK — Checkouts ไม่ reference โมดูล Carts) |
| AmountAmount | decimal(19,4) | N | | `56500.0000` | = SUM(Quantity x UnitPrice) ของตะกร้า ณ เวลา start |
| AmountCurrency | char(3) | N | | `THB` | สกุลของยอดที่ล็อก |
| NotificationRecipient | nvarchar(320) | Y | | `somchai.p@demo.pol.local` (`NULL` = ไม่ส่ง) | email ผู้รับลิงก์สรุป (optional) — ไหลต่อไปที่ order ตอน confirm |
| Status | int | N | | `1` (Confirmed) | `SessionStatus` (Started=0, Confirmed=1, Abandoned=2) |
| CreatedAt | datetime2 | N | | `2026-07-26T08:15:00Z` | เวลาที่เปิด checkout |

### Item -> `shop.CheckoutSessionItems`
1 บรรทัด = 1 ผู้เอาประกัน. field ผู้เอาประกัน + ราคา/ทุน/ผู้รับประกัน เป็น **snapshot ณ เวลาซื้อ**
(ไม่ตามการแก้ catalog ทีหลัง). FK composite `(SessionId, MerchantId)` -> `CheckoutSessions (Id, MerchantId)`
cascade.

> ตัวอย่าง: derive จาก `Checkouts.Domain/Items/Item.cs` (validate ชุดเดียวกับ `shop.OrderItems`) — ไม่มี seed;
> ค่าที่ยกมาเทียบเคียงกับ `shop.OrderItems` ใน `seed-demo.sql` ซึ่งเป็นปลายทางของข้อมูลชุดเดียวกัน.

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `7b0e5c19-…-42fd` | app assign ตอน `Session.Start` |
| SessionId | uniqueidentifier | N | FK, IX | `ec000000-…-0001` | composite กับ MerchantId (Cascade) |
| MerchantId | uniqueidentifier | N | FK, IX | `e1000000-…-0001` | index `(SessionId, MerchantId)`; denormalize จาก session แม่ |
| ProductId | uniqueidentifier | N | | `e9000000-…-0006` | สินค้าที่ซื้อ |
| Quantity | int | N | | `1` | บังคับ 1 เสมอ — 1 บรรทัด = 1 ผู้เอาประกัน |
| UnitPriceAmount | decimal(19,4) | N | | `15900.0000` | เบี้ยที่ตกลง ณ เวลา start |
| UnitPriceCurrency | char(3) | N | | `THB` | สกุลเบี้ย |
| SumInsuredAmount | decimal(19,4) | N | | `1000000.0000` | ทุนประกัน snapshot |
| SumInsuredCurrency | char(3) | N | | `THB` | สกุลทุนประกัน |
| InsurerName | nvarchar(200) | N | | `วิริยะประกันภัย` | property ชื่อ `Insurer` |
| CoverageDurationDays | int | N | | `365` | ระยะคุ้มครอง snapshot |
| InsuredFirstName | nvarchar(200) | N | | `สมชาย` | PII — อ่านแบบ mask, การเปิดจริงถูก audit |
| InsuredLastName | nvarchar(200) | N | | `ใจดี` | PII |
| InsuredIdNumber | nvarchar(20) | N | | `1103700123456` (13 หลัก) | PII |
| InsuredDateOfBirth | datetime2 | N | | `1985-03-15T00:00:00Z` | PII — เก็บเป็น datetime2 (ไม่ใช่ `date`) |

### Order -> `shop.Orders`
`Id` ไม่ใช่ value-generated (แอป assign). `SummaryToken` = capability opaque สำหรับลูกค้าเปิดหน้าสรุปแบบ
anonymous (อ่านตรงจากตาราง — proc `usp_resolve_order_summary` ถูกลบไปพร้อม RLS).

> ตัวอย่าง: `seed-demo.sql` (40 rows `ed000000-…` — 25 Paid / 10 AwaitingPayment / 5 Cancelled);
> รูปแบบ token/TTL จาก `Orders.Domain/Order.cs`.

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `ed000000-…-0016` | app-assigned (ไม่ใช่ value-generated) |
| MerchantId | uniqueidentifier | N | IX, AK | `e1000000-…-0001` | alternate key `AK_Orders_Id_MerchantId` — เป้าของ composite FK จาก `shop.OrderItems` |
| CheckoutSessionId | uniqueidentifier | Y | UQ* | `ec000000-…-0001` (`NULL` ถ้าไม่ได้มาทาง checkout) | unique เมื่อ NOT NULL (1 order ต่อ session) — กัน CheckoutConfirmed ที่ถูก replay สร้าง order ซ้ำ |
| PaymentSessionId | uniqueidentifier | Y | IX* | `ee000000-…-0016` (`NULL` ก่อนเริ่มจ่าย) | index เมื่อ NOT NULL |
| AmountAmount | decimal(19,4) | N | | `56500.0000` | ยอดที่ต้องชำระ — `MarkPaid` ตรวจยอด+สกุลซ้ำก่อนเปลี่ยนสถานะ |
| AmountCurrency | char(3) | N | | `THB` | สกุลของยอด |
| Status | int | N | | `1` (Paid) | `OrderStatus` (AwaitingPayment=0, Paid=1, Cancelled=2) |
| SummaryToken | nvarchar(64) | N | UQ | `3f7a91c0e4b8426d8c15aa72e6d40391` (`Guid` N-format 32 hex; seed ใช้ `demo-ord-00016`) | opaque capability token — ลูกค้าเปิดหน้าสรุปแบบ anonymous ด้วยค่านี้; หมุนใหม่ทุกครั้งที่ resend |
| SummaryTokenExpiresAt | datetime2 | N | | `2026-07-29T08:15:00Z` (= CreatedAt + 72h) | TTL ของลิงก์สรุป — เปิดหลังหมดอายุได้ 410 Gone |
| NotificationRecipient | nvarchar(320) | Y | | `somchai.p@demo.pol.local` (`NULL` = ไม่มีผู้รับ) | ไหลมาจาก checkout session; ใช้ตอน resend ลิงก์สรุป |
| PaidAt | datetime2 | Y | | `2026-07-26T10:15:00Z` (`NULL` เมื่อยังไม่จ่าย) | set ตอน webhook ยืนยัน Paid |
| CreatedAt | datetime2 | N | | `2026-07-26T08:15:00Z` | เวลาที่เปิด order |

### Item -> `shop.OrderItems`
เดิมชื่อ `OrderLines` — rename ด้วย `sp_rename` ใน migration `20260723122929_RenameOrderLinesToOrderItems`
(rows/GRANT/PK/FK คงอยู่). โครงเหมือน `CheckoutSessionItems` เป๊ะ ต่างที่ parent เป็น Order.
INSERT-only (ค่าที่ต้องแก้ทีหลังไปอยู่ `OrderItemPolicies` แทน).

> ตัวอย่าง: `seed-demo.sql` (4 rows `ef000000-…` — 2 รายการอยู่ order เดียวกันและเป็นคนเอาประกันคนเดียวกัน
> เพื่อครอบเคส "ภาคสมัครใจ + พ.ร.บ. รถคันเดียว"; `ef…0004` ตั้งใจไม่มีแถวใน `OrderItemPolicies`).

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `ef000000-…-0001` | app assign ตอน `Order.Create` |
| OrderId | uniqueidentifier | N | FK, IX | `ed000000-…-0016` | composite กับ MerchantId -> `Orders (Id, MerchantId)` (Cascade) |
| MerchantId | uniqueidentifier | N | FK, IX | `e1000000-…-0001` | index `(OrderId, MerchantId)`; denormalize จาก order แม่ |
| ProductId | uniqueidentifier | N | | `e9000000-…-0006` | สินค้าที่ขาย |
| Quantity | int | N | | `1` | บังคับ 1 — 1 บรรทัด = 1 ผู้เอาประกัน; ผลรวมของทุกบรรทัดต้องเท่ากับ `Orders.Amount` เป๊ะ |
| UnitPriceAmount | decimal(19,4) | N | | `15900.0000` | เบี้ยที่ขายจริง (snapshot ณ เวลาซื้อ) |
| UnitPriceCurrency | char(3) | N | | `THB` | สกุลเบี้ย |
| SumInsuredAmount | decimal(19,4) | N | | `1000000.0000` (พ.ร.บ. = `200000.0000`) | ทุนประกัน snapshot |
| SumInsuredCurrency | char(3) | N | | `THB` | สกุลทุนประกัน |
| InsurerName | nvarchar(200) | N | | `วิริยะประกันภัย` | property ชื่อ `Insurer` — snapshot ไม่ตามการแก้ catalog |
| CoverageDurationDays | int | N | | `365` | ระยะคุ้มครอง snapshot |
| InsuredFirstName | nvarchar(200) | N | | `สมชาย` | PII |
| InsuredLastName | nvarchar(200) | N | | `ใจดี` | PII |
| InsuredIdNumber | nvarchar(20) | N | | `1103700123456` | PII |
| InsuredDateOfBirth | datetime2 | N | | `1985-03-15T00:00:00Z` | PII |

### ItemPolicy -> `shop.OrderItemPolicies`
policy-reference record ต่อ OrderItem — aggregate **mutable** 1:1 กับ OrderItem (ต่างจาก OrderItem เองที่
INSERT-only) สำหรับกรอกเลขกรมธรรม์/เบี้ย/สถานะหักส่งหลังการขาย. invariant บังคับใน `Apply` ของ aggregate.

> ตัวอย่าง: `seed-demo.sql` (3 rows `f1000000-…` ครอบทั้ง Voluntary/Compulsory และ Deducted/NotApplicable) —
> invariant จาก `Orders.Domain/Items/ItemPolicy.cs`.

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `f1000000-…-0001` | app assign ตอน `ItemPolicy.Create` |
| OrderItemId | uniqueidentifier | N | UQ | `ef000000-…-0001` | 1:1 กับ `shop.OrderItems.Id` — item ที่ยังไม่กรอกกรมธรรม์จะ **ไม่มีแถวที่นี่เลย** (ไม่ใช่แถวที่ null ทั้งหมด) |
| MerchantId | uniqueidentifier | N | IX | `e1000000-…-0001` | ตั้งครั้งเดียวตอน Create — `Apply` ไม่แตะ |
| InsuranceCategory | int | Y | | `0` (Voluntary/ภาคสมัครใจ) | `InsuranceCategory` (Voluntary=0, Compulsory=1). null = ยังไม่กรอก (ไม่มีสมาชิก enum สำหรับ "ยังไม่ระบุ") |
| ReferenceNumberType | int | Y | | `0` (PolicyNumber) | `ReferenceNumberType` (PolicyNumber=0, NotificationNumber=1). ต้องมาคู่กับ `ReferenceNumber` ทั้งสองทาง |
| ReferenceNumber | nvarchar(100) | Y | | `POL-2026-VP-000123` | เลขกรมธรรม์/เลขรับแจ้ง (แล้วแต่ type). ค่าว่าง/ช่องว่างถือเป็น "ยังไม่กรอก" (เก็บเป็น null) |
| RenewalReminderNumber | nvarchar(100) | Y | | `REM-2026-VC-045` | เลขใบเตือนต่ออายุ — กรอกได้เมื่อมี `ReferenceNumber` แล้วเท่านั้น |
| EndorsementNumber | nvarchar(100) | Y | | `END-2026-0007` | สลักหลัง — กรอกได้เมื่อมี `ReferenceNumber` แล้วเท่านั้น |
| InsuredObjectReference | nvarchar(100) | Y | | `กข-1234 กรุงเทพมหานคร` | อ้างอิงวัตถุที่เอาประกัน (ทะเบียนรถ ฯลฯ) — generic ไม่ผูกกับ Motor |
| GrossPremiumAmount | decimal(19,4) | Y | | `15900.0000` | เบี้ยรวม — ต้องตั้งคู่กับ Net (both-or-neither) และ >= Net |
| GrossPremiumCurrency | char(3) | Y | | `THB` | บังคับ THB เท่านั้น |
| NetPremiumAmount | decimal(19,4) | Y | | `15000.0000` (เท่ากับ Gross ก็ได้) | เบี้ยสุทธิ |
| NetPremiumCurrency | char(3) | Y | | `THB` | บังคับ THB เท่านั้น |
| PremiumRemittanceStatus | int | N | | `1` (Deducted) | `PremiumRemittanceStatus` (NotApplicable=0, Deducted=1) |
| DeductedAt | date | Y | | `2026-07-15` (`NULL` เมื่อ NotApplicable) | `DateOnly` — วันที่หักส่ง. required เมื่อ Deducted, ห้ามเป็นอนาคต (เทียบวันที่ไทย UTC+7), และถูกล้างอัตโนมัติเมื่อกลับเป็น NotApplicable |
| CreatedAt | datetime2 | N | | `2026-07-26T08:15:00Z` | เวลาที่สร้างระเบียนเปล่า |
| UpdatedAt | datetime2 | N | | `2026-07-26T09:40:00Z` | ขยับทุกครั้งที่ `Apply` สำเร็จ |

### ItemPolicyAudit -> `shop.OrderItemPolicyAudits`  (append-only)
audit ของทุกการเขียน `OrderItemPolicies`.

> ตัวอย่าง: derive จาก `Orders.Domain/Items/ItemPolicyAudit.cs` — ไม่มี seed.

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `c8f43ba7-…-1d05` | `Guid.NewGuid()` ตอน `ItemPolicyAudit.For` |
| OrderItemId | uniqueidentifier | N | IX | `ef000000-…-0001` | item ที่ถูกเขียนกรมธรรม์ |
| MerchantId | uniqueidentifier | N | IX | `e1000000-…-0001` | index `(MerchantId, OccurredAt)` |
| Operation | int | N | | `0` (Created) | `AuditOperation` (Created=0, Updated=1) |
| ActorKind | int | N | | `1` (Merchant) | `ActorKind` (Admin=0, Merchant=1) |
| ActorId | nvarchar(200) | N | | `e5000000-…-0001` (merchant user) / `demo-adm-1` (admin subject) | ตัวตนผู้เขียน — เก็บเป็น string เพราะสอง actor kind ใช้คนละรูปแบบ id |
| ChangeSummary | nvarchar(500) | N | | `ReferenceNumber,EndorsementNumber` (`""` เมื่อเขียนแล้วไม่มีอะไรเปลี่ยน) | **ชื่อ field ที่เปลี่ยน คั่นด้วย comma — ไม่เคยเก็บค่า** จึงไม่ต้อง redact แถวนี้เลย |
| CorrelationId | nvarchar(200) | N | | `9f2c1ab34d5e4f6789012345abcdef01` | ผูกกับ request เดียวกัน |
| OccurredAt | datetime2 | N | | `2026-07-26T09:40:00Z` | เวลาที่เขียน |

### RevealAudit -> `shop.OrderItemRevealAudits`  (append-only)
audit ของการเปิดอ่าน PII ผู้เอาประกันแบบไม่ mask (unmask reveal).

> ตัวอย่าง: derive จาก `Orders.Domain/Items/RevealAudit.cs` — ไม่มี seed.

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `0a5cd8e2-…-6f37` | `Guid.NewGuid()` ตอน `RevealAudit.For` |
| OrderItemId | uniqueidentifier | N | IX | `ef000000-…-0001` | 1 แถวต่อ 1 item ที่ถูกเปิด — อ่าน order ที่มี N item เขียน N แถว |
| MerchantId | uniqueidentifier | N | IX | `e1000000-…-0001` | index `(MerchantId, RevealedAt)` |
| ActorType | nvarchar(32) | N | | `merchant-user` | `"admin"` หรือ `"merchant-user"` — ตอนนี้เขียนแค่ `merchant-user` (endpoint อ่านของ admin ยังไม่อยู่ใน scope) |
| ActorId | nvarchar(200) | N | | `e5000000-…-0001` | ตัวตนผู้เปิดอ่าน |
| CorrelationId | nvarchar(200) | N | | `9f2c1ab34d5e4f6789012345abcdef01` | ผูกกับ request เดียวกัน |
| RevealedAt | datetime2 | N | | `2026-07-26T09:40:00Z` | เวลาที่เปิดอ่าน (ไม่ใช่ hash chain แบบ `merch.VaultRevealAudits`) |

---

## txn schema (context: MerchantRuntime) — 4 ตาราง

### Session -> `txn.PaymentSessions`
แตะ PSP ครั้งแรกตอนสร้าง redirect. `RowVersion` กัน concurrent claim. `(Psp, PspExternalChargeId)` unique
กัน webhook ซ้ำ.

> ตัวอย่าง: `seed-demo.sql` (36 rows `ee000000-…`, 1 ต่อ order ยกเว้น 4 order ที่ยังไม่เริ่มจ่าย).

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `ee000000-…-0016` | app assign; ใช้เป็น `Idempotency-Key` ที่ส่งให้ Omise ด้วย (`ToString("N")`) |
| MerchantId | uniqueidentifier | N | | `e1000000-…-0001` | copy จาก order แม่ |
| OrderId | uniqueidentifier | N | IX | `ed000000-…-0016` | order ที่กำลังชำระ |
| AmountAmount | decimal(19,4) | N | | `56500.0000` | copy จาก order แม่ — webhook ตรวจยอดนี้ก่อนยืนยัน Paid |
| AmountCurrency | char(3) | N | | `THB` | copy จาก order แม่ |
| Method | nvarchar(32) | N | | `promptpay` | payment method code (card/promptpay/installment) — ต้องอยู่ใน `EnabledMethods` ของ connection |
| Psp | int | N | | `0` (2C2P) | `Code` (TwoCTwoP=0, Omise=1) |
| PspExternalChargeId | nvarchar(256) | Y | UQ* | `demo_chrg_16` (`NULL` ก่อน PSP ตอบ) | unique กับ Psp เมื่อ NOT NULL — กัน webhook ตัวเดิมถูกประมวลผลซ้ำ |
| RedirectUrl | nvarchar(2048) | Y | | `https://demo.psp.local/checkout/16` | `authorize_uri` ของ PSP; NULL ตราบที่ยัง Created |
| Status | int | N | | `2` (Paid) | `SessionStatus` (Created=0, Redirected=1, Paid=2, Failed=3, Expired=4) |
| RowVersion | rowversion | N | | `0x00000000000007D1` (SQL Server สร้างเอง) | concurrency token — กัน claim ซ้อนจาก webhook ที่มาพร้อมกัน; **ห้ามใส่ค่าตอน INSERT** |
| CreatedAt | datetime2 | N | | `2026-07-26T08:20:00Z` | ตอนสร้าง session (หลัง order ~5 นาทีใน seed) |
| UpdatedAt | datetime2 | N | | `2026-07-26T09:20:00Z` | ขยับตอนสถานะเปลี่ยน |

### Connection -> `txn.PspConnections`
config การเชื่อม PSP ต่อ merchant. secret จริงอยู่ใน vault (`SecretRefName` ชี้ไป `merch.VaultSecrets.Name`).
webhook resolve merchant จากตารางนี้ตรงๆ (proc `usp_resolve_webhook_merchant` ถูกลบไปพร้อม RLS).

> ตัวอย่าง: `seed-demo.sql` (6 rows `e8000000-…` — merchant ละ 2 PSP; `e8…0006` เป็น `IsEnabled = 0`).

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `e8000000-…-0001` | id นี้ถูกใส่ในทั้ง webhook URL และ idempotency key |
| MerchantId | uniqueidentifier | N | UQ | `e1000000-…-0001` | unique `(MerchantId, Psp)` — 1 merchant มีได้ 1 connection ต่อ PSP |
| Psp | int | N | UQ | `0` (2C2P) | `Code` — wire code เป็น `"2c2p"`/`"omise"` |
| EnabledMethods | nvarchar(256) | N | | `card,promptpay,installment` | CSV ของ method — ต้องเป็น subset ของ `merch.Merchants.EnabledChannels` |
| SecretRefName | nvarchar(128) | N | | `psp/vprivilege/2c2p` | -> `merch.VaultSecrets.Name` (write-only secret). seed ตั้งชื่อไว้เฉยๆ โดยไม่มี secret จริงหนุนหลัง |
| Metadata | nvarchar(max) | Y | | `NULL` / `{"Config":{…},"MerchantId":"…","SecretHints":{"secretKey":"****3a9f"}}` | non-secret PSP config verbatim + masked hint สำหรับอ่านกลับ |
| IsEnabled | bit | N | | `1` | ปิดชั่วคราวด้วย 0 โดยไม่ต้องลบ config |
| CreatedAt | datetime2 | N | | `2026-07-26T08:15:00Z` | ตอน provision |

### OutboxMessage -> `txn.OutboxMessages`
transactional outbox + lease สำหรับ dispatcher. index `(ProcessedAt, LeaseExpiresAt)` สำหรับ poll.

> ตัวอย่าง: derive จาก `BuildingBlocks.Infrastructure/Outbox/OutboxMessage.cs` +
> `Persistence.MerchantRuntime/Outbox/EfOutbox.cs` — ไม่มี seed (demo dataset ไม่เขียน outbox).

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `019820c4-…-7f31` (`Guid.CreateVersion7()`) | UUIDv7 = เรียงตามเวลา |
| MerchantId | uniqueidentifier | N | CK | `e1000000-…-0001` | `CK_OutboxMessages_NoSentinel`: `MerchantId <> 'f0f0f0f0-0000-4000-8000-00000000ad17'` — sentinel row ย้ายไป `merch.UserOutbox` แล้ว |
| Type | nvarchar(256) | N | | `CheckoutConfirmed` / `PaymentPaid` | ชนิด message = ชื่อคลาสของ event (`type.Name`) |
| Payload | nvarchar(max) | N | | `{"PaymentSessionId":"ee000000-…","OrderId":"ed000000-…",…}` | JSON ของ event object |
| OccurredAt | datetime2 | N | | `2026-07-26T09:20:00Z` | enqueue ใน tx เดียวกับการเปลี่ยนสถานะ domain |
| ProcessedAt | datetime2 | Y | IX | `NULL` (ยังไม่ส่ง) | null = ยังไม่ส่ง |
| Attempts | int | N | | `0` | เพิ่มทุกครั้งที่ dispatcher claim |
| Error | nvarchar(2048) | Y | | `NULL` | error ล่าสุด |
| LeaseExpiresAt | datetime2 | Y | IX | `2026-07-26T09:21:00Z` | หมดอายุแล้วให้ตัวอื่นหยิบต่อ |
| LeaseOwner | nvarchar(256) | Y | | `pol-api-7d9c4:1` | dispatcher ที่ถือ lease (`{MachineName}:{ProcessId}`) — claim ผ่าน raw SQL ต่างจากฝั่ง `merch.UserOutbox` |

### IdempotencyRecord -> `txn.IdempotencyRecords`
idempotency key store (PK = Key string). กัน replay/duplicate.

> ตัวอย่าง: derive จาก `HandlePspWebhookHandler.cs` (คนสร้าง key) + `EfIdempotencyStore.cs` — ไม่มี seed.

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Key | nvarchar(400) | N | PK | `2c2p:e8000000-…-0001:event:evt_5f3a91` | idempotency key. webhook เขียน 2 key ต่อ 1 event: `{psp}:{connectionId}:event:{eventId}` และ `{psp}:{connectionId}:charge:{chargeId}:{status}` — ใส่ connection id เพราะ event id ของ PSP unique แค่ระดับ merchant |
| Context | nvarchar(256) | N | | `psp-webhook` | scope/handler ของ key |
| MerchantId | uniqueidentifier | N | | `e1000000-…-0001` | merchant ที่ claim key — claim เกิดหลัง resolve merchant แล้วเสมอ |
| CreatedAt | datetime2 | N | | `2026-07-26T09:20:00Z` | เวลาที่ claim |

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
