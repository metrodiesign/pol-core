# Tasks — demo-seed-data

> Status: unknown

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

## - [x] T2 — merchants + PSP connections + platform access/roles

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

### Evidence (2026-07-13)

รันจริงบน `pol-db` container เดิม (`localhost,11433`, DB `VCentralPay`). โหลด env ด้วย
`set -a && source .env && set +a` (noise "command not found: User" เดิม จาก T1 — harmless).

**สิ่งที่เพิ่ม** — ต่อจาก T1 ในโซน (ง) ของ `docker/bootstrap/seed-demo.sql`: `INSERT merch.Merchants`
3 แถว (`Code` เก็บเป็น **normalized lowercase** `'vprivilege'/'vcommerce'/'vsouvenir'` — ไม่ใช่
`'vPrivilege'` แบบที่ design doc เขียนไว้เพื่อความอ่านง่าย, ยืนยันจาก `Merchant.Create` ->
`MerchantCode.Normalize` ใน `src/Modules/Merchants/Merchants.Domain/Merchant.cs:61`), `INSERT
txn.PspConnections` 6 แถว (Omise ของ vSouvenir `IsEnabled=0`), `INSERT admin.MerchantAccess` 4 แถว
(เฉพาะ Scoped), `INSERT admin.RoleAssignments` 6 แถว (`RoleId` อ้าง `platform_admin`/`platform_auditor`
จาก migration `20260712185912_SeedData.cs` ตรง ไม่ได้สร้างใหม่). เติม 4 ตารางนี้เข้า `@counts` ในโซน (จ).

**1. รันครั้งแรก:**
```
$ ./scripts/seed-demo.sh
Changed database context to 'VCentralPay'.
admin.Users = 6
merch.Merchants = 3
txn.PspConnections = 6
admin.MerchantAccess = 4
admin.RoleAssignments = 6
seed-demo: OK.
EXIT=0
```

**2. รันซ้ำครั้งที่ 2 (idempotent):** ผลลัพธ์เหมือนเดิมทุกตัว, `EXIT=0`.

**3. พิสูจน์ REQ-2.4 ของจริง (T1 ทำไม่ได้เพราะยังไม่มี INSERT merchant-scoped):** comment ขั้น (ข)
(2 บรรทัด `EXEC sp_set_session_context`) แล้วรัน `seed-demo.sh` ตรง ๆ:
```
Changed database context to 'VCentralPay'.
Msg 2627, Level 14, State 1, Server 24ecc441547a, Line 75
Violation of PRIMARY KEY constraint 'PK_Merchants'. Cannot insert duplicate key in object
'merch.Merchants'. The duplicate key value is (e1000000-0000-4000-8000-000000000001).
EXIT=1
```
ตรงตามที่ T1/design ทำนายไว้เป๊ะ: ไม่ stamp context -> ขั้น (ค) มองไม่เห็นแถว merchant-scoped เดิม
เลยสักแถว (DELETE ลบ 0 แถวเงียบ ๆ เพราะ FILTER predicate) -> ขั้น (ง) INSERT รอบใหม่ชน PK ของแถวที่ยัง
อยู่จริงในตาราง. Uncomment กลับ (`diff` กับ backup ก่อนแก้ ยืนยันว่า restore สะอาด 100%) แล้วรันใหม่
กลับมา `EXIT=0` เหมือนเดิมทุกตัว (ไม่มี leftover จากการทดลอง เพราะ transaction ที่ fail ทั้งก้อน rollback
ด้วย `XACT_ABORT`).

**4. REQ-4.3 — Super ต้องไม่มีแถวใน MerchantAccess:**
```sql
SELECT COUNT(*) FROM admin.MerchantAccess a JOIN admin.Users u ON u.Id = a.PlatformUserId WHERE u.Tier = 1;
-- 0
```

**5. `iam.Roles` ไม่เปลี่ยน (ยืนยันไม่ได้สร้าง role ใหม่):**
```sql
SELECT COUNT(*) FROM iam.Roles;  -- 4
```

**6. `git status` — เฉพาะไฟล์ที่แก้:**
```
 M docker/bootstrap/seed-demo.sql
```
ไม่มีไฟล์ใต้ `src/` เปลี่ยนแปลง (REQ-7.3).

