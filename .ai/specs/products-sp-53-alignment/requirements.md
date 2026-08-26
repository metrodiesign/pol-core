# Requirements: products-sp-53-alignment

> Status: unknown
> Scope: จัด `src/Modules/Products` ให้ตรงเอกสารต้นเรื่อง `docs/reference/vcentralpay-sp-quick-reference.pdf` v1.0 — input surface ตาม §2, item shape ตาม §5.2, error mapping ตาม §6

## บริบท

`src/Modules/Products` ถูกสร้างจาก "VCentralPay SP guide" ตอนที่เอกสารยังไม่อยู่ใน repo (comment ใน
`Product.cs`, `ProductGroup.cs`, `ListProducts.cs` อ้าง §2/§5.2 แบบลอย ๆ) ตอนนี้เอกสารจริงครบ 8 หน้า
อยู่ใน repo แล้ว จึงเทียบได้ทุกช่อง — พบเพี้ยน 6 จุดในชั้น input (§2) และ field เกิน 9 ตัวในชั้น entity (§5.2)

user ตัดสินแล้วว่า **field ที่ไม่ตรง §5.2 ให้ลบออกทั้งหมด** รวมถึงตัวที่มี consumer นอก Products —
`Product` จะเป็น mirror ของ §5.2 result set ตรง ๆ ไม่มี field ส่วนเกินของ repo

งานนี้ DROP 8 คอลัมน์ + เปลี่ยน gate ของ cart/checkout = destructive ต้อง backup ก่อนรันบน env ที่มี
ข้อมูลจริง; local ใช้ `docker compose down -v`

## Requirements (EARS)

## REQ-1: `Product` ต้องเป็น mirror ของ §5.2 result set

