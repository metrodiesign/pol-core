# Design — demo-seed-data

> Status: approved 2026-07-13 (quick, no gates)

## 1. Shape

ไฟล์ใหม่ 2 ไฟล์ + แก้ README 1 ที่. **ไม่มีไฟล์ใต้ `src/` ถูกแตะ** (REQ-7.3).

```
docker/bootstrap/
  01-principals.sql      (มีอยู่)
  assert-fresh-db.sql    (มีอยู่)
  seed-demo.sql          <- ใหม่: demo dataset ทั้งหมด, idempotent, 1 transaction
scripts/
  seed-demo.sh           <- ใหม่: wrapper sqlcmd (อ่าน env, ไม่มี secret ในไฟล์)
README.md                <- แก้: หัวข้อ "Demo seed data (dev only)"
```

การรัน:

```bash
set -a && source .env && set +a
./scripts/seed-demo.sh          # โหลด/โหลดซ้ำ demo dataset (idempotent)
```

`seed-demo.sh` ทำแค่: ตรวจว่ามี `POL_SA_PASSWORD` (หรือ `MSSQL_SA_PASSWORD`) + `POL_DB` แล้วเรียก

```bash
sqlcmd -S "${POL_SQL_SERVER:-localhost,11433}" -U sa -P "$PASS" -C -b \
       -v DbName="$DB" -i docker/bootstrap/seed-demo.sql
```

`-b` = exit non-zero เมื่อ RAISERROR (สำคัญกับ REQ-1.6).

## 2. โครงของ `seed-demo.sql`

```
SET NOCOUNT ON; SET XACT_ABORT ON;           -- REQ-1.5
USE [$(DbName)];
BEGIN TRAN;

-- (ก) admin.Users + ตารางลูกฝั่ง control plane (ไม่มี RLS) — ลบก่อน ใส่ใหม่   REQ-2.4
DELETE admin.RoleAssignments WHERE Id LIKE 'e4______-%';
DELETE admin.MerchantAccess  WHERE Id LIKE 'e3______-%';
DELETE admin.Users           WHERE Id LIKE 'e2______-%';
INSERT admin.Users ...       -- 6 แถว, DemoSuperId = 'e2000000-0000-4000-8000-000000000001'

-- (ข) stamp RLS context: สาขา Super ของ sec.fn_merchant_predicate                REQ-2.2
EXEC sp_set_session_context @key = N'UserId',     @value = <DemoSuperId>;
EXEC sp_set_session_context @key = N'MerchantId', @value = '00000000-0000-0000-0000-000000000000';

-- (ค) ลบ demo merchant-scoped (ลูก -> พ่อ)                                      REQ-1.3
DELETE txn.PaymentSessions ... shop.Orders ... shop.CheckoutSessions ...
DELETE shop.CartItems ... shop.Carts ... shop.Products ... txn.PspConnections ...
DELETE merch.RoleAssignments ... merch.ExternalLogins ... merch.Users ... merch.Merchants ...

-- (ง) ใส่ใหม่ (พ่อ -> ลูก)                                                       REQ-3..6
INSERT merch.Merchants / admin.MerchantAccess / admin.RoleAssignments
INSERT txn.PspConnections / merch.Users / merch.ExternalLogins / merch.RoleAssignments
INSERT shop.Products / shop.Carts / shop.CartItems / shop.CheckoutSessions
INSERT shop.Orders / txn.PaymentSessions

-- (จ) self-check: นับแถวต่อตาราง, RAISERROR ถ้าตัวใดเป็น 0                       REQ-1.6
COMMIT;
```

หมายเหตุ RLS ที่พลาดง่าย: **FILTER predicate มีผลกับ `DELETE`/`UPDATE` ด้วย** — ถ้าลบ merchant-scoped
ก่อน stamp context จะ "ลบสำเร็จ 0 แถว" เงียบ ๆ แล้ว INSERT รอบสองชน PK (REQ-2.4). และ **`sa` ก็ไม่ bypass**
(`01-principals.sql` บอกไว้ชัด: "NONE is sysadmin — RLS applies to every principal") จึงต้องพึ่งสาขา Super
ล้วน ๆ ไม่ใช่สิทธิ์ของ login.