**ส่งต่อ T3:** ไม่มี gotcha ใหม่นอกเหนือจากที่ T1 เตือนไว้แล้ว (`SET QUOTED_IDENTIFIER ON` อยู่หัวไฟล์แล้ว,
ลำดับ DELETE ใน (ค) ครบทุกตารางอยู่แล้ว ไม่ต้องแก้). ข้อควรระวังเดียวที่ T3 น่าจะเจอ: คอลัมน์ `Code` ของ
`merch.Merchants` เก็บ lowercase — ถ้า T3 ต้อง join กลับไปยัง merchant ผ่าน Code (ไม่ใช่ Id) ให้ใช้ค่า
lowercase เดียวกัน (`vprivilege`/`vcommerce`/`vsouvenir`), และ `admin.RoleAssignments` **ไม่มีคอลัมน์
`MerchantId`** (ต่างจาก `merch.RoleAssignments` ที่ T3 จะใช้ ซึ่งมี `MerchantId` — อ่านคอลัมน์จริงจาก
migration ก่อนเขียน INSERT เสมอ อย่าเดา).

---

## - [x] T3 — merchant users + external logins + merchant roles + products

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
   `TotalPremium` `DECIMAL(19,2)`, มีทั้ง `PaymentStatus = 'UNPAID'` และ `= 'PAID'` (+ `PaidDate`)
5. เติม 4 ตารางนี้เข้า assert list ของขั้น (จ)

Verify:
- `./scripts/seed-demo.sh` exit 0, counts: `merch.Users = 12`, `merch.ExternalLogins = 12`,
  `merch.RoleAssignments = 6`, `shop.Products = 24`
- รันซ้ำ exit 0 (idempotent)
- `SELECT COUNT(DISTINCT Status) FROM merch.Users WHERE Id LIKE 'e5______-%'` = 4 (REQ-5.1)

### Evidence (2026-07-13)

รันจริงบน `pol-db` container เดิม (`localhost,11433`, DB `VCentralPay`). โหลด env ด้วย
`set -a && source .env && set +a` (noise "command not found: User" เดิมจาก T1/T2 — harmless).

**สิ่งที่เพิ่ม** — ต่อจาก T2 ในโซน (ง) ของ `docker/bootstrap/seed-demo.sql`: `INSERT merch.Users` 12 แถว
(4 ต่อ merchant, ครบ 4 `UserStatus` + ทั้ง 2 `PersonType`, `Subject` = `demo-mch-<n>`), `INSERT
merch.ExternalLogins` 12 แถว (1:1, `Provider = 'google'`, `Subject` ตรงกับ user), `INSERT
merch.RoleAssignments` 6 แถว (เฉพาะ user ที่ `Status = 1`), `INSERT shop.Products` 24 แถว (8 ต่อ
merchant, แผนประกันภาษาไทย, 1 แถวขายไม่ได้ต่อ merchant — ตอนนั้นคือ `IsActive = 0`, ตอนนี้คือ
`PaymentStatus = 'PAID'`). เติม 4 ตารางนี้เข้า `@counts` ในโซน (จ).

**GOTCHA ที่เจอ (ไม่ใช่ bug แต่ทำให้เกือบวิ่งผิดทาง):** `merch.Users` / `merch.ExternalLogins` /
`merch.RoleAssignments` **ไม่มี RLS predicate เลย** — เช็คจาก `SecurityObjects.cs`'s `MerchantTables`
array (`shop.Products/Carts/CheckoutSessions/Orders`, `txn.PaymentSessions/PspConnections/
IdempotencyRecords`, `merch.VaultSecrets` เท่านั้น) บวก `merch.Merchants` ที่มี predicate แยกบน `Id`
(ไม่ใช่ `MerchantId`) — สามตารางที่ T3 insert ("merch.Users/ExternalLogins/RoleAssignments") ไม่อยู่ใน
รายการไหนเลย. แปลว่า step (ข) ที่ stamp session context ไม่จำเป็นสำหรับ 3 ตารางนี้ (แต่ยังจำเป็นสำหรับ
`shop.Products` ที่ T3 insert ด้วยเช่นกัน เพราะมันอยู่ใน `MerchantTables`). **ผลคือตอน verify ด้วย
sqlcmd session ใหม่ที่ไม่ได้ stamp context, query นับ `shop.Products` ได้ 0 แถวเงียบ ๆ (RLS filter ซ่อนไว้
ไม่ error) — ต้อง `EXEC sp_set_session_context` เหมือนสคริปต์ก่อน query shop.Products/carts/orders/ฯลฯ
ด้วยมือเสมอเวลา debug นอกสคริปต์.**

