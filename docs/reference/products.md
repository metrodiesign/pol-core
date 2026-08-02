# โมดูล Products

> เอกสารรวมทุกอย่างเกี่ยวกับโมดูล Products ไว้ที่เดียว — เดิมเนื้อหากระจายอยู่ใน
> `entity-fields.md` / `platform-modules.md` / `src-structure.md` / `layers-guide.md` /
> `db-connection-and-rls.md` / `search-filter-sort.md`; ไฟล์เหล่านั้นตอนนี้ชี้กลับมาที่นี่แทน
> การพูดซ้ำ — แก้ข้อมูล Products ที่นี่ที่เดียว

ส่วนแรกด้านล่าง (ฉบับไม่ใช้ศัพท์เทคนิค) อธิบายภาพรวมให้คนที่ไม่ได้เขียนโค้ดเข้าใจ ส่วนที่เหลือ
ต่อจากนั้นเป็นรายละเอียดเชิงเทคนิคสำหรับคนที่ทำงานกับโค้ดโดยตรง

## ปัญหาคืออะไร

ระบบของเราไม่ได้เก็บข้อมูล "กรมธรรม์/ใบเสร็จประกัน" เอง — ข้อมูลจริงอยู่ในระบบของอีก 2
หน่วยงานที่เราไม่ได้เป็นเจ้าของ:

- ฝั่ง **รถยนต์** (ประกันรถ CMI/VMI) — อยู่ในเซิร์ฟเวอร์ชื่อ **hippo**
- ฝั่ง **ไม่ใช่รถยนต์** (ประกันไฟไหม้/เบ็ดเตล็ด FIRE/MISC) — อยู่ในเซิร์ฟเวอร์ชื่อ **mammoth**

ทั้งสองฝั่งเปิด "ช่องทางค้นหา" ให้เรียกใช้ได้ (เหมือนช่องบริการหน้าเคาน์เตอร์ที่รับคำถามแล้วตอบ
ข้อมูลกลับมา) แต่รูปแบบข้อมูลที่ตอบกลับมา ถูกออกแบบตามมาตรฐานของ**เขา** ไม่ใช่ของเรา

## โฟลเดอร์นี้ทำหน้าที่อะไร

`Ports/` คือ **จุดเชื่อมต่อจุดเดียว** ระหว่างระบบของเรากับช่องบริการทั้ง 2 ฝั่ง — เปรียบเทียบ
ง่าย ๆ เหมือน **ล่าม + เจ้าหน้าที่ตรวจเอกสาร** ที่ยืนอยู่หน้าประตูก่อนข้อมูลจะเข้าระบบเรา:

1. **แปลคำถามของเราเป็นภาษาที่เขาเข้าใจ** — ผู้ใช้ต้องระบุก่อนว่าจะค้นฝั่งไหน (ประกันรถ หรือ
   ไม่ใช่รถ) หรือไม่ก็ระบุ `productGroup` ที่บอกฝั่งอยู่ในตัว (เช่น `CMI` = รถ) เพราะช่องบริการ
   ทั้ง 2 ฝั่งแยกกันเด็ดขาด รวมกันไม่ได้ — ถ้าไม่ระบุเลยทั้งคู่ ระบบจะปฏิเสธคำถามทันทีแทนที่จะ
   เดาให้
2. **รับคำตอบตามที่เขาส่งมา โดยไม่รีบเชื่อ** — คำตอบที่ได้กลับมาถือเป็น "ข้อมูลดิบ" ยังไม่ใช่
   ข้อมูลที่ระบบเราจะใช้งานตรง ๆ (เหตุผลอยู่หัวข้อถัดไป)
3. **ตรวจสอบก่อนรับเข้า** — ถ้าคำตอบผิดรูปแบบ หรือข้อมูลบางแถวไม่ครบ (เช่น ไม่มีเลขกรมธรรม์)
   แถวนั้นจะถูก**ข้าม**ไม่นำเข้าระบบ พร้อมบันทึกเหตุผลไว้ให้ตรวจสอบย้อนหลังได้ ไม่ทำให้หน้าจอ
   ทั้งหน้าใช้งานไม่ได้เพราะข้อมูลแค่แถวเดียวเสีย
