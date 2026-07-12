# Tasks — demo-seed-data

> Status: approved 2026-07-13 (quick, no gates)

4 task ตามลำดับ dependency — แต่ละ task ต่อยอด `docker/bootstrap/seed-demo.sql` ไฟล์เดียวกัน
(T1 วางโครง, T2-T4 เติม dataset ทีละชั้นตาม FK). ทุก task จบด้วยการ **รัน `./scripts/seed-demo.sh`
สำเร็จจริงบน DB `localhost,11433`** (pol-db container ของ repo นี้) — ไม่ใช่แค่ "SQL ดูถูก".

Canon ที่ implementer ต้องอ่านก่อนเริ่ม: `.ai/specs/demo-seed-data/requirements.md` + `design.md`,
และดูรูปทรงตารางจริงจาก `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/
20260712185344_InitialSchema.cs` (คอลัมน์ + nullability) กับ `20260712185646_SecurityObjects.cs`
(RLS predicate) — **ห้ามเดาชื่อคอลัมน์**.

---

## - [x] T1 — โครงสคริปต์ + RLS context + platform users + runner

Satisfies: 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 2.1, 2.2, 2.3, 2.4, 4.1, 4.2, 7.1, 7.2, 7.3

Scope (design §1, §2, §3, §5):
1. `docker/bootstrap/seed-demo.sql` — โครงตาม design §2: `SET NOCOUNT ON` + `SET XACT_ABORT ON`,
   `USE [$(DbName)]`, `BEGIN TRAN` … `COMMIT`
2. ขั้น (ก): `DELETE` demo `admin.RoleAssignments` (`e4…`) -> `admin.MerchantAccess` (`e3…`) ->
   `admin.Users` (`e2…`) แล้ว `INSERT admin.Users` 6 แถวตาม design §4 (Tier/Status ครบ,
   FK `PositionId`/`OfficeId`/`LevelId`/`DivisionId` ชี้ GUID `cfg.*` ที่ migration seed ไว้แล้ว)
3. ขั้น (ข): `sp_set_session_context` `UserId` = `e2000000-0000-4000-8000-000000000001` +
   `MerchantId` = `00000000-0000-0000-0000-000000000000` (สาขา Super ของ `sec.fn_merchant_predicate`)
4. ขั้น (ค): `DELETE` demo merchant-scoped **ทุกตาราง** ตามลำดับลูก->พ่อ (วางไว้ครบตั้งแต่ T1 แม้ T1 ยัง
   ไม่ INSERT ตารางเหล่านั้น — T2-T4 จะแค่เติม INSERT ไม่ต้องกลับมาแก้ลำดับลบ)
5. ขั้น (จ): นับแถว demo ต่อตารางลง table variable, `SELECT` ออกมาให้เห็น, แล้ว
   `THROW 51000` ถ้าตารางใดที่ "ควรมีแถวแล้ว" ได้ 0 — **T1 ให้ assert เฉพาะ `admin.Users`**;
   T2-T4 จะเติมตารางของตัวเองเข้า assert list
6. `scripts/seed-demo.sh` — bash, `set -euo pipefail`; อ่าน `POL_SQL_SERVER` (default `localhost,11433`),
   `POL_DB` (default `VCentralPay`), password จาก `POL_SA_PASSWORD` หรือ `MSSQL_SA_PASSWORD`;
   fail พร้อมข้อความชัดถ้าไม่มี password; เรียก `sqlcmd … -C -b -v DbName=… -i docker/bootstrap/seed-demo.sql`.
   **ห้ามมี password/secret ในไฟล์** (REQ-7.2)
7. `README.md` — หัวข้อ "Demo seed data (dev only)" สั้น ๆ: รันยังไง, มีอะไรบ้าง, ทำไมไม่ใช่ migration,
   บัญชี seed login Google จริงไม่ได้
8. **ห้ามแตะไฟล์ใต้ `src/`** (REQ-7.3)

Verify:
- `./scripts/seed-demo.sh` exit 0 และพิมพ์ `admin.Users = 6`
- รันซ้ำครั้งที่ 2 exit 0 เหมือนเดิม (idempotent — ไม่ชน PK)
- พิสูจน์ REQ-2.4: ลอง comment ขั้น (ข) ชั่วคราวแล้วรัน -> ต้องพัง (PK violation หรือ 0 rows deleted);
  uncomment กลับ. เขียนผลลงใน Evidence
- `git status` ไม่มีไฟล์ใต้ `src/` เปลี่ยน

