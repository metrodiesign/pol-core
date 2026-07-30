# Requirements — demo-seed-data

> Status: approved 2026-07-13 (quick, no gates)

## Context

วันนี้ DB ที่ migrate เสร็จใหม่มีข้อมูลแค่ 2 ก้อน (จาก migration `20260712185912_SeedData`):
IAM catalog (`iam.PermissionGroups`/`Permissions`/`Roles`/`RolePermissions`) และ master data
(`cfg.Positions`/`Offices`/`Levels`/`Divisions`). **ทุกตารางที่เหลือว่างเปล่า** — `merch.Merchants`,
`merch.Users`, `admin.Users`, `shop.Products`/`Carts`/`CartItems`/`CheckoutSessions`/`Orders`,
`txn.PspConnections`/`PaymentSessions` — จึงเปิดคอนโซล/เรียก API แล้วไม่เห็นอะไรเลย และทดสอบ
SFS (search/filter/sort), pagination, reconciliation report ไม่ได้เพราะไม่มีข้อมูลให้กรอง.

งานนี้เพิ่ม **demo dataset** ที่ครอบคลุมทั้ง funnel (merchant -> ผู้ใช้ทั้งสองฝั่ง -> สินค้า ->
ตะกร้า -> checkout -> order -> payment session) ในปริมาณที่ "เห็นภาพข้อมูลจริง" ได้จริง.

Locked decisions (user ตัดสิน 2026-07-13 — ห้าม re-litigate):
- **กลไก = SQL script รันมือ** (`docker/bootstrap/seed-demo.sql` + `scripts/seed-demo.sh`) ตามแพทเทิร์นเดิม
  ของ `01-principals.sql` / `assert-fresh-db.sql` — **ไม่ใช่ EF migration ตัวที่ 4** (จะรันบน prod ด้วย)
  และ **ไม่ใช่ boot-time seeder ใน C#** (ต้องเขียน app code ใหม่)
- **ปริมาณ = กลาง** — 3 merchants / 6 platform users / 12 merchant users / 6 PSP connections /
  24 products / 6 carts / 4 checkout sessions / 40 orders / 36 payment sessions
- **บัญชี login จริงไม่ได้** — Google `Subject` เป็นค่า seed ปลอม; bootstrap Super ตัวจริงยังใช้
  deny-dance เดิม (ไม่ถูกแตะต้อง)

## REQ-1: กลไกและตำแหน่งของ seed

- 1.1 THE SYSTEM SHALL จัดเก็บ demo seed ทั้งหมดไว้ใน `docker/bootstrap/seed-demo.sql` ซึ่ง **ไม่อยู่ใน
  EF migration chain** — `dotnet ef database update` ต้องไม่แตะ demo data แม้แต่แถวเดียว.
- 1.2 THE SYSTEM SHALL จัดหา `scripts/seed-demo.sh` เป็นตัวรัน: อ่าน connection/credential จาก
  environment (`.env` แบบเดียวกับที่ CI/compose ใช้: `POL_SA_PASSWORD`, `POL_DB`) แล้วเรียก `sqlcmd -b`
  กับไฟล์ SQL ข้างต้น.
- 1.3 WHEN `seed-demo.sql` ถูกรันซ้ำบน DB ที่มี demo data อยู่แล้ว THE SYSTEM SHALL ให้ผลลัพธ์เท่ากับ
  รันครั้งแรก (idempotent) — ลบเฉพาะแถว demo ของตัวเองแล้วใส่กลับ ไม่ TRUNCATE และไม่แตะแถวที่ไม่ใช่ demo.
- 1.4 THE SYSTEM SHALL ตั้ง id ของทุกแถว demo เป็น GUID คงที่ (deterministic) ที่มี prefix ประจำตาราง —
  ห้ามใช้ `NEWID()` — เพื่อให้ 1.3 ลบแถวของตัวเองได้แม่นยำและทุก environment ได้ id ชุดเดียวกัน.
- 1.5 IF การใส่ข้อมูลส่วนใดล้มเหลว THEN THE SYSTEM SHALL rollback ทั้งชุด (ทั้งสคริปต์อยู่ใน transaction
  เดียว + `SET XACT_ABORT ON`) — ห้ามทิ้ง DB ไว้ในสภาพ seed ครึ่งเดียว.
- 1.6 WHEN seed สำเร็จ THE SYSTEM SHALL พิมพ์จำนวนแถวต่อตารางที่ใส่ และ THE SYSTEM SHALL fail ด้วย
  `RAISERROR` ถ้าตารางเป้าหมายใดได้ 0 แถว (self-check ในตัวสคริปต์).