4. **แปลงเป็นข้อผิดพลาดที่เข้าใจง่าย** — ถ้าอีกฝั่งปฏิเสธคำถาม (เช่น ใส่วันที่ผิด) ผู้ใช้จะเห็น
   ข้อความแจ้งเตือนที่แก้ไขได้เอง; ถ้าอีกฝั่ง**ล่มหรือติดต่อไม่ได้เลย** ผู้ใช้จะเห็นข้อความว่า
   "ระบบต้นทางไม่พร้อมใช้งาน" — ไม่ใช่ข้อความ error ทางเทคนิคที่งง ๆ และรายละเอียดจริง (เช่น
   ชื่อเซิร์ฟเวอร์ที่ต่อไม่ได้) จะไม่หลุดออกไปให้ผู้ใช้เห็นเด็ดขาด — บันทึกไว้แค่ใน log ฝั่งเราเท่านั้น
   (log เองก็ไม่มีรหัสผ่าน/credential ปนอยู่ — ตามกฎห้าม log ข้อมูล sensitive)

## ทำไมต้องมี "กำแพงกั้น" นี้ ทั้งที่เพิ่มความซับซ้อน

เพราะเราไม่ได้เป็นเจ้าของข้อมูล 2 ระบบนั้น — ถ้าวันหนึ่งเขาเปลี่ยนรูปแบบข้อมูล (เช่น เปลี่ยน
ชื่อคอลัมน์ เพิ่ม/ลดข้อมูลบางอย่าง) **ผลกระทบจะหยุดอยู่แค่จุดเชื่อมต่อนี้จุดเดียว** ไม่ลามเข้าไป
ในส่วนอื่นของระบบเรา (ตะกร้าสินค้า การชำระเงิน หน้าเว็บ) — เหมือนมีด่านตรวจเอกสารหน้าประตูเดียว
แทนที่จะให้ทุกแผนกในบริษัทต้องคุ้นเคยกับรูปแบบเอกสารของอีกบริษัทเอง

อีกเหตุผลคือ **ป้องกันการขายซ้ำ**: ถ้าเอกสารกรมธรรม์ไหนถูกจ่ายเงินแล้ว (`PAID`) ทางฝั่งเรา
ระบบจะไม่ยอมให้ข้อมูลที่ดึงมาใหม่ ทำให้สถานะนั้น "ถอยกลับ" เป็นยังไม่จ่าย แม้ต้นทางจะยังส่ง
ข้อมูลเก่ามาซ้ำก็ตาม — กันไม่ให้กรมธรรม์ที่ขายไปแล้วถูกเสนอขายซ้ำอีกรอบโดยไม่ตั้งใจ

## สรุปเป็นภาพเดียว

```
ผู้ใช้ค้นหากรมธรรม์ (ระบุฝั่งรถ/ไม่ใช่รถ หรือ productGroup ที่บอกฝั่งอยู่ในตัว)
        │
        ▼
   [จุดเชื่อมต่อนี้]  ── ยิงไปช่องบริการฝั่งที่ผู้ใช้ระบุ
        │
        ▼
  ระบบต้นทาง (hippo หรือ mammoth) ตอบข้อมูลดิบกลับมา
        │
        ▼
   [จุดเชื่อมต่อนี้]  ── ตรวจ/กรอง/แปลงเป็นรูปแบบของเรา, ทิ้งแถวที่ข้อมูลไม่ครบ
        │
        ▼
   บันทึกเข้าระบบเรา (ไม่ทับสถานะ "จ่ายแล้ว" ที่มีอยู่)
        │
        ▼
      ผู้ใช้เห็นผลลัพธ์
```

หมายเหตุ: วันนี้ "ระบบต้นทาง" ที่เราเชื่อมอยู่เป็น**ระบบจำลอง**ที่สร้างขึ้นเองภายในบริษัท
(ให้หน้าตา/พฤติกรรมเหมือนของจริงทุกประการ) เพื่อให้ทีมอื่นพัฒนาต่อได้โดยไม่ต้องรอเชื่อมกับ
ระบบจริงของอีก 2 หน่วยงานก่อน — วันที่เชื่อมของจริง จะเปลี่ยนแค่ "ที่อยู่" ที่ระบบไปติดต่อ
ไม่ต้องแก้โค้ดส่วนอื่นเลย

---

## บริบท + บทบาท (technical)

สิ่งที่ขายบนแพลตฟอร์ม = **เอกสารประกัน** (กรมธรรม์/ใบคำขอ/สลักหลัง/ใบต่ออายุ) ที่รอเก็บเงิน
**แคตตาล็อกกลางเดียว** (`shop.Products`, ไม่มีคอลัมน์ `MerchantId`, ไม่มี query filter) — ไม่แยกต่อ
merchant `Product` เป็น mirror ของ result set §5.2 ใน
[`vcentralpay-sp-quick-reference.pdf`](./vcentralpay-sp-quick-reference.pdf) ตรง ๆ (spec
`products-sp-53-alignment`)

