# Entity Field Reference (persisted model)

> Generated 2026-06-23 from `ProducerDbContextModelSnapshot.cs` (the authoritative EF model) + the domain
> enums + the RLS/proc/grant migrations. นี่คือรูปจริงของตารางใน schema `producer` ของ `ProducerDbContext`
> ตัวเดียว (modular monolith — ทุกโมดูล map เข้า DbContext เดียวกัน). สะท้อนสถานะปัจจุบัน: Identity module ถูกลบ
> + date field ไม่มี suffix `Utc` แล้ว. แก้ entity/migration เมื่อไหร่ regenerate ไฟล์นี้ตามด้วย.
>
> ขอบเขต: เฉพาะ entity ที่ persist ลง DB. Value object ที่ไม่มีตารางของตัวเอง (เช่น `Money` = `MinorUnits:long`
> + `Currency`) ถูก map เป็นคอลัมน์ของ entity เจ้าของ (เช่น `AmountMinorUnits`/`AmountCurrency`).

## Legend

- **Type** = SQL Server column type. `nvarchar(n)` = Unicode string ยาวสุด n; `nvarchar(max)` = ไม่จำกัด;
  `datetime2` = UTC timestamp (เก็บเป็น UTC เสมอ; field/column **ไม่ใส่** suffix `Utc` — `CreatedAt`/`UpdatedAt`/...); `uniqueidentifier` = Guid; `bigint` = `long`;
  `bit` = bool; `varbinary` = bytes; `rowversion` = optimistic-concurrency token.