- 1.7 THE SYSTEM SHALL พิมพ์ server + database ที่กำลังจะ seed ก่อนลงมือเสมอ, และ IF เป้าหมายไม่ใช่
  localhost THEN THE SYSTEM SHALL ปฏิเสธการ seed เว้นแต่ตั้ง `POL_ALLOW_DEMO_SEED=1` โดยเจตนา — สคริปต์
  ลบแล้วเขียนใหม่ในฐานะ `sa` การเผลอ source `.env` ของ prod/staging แล้วรันต้องไม่ปลูก demo data ลงที่นั่นเงียบ ๆ.
- 1.8 WHERE เครื่องไม่มี `sqlcmd` บน host THE SYSTEM SHALL รัน seed ผ่าน `sqlcmd` ที่มากับ container
  `pol-db` แทน (feed ไฟล์ทาง stdin) — README prerequisites ไม่ได้บังคับให้ลง `sqlcmd` บน host และ
  `01-principals.sql` ก็รันจากใน container อยู่แล้ว. IF ไม่มี host `sqlcmd` และเป้าหมายไม่ใช่ compose DB
  THEN THE SYSTEM SHALL fail พร้อมข้อความชัด (container เข้าไม่ถึง server นั้น) ไม่ใช่ redirect เงียบ ๆ
  ไปที่ DB local.

## REQ-2: ผ่าน RLS floor โดยไม่เจาะ bypass

- 2.1 THE SYSTEM SHALL เขียนแถวลง merchant-scoped table ทุกตัว (`shop.*`, `txn.PaymentSessions`,
  `txn.PspConnections`, `merch.Merchants`) โดยผ่าน `sec.MerchantIsolationPolicy` ตามปกติ — ห้ามปิด/แก้
  security policy, ห้ามเพิ่มสมาชิกเข้า `pol_rls_bypass`, ห้ามใช้ `EXECUTE AS` เพื่อหลบ.
- 2.2 THE SYSTEM SHALL ใส่แถว `admin.Users` (ตารางนี้ไม่มี RLS) ที่มี `Tier = 1` (Super) **ก่อน** แถว
  merchant-scoped ใด ๆ แล้ว stamp `sp_set_session_context` ด้วย `UserId` = id ของ Super นั้น และ
  `MerchantId` = `00000000-0000-0000-0000-000000000000` เพื่อให้เข้าเงื่อนไขสาขา Super ของ
  `sec.fn_merchant_predicate`.
- 2.3 WHERE สคริปต์รันด้วย principal ที่ไม่ใช่สมาชิก `pol_rls_bypass` (เช่น `sa`) THE SYSTEM SHALL ยัง
  insert/delete แถว merchant-scoped ได้ครบ — พิสูจน์ว่า 2.2 ทำงานจริง ไม่ได้อาศัย bypass เงียบ ๆ.
- 2.4 THE SYSTEM SHALL ทำขั้นตอนตามลำดับ: (ก) ลบ+ใส่ demo `admin.Users` (ตารางไม่มี RLS) -> (ข) stamp
  session context ตาม 2.2 -> (ค) ลบแถว demo merchant-scoped -> (ง) ใส่แถว demo ที่เหลือ — เพราะ **FILTER
  predicate มีผลกับ `DELETE` ด้วย**: ถ้าลบ merchant-scoped ก่อน stamp จะลบไม่โดนแถวใดเลย (เห็นศูนย์แถว)
  แล้ว INSERT รอบใหม่จะชน primary key.

## REQ-3: Merchants + PSP connections

- 3.1 THE SYSTEM SHALL seed merchant 3 แถวใน `merch.Merchants` ด้วย `Code` ที่อยู่ใน allowlist ของ
  `Merchants.Domain.MerchantCode` เท่านั้น (`vPrivilege` / `vCommerce` / `vSouvenir`), `Status = 0`
  (Active), `Country = 'TH'`, `Currency = 'THB'`.
- 3.2 THE SYSTEM SHALL seed `txn.PspConnections` 6 แถว (2 ต่อ merchant: `Psp = 0` 2C2P และ `Psp = 1`
  Omise) โดย `EnabledMethods` เป็น comma-separated method code verbatim จากชุด
  `card` / `promptpay` / `installment` ตามที่ `Payments.Domain.Psp.Connection` คาดหวัง.
- 3.3 THE SYSTEM SHALL ไม่ seed `merch.VaultSecrets` — `SecretRefName` ของ connection ชี้ไปยัง ref ที่ยัง
  ไม่มี secret จริง (demo ไม่แตะ vault, ไม่ยิง PSP จริง).