### Evidence (2026-07-13)

รันจริงบน `pol-db` container (`localhost,11433`, DB `VCentralPay`), `sqlcmd` ที่ `/opt/homebrew/bin/sqlcmd`.
โหลด env ด้วย `set -a && source .env && set +a` (แสดง noise "command not found: User" จากบรรทัด
connection-string ใน `.env` — harmless, ตัวแปรถูก set จริง).

**1. รันครั้งแรก:**
```
$ ./scripts/seed-demo.sh
Changed database context to 'VCentralPay'.
admin.Users = 6
seed-demo: OK.
EXIT=0
```

**2. รันซ้ำครั้งที่ 2 (idempotent):**
```
$ ./scripts/seed-demo.sh
Changed database context to 'VCentralPay'.
admin.Users = 6
seed-demo: OK.
EXIT=0
```

**3. GOTCHA ที่เจอระหว่างทาง (ไม่ได้อยู่ใน design):** รันครั้งแรกพังด้วย
`Msg 1934 ... DELETE failed because the following SET options have incorrect settings: 'QUOTED_IDENTIFIER'`
— `sec.MerchantIsolationPolicy` ใช้ schema-bound inline function เป็น predicate (ตระกูลเดียวกับ
indexed view/filtered index) ซึ่งต้องการ `QUOTED_IDENTIFIER ON` และ sqlcmd session ไม่ set ให้โดย
default. แก้ด้วย `SET QUOTED_IDENTIFIER ON;` ต่อจาก `SET XACT_ABORT ON;` ที่หัวสคริปต์ (ทำครั้งเดียว
คลุมทั้งไฟล์เพราะ session-level setting ข้าม batch ใน sqlcmd connection เดียวกัน) — **T2-T4 ไม่ต้องทำอะไรเพิ่ม
เรื่องนี้ ของมันมีอยู่แล้วที่หัวไฟล์**

**4. พิสูจน์ REQ-2.4** — T1 ยังไม่มี INSERT ตารางที่ merchant-scoped เลย (T2-T4 ยังไม่ทำ) ดังนั้น comment
step (ข) แล้วรัน `seed-demo.sh` ตรง ๆ **ไม่พัง** (exit 0 เหมือนเดิม) เพราะไม่มีอะไรให้ INSERT ชน PK ในตอนนี้ —
เลยพิสูจน์ trap ด้วยการทดลองแยกแทน (insert แถวทดสอบเข้า `merch.Merchants` ด้วยมือ, สลับ stamp/ไม่ stamp
session context, cleanup หลังจบ):
```
-- Setup: stamp context, insert 1 test row into merch.Merchants (e1000000-...-000000000099)
(1 rows affected)   -- insert OK
after_insert = 1

-- DELETE WITHOUT stamping session context (fresh sqlcmd connection, no EXEC sp_set_session_context):
DELETE FROM merch.Merchants WHERE Id LIKE 'e1000000-%';
rows_deleted_without_context_stamp = 0     -- <-- RLS filters it out, DELETE silently "succeeds" removing nothing
still_present = 0                          -- <-- SAME query also returns 0: the row LOOKS gone under this session,
                                            --     but that's the FILTER predicate hiding it, not an actual delete

-- Verify it is still physically there: re-stamp context, re-check, then clean up properly:
visible_with_context_stamped = 1           -- <-- proves the row never left; it was just invisible without the stamp
cleanup_rows_deleted = 1                   -- <-- DELETE only works once context is stamped
```
ผลตรงกับ design's warning เป๊ะ: ไม่ stamp context -> DELETE (และ SELECT ใด ๆ) มองไม่เห็นแถว merchant-scoped
เลยสักแถว ไม่ใช่แค่ "ลบไม่ได้" แต่ "มองไม่เห็นด้วย" — เงียบสนิท ไม่ error. **PK collision จริงจะเห็นเป็นครั้งแรก
ตอน T2 เพิ่ม INSERT เข้า merchant-scoped tables** (ถ้า T2 comment step (ข) แล้วรันซ้ำ จะได้ PK violation
เพราะรอบแรกที่ INSERT สำเร็จ, DELETE รอบสองมองไม่เห็นแถวเดิม, INSERT รอบสองชน PK) — ทดสอบ pattern เดิมซ้ำได้
ตอน T2 เพื่อยืนยัน exact failure mode นั้น. หลัง cleanup แล้วรัน `seed-demo.sh` ปกติ (step (ข) restore แล้ว)
กลับมา exit 0 เหมือนเดิม, `admin.Users` ยังคง 28 แถว (ไม่มี leftover test row).