- `Product`: `DocumentNo` (unique ทั้งแคตตาล็อก — `IX_Products_DocumentNo`), `ProductGroup`
  (`CMI`/`VMI`/`FIRE`/`MISC`), `DocumentType` (`POLICY`/`APPLICATION`/`RENEWAL`/`ENDORSEMENT`),
  `SaleCode`, เลขเอกสาร 4 ชุด, ช่วงคุ้มครอง (`StartDate`/`EndDate`), `LicensePlateNumber`,
  `ShowName` และ **`PaymentStatus` (`UNPAID`/`PAID`) + `PaidDate`** ซึ่งเป็นแกนขายได้/ขายไม่ได้
  (ไม่มี `IsActive` แล้ว)
- ค่าเงินเป็น `decimal(19,2)` เปล่า **ไม่ใช่ `Money`**: `TotalPremium` (บังคับ) + breakdown
  `NetPremium`/`Stamp`/`TaxVat`/`CommissionAmount`/`CommissionPercent` — `Create` throw ถ้า
  `TotalPremium <= 0`, ค่าติดลบ, ทศนิยมเกิน 2 ตำแหน่ง, `StartDate > EndDate` หรือ `CMI` +
  `APPLICATION`; currency (`THB`) ถูก mint ที่ boundary เดียวคือ cart add-item
- เป็น source ของเบี้ย**และ field เอกสาร**เสมอ — ทั้ง Cart (เบี้ย) และ Checkout (field เอกสาร)
  ดึงจาก catalog ตอนทำรายการ, ไม่รับค่าเหล่านี้จาก client
- endpoints: `GET /products` **ตัวเดียว** — แคตตาล็อกเป็น read-only ผ่าน HTTP (เอกสารมาจากระบบ
  กรมธรรม์ต้นทาง ไม่ได้เกิดจาก merchant กรอกฟอร์ม); write seam คือ `CreateProductCommand` ที่ไม่ถูก
  map เป็น route (**ไม่มี SFS** — รับแค่ `page`/`limit` + typed `productFilters` ที่บังคับมี
  `saleCode`)
- **read path ไม่อ่าน `shop.Products` แล้ว** (spec `products-sp-gateway`) — `GET /products` ค้นสด
  ผ่าน `ISpDocumentGateway` ไปยัง SP ของระบบต้นทาง (วันนี้คือ database จำลอง
  `hippodb`/`mammothdb` ใน container เดียวกับ DB หลัก; วันเชื่อมของจริงเปลี่ยนแค่ connection
  string) แล้ว **upsert ผลลัพธ์กลับเข้า `shop.Products` ด้วย key `DocumentNo`** เพื่อให้
  cart/checkout ยังอ้าง `Guid` เดิมได้ — `IProductRepository` จึงไม่มี `ListAsync` อีกแล้ว เหลือ
  `UpsertByDocumentNoAsync` + `GetAsync`
- **paging / order / search window เป็นของ SP ไม่ใช่ของเรา**: order คงที่ด้วย `DocumentNo`,
  window 6 เดือน (Motor `RENEWAL` ใช้ `EndDate` 2 เดือน) และการนับทั้งหมดเกิดใน SP — โค้ดฝั่งเรา
  คัดลอกค่ามาใส่ envelope ตรง ๆ ไม่คำนวณซ้ำ

### ตารางฟีเจอร์