## REQ-4: Platform users (control plane)

- 4.1 THE SYSTEM SHALL seed `admin.Users` 6 แถว: Super 2 (หนึ่งในนั้นคือตัวที่ใช้ stamp ตาม 2.2),
  Scoped 3, และ 1 แถว `Status = 1` (Suspended) เพื่อให้เห็นทั้ง 2 สถานะ.
- 4.2 THE SYSTEM SHALL ผูก `PositionId` / `OfficeId` / `LevelId` / `DivisionId` ของแต่ละ platform user
  ไปยัง GUID ที่ `cfg.*` seed ไว้แล้วในmigration (`a1…`/`b2…`/`c3…`/`d4…`) — ห้ามสร้าง master row ใหม่.
- 4.3 THE SYSTEM SHALL seed `admin.MerchantAccess` ให้เฉพาะ platform user ที่เป็น Scoped — Super ต้อง
  ไม่มีแถว (RLS สาขา Super ไม่ได้อ่านตารางนี้) และ Scoped อย่างน้อย 1 คนต้องเห็นแค่ 1 merchant เพื่อให้
  ทดสอบ scoped isolation ได้จริง.
- 4.4 THE SYSTEM SHALL seed `admin.RoleAssignments` ผูก platform user ไปยัง role ที่ migration seed ไว้
  (`platform_admin` = `11111111-…`, `platform_auditor` = `55555555-…`) — ห้ามสร้าง role ใหม่ใน `iam.*`.

## REQ-5: Merchant users + สินค้า

- 5.1 THE SYSTEM SHALL seed `merch.Users` 12 แถว (4 ต่อ merchant) ครอบคลุมทุกค่า
  `Merchants.Domain.Users.UserStatus`: `PendingApproval = 0`, `Active = 1`, `Rejected = 2`,
  `Suspended = 3` และครอบคลุมทั้ง `PersonType` `Individual = 0` และ `Juristic = 1`.
- 5.2 THE SYSTEM SHALL seed `merch.ExternalLogins` (`Provider = 'google'`) หนึ่งแถวต่อ merchant user
  ที่มี `Subject` เป็นค่า seed ปลอม (prefix ที่มองออกว่าเป็น demo) — login Google จริงด้วยบัญชีเหล่านี้
  ต้องไม่สำเร็จ.
- 5.3 THE SYSTEM SHALL seed `merch.RoleAssignments` ผูก merchant user ที่ `Status = 1` (Active) ไปยัง
  role ที่ migration seed ไว้ (`merchant_manager` = `aaaaaaaa-…`, `merchant_staff` = `bbbbbbbb-…`)
  พร้อม `MerchantId` ของ user นั้น.
- 5.4 THE SYSTEM SHALL seed `shop.Products` 100 แถว (34 / 33 / 33 ต่อ merchant) เป็นเอกสารประกันที่อ่านแล้ว
  เข้าใจว่าเป็นข้อมูลจริงของธุรกิจ, `DocumentNo` ไม่ซ้ำต่อ merchant, `TotalPremium` เป็น
  `DECIMAL(19,2)` (ทศนิยมไม่เกิน 2 ตำแหน่ง — `Product.Create` throw ไม่ปัดให้) และมีทั้งเอกสารที่ยัง
  ขายได้ (`PaymentStatus = 'UNPAID'`) และเอกสารที่ขายไม่ได้แล้ว (`PaymentStatus = 'PAID'` + `PaidDate`
  มีค่า) — แกน "ขายได้/ขายไม่ได้" คือ `PaymentStatus` ไม่ใช่ `IsActive` ที่ถูกลบไปแล้ว
  (products-sp-53-alignment REQ-2.1/2.4).
- 5.5 WHERE จำนวนสินค้ามากเกินกว่าจะเขียนมือทีละแถว THE SYSTEM SHALL generate ส่วนที่เหลือแบบ
  deterministic (plan-line x tier cross join + row number -> id) — id ของ 24 แถวแรกที่ `shop.CartItems`
  อ้างถึงต้องไม่ขยับ.