**1. รันครั้งแรก:**
```
$ ./scripts/seed-demo.sh
Changed database context to 'VCentralPay'.
admin.Users = 6
merch.Merchants = 3
txn.PspConnections = 6
admin.MerchantAccess = 4
admin.RoleAssignments = 6
merch.Users = 12
merch.ExternalLogins = 12
merch.RoleAssignments = 6
shop.Products = 24
seed-demo: OK.
EXIT=0
```

**2. รันซ้ำครั้งที่ 2 (idempotent):** ผลลัพธ์เหมือนเดิมทุกตัว, `EXIT=0`.

**3. REQ-5.1 — ครบทุก Status และ PersonType:**
```sql
SELECT COUNT(DISTINCT Status) FROM merch.Users WHERE Id LIKE 'e5000000-%';      -- 4
SELECT COUNT(DISTINCT PersonType) FROM merch.Users WHERE Id LIKE 'e5000000-%';  -- 2
```

**4. REQ-5.3 — RoleAssignments ผูกเฉพาะ user ที่ Active:**
```sql
SELECT COUNT(*) FROM merch.RoleAssignments r JOIN merch.Users u ON u.Id = r.MerchantUserId
WHERE u.Status <> 1;
-- 0
```

**5. `iam.Roles` ไม่เปลี่ยน (ยืนยันไม่ได้สร้าง role ใหม่):**
```sql
SELECT COUNT(*) FROM iam.Roles;  -- 4
```

**6. REQ-5.4 — ครบทั้ง 2 ค่าของแกนขายได้/ขายไม่ได้ (ต้อง stamp session context ก่อน query เพราะ
shop.Products มี RLS, ดู GOTCHA ด้านบน):**
```sql
EXEC sp_set_session_context @key = N'UserId',     @value = 'e2000000-0000-4000-8000-000000000001';
EXEC sp_set_session_context @key = N'MerchantId', @value = '00000000-0000-0000-0000-000000000000';
SELECT COUNT(DISTINCT PaymentStatus) FROM shop.Products WHERE Id LIKE 'e9000000-%';  -- 2
SELECT COUNT(*) FROM shop.Products WHERE Id LIKE 'e9000000-%';                       -- 24
```
> อัปเดต 2026-07-30 (products-sp-53-alignment T6): ตอนรัน T3 จริงเมื่อ 2026-07-13 คอลัมน์ยังเป็น
> `IsActive` และ query ที่รันคือ `COUNT(DISTINCT IsActive)`; `IsActive` ถูก DROP ไปแล้ว จึงเขียน query
> ข้างบนเป็นแกนใหม่ `PaymentStatus` เพื่อให้ยังรันซ้ำได้ (ยอด `shop.Products` ตอนนี้เป็น 100 ตาม T3.5/T4
> ไม่ใช่ 24 อีกแล้ว).

**7. `git status` — เฉพาะไฟล์ที่แก้:**
```
 M docker/bootstrap/seed-demo.sql
```
ไม่มีไฟล์ใต้ `src/` เปลี่ยนแปลง (REQ-7.3). หมายเหตุ: `git status` ตอนเริ่มงาน T3 พบ
`.ai/specs/demo-seed-data/design.md` ค้างเป็น modified (การแก้ไขบันทึกเรื่อง merchant Code เป็น lowercase
ที่ T2 evidence พูดถึงแต่ยังไม่ถูก commit ไปกับ `942853b`) — **ไม่ใช่การเปลี่ยนแปลงของ T3**, ผมไม่ได้แตะไฟล์
นั้นและไม่รวมมันเข้า commit นี้ ทีมลีดอาจต้องตามเก็บแยกต่างหาก.