## 3. GUID namespace (deterministic — REQ-1.4)

ทุก id = `<prefix>-0000-4000-8000-<12 hex>` (RFC-4122 well-formed: version 4, variant 8 — แพทเทิร์นเดียว
กับ HR seed เดิมใน migration). Prefix ต่อตาราง ใช้เป็นทั้ง namespace และ key ของการลบใน (ก)/(ค):

| Prefix | ตาราง | จำนวน |
|--------|-------|-------|
| `e1000000` | `merch.Merchants` | 3 |
| `e2000000` | `admin.Users` | 6 |
| `e3000000` | `admin.MerchantAccess` | 4 |
| `e4000000` | `admin.RoleAssignments` | 6 |
| `e5000000` | `merch.Users` | 12 |
| `e6000000` | `merch.ExternalLogins` | 12 |
| `e7000000` | `merch.RoleAssignments` | 6 |
| `e8000000` | `txn.PspConnections` | 6 |
| `e9000000` | `shop.Products` | 24 |
| `ea000000` | `shop.Carts` | 6 |
| `eb000000` | `shop.CartItems` | 14 |
| `ec000000` | `shop.CheckoutSessions` | 4 |
| `ed000000` | `shop.Orders` | 40 |
| `ee000000` | `txn.PaymentSessions` | 36 |

แถวจำนวนมาก (Orders/PaymentSessions/Products) สร้างจาก **`GENERATE_SERIES` / values list + สูตร**
ไม่ต้องเขียนมือทีละแถว:

```sql
DECLARE @n int;
-- id ที่ n: CONVERT(uniqueidentifier, CONCAT('ed000000-0000-4000-8000-', RIGHT(REPLICATE('0',12) + CONVERT(varchar(12), @n), 12)))
```

## 4. Dataset (ปริมาณ "กลาง" ตาม locked decision)

**Merchants (REQ-3.1)** — `Code` ต้องอยู่ใน allowlist ของ `Merchants.Domain.MerchantCode` เท่านั้น:

| Id | Code | DisplayName | EnabledChannels |
|----|------|-------------|-----------------|
| `e1…0001` | `vPrivilege` | บริษัท วีพริวิเลจ จำกัด | `card,promptpay,installment` |
| `e1…0002` | `vCommerce` | บริษัท วีคอมเมิร์ซ จำกัด | `card,promptpay` |
| `e1…0003` | `vSouvenir` | บริษัท วีซูวีเนียร์ จำกัด | `card` |

`Status = 0` (Active — enum มีค่าเดียว), `Country = 'TH'`, `Currency = 'THB'`, `Metadata = '{}'` (NOT NULL).

**PspConnections (REQ-3.2/3.3)** — 2 ต่อ merchant: `Psp = 0` (2C2P) + `Psp = 1` (Omise).
`EnabledMethods` = comma-separated verbatim จาก `card`/`promptpay`/`installment` (subset ของ
`EnabledChannels` ของ merchant นั้น). `SecretRefName` = `psp/<code>/<2c2p|omise>` — ชี้ไป ref ที่ยังไม่มี
secret จริงใน `merch.VaultSecrets` (ไม่ seed vault). `IsEnabled = 1` ยกเว้น Omise ของ vSouvenir = 0
(ให้เห็นทั้งสองค่า). `Metadata = NULL`.

**admin.Users (REQ-4.1/4.2)** — 6 แถว, `Subject` = `demo-adm-<n>` (ปลอม), `Email` = `<name>@demo.pol.local`:

| Id | Tier | Status | หมายเหตุ |
|----|------|--------|----------|
| `e2…0001` | 1 Super | 0 Active | **ตัวที่ใช้ stamp context (REQ-2.2)** |
| `e2…0002` | 1 Super | 0 Active | |
| `e2…0003` | 0 Scoped | 0 Active | เห็น 1 merchant (vPrivilege) |
| `e2…0004` | 0 Scoped | 0 Active | เห็น 2 merchant |
| `e2…0005` | 0 Scoped | 0 Active | auditor |
| `e2…0006` | 0 Scoped | 1 Suspended | |

`PositionId`/`OfficeId`/`LevelId`/`DivisionId` ชี้ไป GUID ของ `cfg.*` ที่ migration seed ไว้แล้ว
(`a1000000-…`/`b2000000-…`/`c3000000-…`/`d4000000-…`) — ห้ามสร้าง master row ใหม่.

**admin.MerchantAccess (REQ-4.3)** — 4 แถว เฉพาะ Scoped: `e2…0003`->vPrivilege; `e2…0004`->vCommerce,
vSouvenir; `e2…0005`->vPrivilege. Super ไม่มีแถว. `AssignedByAdminId` = `e2…0001`.

**admin.RoleAssignments (REQ-4.4)** — 6 แถว ผูกไป role ที่ migration seed แล้ว:
`platform_admin` = `11111111-1111-1111-1111-111111111111` (ให้ Super 2 + Scoped `…0003`/`…0004`),
`platform_auditor` = `55555555-5555-5555-5555-555555555555` (ให้ `…0005`/`…0006`).

**merch.Users (REQ-5.1)** — 12 แถว, 4 ต่อ merchant, สถานะครบทั้ง 4 ค่า
(`PendingApproval`=0, `Active`=1, `Rejected`=2, `Suspended`=3) → 2 Active + 1 Pending + 1 (Rejected หรือ
Suspended สลับกันต่อ merchant). `PersonType` มีทั้ง 0 (Individual) และ 1 (Juristic). `Subject` = `demo-mch-<n>`
(ปลอม), `MerchantId` = merchant ของตัวเอง, `ProducerCode`/`LicenseNumber`/`Phone` ใส่ค่าที่อ่านแล้วสมจริง.

**merch.ExternalLogins (REQ-5.2)** — 12 แถว (1:1 กับ merchant user), `Provider = 'google'`,
`Subject` = ค่าเดียวกับ `merch.Users.Subject` (prefix `demo-mch-` → ไม่มีทางชนกับ Google `sub` จริง).

**merch.RoleAssignments (REQ-5.3)** — 6 แถว: merchant user ที่ `Status = 1` (Active) เท่านั้น (2 คน/merchant)
→ คนแรก `merchant_manager` (`aaaaaaaa-…`), คนที่สอง `merchant_staff` (`bbbbbbbb-…`). `MerchantId` = ของ user,
`AssignedById` = merchant user คนแรกของ merchant นั้น.

**shop.Products (REQ-5.4)** — 24 แถว, 8 ต่อ merchant, ชื่อเป็นแผนประกันจริง (เช่น
"ประกันอุบัติเหตุส่วนบุคคล PA Plus", "ประกันเดินทางต่างประเทศ Travel Gold", "ประกันสุขภาพ Health Care 1M"),
`PriceCurrency = 'THB'`, `PriceAmount` DECIMAL(19,4) ช่วง 350.0000–48,000.0000, `IsActive` = 1 ยกเว้น
1 แถว/merchant เป็น 0.

**shop.Carts + CartItems (REQ-6.1)** — 6 carts (2 ต่อ merchant): 4 `Open` + 2 `CheckedOut`.
`Status` เก็บเป็น **string** (`'Open'`/`'CheckedOut'`) — `CartConfiguration` ใช้ `HasConversion<string>()`
`nvarchar(16)` (ตัวเลขจะพัง). 14 cart items, `ProductId` ต้องเป็นสินค้าของ **merchant เดียวกันกับ cart**
เสมอ (ไม่งั้นได้ข้อมูลข้าม merchant ที่ RLS ไม่จับ เพราะ CartItems scope ผ่าน parent).
`UnitPriceAmount`/`UnitPriceCurrency` = ราคาสินค้า ณ ขณะนั้น.