- 5.6 THE SYSTEM SHALL เติมฟิลด์เอกสารของทั้ง 100 แถวให้ครบตามชนิดเอกสาร — ฟิลด์อ้างอิง
  (`PolicyYear`/`ReferenceYear`/`ReferenceBranch`/`PolicySequenceNo`/`ReferenceNo`), ฝ่ายขาย/นายหน้า/สาขา
  (`SaleFullName`/`BrokerCode`/`BrokerName`/`PolicyBranch`) และยอดเงินย่อย
  (`NetPremium`/`Stamp`/`TaxVat`/`CommissionPercent`/`CommissionAmount`) ต้องมีค่าทุกแถว; ที่เหลือ
  (`ReferencePre`, `PolicyType`, `LicensePlateNumber`, `PolicyNumber`/`ApplicationNumber`/
  `PreviousPolicyNumber`/`EndorsementNumber`) เป็น NULL ได้เฉพาะตามชนิดเอกสาร/ProductGroup.
  `NetPremium + Stamp + TaxVat` ต้องเท่ากับ `TotalPremium` พอดี.
- 5.7 THE SYSTEM SHALL ตั้ง `StartDate`/`EndDate` ของทั้ง 100 แถวให้ตกใน search window ของ
  `ProductRepository.SearchAsync` (RENEWAL -> `EndDate` ใน 2 เดือนข้างหน้า, ที่เหลือ -> `StartDate`
  ภายใน 6 เดือนย้อนหลัง) โดยอิง `SYSUTCDATETIME()` ณ เวลา seed — วันที่ NULL หรือ hardcode ไว้
  ทำให้ `GET /products` คืน 0 แถวไม่ว่าจะส่ง filter อะไร.

## REQ-6: Funnel เชิงธุรกรรม

- 6.1 THE SYSTEM SHALL seed `shop.Carts` 6 แถว ครอบคลุมทั้ง `CartStatus` `Open` และ `CheckedOut`
  (คอลัมน์เก็บเป็น **ชื่อ enum ตัวหนังสือ** ตาม `HasConversion<string>()` ไม่ใช่ตัวเลข) พร้อม
  `shop.CartItems` ที่ `ProductId` ชี้ไปยังสินค้าของ **merchant เดียวกัน** กับ cart เสมอ.
- 6.2 THE SYSTEM SHALL seed `shop.CheckoutSessions` 4 แถว ครอบคลุมทุกค่า
  `Checkouts.Domain.SessionStatus` (`Started = 0`, `Confirmed = 1`, `Abandoned = 2`) โดย
  `AmountAmount` เท่ากับผลรวมของ cart item ที่ผูกอยู่.
- 6.3 THE SYSTEM SHALL seed `shop.Orders` 40 แถว กระจายวันที่ย้อนหลัง 90 วัน ครอบคลุมทุกค่า
  `Orders.Domain.OrderStatus` (`AwaitingPayment = 0`, `Paid = 1`, `Cancelled = 2`) โดยแถว `Paid` ต้องมี
  `PaidAt` ไม่เป็น NULL และแถวที่ไม่ใช่ `Paid` ต้องมี `PaidAt` เป็น NULL, ทุกแถวมี `SummaryToken`
  ไม่ซ้ำกัน และ `SummaryTokenExpiresAt` มีค่า.
- 6.4 THE SYSTEM SHALL seed `txn.PaymentSessions` ครอบคลุมทุกค่า `Payments.Domain.SessionStatus`
  (`Created = 0`, `Redirected = 1`, `Paid = 2`, `Failed = 3`, `Expired = 4`) โดย `MerchantId` และ
  `AmountAmount`/`AmountCurrency` ต้องตรงกับ order ที่ `OrderId` ชี้ไปเสมอ.
- 6.5 WHERE order มีสถานะ `Paid` THE SYSTEM SHALL ให้ payment session ที่ผูกอยู่มีสถานะ `Paid` เช่นกัน
  และ `shop.Orders.PaymentSessionId` ชี้กลับไปยัง session นั้น — ไม่มีคู่ที่ขัดแย้งกัน.
- 6.6 THE SYSTEM SHALL ไม่ seed `txn.OutboxMessages` / `txn.IdempotencyRecords` / audit table ใด ๆ —
  ตารางเหล่านั้นเป็นผลข้างเคียงของ runtime ไม่ใช่ข้อมูลตั้งต้น.

## REQ-7: เอกสารและความปลอดภัย

- 7.1 THE SYSTEM SHALL อธิบายวิธีรัน seed + ขอบเขตของมัน (dev เท่านั้น) ใน `README.md`.
- 7.2 THE SYSTEM SHALL ไม่ฝัง credential/secret ใด ๆ ในไฟล์ SQL หรือ shell script — password ส่งผ่าน
  environment variable เท่านั้น (แพทเทิร์นเดียวกับ `01-principals.sql`).
- 7.3 THE SYSTEM SHALL ไม่แก้ไฟล์ใต้ `src/` — งานนี้เป็น data-only, ห้ามแตะ production code path.
</content>