- **Null** = Y ถ้า nullable, N ถ้า NOT NULL.
- **Key** = PK / FK / UQ (unique) / IX (non-unique index) / UQ* หรือ IX* = filtered index.
- enum-backed column เก็บเป็น `int` (ดูค่าใน [Enums](#enums)) ยกเว้น `Cart.Status` ที่เก็บเป็น **string ชื่อ enum**.
- **Plane:** `control` = control-plane (pol_admin only, ไม่มี RLS predicate); `data` = data-plane ใต้ RLS floor
  (scoped ด้วย `TenantId`). รายการ predicate/grant ที่เป็น authoritative อยู่ใน migration
  (`AddRlsSecurityPolicy`, `AddTenantTable`, `AddAdminIdentityTables`) — ที่นี่บอกระนาบระดับ entity เท่านั้น.

---

## Admin module (control plane)

### AdminAccount -> `producer.AdminAccounts`  (plane: control)
บัญชี admin ของ control plane. `Super` = unrestricted; `Scoped` = เห็นเฉพาะ tenant ที่ถูก assign.
`Subject` เป็น null จนกว่า login ครั้งแรกจะ bind (invite-by-email).

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| Subject | nvarchar(256) | Y | UQ* | Google `sub`; unique เฉพาะตอน NOT NULL (`[Subject] IS NOT NULL`) |
| Email | nvarchar(320) | N | UQ | |
| Tier | int | N | | `AdminTier` (Scoped=0, Super=1) |
| Status | int | N | | `AdminStatus` (Active=0, Suspended=1) |
| CreatedAt | datetime2 | N | | |

### AdminTenantAssignment -> `producer.AdminTenantAssignments`  (plane: control)
M:N ระหว่าง Scoped admin กับ tenant ที่เข้าถึงได้. unassign = hard delete.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| AdminAccountId | uniqueidentifier | N | UQ | unique กับ TenantId |
| TenantId | uniqueidentifier | N | UQ | unique กับ AdminAccountId |
| AssignedByAdminId | uniqueidentifier | N | | admin ที่สั่ง assign (Super) |
| AssignedAt | datetime2 | N | | |

### AdminAccountAudit -> `producer.AdminAccountAudits`  (plane: control, append-only)
audit ของทุก admin action (self-provision/create-scoped/assign/unassign/suspend).

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| Action | nvarchar(64) | N | | `AdminAuditAction` (SelfProvision/CreateScoped/AssignTenant/UnassignTenant/Suspend) |
| ActorId | uniqueidentifier | N | | admin ที่ทำ |
| ActorType | nvarchar(16) | N | | `"admin"` |
| TargetAdminId | uniqueidentifier | Y | | admin เป้าหมาย (ถ้ามี) |
| TenantId | uniqueidentifier | Y | | tenant ที่เกี่ยว (assign/unassign) |
| CorrelationId | nvarchar(128) | N | | |
| OccurredAt | datetime2 | N | | |

### AdminSession -> `producer.AdminSessions`  (plane: control)
server-side session ของ admin BFF. cookie value (opaque 256-bit) **ไม่เคยเก็บ** — เก็บแค่ SHA-256 hash. session
รวมเป็น rotation family (`FamilyId`): rotate = ออก successor ใน family เดิม + mark ตัวเก่า `Superseded` พร้อม link
ไป successor (กัน replay = reuse detection). prune ลบ row ที่เลย absolute expiry.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| FamilyId | uniqueidentifier | N | IX | rotation family; family-wide revoke |
| TokenHash | varbinary(32) | N | UQ | SHA-256 ของ cookie token (lookup O(1)) |
| AdminAccountId | uniqueidentifier | N | IX | -> AdminAccounts.Id; logout-all |
| Status | int | N | | `AdminSessionStatus` (Active=0, Superseded=1, Revoked=2) |
| IssuedAt | datetime2 | N | | |
| IdleExpiresAt | datetime2 | N | | idle sliding (~30m), slide lazy |
| AbsoluteExpiresAt | datetime2 | N | IX | hard cap (~8h); prune sweep key |
| SupersededAt | datetime2 | Y | | เวลาที่ถูก rotate |
| SupersededBySessionId | uniqueidentifier | Y | | successor (immediate-predecessor / reuse check) |
| CreatedIp | nvarchar(45) | Y | | |
| UserAgent | nvarchar(256) | Y | | |

### AdminAuthAudit -> `producer.AdminAuthAudits`  (plane: control, append-only)
audit ของ auth lifecycle (login-success/logout/logout-all/rotated/family-revoked-reuse/auth-denied). แยกจาก
`AdminAccountAudit` เพราะ auth event อาจไม่มี admin id ที่ resolve ได้ (denial ก่อน resolve). ไม่เก็บ secret/
token/raw session id.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| EventType | nvarchar(32) | N | | login-success/logout/logout-all/rotated/family-revoked-reuse/auth-denied |
| AdminAccountId | uniqueidentifier | Y | IX | null เมื่อยังไม่ resolve admin |
| Subject | nvarchar(256) | Y | | Google `sub` |
| Reason | nvarchar(128) | Y | | label สั้น ไม่ sensitive (เหตุผล deny) |
| CorrelationId | nvarchar(128) | N | | |
| OccurredAt | datetime2 | N | | |

---

## Tenant module

### Tenant -> `producer.Tenants`  (plane: data)
บริษัทในเครือ 1 ราย. scalar เป็นคอลัมน์; key อื่นใต้ "tenant" เก็บ verbatim ใน `Metadata` (JSON).

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| Code | nvarchar(64) | N | UQ | tenant code (มนุษย์อ่าน, ใช้ใน route) |
| DisplayName | nvarchar(200) | N | | |
| LegalEntityId | nvarchar(64) | N | | |
| Country | nvarchar(2) | N | | ISO 3166-1 alpha-2 |
| Currency | nvarchar(3) | N | | ISO 4217 |
| EnabledChannels | nvarchar(256) | N | | CSV ของช่องทาง |
| Metadata | nvarchar(max) | N | | JSON verbatim (branding/routing/session/...) |
| Status | int | N | | `TenantStatus` (Active=0) |
| CreatedAt | datetime2 | N | | |

### ProvisioningAudit -> `producer.ProvisioningAudits`  (plane: control, append-only)
audit ของการ provision tenant (cross-tenant ใต้ pol_admin).

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| TenantId | uniqueidentifier | N | | |
| TenantCode | nvarchar(64) | N | | |
| AdminSubject | nvarchar(256) | N | | `sub` ของ admin ผู้ provision |
| CorrelationId | nvarchar(128) | N | | |
| OccurredAt | datetime2 | N | | |

---

## Products module

### Product -> `producer.Products`  (plane: data)

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| TenantId | uniqueidentifier | N | IX | index (TenantId, IsActive) |
| Name | nvarchar(200) | N | | |
| PriceMinorUnits | bigint | N | | `Money.MinorUnits` |
| PriceCurrency | nvarchar(3) | N | | `Money.Currency` (ISO 4217) |
| IsActive | bit | N | IX | |
| CreatedAt | datetime2 | N | | |

---

## Cart module

### Cart -> `producer.Carts`  (plane: data)

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| TenantId | uniqueidentifier | N | | |
| Status | nvarchar(16) | N | | `CartStatus` เก็บเป็น **ชื่อ string** (Open/CheckedOut) |
| CreatedAt | datetime2 | N | | |

### CartItem -> `producer.CartItems`  (plane: data — RLS via `fn_cartitem_predicate(CartId)`)
ไม่มีคอลัมน์ `TenantId` ของตัวเอง — RLS scope ผ่าน parent `Carts.TenantId` (predicate แยกตัว, ดู [Schema objects](#schema-objects-beyond-tables-rls-stored-procedures-principals)).
FK -> Carts (cascade delete). ราคา snapshot จาก catalog ตอนเพิ่ม (ไม่ใช่ราคา client).

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| CartId | uniqueidentifier | N | FK, IX | -> Carts.Id (cascade) |
| ProductId | uniqueidentifier | N | | |
| Quantity | int | N | | |
| UnitPriceMinorUnits | bigint | N | | snapshot จาก Product |
| UnitPriceCurrency | nvarchar(3) | N | | |

---

## Checkout module

### CheckoutSession -> `producer.CheckoutSessions`  (plane: data)
ล็อกยอดจาก subtotal ของ cart (ไม่ใช่ค่าจาก client). Confirm -> emit CheckoutConfirmed -> Orders เปิด order.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| TenantId | uniqueidentifier | N | | |
| CartId | uniqueidentifier | N | | |
| AmountMinorUnits | bigint | N | | |
| AmountCurrency | nvarchar(3) | N | | |
| NotificationRecipient | nvarchar(320) | Y | | email ผู้รับลิงก์สรุป (optional) |
| Status | int | N | | `CheckoutStatus` (Started=0, Confirmed=1, Abandoned=2) |
| CreatedAt | datetime2 | N | | |

---

## Orders module

### Order -> `producer.Orders`  (plane: data)
`Id` ไม่ใช่ value-generated (แอป assign). `SummaryToken` = capability opaque สำหรับลูกค้าเปิดหน้าสรุปแบบ anonymous.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | app-assigned |
| TenantId | uniqueidentifier | N | IX | |
| CheckoutSessionId | uniqueidentifier | Y | UQ* | unique เมื่อ NOT NULL (1 order ต่อ session) |
| PaymentSessionId | uniqueidentifier | Y | IX* | index เมื่อ NOT NULL |
| AmountMinorUnits | bigint | N | | |
| AmountCurrency | nvarchar(3) | N | | |
| Status | int | N | | `OrderStatus` (AwaitingPayment=0, Paid=1, Cancelled=2) |
| SummaryToken | nvarchar(64) | N | UQ | opaque capability token |
| SummaryTokenExpiresAt | datetime2 | N | | TTL ของลิงก์สรุป |
| NotificationRecipient | nvarchar(320) | Y | | |
| PaidAt | datetime2 | Y | | set ตอน webhook ยืนยัน Paid |
| CreatedAt | datetime2 | N | | |

---

## Payments module

### PaymentSession -> `producer.PaymentSessions`  (plane: data)
แตะ PSP ครั้งแรกตอนสร้าง redirect. `RowVersion` กัน concurrent claim. `(Psp, PspExternalChargeId)` unique กัน webhook ซ้ำ.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| TenantId | uniqueidentifier | N | | |
| OrderId | uniqueidentifier | N | IX | |
| AmountMinorUnits | bigint | N | | |
| AmountCurrency | nvarchar(3) | N | | |
| Method | nvarchar(32) | N | | payment method code (card/promptpay/installment) |
| Psp | int | N | | `PspCode` (TwoCTwoP=0, Omise=1) |
| PspExternalChargeId | nvarchar(256) | Y | UQ* | unique กับ Psp เมื่อ NOT NULL |
| RedirectUrl | nvarchar(2048) | Y | | `authorize_uri` ของ PSP |
| Status | int | N | | `PaymentStatus` (Created=0, Redirected=1, Paid=2, Failed=3, Expired=4) |
| RowVersion | rowversion | N | | concurrency token |
| CreatedAt | datetime2 | N | | |
| UpdatedAt | datetime2 | N | | |

### PspConnection -> `producer.PspConnections`  (plane: data)
config การเชื่อม PSP ต่อ tenant. secret จริงอยู่ใน vault (`SecretRefName` ชี้ไป VaultSecrets.Name).

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| TenantId | uniqueidentifier | N | UQ | unique (TenantId, Psp) |
| Psp | int | N | UQ | `PspCode` |
| EnabledMethods | nvarchar(256) | N | | CSV ของ method |
| SecretRefName | nvarchar(128) | N | | -> VaultSecrets.Name (write-only secret) |
| Metadata | nvarchar(max) | Y | | non-secret PSP config verbatim |
| IsEnabled | bit | N | | |
| CreatedAt | datetime2 | N | | |

---

## BuildingBlocks (cross-cutting infrastructure)

### VaultSecretBlob -> `producer.VaultSecrets`  (plane: data)
envelope encryption ต่อ secret. PK = (TenantId, Name). secret write-only, อ่านกลับ mask.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| TenantId | uniqueidentifier | N | PK | |
| Name | nvarchar(128) | N | PK | ชื่อ secret (= PspConnection.SecretRefName) |
| EncryptedSecret | varbinary(max) | N | | ciphertext (เข้ารหัสด้วย DEK) |
| EncryptedDek | varbinary(max) | N | | DEK ห่อด้วย per-tenant KEK |
| KeyId | nvarchar(64) | N | | key id+version ที่ใช้ห่อ DEK |
| Hint | nvarchar(16) | N | | mask hint (ไม่ใช่ตัว secret) |
| CreatedAt | datetime2 | N | | |
| UpdatedAt | datetime2 | N | | |

### VaultRevealAudit -> `producer.VaultRevealAudits`  (plane: data, append-only, tamper-evident)
chain hash ต่อ tenant (`Seq` + `Hash`/`PrevHash`). pol_app insert-only; head อ่านผ่าน proc bypass.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | bigint (identity) | N | PK | |
| TenantId | uniqueidentifier | N | IX | index (TenantId, Id) |
| Seq | bigint | N | UQ | unique (TenantId, Seq) |
| Hash | varbinary(32) | N | | hash ของ entry นี้ |
| PrevHash | varbinary(32) | N | | hash ของ entry ก่อนหน้า (chain) |
| SecretName | nvarchar(128) | N | | |
| RevealedAt | datetime2 | N | | |

### OutboxMessage -> `producer.OutboxMessages`  (plane: data)
transactional outbox + lease สำหรับ worker. index (ProcessedAt, LeaseExpiresAt) สำหรับ poll.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | |
| TenantId | uniqueidentifier | N | | |
| Type | nvarchar(256) | N | | ชนิด message |
| Payload | nvarchar(max) | N | | JSON |
| OccurredAt | datetime2 | N | | |
| ProcessedAt | datetime2 | Y | IX | null = ยังไม่ส่ง |
| LeaseOwner | nvarchar(256) | Y | | worker ที่ถือ lease |
| LeaseExpiresAt | datetime2 | Y | IX | |
| Attempts | int | N | | |
| Error | nvarchar(2048) | Y | | error ล่าสุด |

### IdempotencyRecord -> `producer.IdempotencyRecords`  (plane: data)
idempotency key store (PK = Key string). กัน replay/duplicate.

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Key | nvarchar(400) | N | PK | idempotency key |
| Context | nvarchar(256) | N | | scope/handler ของ key |
| TenantId | uniqueidentifier | N | | |
| CreatedAt | datetime2 | N | | |

### DataProtectionKey -> `producer.DataProtectionKeys`  (plane: control)
ASP.NET Core Data Protection key ring (plumbing, ไม่ใช่ domain entity) — เก็บไว้เป็น control-plane table ให้ OIDC
correlation/state/nonce cookies รอด restart + shared ข้าม instance. อ่าน/เขียนผ่าน keyed pol_admin context
(`EfCoreXmlRepository`) เท่านั้น. pol_admin มีแค่ SELECT/INSERT (key ring เป็น append-only).

| Field | Type | Null | Key | หมายเหตุ |
|---|---|---|---|---|
| Id | int (identity) | N | PK | |
| FriendlyName | nvarchar(256) | Y | | |
| Xml | nvarchar(max) | N | | key-ring element ที่ framework เข้ารหัสมาแล้ว (opaque) |

---

## Schema objects beyond tables (RLS, stored procedures, principals)

ส่วนนี้คือ schema object ที่ไม่ใช่ตาราง แต่เป็นส่วนของ RLS floor + bypass path (compiler มองไม่เห็น — rename
column ต้องไล่มาที่ proc body เองด้วย).

### RLS predicate functions

- `fn_tenant_predicate(@TenantId)` — allow เมื่อ `SESSION_CONTEXT('TenantId') = @TenantId` **หรือ** caller เป็นสมาชิก role `pol_rls_bypass`.
- `fn_cartitem_predicate(@CartId)` — CartItems ไม่มี `TenantId` → allow เมื่อ bypass **หรือ** parent `Carts.TenantId` = `SESSION_CONTEXT('TenantId')`.

### Security policy `producer.TenantIsolationPolicy` (STATE = ON)

| ตาราง | predicate | mode |
|---|---|---|
| PaymentSessions · PspConnections · Products · CheckoutSessions · Carts · Orders · VaultSecrets · IdempotencyRecords · Tenants | `fn_tenant_predicate(TenantId)` (Tenants ใช้ `Id`) | FILTER + BLOCK (INSERT/UPDATE) |
| CartItems | `fn_cartitem_predicate(CartId)` | FILTER + BLOCK (INSERT/UPDATE) |
| OutboxMessages | `fn_tenant_predicate(TenantId)` | BLOCK (INSERT) only — ไม่ filter (dispatcher drain ทุก tenant) |
| VaultRevealAudits | `fn_tenant_predicate(TenantId)` | BLOCK (INSERT) only — append-only; อ่าน head ผ่าน proc |
| AdminAccounts · AdminTenantAssignments · AdminAccountAudits · AdminSessions · AdminAuthAudits · DataProtectionKeys · ProvisioningAudits | — | none (control-plane, pol_admin only) |

### Stored procedures (bypass reads — `WITH EXECUTE AS`)

| proc | EXECUTE AS | อ่าน | ใช้โดย |
|---|---|---|---|
| `usp_resolve_webhook_tenant(@PspConnectionId)` | pol_webhook_resolver | PspConnections -> TenantId | webhook (resolve tenant ก่อน bind, ก่อน verify signature) |
| `usp_resolve_order_summary(@Token)` | pol_webhook_resolver | Orders by SummaryToken (รวม SummaryTokenExpiresAt) | `GET /orders/{token}/summary` (anonymous) |
| `usp_vault_audit_head` | pol_vault_auditor | VaultRevealAudits chain head | vault reveal-audit head read |

> proc body อ้าง column ด้วยชื่อตรงๆ → rename column ต้องตามด้วย `ALTER PROCEDURE` (เช่น `RenameDateColumnsDropUtc` แก้ `usp_resolve_order_summary` ให้ใช้ `SummaryTokenExpiresAt`).

### DB principals (bootstrap: `docker/bootstrap/01-principals.sql`)

| principal | login | บทบาท |
|---|---|---|
| `pol_app` | yes | TenantConsole — own-tenant CRUD (RLS-scoped); outbox INSERT-only; idempotency claim |
| `pol_admin` | yes | AdminConsole — สมาชิก `pol_rls_bypass` → cross-tenant SELECT; เขียน control-plane tables (Admin*/ProvisioningAudit) |
| `pol_worker` | yes | dispatcher — drain OutboxMessages + update Orders (RLS-scoped) |
| `pol_webhook_resolver` | no (login-less) | bypass — รัน `usp_resolve_webhook_tenant` + `usp_resolve_order_summary` |
| `pol_vault_auditor` | no (login-less) | bypass — รัน `usp_vault_audit_head` (อ่าน audit head เท่านั้น) |
| role `pol_rls_bypass` | — | สมาชิก = pol_admin · pol_webhook_resolver · pol_vault_auditor = ทางเดียวที่ข้าม RLS |

> grant รายตารางที่ authoritative อยู่ใน migration (`AddRlsSecurityPolicy`, `AddTenantTable`, `AddVaultRevealAudit`, `AddAdminIdentityTables`). pol_app ไม่เคยได้ grant บน control-plane (admin) tables; vault plaintext อ่านกลับไม่ได้ (envelope encryption).

---

## Enums

ค่าจริงของคอลัมน์ `int` ที่ enum-backed (ค่า stable, แยกจากชื่อ enum):

| Enum | คอลัมน์ที่ใช้ | ค่า |
|---|---|---|
| `AdminTier` | AdminAccounts.Tier | Scoped=0, Super=1 |
| `AdminStatus` | AdminAccounts.Status | Active=0, Suspended=1 |
| `AdminSessionStatus` | AdminSessions.Status | Active=0, Superseded=1, Revoked=2 |
| `TenantStatus` | Tenants.Status | Active=0 (suspend/pending เพิ่มภายหลัง — YAGNI) |
| `CartStatus` | Carts.Status (string) | Open, CheckedOut (เก็บเป็นชื่อ ไม่ใช่ int) |
| `CheckoutStatus` | CheckoutSessions.Status | Started=0, Confirmed=1, Abandoned=2 |
| `OrderStatus` | Orders.Status | AwaitingPayment=0, Paid=1, Cancelled=2 |
| `PaymentStatus` | PaymentSessions.Status | Created=0, Redirected=1, Paid=2, Failed=3, Expired=4 |
| `PspCode` | PaymentSessions.Psp, PspConnections.Psp | TwoCTwoP=0, Omise=1 (wire code: `"2c2p"`/`"omise"`) |

> Identity module (producer-side actor: `TenantUser`/`ExternalLogin`/`RegistrationTicket`/`RegistrationAudit`/
> `TenantUserProfile`) ถูกลบ 2026-06-23 (migration `DropIdentityTables`) — จะ rebuild เป็น Producer module
> ภายหลัง. ตารางเหล่านั้นไม่อยู่ในรุ่นนี้แล้ว.