- 1.1 THE SYSTEM SHALL ลบ property/คอลัมน์ `BranchCode`, `IsActive`, `CreatedAt` ออกจาก `Product` ทั้งหมด เพราะ §5.2 ไม่มี field เหล่านี้
- 1.2 THE SYSTEM SHALL เก็บ `TotalPremium` เป็น `decimal` และ premium breakdown (`NetPremium`, `Stamp`, `TaxVat`, `CommissionAmount`) เป็น `decimal?` — เลิกใช้ `Money` ใน `Product` และลบ 5 คอลัมน์ currency (`TotalPremiumCurrency`, `NetPremiumCurrency`, `StampCurrency`, `TaxVatCurrency`, `CommissionCurrency`) พร้อม computed `Money?` และ helper `RequireThb`
- 1.3 THE SYSTEM SHALL คง `Id` (PK) และ `MerchantId` (tenant floor) ไว้ในฐานะ technical key ทั้งที่ §5.2 ไม่มี — query filter, unique index และทุกโมดูลพึ่งสองตัวนี้
- 1.4 THE SYSTEM SHALL rename คอลัมน์ให้ตรงชื่อ §5.2: `TotalPremiumAmount` -> `TotalPremium`, `NetPremiumAmount` -> `NetPremium`, `StampAmount` -> `Stamp`, `TaxVatAmount` -> `TaxVat` (คอลัมน์ `CommissionAmount`/`CommissionPercent` ชื่อตรงอยู่แล้ว)
- 1.5 WHEN `Product.Create` ได้ค่าเงิน (`TotalPremium` หรือ breakdown ตัวใดก็ตาม) ที่มีทศนิยมเกิน 2 ตำแหน่ง, THE SYSTEM SHALL throw `ArgumentException` — §5.2 เป็น `decimal(19,2)` ถ้าไม่มี guard นี้ DB จะปัดเงียบ ๆ (เคสเดิม: PSP ปัด sub-minor-unit ก่อน charge, PR #79)
- 1.6 THE SYSTEM SHALL คง invariant เดิมทุกข้อ: `TotalPremium <= 0` -> throw, `StartDate > EndDate` -> throw, `Enum.IsDefined` ของ `ProductGroup`/`DocumentType`, และ §1.2 กฎ CMI + APPLICATION ไม่รองรับ
- 1.7 THE SYSTEM SHALL ตัด param `createdAt` ออกจาก `Product.Create(ProductInput input, DateTime createdAt)` และตัด `BranchCode` + เปลี่ยน premium เป็น `decimal`/`decimal?` ใน `ProductInput`

## REQ-2: gate ของ cart/checkout ย้ายจาก `IsActive` ไป `PaymentStatus`

- 2.1 WHEN merchant เพิ่มเอกสารลงตะกร้า (cart add-item) หรือเริ่ม checkout, THE SYSTEM SHALL ปฏิเสธเอกสารที่ `PaymentStatus != UNPAID` แทนการเช็ค `!IsActive` โดยคง HTTP status และข้อความ error เดิม (cart = 400, checkout = 409)
- 2.2 THE SYSTEM SHALL ลบเมธอด `Product.Deactivate()` — ไม่มี production caller และแกน "ถอนจากการขาย" เหลือทางเดียวคือขายจบแล้วเป็น PAID
- 2.3 THE SYSTEM SHALL ให้ `Product.MarkPaid` เหลือแค่ set `PaymentStatus = PAID` + `PaidDate`
- 2.4 WHEN seed demo ถูกรัน, THE SYSTEM SHALL ทำให้แถวที่เดิมเป็น `IsActive = 0` กลายเป็น `PaymentStatus = 'PAID'` + `PaidDate` มีค่า เพื่อคงเจตนาเดิม (demo ต้องมีทั้งเอกสารที่ขายได้และขายไม่ได้) บนแกนใหม่ — ถ้าย้าย gate เฉย ๆ แถวเหล่านี้จะ "กลับมาขายได้"

## REQ-3: input surface ตาม §2 — `@PaymentStatus` (F1) และ `@SaleCode`/`@BranchCode` (F4)

- 3.1 WHEN client ไม่ส่ง `paymentStatus`, THE SYSTEM SHALL กรองเฉพาะ `UNPAID` (§2 default = UNPAID) แทนพฤติกรรมเดิมที่ absent = ALL
- 3.2 THE SYSTEM SHALL รับ `paymentStatus` บน wire เป็น string 3 ค่า `UNPAID | PAID | ALL` (case-sensitive) โดย `ALL` แปลเป็น "ไม่กรอง" — enum `PaymentStatus` ห้ามมีสมาชิก `ALL` (ล็อกไว้โดย spec `checkout-chain-document-fields`)
- 3.3 THE SYSTEM SHALL บังคับให้ `saleCode` เป็น filter ที่ต้องมี (required, MaxLength 20, trim แล้วห้ามว่าง) ทำให้ query param `productFilters` กลายเป็นพารามิเตอร์บังคับ — breaking change ที่ยอมรับแล้ว
- 3.4 THE SYSTEM SHALL ไม่รองรับ `@BranchCode` ทั้งที่ §2 กำหนด Req=Yes เพราะคอลัมน์ `BranchCode` ถูกลบตาม §5.2 (ดูหัวข้อ "นอกขอบเขต")
- 3.5 WHEN `productFilters` ไม่ถูกส่งมาหรือเป็นค่าว่าง, THE SYSTEM SHALL throw `ArgumentException` (400) แทนการ `return null` แบบเดิม — ห้ามใช้ `BadHttpRequestException` เพราะจะกลายเป็น 500
- 3.6 THE SYSTEM SHALL คงพฤติกรรม §2 ที่ตรงแล้วไว้ทุกข้อ: `@SearchText`(100)/`@InsuredName`(200) partial, coverage date 4 ตัว inclusive, `@DocumentType` default ALL, `@PolicyNo`/`@ApplicationNo`(30) exact, `@PaidDateFrom/To`, `@PageNo` NULL/<1 -> 1

## REQ-4: `@PageSize` cap 25 (F2)

- 4.1 WHEN client ส่ง `limit` มากกว่า 25 บน list endpoint ใด ๆ ของ repo, THE SYSTEM SHALL cap ค่าเป็น 25 (§2 `@PageSize` + ย้ำใน §5.1) แทน clamp เดิมที่ `[1, 100]`
- 4.2 THE SYSTEM SHALL อัปเดตเอกสาร SFS (`SfsOpenApi` description, `docs/reference/search-filter-sort.md`) และ `.ai/specs/search-filter-sort/requirements.md` REQ-2.2 ให้ระบุเพดาน 25 ตรงกับโค้ด

## REQ-5: smart search ต้องรวมทะเบียนรถเฉพาะแถว Motor (F3)

- 5.1 WHEN `searchText` ถูกส่งมา, THE SYSTEM SHALL รวม `LicensePlateNumber` เข้า predicate เฉพาะแถวที่ `ProductGroup` เป็น `CMI` หรือ `VMI` (§3 Motor smart search) และไม่รวมสำหรับ `FIRE`/`MISC` (§4 "ไม่รวมทะเบียนรถ")
- 5.2 THE SYSTEM SHALL ทำ Motor gate แบบ per-row ด้วยการเทียบ enum `ProductGroup` ตรง ๆ ใน predicate ไม่ใช่ per-request — ต้องถูกต้องแม้ client ไม่ส่ง `productGroup` และ `InsuranceType` เป็น `builder.Ignore` จึงแปลเป็น SQL ไม่ได้

## REQ-6: search window (F5)

- 6.1 THE SYSTEM SHALL จำกัดผลการค้นหาของเอกสารทั่วไป (`DocumentType != RENEWAL`) ไว้ที่ `StartDate` ย้อนหลังไม่เกิน 6 เดือนจากวันปัจจุบัน (§3/§4 search window, `SearchWindowMonths` = 6)
- 6.2 WHEN `DocumentType` ของแถวเป็น `RENEWAL`, THE SYSTEM SHALL ใช้ window 2 เดือนบน `EndDate` (`EndDate` อยู่ในช่วง [วันนี้, วันนี้ + 2 เดือน)) แทน rule 6 เดือน — ตีความ §3 "window 2 เดือนตาม period_to / p_to" ว่า `period_to` map เป็น `EndDate` และมองไปข้างหน้า (กรมธรรม์ใกล้หมดอายุ พร้อมต่ออายุ)
- 6.3 THE SYSTEM SHALL อ่านวันปัจจุบันผ่าน `BuildingBlocks.Application.IClock` ไม่ใช่ `DateTime.UtcNow` ตรง ๆ เพื่อให้ test ตรึงเวลาได้
- 6.4 THE SYSTEM SHALL เก็บความยาว window ทั้งสองไว้เป็นค่าคงที่ที่มีชื่อ (`SearchWindowMonths` = 6, `RenewalWindowMonths` = 2) ที่จุดเดียว เพื่อให้กลับทิศ/ปรับค่าได้ด้วยการแก้บรรทัดเดียวเมื่อเจ้าของ SP ยืนยันทิศของ RENEWAL window

## REQ-7: ถอด SFS surface ออกจาก products

- 7.1 THE SYSTEM SHALL ให้ `GET /api/v1/products` เลิกรับ query param `filters`, `sort`, `search` — §2 ไม่มี concept นี้ จึงเป็น surface ที่เกินเอกสาร
- 7.2 THE SYSTEM SHALL จัดลำดับผลลัพธ์คงที่ด้วย `OrderBy(DocumentNo)` (unique ต่อ merchant, มี index หนุน) แทน default sort เดิมที่อิง `CreatedAt`
- 7.3 THE SYSTEM SHALL ลบ `ProductSfs.cs` ทั้งไฟล์และเลิกเรียก `ApplySearch`/`ApplyFilters`/`ApplySort` ใน `ProductRepository` โดยไม่แตะ SFS machinery ที่ยังมีผู้ใช้อื่น (admins/roles/policy report/master data)
- 7.4 THE SYSTEM SHALL ให้เอกสาร OpenAPI ของ products ประกาศเฉพาะ `page`/`limit`/`productFilters` — ห้ามโฆษณา `filters`/`sort`/`search` ที่ไม่ทำอะไร
- 7.5 THE SYSTEM SHALL ให้ `ListProductsQuery` เลิก inherit `PagedQuery` แล้วประกาศ `Page`/`Limit` + `required ProductFilterDto ProductFilters` ของตัวเอง — ถ้ายัง inherit จะเหลือ `Filters`/`Sort`/`Search` ที่ตั้งค่าได้แต่ไม่มีใครอ่าน = กับดักเงียบ

## REQ-8: read model ตรง §5.2

- 8.1 THE SYSTEM SHALL ให้ `ProductListItem` มี field ครบทั้ง 32 ตัวของ §5.2 (บวก `Id`) เลิกเป็น "slim subset"
- 8.2 THE SYSTEM SHALL ลบ `ProductView` แล้วให้ `GetProductByIdQuery` คืน `ProductListItem` ตัวเดียวกัน — สอง record ที่มี field ชุดเดียวกันไม่ต้องมีสองอัน
- 8.3 THE SYSTEM SHALL ลบ dead code `GetProductsQuery.cs` และ `IProductRepository.ListByTenantAsync`
- 8.4 THE SYSTEM SHALL ให้ currency ถูก mint เป็น `Money` ที่ boundary จุดเดียว (cart add-item ใน `src/Hosts/Api/Program.cs`) — Cart/Checkout/Order/PaymentSession ยังเป็น `Money{Amount,Currency}` ตาม standing decision

## REQ-9: error mapping ตาม §6

- 9.1 WHEN `saleCode` หายไปหรือ trim แล้วว่าง, THE SYSTEM SHALL ตอบ 400 ProblemDetails พร้อมข้อความอ้าง SP error 50005
- 9.2 WHEN `paymentStatus` ไม่ใช่ `UNPAID`/`PAID`/`ALL`, THE SYSTEM SHALL ตอบ 400 ProblemDetails พร้อมข้อความอ้าง SP error 50007
- 9.3 WHEN `paidDateFrom > paidDateTo`, `coverageStartFrom > coverageStartTo` หรือ `coverageEndFrom > coverageEndTo`, THE SYSTEM SHALL ตอบ 400 ProblemDetails พร้อมข้อความอ้าง SP error 50003 / 50008 / 50009 ตามลำดับ (พฤติกรรมเดิม — ต้องคงไว้)
- 9.4 THE SYSTEM SHALL ส่งทุก error ข้างต้นผ่าน `ArgumentException` -> `ProblemDetailsExceptionHandler` และไม่รั่วข้อความระดับ SQL/infra กลับ client

## REQ-10: DB, EF config และ seed

- 10.1 THE SYSTEM SHALL แก้ `ProductConfiguration` **ทั้งสองไฟล์** (`Products.Infrastructure` = migration owner, `Persistence.MerchantRuntime/Products` = runtime mirror) ให้ mapping ตรงกันเป๊ะ: ลบ mapping 8 คอลัมน์, เลิก `ComplexProperty`, ใช้ `HasPrecision(19, 2)`, rename 4 คอลัมน์, ลบ index `IX_Products_MerchantId_IsActive`
- 10.2 THE SYSTEM SHALL มี migration ใหม่หนึ่งตัวต่อจาก `20260730081227_CheckoutChainDocumentFields` ที่ `DropIndex` + `DropColumn` x8 + `RenameColumn` x4 + `AlterColumn` precision บน `shop.Products` โดยไม่ `DropTable` (จึงไม่ต้อง re-GRANT `pol_app`)
- 10.3 THE SYSTEM SHALL ให้ `Down()` ของ migration คืนสภาพเดิมได้ครบทุกคอลัมน์/index ตาม destructive-ops rule
- 10.4 THE SYSTEM SHALL regen `PolDbContextModelSnapshot.cs` จาก `dotnet ef` เท่านั้น และห้ามแก้ migration/designer เก่า (`scripts/check-migration-lineage.sh` เฝ้าอยู่)
- 10.5 THE SYSTEM SHALL อัปเดต `docker/bootstrap/seed-demo.sql` (INSERT `shop.Products` ทั้งสองก้อน) ให้ตรง column ชุดใหม่ ลบ `CASE WHEN Seq % 7 = 0` และรันทั้งไฟล์ผ่านบน dev DB
- 10.6 THE SYSTEM SHALL อัปเดต spec `demo-seed-data` (REQ-5.4 ใน `requirements.md` + `tasks.md` รวม verify query ที่ยัง `SELECT COUNT(DISTINCT IsActive)`) ให้อ้าง `PaymentStatus` แทน `IsActive`

## REQ-11: เอกสารในโค้ดต้องชี้ไฟล์จริง

- 11.1 THE SYSTEM SHALL ให้ XML doc comment ที่อ้าง "VCentralPay SP guide" ใน `Products.Domain/{DocumentType,PaymentStatus,ProductGroup,Product}.cs` ชี้ path จริง `docs/reference/vcentralpay-sp-quick-reference.pdf` — ต้นเหตุที่โค้ดเพี้ยนคือเอกสารอยู่นอก repo
- 11.2 THE SYSTEM SHALL commit ไฟล์ `docs/reference/vcentralpay-sp-quick-reference.pdf` เข้า repo เป็นเอกสารต้นเรื่องที่ตรวจย้อนได้
- 11.3 THE SYSTEM SHALL อัปเดต spec/docs ทุกจุดที่อ้าง field ที่หายไป (`IsActive`/`CreatedAt`/`BranchCode`/`Money TotalPremium`) ได้แก่ `.ai/specs/checkout-chain-document-fields/*`, `.ai/specs/insurance-pivot/design.md`, `.ai/specs/search-filter-sort/tasks.md`, `docs/reference/{entity-fields,platform-modules,src-structure,search-filter-sort}.md`

## REQ-12: คุณภาพรวม

- 12.1 WHEN งานทั้ง spec เสร็จ, `dotnet build pol-core.slnx -warnaserror` SHALL ผ่าน 0 error / 0 warning
- 12.2 WHEN งานทั้ง spec เสร็จ, test suite ทั้งหมด (รวม `Integration.Tests` บน :11433) SHALL เขียว โดยไม่มี `.only`/`.skip` ค้าง
- 12.3 WHEN งานทั้ง spec เสร็จ, `bash scripts/spec-trace.sh products-sp-53-alignment` SHALL พิมพ์บรรทัดที่ขึ้นต้นด้วย `OK:` (exit code 0 เดี่ยว ๆ เชื่อไม่ได้ — heading ที่ไม่ใช่ `## REQ-N:` ถูก silent-skip)
- 12.4 WHEN งานทั้ง spec เสร็จ, `bash scripts/check-rename-identifiers.sh` และ `bash scripts/check-migration-lineage.sh` SHALL ผ่าน
- 12.5 WHEN recreate DB ใหม่ (`docker compose down -v` + migrate + seed), THE SYSTEM SHALL ให้ `shop.Products` เหลือคอลัมน์ตรง §5.2 เท่านั้น (ตรวจด้วย `INFORMATION_SCHEMA.COLUMNS`)
- 12.6 WHEN ยิง `GET /api/v1/products` บน dev, THE SYSTEM SHALL ผ่าน E2E ทุกเคสในหัวข้อ Verification ของ design.md (ไม่ส่ง `productFilters` -> 400; window; RENEWAL; Motor gate; `limit=1000` -> 25; checkout chain PAID/UNPAID)

## นอกขอบเขต (จงใจ)

- **F6 `@CountMode` + §5.1 pagination envelope** — `TotalRows`/`TotalPages` NULL ตอน FAST + `HasNextPage`/`HasPreviousPage`/`CountMode`/`SearchWindowMonths` ต้องแก้ `PagedResult` = กระทบทุก list endpoint; SFS convention ระบุว่ายังไม่ทำ -> spec แยก
- **map `@BranchCode` ไป `ReferenceBranch`** — §5.2 มี `ReferenceBranch` (`varchar(3)`, "รหัสสาขาของเลขอ้างอิง") ที่หน้าตาเหมือนกัน แต่เอกสารไม่ยืนยันว่าเป็น field เดียวกัน ต้องถามเจ้าของ SP ก่อน map
- **actor -> sale claim** (ย้าย `SaleCode` ไป server-side authorization context) — follow-up spec
- **SP adapter จริง** (motordb/centerdb) — เฟสถัดไป

## Deviation ที่จงใจคง (ตัดสินแล้ว ห้าม re-litigate)

- **precision**: `Product` ใช้ `decimal(19,2)` ตาม §5.2 แต่ `Money` ที่เหลือทั้งระบบยังเป็น `DECIMAL(19,4)` ตาม standing decision -> ค่าที่มี 3-4 ตำแหน่งถูกปฏิเสธที่ `Product.Create` (REQ-1.5) ไม่ใช่ปัดเงียบ
- **`SaleCode` รับจาก client** ขัด §1.1 ข้อ 1 + กรอบสิทธิ์หน้า 2 ที่ระบุว่าควรมาจาก server-side authorization context (user ยืนยันแล้ว)
- **`shop.Products` เป็นแคตตาล็อกกลาง ไม่มีคอลัมน์ `MerchantId`** (user ตัดสิน 2026-07-30 ระหว่าง PR #144 หลังเห็น blast radius) — §5.2 ไม่มี field นี้ และทุก merchant ขายจาก pool เดียวกัน ผลที่ตามมาโดยตั้งใจ: ไม่มี tenant key/query filter บน `Product` (เอนทิตีเดียวใน `MerchantRuntimeDbContext` ที่ไม่มี), `DocumentNo` unique ทั้งระบบแทน unique ต่อ merchant, ขอบเขตต่อ request = `SaleCode` ที่บังคับใน `productFilters` เท่านั้น, และ merchant หนึ่งเห็น/ซื้อเอกสารของ sale code ใดก็ได้ที่ส่งมา ⇒ การกลับไปใส่ tenant key ต้องเป็นการตัดสินใจใหม่ ไม่ใช่ regression fix
- **`DocumentNo`/`PaymentStatus`/`SaleCode` เอกสารให้ NULL ได้ แต่ repo คง NOT NULL** — เอกสารเป็น read model ของระบบต้นทาง; repo นี้เป็นเจ้าของข้อมูล และ `DocumentNo` เป็น unique key ทั้งระบบ
- **`@PolicyNo`/`@ApplicationNo` จำกัด 30 ตาม §2 แต่คอลัมน์ §5.2 เป็น 150** -> ค่าที่ยาวกว่า 30 ค้นไม่ได้; เอกสารไม่สอดคล้องกันเอง ทำตาม §2
- **ไม่มี endpoint activate/deactivate เอกสารอีกต่อไป** (`Deactivate()` ถูกลบ, permission `product.update` ยังจองไว้)