**shop.CheckoutSessions (REQ-6.2)** — 4 แถว ครบทุกสถานะ `Started`=0 / `Confirmed`=1 / `Abandoned`=2
(Confirmed 2 แถว ผูกกับ 2 cart ที่ `CheckedOut`; Started/Abandoned ผูกกับ cart `Open`).
`AmountAmount` = `SUM(Quantity * UnitPriceAmount)` ของ cart นั้น, `AmountCurrency = 'THB'`,
`NotificationRecipient` = อีเมล demo.

**shop.Orders (REQ-6.3)** — 40 แถว, `CreatedAt` = `DATEADD(day, -(n % 90), SYSUTCDATETIME())` (กระจาย 90 วัน),
merchant วนตาม `n % 3`. สถานะ: `Paid`=1 ~24 แถว, `AwaitingPayment`=0 ~11, `Cancelled`=2 ~5.
`PaidAt` NOT NULL **เฉพาะ** `Paid`, NULL สำหรับที่เหลือ. `SummaryToken` = `demo-ord-<n เติมศูนย์>` (unique,
`nvarchar(64)`), `SummaryTokenExpiresAt` = `CreatedAt + 30 วัน`. `CheckoutSessionId` = NULL (checkout demo
มีแค่ 4; order ส่วนใหญ่ไม่ผูก) ยกเว้น 2 แถวที่ผูกกับ Confirmed checkout.
`AmountAmount` DECIMAL(19,4) / `AmountCurrency = 'THB'`.

**txn.PaymentSessions (REQ-6.4/6.5)** — 36 แถว (1 ต่อ order ยกเว้น 4 order `AwaitingPayment` ที่จงใจยังไม่แตะ
PSP — ตรงกับ flow จริงที่ order เกิดก่อน payment). สถานะครบ 5 ค่า:
`Paid`=2 (ทุก order `Paid` — **invariant REQ-6.5**), `Failed`=3 / `Expired`=4 (order `Cancelled`),
`Created`=0 / `Redirected`=1 (order `AwaitingPayment` ที่เหลือ).
`MerchantId` + `AmountAmount` + `AmountCurrency` **ต้องเท่ากับ order** ที่ `OrderId` ชี้ไป.
`Method` ∈ `card`/`promptpay`/`installment` (ต้องอยู่ใน `EnabledMethods` ของ PSP connection ของ merchant นั้น),
`Psp` = 0/1. `PspExternalChargeId` = `demo_chrg_<n>` เฉพาะ Paid/Failed; `RedirectUrl` = `https://demo.psp.local/...`
เฉพาะ Redirected/Paid/Failed. `RowVersion` เป็น `rowversion` — **ห้าม INSERT** (DB สร้างเอง).
`shop.Orders.PaymentSessionId` update กลับหลัง insert (REQ-6.5).

**ไม่ seed** (REQ-6.6/3.3): `txn.OutboxMessages`, `txn.IdempotencyRecords`, `merch.VaultSecrets`,
`merch.VaultRevealAudits`, audit/session ทุกตัว (`admin.Sessions`/`AuthAudits`/`UserAudits`,
`merch.Sessions`/`AuthAudits`/`RegistrationAudits`/`RegistrationNotices`/`ProvisioningAudits`),
`dbo.DataProtectionKeys` — ล้วนเป็นผลข้างเคียงของ runtime.

## 5. Self-check ในตัวสคริปต์ (REQ-1.6)

ท้ายสคริปต์ (ก่อน COMMIT): `SELECT` นับแถว demo ต่อตาราง (นับด้วย prefix เดียวกับตาราง GUID ข้างบน),
พิมพ์ออกมา, และ

```sql
IF EXISTS (SELECT 1 FROM @counts WHERE Rows = 0)
    THROW 51000, N'seed-demo: some target table got 0 rows — seed is incomplete.', 1;
```