**5. ยืนยันไม่แตะแถวเดิม:**
```
$ sqlcmd ... -Q "SELECT COUNT(*) FROM admin.Users"
28    -- 22 pre-existing + 6 demo, ตรงตาม spec
```

**6. `git status` (หลัง T1):**
```
 M README.md
?? docker/bootstrap/seed-demo.sql
?? scripts/seed-demo.sh
```
ไม่มีไฟล์ใต้ `src/` เปลี่ยนแปลง (REQ-7.3).

**ส่งต่อ T2:** step (ค) มี DELETE ครบทุกตาราง merchant-scoped อยู่แล้ว (child->parent) — T2 แค่เติม INSERT
ในโซน (ง) และเติมชื่อตารางเข้า `@counts` ในโซน (จ) เท่านั้น ห้ามแก้ลำดับ DELETE. ตัว `SET QUOTED_IDENTIFIER ON;`
อยู่ที่หัวไฟล์แล้ว ไม่ต้องเติมซ้ำ.

---

## - [ ] T2 — merchants + PSP connections + platform access/roles

Depends on: T1

Satisfies: 3.1, 3.2, 3.3, 4.3, 4.4

Scope (design §4):
1. `INSERT merch.Merchants` 3 แถว — `Code` ต้องอยู่ใน allowlist ของ `Merchants.Domain.MerchantCode`
   (`vPrivilege`/`vCommerce`/`vSouvenir`) เท่านั้น; `Metadata` NOT NULL (ใช้ `'{}'`)
2. `INSERT txn.PspConnections` 6 แถว (2C2P `Psp = 0` + Omise `Psp = 1` ต่อ merchant);
   `EnabledMethods` = comma-separated `card`/`promptpay`/`installment` และต้องเป็น **subset ของ
   `EnabledChannels`** ของ merchant นั้น; `IsEnabled` มีทั้ง 1 และ 0; **ไม่ seed `merch.VaultSecrets`**
3. `INSERT admin.MerchantAccess` 4 แถว — เฉพาะ platform user ที่ `Tier = 0` (Scoped); Super ต้องไม่มีแถว
4. `INSERT admin.RoleAssignments` 6 แถว — `RoleId` ต้องเป็น GUID ของ role ที่ migration seed ไว้แล้ว
   (`platform_admin` = `11111111-1111-1111-1111-111111111111`,
   `platform_auditor` = `55555555-5555-5555-5555-555555555555`) — **ห้ามสร้าง role ใหม่ใน `iam.*`**
5. เติม 4 ตารางนี้เข้า assert list ของขั้น (จ)

Verify:
- `./scripts/seed-demo.sh` exit 0, counts: `merch.Merchants = 3`, `txn.PspConnections = 6`,
  `admin.MerchantAccess = 4`, `admin.RoleAssignments = 6`
- รันซ้ำ exit 0 (idempotent)
- `SELECT COUNT(*) FROM admin.MerchantAccess a JOIN admin.Users u ON u.Id = a.PlatformUserId
  WHERE u.Tier = 1` = 0 (REQ-4.3 — Super ไม่มีแถว)

---

## - [ ] T3 — merchant users + external logins + merchant roles + products

Depends on: T2

Satisfies: 5.1, 5.2, 5.3, 5.4

Scope (design §4):
1. `INSERT merch.Users` 12 แถว (4 ต่อ merchant) — `Status` ครบทั้ง 4 ค่า (`PendingApproval` = 0,
   `Active` = 1, `Rejected` = 2, `Suspended` = 3), `PersonType` มีทั้ง 0 (Individual) และ 1 (Juristic),
   `Subject` = `demo-mch-<n>` (ปลอม), `MerchantId` = merchant ของตัวเอง
2. `INSERT merch.ExternalLogins` 12 แถว 1:1 — `Provider = 'google'`, `Subject` ตรงกับของ user
3. `INSERT merch.RoleAssignments` 6 แถว — เฉพาะ user ที่ `Status = 1` (Active); `RoleId` = role ที่
   migration seed ไว้ (`merchant_manager` = `aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa`,
   `merchant_staff` = `bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb`); `MerchantId` = ของ user นั้น
4. `INSERT shop.Products` 24 แถว (8 ต่อ merchant) — ชื่อเป็นแผนประกันที่อ่านแล้วสมจริง (ภาษาไทย),
   `PriceCurrency = 'THB'`, `PriceAmount` `DECIMAL(19,4)`, มีทั้ง `IsActive = 1` และ `= 0`