| ฟีเจอร์ | รายละเอียด | สถานะ |
|---|---|---|
| สร้างเอกสาร | ไม่มี HTTP endpoint — `POST /products` ถูกถอดออก (แคตตาล็อก read-only, เอกสารมาจากระบบต้นทาง); เข้าถึงได้ผ่าน `CreateProductCommand` ภายในเท่านั้น | ถอดออกแล้ว |
| เบี้ยเป็น source of truth | Cart ดึงเบี้ยจาก catalog ตอน add แล้ว mint `Money.Of(TotalPremium, "THB")` — ไม่รับเบี้ยจาก client | มีแล้ว |
| field เอกสารบน `Product` | `DocumentNo`/`ProductGroup`/`DocumentType`/`PolicyNumber`/`StartDate`/`EndDate` — snapshot เข้า `OrderItem` ตอน checkout-start (server-side, ไม่รับจาก client) | มีแล้ว (checkout-chain-document-fields) |
| List ตาม §2 input contract | `GET /products` — `page`/`limit` (cap 25) + typed `productFilters` (`saleCode` บังคับ, `paymentStatus` default `UNPAID`, smart search รวมทะเบียนรถเฉพาะแถว Motor, search window 6 เดือน / `RENEWAL` 2 เดือน); **ไม่มี** `filters`/`sort`/`search` (`ProductSfs` ถูกลบ) — filter เพิ่ม 2 ตัวจาก products-sp-gateway: `insuranceType` (`Motor`\|`NonMotor`, บังคับเมื่อไม่ได้ส่ง `productGroup` และห้ามขัดแย้งกัน -> 400) ซึ่งเป็นตัวเลือกว่าจะยิง SP ตัวไหน และ `countMode` (`EXACT`\|`FAST`, absent = `EXACT`) | มีแล้ว (products-sp-53-alignment + products-sp-gateway) |
| ค้นสดผ่าน SP + upsert แคตตาล็อก | `GET /products` เรียก `ISpDocumentGateway.SearchAsync` (adapter ADO.NET `CommandType.StoredProcedure` ใน `Products.Infrastructure/Sp/`) -> map แถว wire เป็น `ProductInput` (ข้ามแถวที่ key ว่าง/เบี้ยไม่ถูก/enum ไม่รู้จัก + log warning) -> `UpsertByDocumentNoAsync` เข้า `shop.Products` (ไม่มี -> `Create`, มีแล้ว -> `RefreshFromExternal` ซึ่งไม่ downgrade `PAID` ของ local และ throw เมื่อ `DocumentNo` ไม่ตรงหรือ `ProductGroup` สลับฝั่ง Motor/NonMotor) -> ตอบ item พร้อม `Guid` ของแถว local | มีแล้ว (products-sp-gateway) |
| Response envelope §5.1 | `ProductPage` = `items` + `totalRows?`/`totalPages?`/`pageNo`/`pageSize`/`hasNextPage`/`hasPreviousPage`/`countMode`/`searchWindowMonths` คัดลอกจาก result set แรกของ SP ตรง ๆ (ไม่คำนวณซ้ำ); `countMode=FAST` -> `totalRows`/`totalPages` เป็น `null` — **breaking change** จาก `PagedResult` เดิม (`page`/`limit`/`total`) และใช้เฉพาะ endpoint นี้ endpoint อื่นทั้ง repo ยังเป็น `PagedResult` | มีแล้ว (products-sp-gateway) |
| SP ล่ม/ต่อไม่ได้ | `SqlException` ที่ไม่ใช่ error 50001-50009 (timeout/login/network/column drift) -> `UpstreamUnavailableException` -> **503** พร้อม detail คงที่ ไม่รั่วข้อความ SQL (รายละเอียดเต็มลง log ฝั่ง server เท่านั้น); connection string ที่ config parse ไม่ได้ (`ArgumentException`/`FormatException` จาก `new SqlConnection`) ก็เข้าทาง 503 นี้เช่นกัน — misconfig ฝั่ง operator ไม่ใช่ input ผิดของ caller (Codex review PR #150); input ที่ SP ปฏิเสธ (50001-50009) -> **400** | มีแล้ว (products-sp-gateway) |
| DocumentNo case-insensitive matching | `IX_Products_DocumentNo` unique ใต้ default DB collation (case-insensitive) — ทุกจุดที่เทียบ/dedupe `DocumentNo` ฝั่ง CLR (`UpsertByDocumentNoAsync` dictionary key, `SpDocumentItemMapper` in-page dedupe, `RefreshFromExternal` guard) ใช้ `OrdinalIgnoreCase` ให้ตรง semantics ของ index; refresh แล้ว adopt casing จาก wire (พบจาก Codex review PR #150) | มีแล้ว (products-sp-gateway) |
| Query รายตัวภายใน | `GetProductById` ผ่าน Mediator — ผู้ใช้คือ Cart/Checkout ตอน add item / เริ่ม checkout (ไม่มี public endpoint) | มีแล้ว |
| mark เอกสารเป็น PAID | `Product.MarkPaid` ผ่าน `DocumentPaidOnOrderPaidConsumer` ตอน order จ่ายสำเร็จ — เป็นทางเดียวที่ทำให้เอกสารขายซ้ำไม่ได้ | มีแล้ว |
| แก้ไข/ถอนเอกสารจากการขาย | ไม่มี endpoint และ **ไม่มี `Deactivate()`** อีกแล้ว (ถูกลบใน products-sp-53-alignment) — แกน "ขายไม่ได้" เหลือทางเดียวคือขายจบแล้วเป็น `PAID`; permission `product.update` ถูกถอดออกทั้งคู่กับ `product.create` ใน migration `20260731065539_RetireCatalogPermissions` | ยังไม่มี |
| อ่านรายตัว public | target: `GET /api/producer/v1/products/{productId}` สำหรับหน้า detail ฝั่ง console | ยังไม่มี |
| Product versioning + quote | target formalize แล้ว: `Product` (identity/สถานะ) + `ProductVersion` (immutable version ของชื่อ/coverage/premium/currency/effective period) + `ProductQuote` (optional เมื่อราคาต้องคำนวณจากข้อมูลผู้เอาประกัน) — ยังไม่เริ่มทั้งคู่ | ยังไม่มี |

**สถานะ: มีแล้ว** — ไม่ใช่ generic catalog item อีกต่อไป: `Product` เป็น **เอกสารประกัน** ที่ mirror
§5.2 ของ SP quick reference ตรง ๆ — field แผนประกันชุด insurance-pivot (`SumInsured`/
`CoverageDurationDays`/`Insurer`) และ `Name`/`Price`/`IsActive`/`CreatedAt` **ถูกลบทั้งหมด**; target
ยกระดับเป็น Product/ProductVersion/ProductQuote ยังไม่เริ่ม; ยังไม่มีเส้นทางแก้ไขเอกสาร SP adapter
ของจริงเขียนแล้วและใช้งานอยู่ (products-sp-gateway) โดยชี้ไปที่ database จำลอง
`hippodb`/`mammothdb` ซึ่งรัน SP ตัวเดียวกับ contract ในทุก environment — การเชื่อม
`motordb`/`centerdb` ของจริงเหลือแค่เปลี่ยน `SpDocument:MotorConnectionString`/
`NonMotorConnectionString` ทาง config

## Schema — `shop.Products`

> ตารางนี้เคยมี `Name`, `InsurerName`, `CoverageDurationDays`, `PriceAmount`/`PriceCurrency`,
> `SumInsuredAmount`/`SumInsuredCurrency`, `BranchCode`, `IsActive`, `CreatedAt` — **ทั้งหมดถูกลบ**
> (`IsActive` -> gate ใช้ `PaymentStatus == UNPAID` แทน, `Deactivate()` ถูกลบ, permission
> `product.update`/`product.create` ถูกถอดออกทั้งคู่) และ 4 คอลัมน์ `*PremiumAmount`/`StampAmount`/
> `TaxVatAmount` ถูก rename ให้ตรงชื่อ §5.2

> ตัวอย่าง: migration `20260730072057` (6 แถวตัวอย่าง) + `seed-demo.sql` (500 rows `e9000000-…` —
> เติมทุกฟิลด์เอกสารครบตามชนิดเอกสาร, `PAID` 71 แถว / `UNPAID` 429 แถว)

> `shop.Products` เป็น **แคตตาล็อกกลาง** — ไม่มีคอลัมน์ `MerchantId` ไม่มี query filter ต่อ merchant
> (ต่างจากทุกตารางอื่นในระบบ) ขอบเขตต่อ request มาจาก `SaleCode` ที่บังคับใน `productFilters`

| Field | Type | Null | Key | ตัวอย่าง | หมายเหตุ |
|---|---|---|---|---|---|
| Id | uniqueidentifier | N | PK | `e9000000-…-0006` | app assign |
| ProductGroup | varchar(10) | N | | `CMI` (`VMI`/`FIRE`/`MISC`) | = `SourceSystem` ของ §5.2; `CMI`/`VMI` = Motor |
| DocumentType | varchar(20) | N | | `POLICY` (`APPLICATION`/`RENEWAL`/`ENDORSEMENT`) | `CMI` + `APPLICATION` ไม่รองรับ (throw ตอน `Create`) |
| DocumentNo | nvarchar(150) | N | UQ | `69301/กธ/910001` | unique **ทั้งระบบ** (`IX_Products_DocumentNo`) + เป็น order key ของ `GET /products`; column collation case-insensitive (default DB collation) — โค้ดฝั่ง CLR ที่เทียบ/dedupe ค่านี้ (`ProductRepository.UpsertByDocumentNoAsync`, `SpDocumentItemMapper`, `Product.RefreshFromExternal`) ต้องใช้ `OrdinalIgnoreCase` ให้ตรง index มิฉะนั้น row ต่างแค่ตัวพิมพ์จะชน unique ซ้อน (พบจาก Codex review PR #150) |
| PolicyYear | varchar(2) | Y | | `69` | ปี พ.ศ. 2 หลัก |
| ReferenceBranch | varchar(3) | Y | | `301` | รหัสสาขาของเลขอ้างอิง (**ไม่ใช่** `@BranchCode` ของ §2 — ยังไม่ยืนยันว่า field เดียวกัน) |
| ReferencePre | varchar(20) | Y | | `บต` | prefix เลขอ้างอิง |
| PolicySequenceNo | varchar(30) | Y | | `900008` | ลำดับที่ในเล่ม |
| ReferenceYear | varchar(2) | Y | | `69` | ปีของเลขอ้างอิง |
| ReferenceNo | varchar(30) | Y | | `910007-10` | เลขอ้างอิง |
| SaleCode | varchar(20) | N | IX | `77001` | รหัสผู้ขาย (ตัวเลข 5 หลัก) — filter บังคับของ `GET /products` (§2 `@SaleCode`) |
| SaleFullName | nvarchar(500) | Y | | `สมชาย ใจดี` | ชื่อผู้ขาย |
| BrokerCode | varchar(20) | Y | | `BRK001` | รหัสนายหน้า |
| BrokerName | nvarchar(500) | Y | | `บริษัทนายหน้า จำกัด` | ชื่อนายหน้า |
| PolicyBranch | nvarchar(250) | Y | | `สาขาสีลม` | สาขาที่ออกกรมธรรม์ |
| PolicyType | nvarchar(250) | Y | | `ประกันภัยรถยนต์ภาคบังคับ` | ชนิดกรมธรรม์ |
| PolicyNumber | varchar(150) | Y | | `P-2569-000123` | เลขกรมธรรม์ (§2 `@PolicyNo` ค้นได้แค่ 30 ตัวแรกตามเอกสาร) |
| ApplicationNumber | varchar(150) | Y | | `A-2569-000123` | เลขใบคำขอ |
| PreviousPolicyNumber | varchar(150) | Y | | `P-2568-000123` | เลขกรมธรรม์เดิม (ใช้ตอนต่ออายุ) |
| EndorsementNumber | varchar(150) | Y | | `E-2569-0001` | เลขสลักหลัง |
| StartDate | datetime2(0) | Y | | `2026-07-01T00:00:00` | วันเริ่มคุ้มครอง; window ทั่วไป = ย้อนหลังไม่เกิน 6 เดือน |
| EndDate | datetime2(0) | Y | | `2027-06-30T00:00:00` | วันสิ้นสุด; `RENEWAL` ใช้ window 2 เดือนข้างหน้าบนคอลัมน์นี้. `Create` throw ถ้า `StartDate > EndDate` |
| ShowName | nvarchar(500) | Y | | `นางสาวสมหญิง รักดี` | ชื่อที่แสดงบนเอกสาร |
| LicensePlateNumber | nvarchar(100) | Y | | `กก 1234 กรุงเทพมหานคร` | ทะเบียนรถ — เข้า smart search **เฉพาะแถว `CMI`/`VMI`** |
| TotalPremium | decimal(19,2) | N | | `1200.00` | เบี้ยรวมที่ลูกค้าจ่าย; `Create` throw ถ้า <= 0 หรือทศนิยม > 2 ตำแหน่ง |
| NetPremium | decimal(19,2) | Y | | `1100.00` | เบี้ยสุทธิ |
| Stamp | decimal(19,2) | Y | | `5.00` | อากรแสตมป์ |
| TaxVat | decimal(19,2) | Y | | `95.00` | ภาษีมูลค่าเพิ่ม |
| CommissionAmount | decimal(19,2) | Y | | `132.00` | ค่าคอมมิชชันเป็นจำนวนเงิน |
| CommissionPercent | decimal(19,6) | Y | | `12.000000` | ค่าคอมมิชชันเป็นเปอร์เซ็นต์ |
| PaymentStatus | varchar(10) | N | IX | `UNPAID` (`PAID`) | **แกนขายได้/ขายไม่ได้** — cart/checkout รับเฉพาะ `UNPAID` |
| PaidDate | datetime2(0) | Y | | `2026-07-23T09:00:00` | ตั้งคู่กับ `PaymentStatus = PAID` เสมอ (`MarkPaid`) |

`InsuranceType` (`Motor`/`NonMotor`) เป็น computed property บน entity (`builder.Ignore`)
**ไม่มีคอลัมน์** — คำนวณจาก `ProductGroup is CMI or VMI`

**คืออะไร**: รายการ "เอกสารประกัน" (กรมธรรม์/ใบคำขอ/สลักหลัง/ใบต่ออายุ) ที่ merchant มีอยู่และรอ
เก็บเงิน — เหมือนแฟ้มใบแจ้งหนี้ที่ยังไม่ชำระ ไม่ใช่ป้ายราคาบนชั้นวาง
**บทบาท**: เป็นจุดเริ่มต้นของทุก flow ขาย — เบี้ยและ field เอกสารของทุกจุดปลายทาง (CartItems,
CheckoutSessionItems, OrderItems) เป็นการ "snapshot" มาจากตารางนี้ตอนหยิบใส่ตะกร้า ไม่มีจุดไหน
อ่านราคาสดจาก Products อีกเลยหลัง snapshot แล้ว
**ถ้าไม่มีตารางนี้จะพังยังไง**: ไม่มีที่มาของเบี้ย/เลขเอกสารให้ snapshot — ตัวแทนขายต้องพิมพ์เอง
ทุกครั้ง (เสี่ยงพิมพ์ผิดหรือเก็บเงินไม่ตรงกรมธรรม์) และไม่มีทางกันการขายเอกสารเดิมซ้ำ เพราะ gate
ที่กันเอกสาร `PAID` เข้าตะกร้าจะไม่มีอะไรให้เช็ค
**ทำงานยังไง**: `Product.Create(ProductInput)` (`Products.Domain/Product.cs`) enforce
`TotalPremium > 0`, ค่าเงินทุกตัวห้ามติดลบและห้ามมีทศนิยมเกิน 2 ตำแหน่ง (throw
`ArgumentException` — ไม่ปล่อยให้ DB ปัดเงียบ ๆ), `StartDate <= EndDate`, `Enum.IsDefined` ของ
`ProductGroup`/`DocumentType` และกฎ `CMI` + `APPLICATION` ไม่รองรับ หลังสร้างแล้ว state ที่เปลี่ยน
ได้มีทางเดียวคือ `MarkPaid(paidDate)` (set `PaymentStatus = PAID` + `PaidDate`) ซึ่งถูกเรียกโดย
`DocumentPaidOnOrderPaidConsumer` ตอน order จ่ายสำเร็จ — **ไม่มี** `Rename`/`Deactivate`/`Activate`
และไม่มี endpoint แก้ไข endpoint `POST /carts/{cartId}/items` ดึงเบี้ยจาก catalog ตรงนี้เสมอแล้ว
mint `Money.Of(product.TotalPremium, "THB")` ก่อนส่งต่อเป็น `UnitPrice` ให้ `AddItemToCartCommand`
— comment ในโค้ดเขียนตรง ๆ ว่า "the unit price is the catalog's, NEVER the client's"

funnel ธุรกิจ (ลำดับ schema `shop`): `Products → Carts → CheckoutSessions → Orders → PaymentSessions`
— เส้นจาก `Products` ไปยัง `CartItems`/`CheckoutSessionItems`/`OrderItems` เป็น **app-layer only**
(ไม่มี DB FK, เพราะ snapshot ค่าตอนหยิบไม่ใช่การอ้างอิงสด)

grant `pol_app` บน `shop.Products`: SELECT/INSERT/UPDATE/DELETE

## โครงสร้างไฟล์ (`src/Modules/Products/`)

| ไฟล์ | ชั้น | บทบาท |
|------|------|-------|
| `Product.cs` | Domain | aggregate = mirror ของ §5.2 — `Create(ProductInput)`, `MarkPaid` เท่านั้น (ไม่มี `Rename`/`Deactivate`) |
| `ProductInput.cs` / `ProductGroup.cs` / `DocumentType.cs` / `PaymentStatus.cs` / `InsuranceType.cs` | Domain | input record + enum ทั้งชุด (`InsuranceType` = computed `Motor`/`NonMotor`, ไม่มีคอลัมน์) |
| `CreateProductCommand.cs` | App | `ICommand<Guid>` + handler — **ไม่ใช่** `IMerchantScoped`; ไม่ reachable ผ่าน HTTP, เป็น write seam ที่จองไว้ให้ importer ในอนาคต — ผู้เรียกที่มีจริงตอนนี้คือ test เท่านั้น |
| `ListProducts.cs` | App | `ListProductsQuery` (page/limit + `required ProductFilterDto ProductFilters` ที่มี `insuranceType`/`countMode`) → **`ProductPage`** (envelope §5.1) ไม่ใช่ `PagedResult` แล้ว — handler ค้นสดผ่าน `ISpDocumentGateway` -> `SpDocumentItemMapper` -> `UpsertByDocumentNoAsync`; **ไม่มี SFS** |
| `GetProductById.cs` | App | lookup ต่อ id (ใช้ตอนตั้งราคา cart line ฝั่ง server) — คืน `ProductListItem` ตัวเดียวกัน |
| `DocumentPaidOnOrderPaidConsumer.cs` | App | consume `OrderPaid` -> `Product.MarkPaid` (idempotent ต่อ replay) |
| `IProductRepository.cs` | App | port (`Add`/`GetAsync`/`UpsertByDocumentNoAsync`) — `ListAsync` ถูกลบใน products-sp-gateway |
| `Ports/ISpDocumentGateway.cs` / `SpDocumentContracts.cs` / `SpDocumentSearchRejectedException.cs` / `SpDocumentItemMapper.cs` | App | ชั้นแยก Domain ออกจาก wire contract ของ SP ต้นทาง: port + DTO §5.1/§5.2 (nullable ทั้งชุด, enum เป็น raw string) + exception ที่ได้ 400 + mapper `SpDocumentItem` -> `ProductInput?` (ข้ามแถวเสีย + dedupe) — **type ชุดนี้ห้ามหลุดออกนอก `Products.Application`/`Products.Infrastructure`** (guard `tests/Hosts.Tests/SpInsulationTests.cs`) |
| `Products.Infrastructure/Sp/SpDocumentGateway.cs` / `SpDocumentOptions.cs` | Infra | adapter ADO.NET (`Microsoft.Data.SqlClient` + `CommandType.StoredProcedure`) ยิง `usp_Motor_SearchDocument`/`usp_NonMotor_SearchDocument`; connection string มาจาก section `SpDocument` (ว่างไว้ = derive จาก `ConnectionStrings:App` เป็น `hippodb`/`mammothdb`), `@BranchCode` เติมที่ adapter ไม่รับจาก client |
| `ProductConfiguration.cs` / `ProductsModuleRegistration.cs` | Infra | EF config + `AddProductsModule()` ลงทะเบียน `AddSingleton<ISpDocumentGateway, SpDocumentGateway>()` จริง (singleton เพราะ gateway ไม่ถือ state อะไรนอกจาก options/logger, เปิด connection ใหม่ต่อ call) — implementation ของ `IProductRepository` อยู่นอกโมดูล |
| `Persistence.MerchantRuntime/Products/ProductConfiguration.cs` / `ProductRepository.cs` | Infra (นอกโมดูล) | EF config + repo ตัวจริงที่ผูก runtime context — โมดูลถือแค่ port (`IProductRepository`) |

**จุดสังเกต**: คนใหม่ grep หา implementation ของ `IProductRepository` ใน `Products.Infrastructure`
จะไม่เจอ — ต้องรู้ว่ามันย้ายออกไปอยู่ `Persistence.MerchantRuntime` ทั้งก้อนแล้ว (ต่างจาก pattern
ทั่วไปที่ Infrastructure project ของโมดูลธุรกิจมักมี repository implementation ของตัวเอง)

`Products.Application` reference แค่ `Products.Domain`, `Contracts`, `BuildingBlocks.Application`
— ไม่แตะ `.Domain` โมดูลอื่นเลย (ไม่มี cross-module `.Domain` reference)

## ข้อยกเว้นจาก convention ทั่วไปของ repo

- **ไม่มี merchant isolation floor** — `Product` เป็น entity เดียวใน `MerchantRuntimeDbContext`
  ที่ไม่มี tenant key column และไม่มี `HasQueryFilter` (comment ในไฟล์ configuration บอกตรง ๆ)
  `MerchantRuntimeDbContext.OnModelCreating` เรียก `new ProductConfiguration()` โดย**ไม่ส่ง context
  เข้าไป** (ต่างจากทุก config อื่นในไฟล์เดียวกันที่ส่ง `this` เพื่อผูก query filter) endpoint นี้ยัง
  **บังคับ session ของ merchant-user เหมือนทุก endpoint** (authentication ยังอยู่) — ที่หายไปคือ
  **row filtering ต่อ merchant** เท่านั้น ผลคือ merchant-user ที่ล็อกอินแล้วเห็นเอกสารของ
  `SaleCode` ที่ตัวเองส่งมาได้ทุกใบ ไม่ว่าใบนั้นจะ "เป็นของ" merchant ไหน — เพราะแคตตาล็อกไม่มี
  เจ้าของตั้งแต่แรก
- **`GET /products` เป็น endpoint อ่านแล้วเขียน** แม้เป็น HTTP GET — ตั้งใจ: cart/checkout ต้องอ้าง
  `Guid` ของแถว local ต่อ connection ของ SP มาจาก section `SpDocument` ไม่ใช่
  `ConnectionStrings:App` และเป็นข้อยกเว้นที่ระบุชื่อไว้ใน `RawConnectionTests`
- **ไม่มี SFS (Search/Filter/Sort) ทั่วไป** — รับแค่ `page`/`limit` (cap 25) + typed
  `productFilters` ที่บังคับมี `saleCode`, order คงที่ด้วย `DocumentNo` (ไม่ใช่ `CreatedAt DESC`
  แบบ list endpoint อื่น) `ProductSfs.cs` ถูกลบไปแล้ว ตัวอย่างโค้ดแบบ SFS เดิมใน
  `search-filter-sort.md` §7/§12.2 เป็น**ตัวอย่างเชิงสมมติของ pattern เท่านั้น** ไม่ตรงกับโค้ด
  Products จริงอีกต่อไป
- **ไม่มี concurrency token** — ไม่มี `RowVersion` ทั้งใน aggregate และใน `ProductConfiguration`
  (ต่าง จาก entity อื่นที่มักมี optimistic concurrency)