นี่คือ runnable check ของงานนี้ — ไม่มี unit test เพิ่ม เพราะไม่มีโค้ดโปรดักชันใหม่ (REQ-7.3) และ CI
ไม่รัน demo seed (`assert-fresh-db.sql` pin count ของ seed migration ไว้ — demo data ต้องไม่ทำให้มันแดง;
demo ไม่แตะ `iam.*`/`cfg.*` จึงไม่กระทบ).

## Requirement Traceability

| REQ | ที่อยู่ |
|-----|---------|
| 1.1 | `docker/bootstrap/seed-demo.sql` (นอก EF migration chain — §1) |
| 1.2 | `scripts/seed-demo.sh` (§1) |
| 1.3 | `seed-demo.sql` ขั้น (ก)+(ค) delete-by-prefix แล้ว insert (§2) |
| 1.4 | GUID namespace table (§3) |
| 1.5 | `SET XACT_ABORT ON` + `BEGIN TRAN`/`COMMIT` (§2) |
| 1.6 | ขั้น (จ) count + `THROW 51000` (§5); `sqlcmd -b` ใน `seed-demo.sh` (§1) |
| 2.1 | ไม่แตะ `sec.MerchantIsolationPolicy` / `pol_rls_bypass` (§2 หมายเหตุ RLS) |
| 2.2 | `sp_set_session_context` UserId=DemoSuper + MerchantId=Guid.Empty (§2 ขั้น (ข)) |
| 2.3 | สคริปต์รันด้วย `sa` ซึ่งไม่ใช่สมาชิก bypass (§1 `seed-demo.sh`; §2 หมายเหตุ RLS) |
| 2.4 | ลำดับ (ก)->(ข)->(ค)->(ง) (§2) |
| 3.1 | INSERT `merch.Merchants` 3 แถว (§4 Merchants) |
| 3.2 | INSERT `txn.PspConnections` 6 แถว (§4 PspConnections) |
| 3.3 | ไม่ seed `merch.VaultSecrets` (§4 PspConnections, §4 "ไม่ seed") |
| 4.1 | INSERT `admin.Users` 6 แถว (§4 admin.Users) |
| 4.2 | FK ชี้ GUID `cfg.*` เดิม (§4 admin.Users) |
| 4.3 | INSERT `admin.MerchantAccess` 4 แถว เฉพาะ Scoped (§4 admin.MerchantAccess) |
| 4.4 | INSERT `admin.RoleAssignments` 6 แถว -> role id จาก migration (§4 admin.RoleAssignments) |
| 5.1 | INSERT `merch.Users` 12 แถว ครบ 4 status + 2 PersonType (§4 merch.Users) |
| 5.2 | INSERT `merch.ExternalLogins` 12 แถว, Subject `demo-mch-*` (§4 merch.ExternalLogins) |
| 5.3 | INSERT `merch.RoleAssignments` 6 แถว (§4 merch.RoleAssignments) |
| 5.4 | INSERT `shop.Products` 24 แถว (§4 shop.Products) |
| 6.1 | INSERT `shop.Carts` 6 + `shop.CartItems` 14 (§4 shop.Carts + CartItems) |
| 6.2 | INSERT `shop.CheckoutSessions` 4 (§4 shop.CheckoutSessions) |
| 6.3 | INSERT `shop.Orders` 40 (§4 shop.Orders) |
| 6.4 | INSERT `txn.PaymentSessions` 36 (§4 txn.PaymentSessions) |
| 6.5 | invariant Paid<->Paid + `UPDATE shop.Orders.PaymentSessionId` (§4 txn.PaymentSessions) |
| 6.6 | รายการ "ไม่ seed" (§4) |
| 7.1 | `README.md` หัวข้อ "Demo seed data (dev only)" (§1) |
| 7.2 | password ผ่าน env ใน `scripts/seed-demo.sh` เท่านั้น (§1) |
| 7.3 | ไม่มีไฟล์ใต้ `src/` ในรายการไฟล์ (§1) |
</content>