5. เติม 4 ตารางนี้เข้า assert list ของขั้น (จ)

Verify:
- `./scripts/seed-demo.sh` exit 0, counts: `merch.Users = 12`, `merch.ExternalLogins = 12`,
  `merch.RoleAssignments = 6`, `shop.Products = 24`
- รันซ้ำ exit 0 (idempotent)
- `SELECT COUNT(DISTINCT Status) FROM merch.Users WHERE Id LIKE 'e5______-%'` = 4 (REQ-5.1)

---

## - [ ] T4 — funnel: carts, checkouts, orders, payment sessions

Depends on: T3

Satisfies: 6.1, 6.2, 6.3, 6.4, 6.5, 6.6

Scope (design §4):
1. `INSERT shop.Carts` 6 แถว — `Status` เก็บเป็น **string** `'Open'`/`'CheckedOut'`
   (`CartConfiguration` ใช้ `HasConversion<string>()` — ใส่ตัวเลขจะพัง) + `shop.CartItems` 14 แถว
   โดย `ProductId` ต้องเป็นสินค้าของ **merchant เดียวกันกับ cart** เสมอ
2. `INSERT shop.CheckoutSessions` 4 แถว ครบทุกสถานะ (`Started` = 0 / `Confirmed` = 1 / `Abandoned` = 2);
   `AmountAmount` = `SUM(Quantity * UnitPriceAmount)` ของ cart ที่ผูก
3. `INSERT shop.Orders` 40 แถว — กระจาย `CreatedAt` ย้อนหลัง 90 วัน, สถานะครบ 3 ค่า
   (`AwaitingPayment` = 0 / `Paid` = 1 / `Cancelled` = 2), `PaidAt` NOT NULL **เฉพาะ** `Paid`,
   `SummaryToken` unique, `SummaryTokenExpiresAt` มีค่า
4. `INSERT txn.PaymentSessions` 36 แถว — สถานะครบ 5 ค่า (`Created` = 0 / `Redirected` = 1 / `Paid` = 2 /
   `Failed` = 3 / `Expired` = 4); `MerchantId` + `AmountAmount` + `AmountCurrency` **ต้องเท่ากับ order**
   ที่ `OrderId` ชี้ไป; `Method` ต้องอยู่ใน `EnabledMethods` ของ PSP connection ของ merchant นั้น;
   **ห้าม INSERT `RowVersion`** (คอลัมน์ `rowversion` DB สร้างเอง)
5. `UPDATE shop.Orders SET PaymentSessionId = …` ให้ทุก order `Paid` ชี้กลับไปยัง payment session ที่
   `Status = Paid` (REQ-6.5 — ไม่มีคู่ที่ขัดแย้ง)
6. **ไม่ seed** `txn.OutboxMessages` / `txn.IdempotencyRecords` / audit / session ตารางใด ๆ (REQ-6.6)
7. เติม 4 ตารางนี้เข้า assert list ของขั้น (จ) — ถึงตรงนี้ assert list ครบทุกตาราง demo

Verify:
- `./scripts/seed-demo.sh` exit 0, counts: `shop.Carts = 6`, `shop.CartItems = 14`,
  `shop.CheckoutSessions = 4`, `shop.Orders = 40`, `txn.PaymentSessions = 36`
- รันซ้ำ exit 0 (idempotent)
- invariant REQ-6.5: `SELECT COUNT(*) FROM shop.Orders o LEFT JOIN txn.PaymentSessions p
  ON p.Id = o.PaymentSessionId WHERE o.Status = 1 AND (p.Id IS NULL OR p.Status <> 2)` = 0
- invariant REQ-6.4: `SELECT COUNT(*) FROM txn.PaymentSessions p JOIN shop.Orders o ON o.Id = p.OrderId
  WHERE p.MerchantId <> o.MerchantId OR p.AmountAmount <> o.AmountAmount
  OR p.AmountCurrency <> o.AmountCurrency` = 0
- invariant REQ-6.1: cart item ไม่ข้าม merchant —
  `SELECT COUNT(*) FROM shop.CartItems i JOIN shop.Carts c ON c.Id = i.CartId
  JOIN shop.Products pr ON pr.Id = i.ProductId WHERE pr.MerchantId <> c.MerchantId` = 0
- `dotnet build pol-core.slnx` เขียว (ยืนยันว่าไม่มีอะไรใต้ `src/` ถูกแตะ)
</content>