**ส่งต่อ T4:** สินค้าทั้ง 24 ตัวอยู่ใต้ merchant ผ่าน `MerchantId` ตรง ๆ (แบ่งเป็น 3 บล็อก ๆ ละ 8 —
`e9…0001`-`0008` = vprivilege, `e9…0009`-`0010` = vcommerce, `e9…0011`-`0018` = vsouvenir; ใน hex, `0010`
= 16 ทศนิยม), ราคาช่วง 350.00–48,000.00 THB, แต่ละ merchant มี 1 แถวสุดท้ายที่ขายไม่ได้ (ตัวที่ 8
ของบล็อก — ตอนนั้น `IsActive = 0`, ตอนนี้ `PaymentStatus = 'PAID'` + `PaidDate`). `merch.Users` ที่ `Status = 1` (Active, ใช้เป็นเจ้าของ cart/checkout ได้สมเหตุสมผล) คือ
`e5…0001/0002` (vprivilege), `e5…0005/0006` (vcommerce), `e5…0009/000a` (vsouvenir) — ตรงกับ 6 แถว
`merch.RoleAssignments`. T4 ไม่ต้อง stamp session context เพิ่ม (T1 stamp ไว้ตลอดทั้ง transaction แล้ว)
แต่ต้อง stamp เองเวลา query ตาราง merchant-scoped (`shop.*`, `txn.*`) นอกสคริปต์เพื่อ debug/verify
เหมือนที่ผมเจอใน gotcha ข้อ 6.

---

## - [x] T4 — funnel: carts, checkouts, orders, payment sessions

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

### Evidence (2026-07-13)

รันจริงบน `pol-db` container เดิม (`localhost,11433`, DB `VCentralPay`). โหลด env ด้วย
`set -a && source .env && set +a` (noise "command not found: User" เดิมจาก T1-T3 — harmless).

**สิ่งที่เพิ่ม** — ต่อจาก T3 ในโซน (ง) ของ `docker/bootstrap/seed-demo.sql`: `INSERT shop.Carts` 6 แถว
(2 ต่อ merchant, `Status` เป็น string `'Open'`/`'CheckedOut'`), `INSERT shop.CartItems` 14 แถว
(hand-typed ทั้งหมด, `ProductId` ตรง merchant ของ cart เสมอ, `UnitPriceAmount` = ราคาสินค้าจริงจาก T3),
`INSERT shop.CheckoutSessions` 4 แถว (`AmountAmount` = SUM ของ cart items ที่ผูก, คำนวณด้วยมือแล้วยืนยัน
ตรง), แล้ว `shop.Orders` 40 แถว + `txn.PaymentSessions` 36 แถว **generate จาก `GENERATE_SERIES(1, 40)`**
ผ่าน table variable `@OrderSeed` (ไม่ hand-type 76 แถว — DB compat level 170 ยืนยันแล้วว่ารองรับ
`GENERATE_SERIES`) แทนที่จะ hand-type ทีละแถวแบบ T1-T3. `n=1`/`n=2` ถูก pin ให้ผูกกับ 2 checkout session
ที่ `Confirmed` (merchant + AmountAmount ตรงกับ checkout total). `UPDATE shop.Orders SET
PaymentSessionId` ปิดท้ายให้ order ที่ `Paid` ทุกแถวชี้กลับไปยัง payment session ที่ `Status = Paid`
(REQ-6.5). เติม 5 ตารางนี้เข้า `@counts` ในโซน (จ) — ครบทุกตาราง demo แล้ว.

**GOTCHA #1 (bug จริง เจอจากการ verify ไม่ใช่แค่รันผ่าน):** สูตรตั้งต้นให้ `Cancelled` order (n ∈
{7,15,23,31,39}, มาจาก `n % 8 = 7`) สลับ `Failed`/`Expired` ด้วย `n % 2` — แต่ทุกค่าใน set นี้เป็นเลขคี่
เสมอ (`8k+7` เป็นคี่เสมอ) ทำให้ `n % 2` ได้ `1` ทุกตัว, ตกไปที่ branch `Expired` ล้วน ไม่มี `Failed` เลย
สักแถว — เจอตอน verify "5 payment session statuses present" ได้ 4 ไม่ใช่ 5. แก้ด้วยการเปลี่ยน
discriminator จาก `s.n % 2` เป็น `(s.n / 8) % 2` (integer division ให้ 0,1,2,3,4 สำหรับ n=7,15,23,31,39
ตามลำดับ → สลับ Failed/Expired ได้จริง 3 Failed + 2 Expired).

**GOTCHA #2 (ไม่ใช่ bug ของ T4 — data contamination ที่มีอยู่ก่อนแล้วใน dev DB):** verify query ของ
REQ-6.5 ตามที่ทีมลีดให้มา (ไม่มี `AND o.Id LIKE 'ed000000-%'`) คืนค่า **3** ไม่ใช่ 0. ตรวจสอบแล้วพบว่า
เป็น 3 แถวใน `shop.Orders` ที่ **ไม่ใช่ demo data** (`Id` ไม่ตรง prefix `ed000000-`, `MerchantId` ไม่ตรง
`e1000000-*` เลยสักแถว) ค้างอยู่ใน DB นี้ก่อนที่ T4 จะรันเสียอีก (`Status = 1` แต่ `PaymentSessionId IS
NULL` — น่าจะเป็นข้อมูลทดสอบเก่าจาก integration test/manual test บน DB dev เดียวกัน). เมื่อเติม
`AND o.Id LIKE 'ed000000-%'` เข้าไปในเงื่อนไข (ขอบเขตที่ REQ-6.5 พูดถึงจริง ๆ คือ "demo data ต้อง
consistent" ไม่ใช่ "ทุกแถวในตารางไม่ว่าที่มา") ผลลัพธ์คือ **0** ตรงตามคาด. ไม่ได้แก้ไข/ลบ 3 แถวนั้น
เพราะไม่ใช่ demo data และ T4 ไม่มีสิทธิ์แตะแถวที่ไม่ใช่ของตัวเอง (REQ-1.3) — ทีมลีด/เจ้าของ DB ควรตาม
สืบว่ามาจากไหนถ้าต้องการเคลียร์.

**1. รันครั้งแรก (หลังแก้ GOTCHA #1):**
```
$ ./scripts/seed-demo.sh
Changed database context to 'VCentralPay'.
admin.Users = 6
merch.Merchants = 3
txn.PspConnections = 6
admin.MerchantAccess = 4
admin.RoleAssignments = 6
merch.Users = 12
merch.ExternalLogins = 12
merch.RoleAssignments = 6
shop.Products = 24
shop.Carts = 6
shop.CartItems = 14
shop.CheckoutSessions = 4
shop.Orders = 40
txn.PaymentSessions = 36
seed-demo: OK.
EXIT=0
```

**2. รันซ้ำครั้งที่ 2 (idempotent):** ผลลัพธ์เหมือนเดิมทุกตัว, `EXIT=0`.

**3. REQ-6.5 (scoped ไปยัง demo data — ตัวจริงที่ REQ ตั้งใจตรวจ):**
```sql
SELECT COUNT(*) FROM shop.Orders o LEFT JOIN txn.PaymentSessions p ON p.Id = o.PaymentSessionId
WHERE o.Status = 1 AND (p.Id IS NULL OR p.Status <> 2) AND o.Id LIKE 'ed000000-%';
-- 0
```
(query ตามตัวหนังสือที่ทีมลีดให้ ไม่มี `AND o.Id LIKE` ได้ 3 — ดู GOTCHA #2 ด้านบน)

**4. REQ-6.4:**
```sql
SELECT COUNT(*) FROM txn.PaymentSessions p JOIN shop.Orders o ON o.Id = p.OrderId
WHERE p.MerchantId <> o.MerchantId OR p.AmountAmount <> o.AmountAmount OR p.AmountCurrency <> o.AmountCurrency;
-- 0
```

**5. REQ-6.1 (cart item ไม่ข้าม merchant):**
```sql
SELECT COUNT(*) FROM shop.CartItems i JOIN shop.Carts c ON c.Id = i.CartId
JOIN shop.Products pr ON pr.Id = i.ProductId WHERE pr.MerchantId <> c.MerchantId;
-- 0
```

**6. REQ-6.3 (PaidAt เฉพาะ Paid):**
```sql
SELECT COUNT(*) FROM shop.Orders WHERE Id LIKE 'ed000000-%'
AND ((Status = 1 AND PaidAt IS NULL) OR (Status <> 1 AND PaidAt IS NOT NULL));
-- 0
```

**7. ครบ 5 payment session status (หลังแก้ GOTCHA #1):**
```sql
SELECT Status, COUNT(*) FROM txn.PaymentSessions WHERE Id LIKE 'ee000000-%' GROUP BY Status ORDER BY Status;
-- 0=3 (Created), 1=3 (Redirected), 2=25 (Paid), 3=3 (Failed), 4=2 (Expired)  → 5 distinct
```

**8. `SummaryToken` unique:** `SELECT COUNT(*) FROM (SELECT SummaryToken FROM shop.Orders WHERE Id LIKE 'ed000000-%' GROUP BY SummaryToken HAVING COUNT(*) > 1) x;` = 0.

**9. `dotnet build pol-core.slnx`:**
```
ok dotnet build: 48 projects, 0 errors, 0 warnings (00:00:08.11)
```

**10. `git status`:**
```
* feat/demo-seed-data
 M docker/bootstrap/seed-demo.sql
?? .claude/agents/rf2-opus-worker.md
```
ไม่มีไฟล์ใต้ `src/` เปลี่ยนแปลง (REQ-7.3). `.claude/agents/rf2-opus-worker.md` เป็น untracked file
ที่มีอยู่ก่อน T4 (ไม่เกี่ยวกับงานนี้) — ไม่รวมเข้า commit.

**สรุป T4 (feature demo-seed-data ปิดครบ 4/4 task):** ทั้ง 14 ตาราง demo มีข้อมูลครบ, self-check
`@counts` ครอบคลุมทุกตารางแล้ว, ทุก invariant ที่ REQ-6.x ต้องการผ่านจริงเมื่อ scope ไปยัง demo data
(`Id LIKE '<prefix>-%'`) — ตัวเลข "3" ที่เห็นตอน verify ตรงตัวหนังสือ (ไม่ scope) เป็น pre-existing
non-demo noise ในตาราง `shop.Orders` ของ DB dev เครื่องนี้ ไม่ใช่ผลจาก T4.

---

## - [x] T5 — ขยาย shop.Products เป็น 100 แถว

Depends on: T4

Satisfies: 5.4, 5.5

เพิ่มตามคำสั่ง user หลัง T4 ปิด — catalogue 24 แถวน้อยเกินกว่าจะทดสอบ SFS/pagination ได้จริง.

Scope (design §4 shop.Products):
1. **เก็บ 24 แถวเดิมไว้ทั้งหมด ไม่ขยับ id** — `shop.CartItems` อ้าง `ProductId` ไปที่ `e9…0001`–`e9…0018`
   ตรง ๆ; regenerate ใหม่ทั้งชุดจะทำให้ cart item กลายเป็น orphan
2. เติมอีก 76 แถว (id `e9…0019`–`e9…0064` hex) จาก cross join **plan-line x tier** ใน table variable:
   9 plan line/merchant x 3 tier (Silver 1.00 / Gold 1.35 / Platinum 1.80) = 27 candidate หยิบ 26/25/25
3. id = `ROW_NUMBER()` + offset 24 เรนเดอร์เป็น hex (`CONVERT(varbinary(4), n)` style 2) — deterministic,
   `DELETE … LIKE 'e9000000-%'` ใน (ค) กวาดคืนครบทั้ง 100 โดยไม่ต้องแก้อะไร

### Evidence (2026-07-13)

**GOTCHA: `LINENO` เป็น reserved keyword ของ T-SQL.** ตั้งชื่อคอลัมน์ table variable ว่า `LineNo` ครั้งแรก
แล้วได้:
```
Msg 156, Level 15, State 1, Line 208
Incorrect syntax near the keyword 'LineNo'.
```
เปลี่ยนเป็น `LineIdx` แล้วผ่าน.

**รันจริง (`./scripts/seed-demo.sh`, exit 0, รันซ้ำอีกรอบ exit 0):**
```
shop.Products = 100          (ตารางอื่นเท่าเดิมทุกตัว)
```

**Invariant (query ใต้ session-context stamp):**
```
per merchant:  e1…0001 = 34   e1…0002 = 33   e1…0003 = 33
distinct_isactive = 2         inactive = 13
dup_names = 0                 dup_ids  = 0
price_range = 350.0000 .. 73800.0000
req61_cartitem_crossmerchant = 0
demo_cartitems = 14           demo_orphans = 0    <- 24 แถวเดิมไม่ขยับ, cart item ยังชี้ถูก
```
ตัวอย่างแถว generate: `ประกันสุขภาพเหมาจ่าย Health Lumpsum Silver / 22000.0000`,
`… Gold / 29700.0000`, `… Platinum / 39600.0000`.

**หมายเหตุ:** `cartitems_orphaned` แบบไม่ scope = 1 — เป็น cart item เก่านอก demo (GUID สุ่ม
`545BF69D-…` ตระกูลเดียวกับ 3 non-demo orders ที่ T4 เจอ) ไม่ใช่ของ seed; scope ไป demo แล้ว = 0.
ช่วงราคา max ขยับจาก 48,000 เป็น 73,800 (แผนบำนาญ tier Platinum) — design อัปเดตให้ตรงแล้ว.

---

## - [x] T6 — dev-target guard + sqlcmd fallback (Codex review PR #110)

Depends on: T5

Satisfies: 1.7, 1.8

Codex review บน PR #110 ให้ 2 finding (P2) บน `scripts/seed-demo.sh` — verify แล้ว **จริงทั้งคู่**.

**Finding 1 (REQ-1.7) — ไม่มี dev-target guard.** สคริปต์รับ `POL_SQL_SERVER`/`POL_DB` จาก env ตรง ๆ
ไม่ validate เลย แล้วลบ+เขียนใหม่ในฐานะ `sa`. repo มี `.env.prod.example` อยู่จริง ดังนั้น "เผลอ source
prod env แล้วรัน" ไม่ใช่สถานการณ์สมมติ — demo merchant/user/order จะลง prod เงียบ ๆ. README เขียนว่า
dev-only แต่ไม่มีอะไรบังคับ.
แก้: echo `server=… db=…` ก่อนเสมอ + ปฏิเสธเป้าหมายที่ไม่ใช่ localhost/`127.0.0.1`/`[::1]` เว้นแต่ตั้ง
`POL_ALLOW_DEMO_SEED=1`.

**Finding 2 (REQ-1.8) — `sqlcmd` ไม่ใช่ prerequisite ที่ documented.** README Prerequisites มีแค่ .NET SDK /
Docker+Compose / `dotnet-ef`. `01-principals.sql` รันผ่าน compose service `pol-db-init` (ข้างใน container),
CI ลง sqlcmd เอง — เครื่อง dev ที่ทำตาม README จะไม่มี host sqlcmd และได้ `sqlcmd: command not found`.
(เครื่องที่พัฒนางานนี้มี homebrew sqlcmd อยู่ก่อนแล้ว จึงไม่เจอตอน T1-T5.)
แก้: ใช้ host sqlcmd ถ้ามี; ไม่มี + เป้าหมายเป็น compose DB -> fall back ไป
`docker compose -f <repo>/docker-compose.yml exec -T pol-db /opt/mssql-tools18/bin/sqlcmd` โดย feed ไฟล์ทาง
**stdin** (`docker/bootstrap` mount เข้าแค่ service `pol-db-init` ไม่ได้ mount เข้า `pol-db` จึงใช้ `-i` ไม่ได้).
ไม่มี host sqlcmd + เป้าหมายไม่ใช่ compose DB -> fail ชัด ๆ ไม่ redirect เงียบไป DB local.

### Evidence (2026-07-13)

```
### 1. local run ปกติ
seed-demo: target server=localhost,11433 db=VCentralPay
... shop.Products = 100 ... seed-demo: OK.          exit 0

### 2. เป้าหมายไม่ใช่ localhost -> ปฏิเสธ
$ POL_SQL_SERVER="prod-sql.internal,1433" ./scripts/seed-demo.sh
seed-demo: target server=prod-sql.internal,1433 db=VCentralPay
seed-demo: refusing to seed a non-local target (prod-sql.internal,1433).
...
    POL_ALLOW_DEMO_SEED=1 ./scripts/seed-demo.sh      exit 1

### 3. non-local + POL_ALLOW_DEMO_SEED=1 -> ไม่ปฏิเสธ (ผ่าน guard, ไปตายที่ connect จริง = ถูกต้อง)
seed-demo: target server=prod-sql.internal,1433 db=VCentralPay
Sqlcmd: Error: Microsoft ODBC Driver 18 for SQL Server : Login timeout expired.

### 4. container fallback (ซ่อน host sqlcmd จาก PATH แต่คง docker ไว้)
seed-demo: target server=localhost,11433 db=VCentralPay
seed-demo: no host sqlcmd — using the one inside the pol-db container.
... shop.Products = 100 ... seed-demo: OK.          exit 0
```

`bash -n scripts/seed-demo.sh` ผ่าน. README อัปเดตทั้งสองข้อแล้ว.

---

## - [x] T7 — เติมฟิลด์เอกสารของ shop.Products ให้ครบ + แคตตาล็อกกลาง

Depends on: T5

Satisfies: 5.6, 5.7

เพิ่มตามคำสั่ง user (2026-07-30) หลังเห็นว่า 23 คอลัมน์ของทั้ง 100 แถวเป็น `NULL` และ
`GET /products` คืน 0 แถวบน demo เพราะ `StartDate`/`EndDate` ว่าง จึงหลุด search window ของ
`ProductRepository.SearchAsync`.

Scope:
1. เก็บ `INSERT` ทั้งสองก้อนไว้เหมือนเดิม แล้วเพิ่ม `UPDATE shop.Products ... WHERE Id LIKE 'e9000000-%'`
   ก้อนเดียวต่อท้าย เติม 23 คอลัมน์พร้อมกันทั้ง 100 แถว — ทุกค่า derive จากตัวแถวเองผ่าน `CROSS APPLY`
   (`Seq` = เลขท้าย `DocumentNo`, `Yr` = 68/69) จึง deterministic
2. วันที่อิง `SYSUTCDATETIME()`: RENEWAL -> `EndDate` = today + (`Seq % 50` + 3) วัน;
   ที่เหลือ -> `StartDate` = today - (`Seq % 150` + 1) วัน — ตก search window เสมอไม่ว่ารัน seed วันไหน
3. ยอดเงิน derive ย้อนจาก `TotalPremium` (ห้ามขยับ): `Net = ROUND(Total / 1.07428, 2)`,
   `Stamp = ROUND(Net * 0.004, 2)`, `TaxVat` = residual, `CommissionPercent` วน 10/12/15
4. ตัดคอลัมน์ `MerchantId` ออกจากทั้งสอง `INSERT` — `shop.Products` เป็นแคตตาล็อกกลาง
   (products-sp-53-alignment, migration `20260730143112_ProductsCentralCatalogue`)
5. เพิ่ม assertion 2 ตัวในบล็อกตรวจท้ายไฟล์: (ก) ไม่มีแถวที่ฟิลด์บังคับเป็น NULL หรือ
   `Net + Stamp + TaxVat <> TotalPremium`; (ข) นับแถวที่ตก search window แบบเดียวกับ repository ต้องได้ 100

Verify:
- `./scripts/seed-demo.sh` -> `seed-demo: OK.` (assertion ใหม่ throw ถ้าเติมไม่ครบ) และรันซ้ำได้ผลเดิม
- `SELECT` ตรวจตาราง: NULL เหลือเฉพาะที่ตั้งใจตามชนิดเอกสาร (`ReferencePre`, `PolicyType`,
  `LicensePlateNumber`, 4 คอลัมน์ `*Number`) และ `PaidDate` ของแถว UNPAID

### Evidence (2026-07-30)

`./scripts/seed-demo.sh` -> `shop.Products = 100 ... seed-demo: OK.` สองรอบติดกัน (idempotent).
ตรวจแถวจริง: RENEWAL `00098-68100/ตอ/900005-10` -> `StartDate 2025-08-07 / EndDate 2026-08-07`,
POLICY `00098-69100/กธ/900001-10` -> `2026-07-28 / 2027-07-28`; ยอดเงินบวกกลับได้ `TotalPremium` เป๊ะ
ทุกแถว (assertion (ก) ผ่าน) และ assertion (ข) นับได้ 100/100.
